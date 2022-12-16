namespace CnCNetServer;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

internal sealed class TunnelV2 : Tunnel
{
    private const int PlayerIdSize = sizeof(short);
    private const int MaxRequestsGlobal = 1000;
    private const int MinGameClients = 2;
    private const int MaxGameClients = 8;

    public TunnelV2(ILogger<TunnelV2> logger, IOptions<ServiceOptions> options, IHttpClientFactory httpClientFactory)
        : base(logger, options, httpClientFactory)
    {
    }

    protected override int Version => 2;

    protected override int Port => ServiceOptions.Value.TunnelV2Port;

    protected override int MinimumPacketSize => 4;

    public Task StartHttpServerAsync(CancellationToken cancellationToken)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        string httpScheme = ServiceOptions.Value.TunnelV2Https ? Uri.UriSchemeHttps : Uri.UriSchemeHttp;

        builder.Logging.ConfigureLogging(ServiceOptions.Value.ServerLogLevel, ServiceOptions.Value.SystemLogLevel);
        builder.WebHost.UseUrls(FormattableString.Invariant($"{httpScheme}://*:{ServiceOptions.Value.TunnelV2Port}"));

        WebApplication app = builder.Build();

        app.MapGet("/maintenance", HandleMaintenanceRequest);
        app.MapGet("/maintenance/{requestMaintenancePassword}", HandleMaintenanceRequest);
        app.MapGet("/status", HandleStatusRequest);
        app.MapGet("/request", HandleRequestRequest);

        if (Logger.IsEnabled(LogLevel.Information))
        {
            Logger.LogInfo(FormattableString.Invariant(
                $"{DateTimeOffset.Now} V{Version} Tunnel {httpScheme} server started on port {ServiceOptions.Value.TunnelV2Port}."));
        }

