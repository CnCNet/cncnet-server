namespace CnCNetServer;

using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

internal abstract class Tunnel : IDisposable
{
    protected const int CommandRateLimit = 60; // 1 per X seconds

    private const int MasterAnnounceInterval = 60 * 1000;
    private const int MaxPingsPerIp = 20;
    private const int MaxPingsGlobal = 5000;

    private readonly string name;
    private readonly string masterPassword;
    private readonly System.Timers.Timer heartbeatTimer = new(MasterAnnounceInterval);
    private readonly IHttpClientFactory httpClientFactory;
    private readonly int port;
    private readonly bool noMasterAnnounce;

    protected Tunnel(int version, int port, int ipLimit, ILogger<TunnelV3> logger, Options options, IHttpClientFactory httpClientFactory)
    {
        this.httpClientFactory = httpClientFactory;
        this.port = port;
        Version = version;
        IpLimit = ipLimit;
        Logger = logger;
        name = options.Name.Length == 0 ? "Unnamed server" : options.Name.Replace(";", string.Empty);
        MaxClients = options.MaxClients < 2 ? 200 : options.MaxClients;
        noMasterAnnounce = options.NoMasterAnnounce;
        masterPassword = options.MasterPassword;
        MaintenancePassword = options.MaintenancePassword;
        Mappings = new Dictionary<uint, TunnelClient>(MaxClients);
        ConnectionCounter = new Dictionary<int, int>(MaxClients);
        Client = new UdpClient(port);
    }

    protected bool MaintenanceModeEnabled { get; set; }

    protected Dictionary<int, int> ConnectionCounter { get; }

    protected Dictionary<uint, TunnelClient> Mappings { get; }

    protected Dictionary<int, int> PingCounter { get; } = new(MaxPingsGlobal);

    protected string MaintenancePassword { get; }

    protected int Version { get; }

    protected ILogger Logger { get; }

    protected UdpClient Client { get; }

    protected int MaxClients { get; }

    protected int IpLimit { get; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await StartHeartbeatAsync(cancellationToken).ConfigureAwait(false);

        byte[] buffer = new byte[1024];
        var remoteEp = new IPEndPoint(IPAddress.Any, 0);

        while (!cancellationToken.IsCancellationRequested)
        {
            SocketReceiveFromResult socketReceiveFromResult = await Client.Client.ReceiveFromAsync(buffer, SocketFlags.None, remoteEp, cancellationToken).ConfigureAwait(false);

            if (socketReceiveFromResult.ReceivedBytes >= 8)
                await ReceiveAsync(buffer, socketReceiveFromResult.ReceivedBytes, remoteEp, cancellationToken).ConfigureAwait(false);
        }
    }

    public virtual void Dispose()
    {
        Client.Dispose();
        heartbeatTimer.Dispose();
    }

    protected abstract Task<int> CleanupConnectionsAsync(CancellationToken cancellationToken);

    protected abstract Task ReceiveAsync(byte[] buffer, int size, IPEndPoint remoteEp, CancellationToken cancellationToken);

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

    private async Task SendMasterServerHeartbeatAsync(int clients, CancellationToken cancellationToken)
    {
        string path = FormattableString.Invariant($"?version={Version}&name={Uri.EscapeDataString(name)}&port={port}&clients={clients}&maxclients={MaxClients}&masterpw={Uri.EscapeDataString(masterPassword)}&maintenance={(MaintenanceModeEnabled ? 1 : 0)}");
        HttpResponseMessage? httpResponseMessage = null;

        try
        {
            httpResponseMessage = await httpClientFactory.CreateClient(nameof(Tunnel)).GetAsync(path, cancellationToken).ConfigureAwait(false);

            _ = httpResponseMessage.EnsureSuccessStatusCode();

            string responseContent = await httpResponseMessage.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!"OK".Equals(responseContent, StringComparison.OrdinalIgnoreCase))
                throw new MasterServerException(responseContent);

            Logger.LogMessage(FormattableString.Invariant($"{DateTime.UtcNow} Tunnel V{Version} Heartbeat sent."));
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
        heartbeatTimer.Elapsed += (_, _) => _ = SendHeartbeatAsync(cancellationToken);
        heartbeatTimer.Enabled = true;

        return SendHeartbeatAsync(cancellationToken);
    }

    private async Task SendHeartbeatAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            heartbeatTimer.Enabled = false;

        int clients = await CleanupConnectionsAsync(cancellationToken).ConfigureAwait(false);

        if (!noMasterAnnounce)
            await SendMasterServerHeartbeatAsync(clients, cancellationToken).ConfigureAwait(false);
    }
}