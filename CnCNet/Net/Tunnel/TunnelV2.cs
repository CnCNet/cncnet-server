namespace CnCNetServer;

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

internal sealed class TunnelV2 : Tunnel
{
    private const int MaxRequestsGlobal = 1000;
    private const int MinGameClients = 2;
    private const int MaxGameClients = 8;

    private readonly SemaphoreSlim mappingsSemaphoreSlim = new(1, 1);
    private readonly SemaphoreSlim connectionCounterSemaphoreSlim = new(1, 1);
    private readonly WebApplication app;

    public TunnelV2(ILogger<TunnelV2> logger, Options options, IHttpClientFactory httpClientFactory)
        : base(logger, options, httpClientFactory)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();

        builder.Logging.ConfigureLogging(options);
        builder.WebHost.UseUrls(FormattableString.Invariant($"http://*:{options.TunnelV2Port}"));

        app = builder.Build();
    }

    protected override int Version => 2;

    protected override int DefaultPort => 50000;

    protected override int DefaultIpLimit => 4;

    protected override int Port => Options.TunnelV2Port;

    public Task StartHttpServerAsync()
    {
        app.MapGet("/maintenance", HandleMaintenanceRequest);
        app.MapGet("/maintenance/{requestMaintenancePassword}", HandleMaintenanceRequest);
        app.MapGet("/status", HandleStatusRequest);
        app.MapGet("/request", HandleRequestRequest);

        Logger.LogInfo(FormattableString.Invariant(
            $"{DateTimeOffset.Now} V{Version} Tunnel HTTP server started on port {Options.TunnelV2Port}."));

        return app.RunAsync();
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync().ConfigureAwait(false);
        mappingsSemaphoreSlim.Dispose();
        connectionCounterSemaphoreSlim.Dispose();

        await app.StopAsync(CancellationToken.None).ConfigureAwait(false);
    }

    protected override async Task<int> CleanupConnectionsAsync(CancellationToken cancellationToken)
    {
        int clients;

        await mappingsSemaphoreSlim.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            foreach (KeyValuePair<uint, TunnelClient> mapping in Mappings.Where(x => x.Value.TimedOut).ToList())
            {
                Mappings.Remove(mapping.Key);

                if (Logger.IsEnabled(LogLevel.Information))
                {
                    Logger.LogInfo(
                        FormattableString.Invariant($"{DateTimeOffset.Now} Removed V{Version} client from ") +
                        FormattableString.Invariant($"{mapping.Value.RemoteEp}, {Mappings.Count} clients from ") +
                        FormattableString.Invariant($"{Mappings.Values.Select(q => q.RemoteEp?.Address)
                            .Where(q => q is not null).Distinct().Count()} IPs."));
                }
            }

            clients = Mappings.Count;

            PingCounter.Clear();
        }
        finally
        {
            mappingsSemaphoreSlim.Release();
        }

        await connectionCounterSemaphoreSlim.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            ConnectionCounter.Clear();
        }
        finally
        {
            connectionCounterSemaphoreSlim.Release();
        }

        return clients;
    }

    protected override async Task ReceiveAsync(
        ReadOnlyMemory<byte> buffer, IPEndPoint remoteEp, CancellationToken cancellationToken)
    {
        uint senderId = (uint)IPAddress.NetworkToHostOrder(BitConverter.ToInt16(buffer[..2].Span));
        uint receiverId = (uint)IPAddress.NetworkToHostOrder(BitConverter.ToInt16(buffer[2..4].Span));

        if (Logger.IsEnabled(LogLevel.Debug))
        {
            Logger.LogDebug(
                FormattableString.Invariant($"V{Version} client {remoteEp.Address} ({senderId} -> {receiverId}) received") +
                FormattableString.Invariant($" {buffer.Length}: {Convert.ToHexString(buffer.Span)}"));
        }

        if ((senderId == receiverId && senderId != 0) || remoteEp.Address.Equals(IPAddress.Loopback)
             || remoteEp.Address.Equals(IPAddress.Any) || remoteEp.Address.Equals(IPAddress.Broadcast) || remoteEp.Port == 0)
        {
            if (Logger.IsEnabled(LogLevel.Debug))
            {
                Logger.LogDebug(
                    FormattableString.Invariant($"V{Version} client {remoteEp.Address}:{remoteEp.Port} invalid endpoint."));
            }

            return;
        }

        await mappingsSemaphoreSlim.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (senderId == 0 && receiverId == 0)
            {
                if (buffer.Length == 50 && !IsPingLimitReached(remoteEp.Address))
                {
                    if (Logger.IsEnabled(LogLevel.Debug))
                    {
                        Logger.LogDebug(
                            FormattableString.Invariant($"V{Version} client {remoteEp.Address} (new) sending:") +
                            FormattableString.Invariant($" {Convert.ToHexString(buffer.Span[..12])}."));
                    }

                    await Client!.Client.SendToAsync(buffer[..12], SocketFlags.None, remoteEp, cancellationToken)
                        .ConfigureAwait(false);
                }
                else if (Logger.IsEnabled(LogLevel.Debug))
                {
                    Logger.LogDebug(FormattableString.Invariant($"V{Version} client {remoteEp.Address} (new):") +
                        FormattableString.Invariant($" ping limit reached or size {buffer.Length} not 50."));
                }

                return;
            }

            if (Mappings.TryGetValue(senderId, out TunnelClient? sender))
            {
                if (sender.RemoteEp == null)
                {
                    sender.RemoteEp = new IPEndPoint(remoteEp.Address, remoteEp.Port);

                    if (Logger.IsEnabled(LogLevel.Information))
                    {
                        Logger.LogInfo(
                            FormattableString.Invariant($"{DateTimeOffset.Now} New V{Version} client from {remoteEp}, ") +
                            FormattableString.Invariant($"{Mappings.Count} clients from ") +
                            FormattableString.Invariant($"{Mappings.Values.Select(q => q.RemoteEp?.Address)
                                .Where(q => q is not null).Distinct().Count()} IPs."));
                    }
                }
                else if (!remoteEp.Address.MapToIPv4().Equals(sender.RemoteEp.Address.MapToIPv4()))
                {
                    if (Logger.IsEnabled(LogLevel.Debug))
                    {
                        Logger.LogDebug(
                            FormattableString.Invariant($"V{Version} client {remoteEp.Address.MapToIPv4()}:{remoteEp.Port}") +
                            FormattableString.Invariant($" did not match {sender.RemoteEp.Address.MapToIPv4()}:{sender.RemoteEp.Port}."));
                    }

                    return;
                }

                sender.SetLastReceiveTick();

                if (Mappings.TryGetValue(receiverId, out TunnelClient? receiver))
                {
                    if (Logger.IsEnabled(LogLevel.Debug))
                        Logger.LogDebug(FormattableString.Invariant($"V{Version} client {remoteEp.Address} mapping found."));

                    if (receiver.RemoteEp is not null
                        && !receiver.RemoteEp.Address.MapToIPv4().Equals(sender.RemoteEp.Address.MapToIPv4()))
                    {
                        if (Logger.IsEnabled(LogLevel.Debug))
                        {
                            Logger.LogDebug(
                                FormattableString.Invariant($"V{Version} client {remoteEp.Address} (existing) sending:") +
                                FormattableString.Invariant($" {Convert.ToHexString(buffer.Span)}."));
                        }

                        await Client!.Client.SendToAsync(buffer, SocketFlags.None, receiver.RemoteEp, cancellationToken)
                            .ConfigureAwait(false);
                    }
                }
                else if (Logger.IsEnabled(LogLevel.Debug))
                {
                    Logger.LogDebug(
                        FormattableString.Invariant($"V{Version} client {remoteEp.Address} mapping not found or receiver") +
                        FormattableString.Invariant($" {receiver?.RemoteEp!.Address.MapToIPv4()} is sender") +
                        FormattableString.Invariant($" {sender.RemoteEp.Address.MapToIPv4()}."));
                }
            }
        }
        finally
        {
            int locks = mappingsSemaphoreSlim.Release();

            if (Logger.IsEnabled(LogLevel.Debug))
            {
                Logger.LogDebug(
                    FormattableString.Invariant($"V{Version} client {remoteEp.Address} message handled,") +
                    FormattableString.Invariant($" pending receive threads: {locks}."));
            }
        }
    }

    private async Task<IResult> HandleMaintenanceRequest(
        HttpRequest request, CancellationToken cancellationToken, string? requestMaintenancePassword = null)
    {
        if (!await IsNewConnectionAllowedAsync(request.HttpContext.Connection.RemoteIpAddress!, cancellationToken)
            .ConfigureAwait(false))
        {
            return Results.StatusCode((int)HttpStatusCode.TooManyRequests);
        }

        if (Options.MaintenancePassword.Any()
            && Options.MaintenancePassword.Equals(requestMaintenancePassword, StringComparison.Ordinal))
        {
            MaintenanceModeEnabled = true;

            Logger.LogWarning(FormattableString.Invariant(
                $"{DateTimeOffset.Now} Maintenance mode enabled by {request.HttpContext.Connection.RemoteIpAddress}."));

            return Results.Ok();
        }

        Logger.LogWarning(FormattableString.Invariant(
            $"{DateTimeOffset.Now} Invalid Maintenance mode request by {request.HttpContext.Connection.RemoteIpAddress}."));

        return Results.Unauthorized();
    }

    private async Task<IResult> HandleStatusRequest(HttpRequest request, CancellationToken cancellationToken)
    {
        if (!await IsNewConnectionAllowedAsync(request.HttpContext.Connection.RemoteIpAddress!, cancellationToken)
            .ConfigureAwait(false))
        {
            return Results.StatusCode((int)HttpStatusCode.TooManyRequests);
        }

        string status;

        await mappingsSemaphoreSlim.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            status = FormattableString.Invariant($"{MaxClients - Mappings.Count} slots free.") +
                FormattableString.Invariant($"\n{Mappings.Count} slots in use.\n");
        }
        finally
        {
            mappingsSemaphoreSlim.Release();
        }

        return Results.Text(status);
    }

    private async Task<IResult> HandleRequestRequest(
        HttpRequest request, int? clients, CancellationToken cancellationToken)
    {
        if (MaintenanceModeEnabled)
            return Results.StatusCode((int)HttpStatusCode.ServiceUnavailable);

        if (clients is null or < MinGameClients or > MaxGameClients)
            return Results.StatusCode((int)HttpStatusCode.BadRequest);

        var clientIds = new List<int>(clients.Value);

        await mappingsSemaphoreSlim.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (Mappings.Count + clients <= MaxClients)
            {
                var rand = new Random();

                while (clients > 0)
                {
                    int clientId = rand.Next(0, short.MaxValue);

                    if (!Mappings.ContainsKey((uint)clientId))
                    {
                        clients--;

                        Mappings.Add((uint)clientId, new TunnelClient());

                        clientIds.Add(clientId);

                        if (Logger.IsEnabled(LogLevel.Information))
                        {
                            var host = new IPEndPoint(
                                request.HttpContext.Connection.RemoteIpAddress!,
                                request.HttpContext.Connection.RemotePort);

                            Logger.LogInfo(
                                FormattableString.Invariant($"{DateTimeOffset.Now} New V{Version} client from host {host}, ") +
                                FormattableString.Invariant($"{Mappings.Count} clients from ") +
                                FormattableString.Invariant($"{Mappings.Values.Select(q => q.RemoteEp?.Address)
                                    .Where(q => q is not null).Distinct().Count()} IPs."));
                        }
                    }
                }
            }
        }
        finally
        {
            mappingsSemaphoreSlim.Release();
        }

        if (clientIds.Count < 2)
            return Results.StatusCode((int)HttpStatusCode.ServiceUnavailable);

        string msg = FormattableString.Invariant($"[{string.Join(",", clientIds)}]");

        return Results.Text(msg);
    }

    private async Task<bool> IsNewConnectionAllowedAsync(IPAddress address, CancellationToken cancellationToken)
    {
        await connectionCounterSemaphoreSlim.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (ConnectionCounter.Count >= MaxRequestsGlobal)
                return false;

            int ipHash = address.GetHashCode();

            if (ConnectionCounter.TryGetValue(ipHash, out int count) && count >= GetIpLimit())
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