        return app.RunAsync(cancellationToken);
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync().ConfigureAwait(false);
    }

    protected override int CleanupConnections()
    {
        int clients = base.CleanupConnections();

        ConnectionCounter!.Clear();

        return clients;
    }

    protected override async ValueTask ReceiveAsync(
        ReadOnlyMemory<byte> buffer, IPEndPoint remoteEp, CancellationToken cancellationToken)
    {
        uint senderId = (uint)IPAddress.NetworkToHostOrder(BitConverter.ToInt16(buffer[..PlayerIdSize].Span));
        uint receiverId = (uint)IPAddress.NetworkToHostOrder(BitConverter.ToInt16(buffer[PlayerIdSize..(PlayerIdSize * 2)].Span));

        if (Logger.IsEnabled(LogLevel.Debug))
        {
            Logger.LogDebug(
                FormattableString.Invariant($"V{Version} client {remoteEp} ({senderId} -> {receiverId}) received") +
                FormattableString.Invariant($" {buffer.Length} bytes."));
        }
        else if (Logger.IsEnabled(LogLevel.Trace))
        {
            Logger.LogTrace(
                FormattableString.Invariant($"V{Version} client {remoteEp} ({senderId} -> {receiverId}) received") +
                FormattableString.Invariant($" {buffer.Length} bytes: {Convert.ToHexString(buffer.Span)}."));
        }

        if ((senderId == receiverId && senderId != 0) || remoteEp.Address.Equals(IPAddress.Loopback)
             || remoteEp.Address.Equals(IPAddress.Any) || remoteEp.Address.Equals(IPAddress.Broadcast) || remoteEp.Port == 0)
        {
            if (Logger.IsEnabled(LogLevel.Debug))
            {
                Logger.LogDebug(
                    FormattableString.Invariant($"V{Version} client {remoteEp} invalid endpoint."));
            }

            return;
        }

        if (await HandlePingRequestAsync(senderId, receiverId, buffer, remoteEp, cancellationToken).ConfigureAwait(false))
            return;

        if (Mappings!.TryGetValue(senderId, out TunnelClient? sender))
        {
            if (sender.RemoteEp == null)
            {
                sender.RemoteEp = remoteEp;

                if (Logger.IsEnabled(LogLevel.Information))
                {
                    Logger.LogInfo(
                        FormattableString.Invariant($"{DateTimeOffset.Now} New V{Version} client from {remoteEp}, "));
                }

                if (Logger.IsEnabled(LogLevel.Debug))
                {
                    Logger.LogDebug(
                        FormattableString.Invariant($"{Mappings.Count} clients from ") +
                        FormattableString.Invariant($"{Mappings.Values.Select(q => q.RemoteEp?.Address)
                            .Where(q => q is not null).Distinct().Count()} IPs."));
                }
            }
            else if (!remoteEp.Equals(sender.RemoteEp))
            {
                if (Logger.IsEnabled(LogLevel.Debug))
                {
                    Logger.LogDebug(
                        FormattableString.Invariant($"V{Version} client {remoteEp}") +
                        FormattableString.Invariant($" did not match {sender.RemoteEp}."));
                }

                return;
            }

            sender.SetLastReceiveTick();

            if (Mappings.TryGetValue(receiverId, out TunnelClient? receiver))
            {
                if (Logger.IsEnabled(LogLevel.Debug))
                    Logger.LogDebug(FormattableString.Invariant($"V{Version} client {remoteEp} {receiverId} mapping found."));

                if (receiver.RemoteEp is not null
                    && !receiver.RemoteEp.Equals(sender.RemoteEp))
                {
                    if (Logger.IsEnabled(LogLevel.Debug))
                    {
                        Logger.LogDebug(
                            FormattableString.Invariant($"V{Version} client {remoteEp} ({senderId}) sending {buffer.Length} bytes to ") +
                            FormattableString.Invariant($"{receiver.RemoteEp} ({receiverId})."));
                    }
                    else if (Logger.IsEnabled(LogLevel.Trace))
                    {
                        Logger.LogTrace(
                            FormattableString.Invariant($"V{Version} client {remoteEp} ({senderId}) sending {buffer.Length} bytes to ") +
                            FormattableString.Invariant($"{receiver.RemoteEp} ({receiverId}): ") +
                            FormattableString.Invariant($" {Convert.ToHexString(buffer.Span)}."));
                    }

                    await Client!.SendToAsync(buffer, SocketFlags.None, receiver.RemoteEp, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            else if (Logger.IsEnabled(LogLevel.Debug))
            {
                Logger.LogDebug(
                    FormattableString.Invariant($"V{Version} client {remoteEp} receiver mapping {receiverId} not found."));
            }
        }

        if (Logger.IsEnabled(LogLevel.Debug))
        {
            Logger.LogDebug(
                FormattableString.Invariant($"V{Version} client {remoteEp} message handled"));
        }
    }

    private IResult HandleMaintenanceRequest(HttpRequest request, string? requestMaintenancePassword = null)
    {
        if (!IsNewConnectionAllowed(request.HttpContext.Connection.RemoteIpAddress!))
            return Results.StatusCode((int)HttpStatusCode.TooManyRequests);

        if (ServiceOptions.Value.MaintenancePassword!.Any()
            && ServiceOptions.Value.MaintenancePassword!.Equals(requestMaintenancePassword, StringComparison.Ordinal))
        {
            MaintenanceModeEnabled = true;

            if (Logger.IsEnabled(LogLevel.Warning))
            {
                Logger.LogWarning(FormattableString.Invariant(
                    $"{DateTimeOffset.Now} Maintenance mode enabled by {request.HttpContext.Connection.RemoteIpAddress}."));
            }

            return Results.Ok();
        }

        if (Logger.IsEnabled(LogLevel.Warning))
        {
            Logger.LogWarning(FormattableString.Invariant(
                $"{DateTimeOffset.Now} Invalid Maintenance mode request by {request.HttpContext.Connection.RemoteIpAddress}."));
        }

        return Results.Unauthorized();
    }

    private IResult HandleStatusRequest(HttpRequest request)
    {
        if (!IsNewConnectionAllowed(request.HttpContext.Connection.RemoteIpAddress!))
            return Results.StatusCode((int)HttpStatusCode.TooManyRequests);

        int mappingCount = Mappings!.Count;
        string status = FormattableString.Invariant($"{ServiceOptions.Value.MaxClients - mappingCount} slots free.") +
                        FormattableString.Invariant($"\n{mappingCount} slots in use.\n");

        return Results.Text(status);
    }

    private IResult HandleRequestRequest(HttpRequest request, int? clients)
    {
        if (MaintenanceModeEnabled)
            return Results.StatusCode((int)HttpStatusCode.ServiceUnavailable);

        if (clients is null or < MinGameClients or > MaxGameClients)
            return Results.StatusCode((int)HttpStatusCode.BadRequest);

        var clientIds = new List<int>(clients.Value);

        if (Mappings!.Count + clients <= ServiceOptions.Value.MaxClients)
        {
            if (Logger.IsEnabled(LogLevel.Information))
            {
                var host = new IPEndPoint(
                    request.HttpContext.Connection.RemoteIpAddress!,
                    request.HttpContext.Connection.RemotePort);

                Logger.LogInfo(FormattableString.Invariant(
                    $"{DateTimeOffset.Now} New V{Version} lobby from host {host} with {clients} clients."));
            }

            var rand = new Random();

            while (clients > 0)
            {
                int clientId = rand.Next(0, short.MaxValue);

                if (Mappings.TryAdd((uint)clientId, new(ServiceOptions.Value.ClientTimeout)))
                {
                    clients--;

                    clientIds.Add(clientId);
                }
            }
        }

        if (clientIds.Count < MinGameClients)
            return Results.StatusCode((int)HttpStatusCode.ServiceUnavailable);

        string msg = FormattableString.Invariant($"[{string.Join(",", clientIds)}]");

        if (Logger.IsEnabled(LogLevel.Information))
        {
            var host = new IPEndPoint(
                request.HttpContext.Connection.RemoteIpAddress!,
                request.HttpContext.Connection.RemotePort);

            Logger.LogInfo(FormattableString.Invariant(
                $"{DateTimeOffset.Now} New V{Version} lobby from host {host} response: {msg}."));
        }

        return Results.Text(msg);
    }

    private bool IsNewConnectionAllowed(IPAddress address)
    {
        if (ConnectionCounter!.Count >= MaxRequestsGlobal)
            return false;

        int ipHash = address.GetHashCode();

        if (ConnectionCounter.TryGetValue(ipHash, out int count) && count >= ServiceOptions.Value.IpLimit)
            return false;

        ConnectionCounter[ipHash] = ++count;

        return true;
    }
}