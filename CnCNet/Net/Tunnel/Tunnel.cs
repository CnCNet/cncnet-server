namespace CnCNetServer;

using System.Buffers;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

internal abstract class Tunnel : IAsyncDisposable
{
    protected const int CommandRateLimit = 60; // 1 per X seconds

    private const int MasterAnnounceInterval = 60 * 1000;
    private const int MaxPingsPerIp = 20;
    private const int MaxPingsGlobal = 5000;
    private const int MinMaxClients = 2;
    private const int DefaultMaxClients = 200;
    private const int MinPort = 1024;
    private const int MinIpLimit = 1;

    private readonly string name;
    private readonly System.Timers.Timer heartbeatTimer = new(MasterAnnounceInterval);
    private readonly IHttpClientFactory httpClientFactory;

    private int? port;
    private int? ipLimit;

    protected Tunnel(ILogger logger, Options options, IHttpClientFactory httpClientFactory)
    {
        this.httpClientFactory = httpClientFactory;
        Options = options;
        Logger = logger;
        name = options.Name.Any() ? options.Name.Replace(";", string.Empty) : "Unnamed server";
        MaxClients = options.MaxClients < MinMaxClients ? DefaultMaxClients : options.MaxClients;
        Mappings = new Dictionary<uint, TunnelClient>(MaxClients);
        ConnectionCounter = new Dictionary<int, int>(MaxClients);
    }

    protected abstract int Version { get; }

    protected abstract int DefaultPort { get; }

    protected abstract int Port { get; }

    protected abstract int DefaultIpLimit { get; }

    protected bool MaintenanceModeEnabled { get; set; }

    protected Dictionary<int, int> ConnectionCounter { get; }

    protected Dictionary<uint, TunnelClient> Mappings { get; }

    protected Dictionary<int, int> PingCounter { get; } = new(MaxPingsGlobal);

    protected ILogger Logger { get; }

    protected UdpClient? Client { get; private set; }

    protected int MaxClients { get; }

    protected Options Options { get; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        Client = new UdpClient(GetPort());

        await StartHeartbeatAsync(cancellationToken).ConfigureAwait(false);

        using IMemoryOwner<byte> memoryOwner = MemoryPool<byte>.Shared.Rent(1024);
        Memory<byte> buffer = memoryOwner.Memory[..1024];
        var remoteEp = new IPEndPoint(IPAddress.Any, 0);

        Logger.LogInfo(
            FormattableString.Invariant($"{DateTimeOffset.Now} V{Version} Tunnel UDP server started on port {GetPort()}."));

        while (!cancellationToken.IsCancellationRequested)
        {
            SocketReceiveFromResult socketReceiveFromResult =
                await Client.Client.ReceiveFromAsync(buffer, SocketFlags.None, remoteEp, cancellationToken)
                    .ConfigureAwait(false);

            if (socketReceiveFromResult.ReceivedBytes >= 8)
            {
                await ReceiveAsync(
                    buffer[..socketReceiveFromResult.ReceivedBytes],
                    (IPEndPoint)socketReceiveFromResult.RemoteEndPoint,
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public virtual ValueTask DisposeAsync()
    {
        Client?.Dispose();
        heartbeatTimer.Dispose();

        return ValueTask.CompletedTask;
    }

    protected abstract Task<int> CleanupConnectionsAsync(CancellationToken cancellationToken);

    protected abstract Task ReceiveAsync(
        ReadOnlyMemory<byte> buffer, IPEndPoint remoteEp, CancellationToken cancellationToken);

    protected bool IsPingLimitReached(IPAddress address)
    {
        if (PingCounter.Count >= MaxPingsGlobal)
            return true;

        int ipHash = address.GetHashCode();

        if (PingCounter.TryGetValue(ipHash, out int count) && count >= MaxPingsPerIp)
            return true;

        PingCounter[ipHash] = ++count;

        return false;
    }

    protected int GetIpLimit()
        => ipLimit ??= Options.IpLimit < MinIpLimit ? DefaultIpLimit : Options.IpLimit;

    private async Task SendMasterServerHeartbeatAsync(int clients, CancellationToken cancellationToken)
    {
        string path = FormattableString.Invariant($"?version={Version}&name={Uri.EscapeDataString(name)}&port={GetPort()}") +
            FormattableString.Invariant($"&clients={clients}&maxclients={MaxClients}") +
            FormattableString.Invariant($"&masterpw={Uri.EscapeDataString(Options.MasterPassword)}") +
            FormattableString.Invariant($"&maintenance={(MaintenanceModeEnabled ? 1 : 0)}");
        HttpResponseMessage? httpResponseMessage = null;

        try
        {
            httpResponseMessage = await httpClientFactory.CreateClient(nameof(Tunnel))
                .GetAsync(path, cancellationToken).ConfigureAwait(false);

            string responseContent = await httpResponseMessage.EnsureSuccessStatusCode().Content
                .ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!"OK".Equals(responseContent, StringComparison.OrdinalIgnoreCase))
                throw new MasterServerException(responseContent);

            Logger.LogInfo(FormattableString.Invariant($"{DateTimeOffset.Now} Tunnel V{Version} Heartbeat sent."));
        }
        catch (HttpRequestException ex)
        {
            await Logger.LogExceptionDetailsAsync(ex, httpResponseMessage).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await Logger.LogExceptionDetailsAsync(ex).ConfigureAwait(false);
        }
    }

    private Task StartHeartbeatAsync(CancellationToken cancellationToken)
    {
        heartbeatTimer.Elapsed += (_, _) => SendHeartbeatAsync(cancellationToken);
        heartbeatTimer.Enabled = true;

        return SendHeartbeatAsync(cancellationToken);
    }

    private async Task SendHeartbeatAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (cancellationToken.IsCancellationRequested)
                heartbeatTimer.Enabled = false;

            int clients = await CleanupConnectionsAsync(cancellationToken).ConfigureAwait(false);

            if (!Options.NoMasterAnnounce)
                await SendMasterServerHeartbeatAsync(clients, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await Logger.LogExceptionDetailsAsync(ex).ConfigureAwait(false);
        }
    }

    private int GetPort()
    {
        return port ??= Port <= MinPort ? DefaultPort : Port;
    }
}