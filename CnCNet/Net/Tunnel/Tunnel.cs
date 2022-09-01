namespace CnCNetServer;

using System.Buffers;

internal abstract class Tunnel : IAsyncDisposable
{
    protected const int CommandRateLimit = 60; // 1 per X seconds

    private const int MasterAnnounceInterval = 60 * 1000;
    private const int MaxPingsPerIp = 20;
    private const int MaxPingsGlobal = 5000;
    private const int PingRequestPacketSize = 50;
    private const int PingResponsePacketSize = 12;
    private const int MaximumPacketSize = 128;

    protected SemaphoreSlim? MappingsSemaphoreSlim;

    private readonly IHttpClientFactory httpClientFactory;

    private System.Timers.Timer? heartbeatTimer;
    private Dictionary<int, int>? pingCounter;

    protected Tunnel(ILogger logger, IOptions<ServiceOptions> options, IHttpClientFactory httpClientFactory)
    {
        this.httpClientFactory = httpClientFactory;
        Options = options;
        Logger = logger;
    }

    protected abstract int Version { get; }

    protected abstract int Port { get; }

    protected abstract int MinimumPacketSize { get; }

    protected ILogger Logger { get; }

    protected IOptions<ServiceOptions> Options { get; }

    protected bool MaintenanceModeEnabled { get; set; }

    protected Dictionary<int, int>? ConnectionCounter { get; private set; }

    protected Dictionary<uint, TunnelClient>? Mappings { get; private set; }

    protected Socket? Client { get; private set; }

