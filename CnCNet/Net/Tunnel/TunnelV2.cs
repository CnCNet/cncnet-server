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

    private readonly SemaphoreSlim mappingsSemaphoreSlim = new(1, 1);
    private readonly SemaphoreSlim connectionCounterSemaphoreSlim = new(1, 1);
    private readonly WebApplication app;

    public TunnelV2(ILogger<TunnelV2> logger, Options options, IHttpClientFactory httpClientFactory)
        : base(2, options.TunnelV2Port <= 1024 ? 50000 : options.TunnelV2Port,
            options.IpLimit < 1 ? 4 : options.IpLimit, logger, options, httpClientFactory)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Logging.ConfigureLogging(options);

        builder.WebHost.UseUrls(FormattableString.Invariant($"http://*:{options.TunnelV2Port}"));
        app = builder.Build();
    }

    public Task StartHttpServerAsync()
    {
        app.MapGet("/maintenance", HandleMaintenanceRequest);
        app.MapGet("/maintenance/{requestMaintenancePassword}", HandleMaintenanceRequest);
        app.MapGet("/status", HandleStatusRequest);
        app.MapGet("/request", HandleRequestRequest);

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
                Logger.LogMessage(
                    FormattableString.Invariant($"Removed V{Version} client from {mapping.Value.RemoteEp}") +
                    FormattableString.Invariant($", {Mappings.Count} clients from ") +
                    FormattableString.Invariant($"{Mappings.Values.Select(q => q.RemoteEp.Address).Distinct().Count()} IPs."));
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
        byte[] buffer, int size, IPEndPoint remoteEp, CancellationToken cancellationToken)
    {
        uint senderId = (uint)IPAddress.NetworkToHostOrder(BitConverter.ToInt16(buffer, 0));
        uint receiverId = (uint)IPAddress.NetworkToHostOrder(BitConverter.ToInt16(buffer, 2));

        if ((senderId == receiverId && senderId != 0) || remoteEp.Address.Equals(IPAddress.Loopback)
            || remoteEp.Address.Equals(IPAddress.Any) || remoteEp.Address.Equals(IPAddress.Broadcast) || remoteEp.Port == 0)
        {
            return;
        }

        await mappingsSemaphoreSlim.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (senderId == 0 && receiverId == 0)
            {
                if (size == 50 && !IsPingLimitReached(remoteEp.Address))
                {
                    await Client!.Client.SendToAsync(buffer.AsMemory()[..12], SocketFlags.None, remoteEp, cancellationToken)
                        .ConfigureAwait(false);
                }

                return;
            }

            if (Mappings.TryGetValue(senderId, out TunnelClient? sender))
            {
                if (!remoteEp.Equals(sender.RemoteEp))
                    return;

                sender.SetLastReceiveTick();

                if (Mappings.TryGetValue(receiverId, out TunnelClient? receiver)
                    && !receiver.RemoteEp.Equals(sender.RemoteEp))
                {
                    await Client!.Client.SendAsync(buffer.AsMemory()[..size], SocketFlags.None, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }
        finally
        {
            mappingsSemaphoreSlim.Release();
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

        if (MaintenancePassword.Length > 0
            && MaintenancePassword.Equals(requestMaintenancePassword, StringComparison.Ordinal))
        {
            MaintenanceModeEnabled = true;

            return Results.Ok();
        }

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

        return Results.Ok(status);
    }

    private async Task<IResult> HandleRequestRequest(
        HttpRequest request, int? clients, CancellationToken cancellationToken)
    {
        if (MaintenanceModeEnabled)
            return Results.StatusCode((int)HttpStatusCode.ServiceUnavailable);

        if (clients is null or < 2 or > 8)
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
                    int clientId = rand.Next(0, int.MaxValue);

                    if (!Mappings.ContainsKey((uint)clientId))
                    {
                        clients--;

                        var tunnelClient = new TunnelClient(new IPEndPoint(
                            request.HttpContext.Connection.RemoteIpAddress!,
                            request.HttpContext.Connection.RemotePort));

                        tunnelClient.SetLastReceiveTick();

                        Mappings.Add((uint)clientId, tunnelClient);

                        clientIds.Add(clientId);
                        Logger.LogMessage(
                            FormattableString.Invariant($"New V{Version} client from {tunnelClient.RemoteEp}, ") +
                            FormattableString.Invariant($"{Mappings.Count} clients from ") +
                            FormattableString.Invariant($"{Mappings.Values.Select(q => q.RemoteEp.Address).Distinct().Count()} IPs."));
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

        return Results.Ok(msg);
    }

    private async Task<bool> IsNewConnectionAllowedAsync(IPAddress address, CancellationToken cancellationToken)
    {
        await connectionCounterSemaphoreSlim.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (ConnectionCounter.Count >= MaxRequestsGlobal)
                return false;

            int ipHash = address.GetHashCode();

            if (ConnectionCounter.TryGetValue(ipHash, out int count) && count >= IpLimit)
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