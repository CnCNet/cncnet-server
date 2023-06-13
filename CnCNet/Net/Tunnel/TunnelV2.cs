namespace CnCNetServer;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

internal sealed class TunnelV2(ILogger<TunnelV2> logger, IOptions<ServiceOptions> options, IHttpClientFactory httpClientFactory)
    : Tunnel(logger, options, httpClientFactory)
{
    private const int PlayerIdSize = sizeof(short);
    private const int MaxRequestsGlobal = 1000;
    private const int MinGameClients = 2;
    private const int MaxGameClients = 8;

    protected override int Version => 2;

    protected override int Port => ServiceOptions.Value.TunnelV2Port;

    protected override int MinimumPacketSize => 4;

    public async ValueTask StartHttpServerAsync(CancellationToken cancellationToken)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        string httpScheme = ServiceOptions.Value.TunnelV2Https ? Uri.UriSchemeHttps : Uri.UriSchemeHttp;

        builder.Logging.ConfigureLogging(ServiceOptions.Value.ServerLogLevel, ServiceOptions.Value.SystemLogLevel);
        _ = builder.WebHost.UseUrls(FormattableString.Invariant($"{httpScheme}://*:{ServiceOptions.Value.TunnelV2Port}"));

        WebApplication app = builder.Build();

        _ = app.MapGet("/maintenance", HandleMaintenanceRequest);
        _ = app.MapGet("/maintenance/{requestMaintenancePassword}", HandleMaintenanceRequest);
        _ = app.MapGet("/status", HandleStatusRequest);
        _ = app.MapGet("/request", HandleRequestRequest);

        if (Logger.IsEnabled(LogLevel.Information))
        {
            Logger.LogInfo(FormattableString.Invariant(
                $"V{Version} Tunnel {httpScheme} server started on port {ServiceOptions.Value.TunnelV2Port}."));
        }

        await app.RunAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override int CleanupConnections()
    {
        int clients = base.CleanupConnections();

        ConnectionCounter!.Clear();

        return clients;
    }

    protected override (uint SenderId, uint ReceiverId) GetClientIds(ReadOnlyMemory<byte> buffer)
    {
        uint senderId = (uint)IPAddress.NetworkToHostOrder(BitConverter.ToInt16(buffer[..PlayerIdSize].Span));
        uint receiverId = (uint)IPAddress.NetworkToHostOrder(BitConverter.ToInt16(buffer[PlayerIdSize..(PlayerIdSize * 2)].Span));

        return (senderId, receiverId);
    }

    protected override bool ValidateClientIds(uint senderId, uint receiverId, ReadOnlyMemory<byte> buffer, IPEndPoint remoteEp)
    {
        if ((senderId == receiverId && senderId is not 0u) || remoteEp.Address.Equals(IPAddress.Loopback)
            || remoteEp.Address.Equals(IPAddress.Any) || remoteEp.Address.Equals(IPAddress.Broadcast) || remoteEp.Port is 0)
        {
            if (Logger.IsEnabled(LogLevel.Debug))
                Logger.LogDebug(FormattableString.Invariant($"V{Version} client {remoteEp} invalid endpoint."));

            return false;
        }

        return true;
    }

    protected override async ValueTask HandlePacketAsync(
        uint senderId, uint receiverId, ReadOnlyMemory<byte> buffer, IPEndPoint remoteEp, CancellationToken cancellationToken)
    {
        if (!HandleSender(senderId, remoteEp, out TunnelClient? sender))
            return;

        if (Mappings!.TryGetValue(receiverId, out TunnelClient? receiver))
            await ForwardPacketAsync(senderId, receiverId, buffer, remoteEp, sender!, receiver, cancellationToken).ConfigureAwait(false);
        else if (Logger.IsEnabled(LogLevel.Debug))
            Logger.LogDebug(FormattableString.Invariant($"V{Version} client {remoteEp} receiver mapping {receiverId} not found."));
    }

    private async ValueTask ForwardPacketAsync(
        uint senderId, uint receiverId, ReadOnlyMemory<byte> buffer, IPEndPoint remoteEp, TunnelClient sender, TunnelClient receiver, CancellationToken cancellationToken)
    {
        if (Logger.IsEnabled(LogLevel.Debug))
            Logger.LogDebug(FormattableString.Invariant($"V{Version} client {remoteEp} {receiverId} receiver mapping found."));

        if (receiver.RemoteEp is null || receiver.RemoteEp.Equals(sender.RemoteEp))
        {
            if (Logger.IsEnabled(LogLevel.Debug))
            {
                Logger.LogDebug(
                    FormattableString.Invariant($"V{Version} client receiver {receiverId} mapping not found or receiver") +
                    FormattableString.Invariant($" {receiver.RemoteEp} equals sender {sender.RemoteEp}."));
            }

            return;
        }

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

        _ = await Client!.SendToAsync(buffer, SocketFlags.None, receiver.RemoteEp, cancellationToken).ConfigureAwait(false);
    }

    private bool HandleSender(uint senderId, IPEndPoint remoteEp, out TunnelClient? sender)
    {
        if (!Mappings!.TryGetValue(senderId, out sender))
        {
            if (Logger.IsEnabled(LogLevel.Debug))
                Logger.LogDebug(FormattableString.Invariant($"V{Version} client {remoteEp} sender mapping {senderId} not found."));

            return false;
        }

        if (sender.RemoteEp is null)
        {
            sender.RemoteEp = remoteEp;

            if (Logger.IsEnabled(LogLevel.Information))
                Logger.LogInfo(FormattableString.Invariant($"New V{Version} client from {remoteEp}, "));

            if (Logger.IsEnabled(LogLevel.Debug))
            {
                Logger.LogDebug(
                    FormattableString.Invariant($"{Mappings.Count} clients from ") +
                    FormattableString.Invariant($"{Mappings.Values.Select(static q => q.RemoteEp?.Address)
                        .Where(static q => q is not null).Distinct().Count()} IPs."));
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

            return false;
        }

        sender.SetLastReceiveTick();

        return true;
    }

    private IResult HandleMaintenanceRequest(HttpRequest request, string? requestMaintenancePassword = null)
    {
        if (!IsNewConnectionAllowed(request.HttpContext.Connection.RemoteIpAddress!))
            return Results.StatusCode((int)HttpStatusCode.TooManyRequests);

        if (ServiceOptions.Value.MaintenancePassword!.Length is not 0
            && ServiceOptions.Value.MaintenancePassword!.Equals(requestMaintenancePassword, StringComparison.Ordinal))
        {
            MaintenanceModeEnabled = true;

            if (Logger.IsEnabled(LogLevel.Warning))
            {
                Logger.LogWarning(FormattableString.Invariant(
                    $"Maintenance mode enabled by {request.HttpContext.Connection.RemoteIpAddress}."));
            }

            return Results.Ok();
        }

        if (Logger.IsEnabled(LogLevel.Warning))
        {
            Logger.LogWarning(FormattableString.Invariant(
                $"Invalid Maintenance mode request by {request.HttpContext.Connection.RemoteIpAddress}."));
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
                    $"New V{Version} lobby from host {host} with {clients} clients."));
            }

            var rand = new Random();

            while (clients > 0)
            {
#pragma warning disable CA5394 // Do not use insecure randomness
                int clientId = rand.Next(0, short.MaxValue);
#pragma warning restore CA5394 // Do not use insecure randomness

                if (!Mappings.TryAdd((uint)clientId, new(ServiceOptions.Value.ClientTimeout)))
                    continue;

                clients--;

                clientIds.Add(clientId);
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

            Logger.LogInfo(FormattableString.Invariant($"New V{Version} lobby from host {host} response: {msg}."));
        }

        return Results.Text(msg);
    }

    private bool IsNewConnectionAllowed(IPAddress address)
    {
        if (ConnectionCounter!.Count >= MaxRequestsGlobal)
            return false;

        int hashCode = address.GetHashCode();

        if (ConnectionCounter.TryGetValue(hashCode, out int count) && count >= ServiceOptions.Value.IpLimit)
            return false;

        ConnectionCounter[hashCode] = ++count;

        return true;
    }
}