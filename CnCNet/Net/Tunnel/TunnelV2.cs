namespace CnCNetServer;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

internal sealed class TunnelV2 : Tunnel
{
    private const int MaxRequestsGlobal = 1000;
    private const int MinGameClients = 2;
    private const int MaxGameClients = 8;

    private SemaphoreSlim? connectionCounterSemaphoreSlim;

    public TunnelV2(ILogger<TunnelV2> logger, IOptions<ServiceOptions> options, IHttpClientFactory httpClientFactory)
        : base(logger, options, httpClientFactory)
    {
    }

    protected override int Version => 2;

    protected override int Port => Options.Value.TunnelV2Port;

    protected override int MinimumPacketSize => 4;

    public Task StartHttpServerAsync(CancellationToken cancellationToken)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();

        builder.Logging.ConfigureLogging(Options.Value.ServerLogLevel, Options.Value.SystemLogLevel);
        builder.WebHost.UseUrls(FormattableString.Invariant($"http://*:{Options.Value.TunnelV2Port}"));

        WebApplication app = builder.Build();

        app.MapGet("/maintenance", HandleMaintenanceRequest);
        app.MapGet("/maintenance/{requestMaintenancePassword}", HandleMaintenanceRequest);
        app.MapGet("/status", HandleStatusRequest);
        app.MapGet("/request", HandleRequestRequest);

        if (Logger.IsEnabled(LogLevel.Information))
        {
            Logger.LogInfo(FormattableString.Invariant(
                $"{DateTimeOffset.Now} V{Version} Tunnel HTTP server started on port {Options.Value.TunnelV2Port}."));
        }

        return app.RunAsync(cancellationToken);
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        connectionCounterSemaphoreSlim = new(1, 1);

        return base.StartAsync(cancellationToken);
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync().ConfigureAwait(false);
        connectionCounterSemaphoreSlim?.Dispose();
    }

    protected override async ValueTask<int> CleanupConnectionsAsync(CancellationToken cancellationToken)
    {
        int clients = await base.CleanupConnectionsAsync(cancellationToken).ConfigureAwait(false);

        await connectionCounterSemaphoreSlim!.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            ConnectionCounter!.Clear();
        }
        finally
        {
            connectionCounterSemaphoreSlim.Release();
        }

        return clients;
    }

    protected override async ValueTask ReceiveAsync(
        ReadOnlyMemory<byte> buffer, IPEndPoint remoteEp, CancellationToken cancellationToken)
    {
        uint senderId = (uint)IPAddress.NetworkToHostOrder(BitConverter.ToInt16(buffer[..2].Span));
        uint receiverId = (uint)IPAddress.NetworkToHostOrder(BitConverter.ToInt16(buffer[2..4].Span));

        if (Logger.IsEnabled(LogLevel.Debug))
        {
            Logger.LogDebug(
                FormattableString.Invariant($"V{Version} client {remoteEp} ({senderId} -> {receiverId}) received") +
                FormattableString.Invariant($" {buffer.Length}: {Convert.ToHexString(buffer.Span)}"));
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

        await MappingsSemaphoreSlim!.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (await HandlePingRequestAsync(senderId, receiverId, buffer, remoteEp, cancellationToken).ConfigureAwait(false))
                return;

            if (Mappings!.TryGetValue(senderId, out TunnelClient? sender))
            {
                if (sender.RemoteEp == null)
                {
                    sender.RemoteEp = new(remoteEp.Address, remoteEp.Port);

                    if (Logger.IsEnabled(LogLevel.Information))
                    {
                        Logger.LogInfo(
                            FormattableString.Invariant($"{DateTimeOffset.Now} New V{Version} client from {remoteEp}, ") +
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
                        Logger.LogDebug(FormattableString.Invariant($"V{Version} client {remoteEp} mapping found."));

                    if (receiver.RemoteEp is not null
                        && !receiver.RemoteEp.Equals(sender.RemoteEp))
                    {
                        if (Logger.IsEnabled(LogLevel.Debug))
                        {
                            Logger.LogDebug(
                                FormattableString.Invariant($"V{Version} client {remoteEp} ({senderId}) sending to ") +
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
                        FormattableString.Invariant($"V{Version} client {remoteEp} mapping not found or receiver") +
                        FormattableString.Invariant($" {receiver?.RemoteEp!} is sender") +
                        FormattableString.Invariant($" {sender.RemoteEp}."));
                }
            }
        }
        finally
        {
            int locks = MappingsSemaphoreSlim.Release();

            if (Logger.IsEnabled(LogLevel.Debug))
            {
                Logger.LogDebug(
                    FormattableString.Invariant($"V{Version} client {remoteEp} message handled,") +
                    FormattableString.Invariant($" pending receive threads: {locks}."));
            }
        }
    }

    private async ValueTask<IResult> HandleMaintenanceRequest(
        HttpRequest request, CancellationToken cancellationToken, string? requestMaintenancePassword = null)
    {
        if (!await IsNewConnectionAllowedAsync(request.HttpContext.Connection.RemoteIpAddress!, cancellationToken)
            .ConfigureAwait(false))
        {
            return Results.StatusCode((int)HttpStatusCode.TooManyRequests);
        }

        if (Options.Value.MaintenancePassword!.Any()
            && Options.Value.MaintenancePassword!.Equals(requestMaintenancePassword, StringComparison.Ordinal))
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

    private async ValueTask<IResult> HandleStatusRequest(HttpRequest request, CancellationToken cancellationToken)
    {
        if (!await IsNewConnectionAllowedAsync(request.HttpContext.Connection.RemoteIpAddress!, cancellationToken)
            .ConfigureAwait(false))
        {
            return Results.StatusCode((int)HttpStatusCode.TooManyRequests);
        }

        string status;

        await MappingsSemaphoreSlim!.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            status = FormattableString.Invariant($"{Options.Value.MaxClients - Mappings!.Count} slots free.") +
                FormattableString.Invariant($"\n{Mappings.Count} slots in use.\n");
        }
        finally
        {
            MappingsSemaphoreSlim.Release();
        }

        return Results.Text(status);
    }

    private async ValueTask<IResult> HandleRequestRequest(
        HttpRequest request, int? clients, CancellationToken cancellationToken)
    {
        if (MaintenanceModeEnabled)
            return Results.StatusCode((int)HttpStatusCode.ServiceUnavailable);

        if (clients is null or < MinGameClients or > MaxGameClients)
            return Results.StatusCode((int)HttpStatusCode.BadRequest);

        var clientIds = new List<int>(clients.Value);

        await MappingsSemaphoreSlim!.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (Mappings!.Count + clients <= Options.Value.MaxClients)
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

                    if (!Mappings.ContainsKey((uint)clientId))
                    {
                        clients--;

                        Mappings.Add((uint)clientId, new());

                        clientIds.Add(clientId);
                    }
                }
            }
        }
        finally
        {
            MappingsSemaphoreSlim.Release();
        }

        if (clientIds.Count < 2)
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

    private async ValueTask<bool> IsNewConnectionAllowedAsync(IPAddress address, CancellationToken cancellationToken)
    {
        await connectionCounterSemaphoreSlim!.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (ConnectionCounter!.Count >= MaxRequestsGlobal)
                return false;

            int ipHash = address.GetHashCode();

            if (ConnectionCounter.TryGetValue(ipHash, out int count) && count >= Options.Value.IpLimit)
                return false;

            ConnectionCounter[ipHash] = ++count;

            return true;
        }
        finally
        {
            connectionCounterSemaphoreSlim.Release();
        }
    }
}