    public virtual async Task StartAsync(CancellationToken cancellationToken)
    {
        Mappings = new(Options.Value.MaxClients);
        ConnectionCounter = new(Options.Value.MaxClients);
        MappingsSemaphoreSlim = new(1, 1);
        Client = new(SocketType.Dgram, ProtocolType.Udp);
        heartbeatTimer = new(MasterAnnounceInterval);
        pingCounter = new(MaxPingsGlobal);

        await StartHeartbeatAsync(cancellationToken).ConfigureAwait(false);

        using IMemoryOwner<byte> memoryOwner = MemoryPool<byte>.Shared.Rent(MaximumPacketSize);
        Memory<byte> buffer = memoryOwner.Memory[..MaximumPacketSize];
        var remoteEp = new IPEndPoint(IPAddress.Any, 0);

        Client.Bind(new IPEndPoint(IPAddress.IPv6Any, Port));

        if (Logger.IsEnabled(LogLevel.Information))
        {
            Logger.LogInfo(FormattableString.Invariant(
                $"{DateTimeOffset.Now} V{Version} Tunnel UDP server started on port {Port}."));
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            SocketReceiveFromResult socketReceiveFromResult =
                await Client.ReceiveFromAsync(buffer, SocketFlags.None, remoteEp, cancellationToken)
                    .ConfigureAwait(false);

            if (socketReceiveFromResult.ReceivedBytes < MinimumPacketSize
                || socketReceiveFromResult.ReceivedBytes > MaximumPacketSize)
            {
                if (Logger.IsEnabled(LogLevel.Debug))
                {
                    Logger.LogDebug(FormattableString.Invariant($"{DateTimeOffset.Now} V{Version} Tunnel invalid UDP ") +
                        FormattableString.Invariant($"packet size {socketReceiveFromResult.ReceivedBytes}."));
                }
            }
            else
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
        heartbeatTimer?.Dispose();
        MappingsSemaphoreSlim?.Dispose();

        return ValueTask.CompletedTask;
    }

    protected virtual async ValueTask<int> CleanupConnectionsAsync(CancellationToken cancellationToken)
    {
        int clients;

        await MappingsSemaphoreSlim!.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            foreach (KeyValuePair<uint, TunnelClient> mapping in Mappings!.Where(x => x.Value.TimedOut).ToList())
            {
                CleanupConnection(mapping.Value);

                Mappings!.Remove(mapping.Key);

                if (Logger.IsEnabled(LogLevel.Information))
                {
                    Logger.LogInfo(
                        FormattableString.Invariant($"{DateTimeOffset.Now} Removed V{Version} client from ") +
                        FormattableString.Invariant($"{mapping.Value.RemoteEp?.ToString() ?? "(not connected)"}, ") +
                        FormattableString.Invariant($"{Mappings.Count} clients from {Mappings.Values
                            .Select(q => q.RemoteEp?.Address).Where(q => q is not null).Distinct().Count()} IPs."));
                }
            }

            clients = Mappings!.Count;

            pingCounter!.Clear();
        }
        finally
        {
            MappingsSemaphoreSlim.Release();
        }

        return clients;
    }

    protected virtual void CleanupConnection(TunnelClient tunnelClient)
    {
    }

    protected abstract ValueTask ReceiveAsync(
        ReadOnlyMemory<byte> buffer, IPEndPoint remoteEp, CancellationToken cancellationToken);

    protected async ValueTask<bool> HandlePingRequestAsync(
        uint senderId, uint receiverId, ReadOnlyMemory<byte> buffer, IPEndPoint remoteEp, CancellationToken cancellationToken)
    {
        if (senderId != 0 || receiverId != 0)
            return false;

        if (buffer.Length == PingRequestPacketSize)
        {
            if (!IsPingLimitReached(remoteEp.Address))
            {
                if (Logger.IsEnabled(LogLevel.Debug))
                {
                    Logger.LogDebug(
                        FormattableString.Invariant($"V{Version} client {remoteEp} replying to ping ") +
                        FormattableString.Invariant($"({pingCounter!.Count}/{MaxPingsGlobal}):") +
                        FormattableString.Invariant($" {Convert.ToHexString(buffer.Span[..PingResponsePacketSize])}."));
                }

                await Client!.SendToAsync(
                        buffer[..PingResponsePacketSize], SocketFlags.None, remoteEp, cancellationToken)
                    .ConfigureAwait(false);

                return true;
            }

            if (Logger.IsEnabled(LogLevel.Debug))
            {
                Logger.LogDebug(FormattableString.Invariant($"V{Version} client {remoteEp} ping request ignored:") +
                                FormattableString.Invariant($" ping limit reached."));
            }

            if (Logger.IsEnabled(LogLevel.Warning))
                Logger.LogWarning("Ping limit reached.");
        }
        else if (Logger.IsEnabled(LogLevel.Debug))
        {
            Logger.LogDebug(FormattableString.Invariant($"V{Version} client {remoteEp.Address} ping request ignored:") +
                            FormattableString.Invariant($" invalid packet size {buffer.Length}."));
        }

        return false;
    }

    private bool IsPingLimitReached(IPAddress address)
    {
        if (pingCounter!.Count >= MaxPingsGlobal)
            return true;

        int ipHash = address.GetHashCode();

        if (pingCounter.TryGetValue(ipHash, out int count) && count >= MaxPingsPerIp)
            return true;

        pingCounter[ipHash] = ++count;

        return false;
    }

    private async ValueTask SendMasterServerHeartbeatAsync(int clients, CancellationToken cancellationToken)
    {
        string path = FormattableString.Invariant($"?version={Version}&name={Uri.EscapeDataString(Options.Value.Name!)}") +
            FormattableString.Invariant($"&port={Port}&clients={clients}&maxclients={Options.Value.MaxClients}") +
            FormattableString.Invariant($"&masterpw={Uri.EscapeDataString(Options.Value.MasterPassword ?? string.Empty)}") +
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

            if (Logger.IsEnabled(LogLevel.Information))
                Logger.LogInfo(FormattableString.Invariant($"{DateTimeOffset.Now} V{Version} Tunnel Heartbeat sent."));
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
        heartbeatTimer!.Elapsed += (_, _) => SendHeartbeatAsync(cancellationToken);
        heartbeatTimer.Enabled = true;

        return SendHeartbeatAsync(cancellationToken);
    }

    private async Task SendHeartbeatAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (cancellationToken.IsCancellationRequested)
            {
                heartbeatTimer!.Enabled = false;

                return;
            }

            int clients = await CleanupConnectionsAsync(cancellationToken).ConfigureAwait(false);

            if (!Options.Value.NoMasterAnnounce)
                await SendMasterServerHeartbeatAsync(clients, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await Logger.LogExceptionDetailsAsync(ex).ConfigureAwait(false);
        }
    }
}