namespace CnCNetServer;

using System.Buffers;
using System.Collections.Concurrent;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

internal abstract class Tunnel : IAsyncDisposable
{
    private const int PingRequestPacketSize = 50;
    private const int PingResponsePacketSize = 12;

    private readonly IHttpClientFactory httpClientFactory;
    private readonly ConcurrentDictionary<int, int>? pingCounter = new();

    private PeriodicTimer? heartbeatTimer;
    private IPAddress? secondaryIpAddress;

    protected Tunnel(ILogger logger, IOptions<ServiceOptions> serviceOptions, IHttpClientFactory httpClientFactory)
    {
        this.httpClientFactory = httpClientFactory;
        ServiceOptions = serviceOptions;
        Logger = logger;
    }

    protected abstract int Version { get; }

    protected abstract int Port { get; }

    protected abstract int MinimumPacketSize { get; }

    protected ILogger Logger { get; }

    protected IOptions<ServiceOptions> ServiceOptions { get; }

    protected bool MaintenanceModeEnabled { get; set; }

    protected ConcurrentDictionary<int, int>? ConnectionCounter { get; } = new();

    protected ConcurrentDictionary<uint, TunnelClient>? Mappings { get; } = new();

    protected Socket? Client { get; private set; }

    public virtual async ValueTask StartAsync(CancellationToken cancellationToken)
    {
        Client = new(SocketType.Dgram, ProtocolType.Udp);
        heartbeatTimer = new(TimeSpan.FromSeconds(ServiceOptions.Value.MasterAnnounceInterval));
#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
#pragma warning disable IDE0058 // Expression value is never used
        StartHeartbeatAsync(cancellationToken);
#pragma warning restore IDE0058 // Expression value is never used
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
        var remoteEp = new IPEndPoint(IPAddress.Any, 0);

        Client.Bind(new IPEndPoint(IPAddress.IPv6Any, Port));

        if (Logger.IsEnabled(LogLevel.Information))
            Logger.LogInfo(FormattableString.Invariant($"V{Version} Tunnel UDP server started on port {Port}."));

        while (!cancellationToken.IsCancellationRequested)
        {
            using IMemoryOwner<byte> memoryOwner = MemoryPool<byte>.Shared.Rent(ServiceOptions.Value.MaxPacketSize);
            Memory<byte> buffer = memoryOwner.Memory[..ServiceOptions.Value.MaxPacketSize];
            SocketReceiveFromResult socketReceiveFromResult;

            try
            {
                socketReceiveFromResult =
                    await Client.ReceiveFromAsync(buffer, SocketFlags.None, remoteEp, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (SocketException ex)
            {
                await Logger.LogExceptionDetailsAsync(ex).ConfigureAwait(false);
                continue;
            }

#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
#pragma warning disable IDE0058 // Expression value is never used
            DoReceiveAsync(
                buffer[..socketReceiveFromResult.ReceivedBytes],
                (IPEndPoint)socketReceiveFromResult.RemoteEndPoint,
                cancellationToken).ConfigureAwait(false);
#pragma warning restore IDE0058 // Expression value is never used
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
        }
    }

    public virtual ValueTask DisposeAsync()
    {
        Client?.Dispose();
        heartbeatTimer?.Dispose();

        return ValueTask.CompletedTask;
    }

    protected virtual void CleanupConnection(TunnelClient tunnelClient)
    {
    }

    protected abstract (uint SenderId, uint ReceiverId) GetClientIds(ReadOnlyMemory<byte> buffer);

    protected abstract bool ValidateClientIds(uint senderId, uint receiverId, ReadOnlyMemory<byte> buffer, IPEndPoint remoteEp);

    protected abstract ValueTask HandlePacketAsync(uint senderId, uint receiverId, ReadOnlyMemory<byte> buffer, IPEndPoint remoteEp, CancellationToken cancellationToken);

    private static IPAddress? GetPublicIpV6Address()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return GetIpV6Addresses().FirstOrDefault();

        var localIpV6Addresses = GetWindowsIpV6Addresses().ToList();
        (IPAddress IpAddress, PrefixOrigin PrefixOrigin, SuffixOrigin SuffixOrigin) publicIpV6Address = localIpV6Addresses.FirstOrDefault(
            static q => q.PrefixOrigin is PrefixOrigin.RouterAdvertisement && q.SuffixOrigin is SuffixOrigin.LinkLayerAddress);

        if (publicIpV6Address.IpAddress is null)
            publicIpV6Address = localIpV6Addresses.FirstOrDefault(static q => q.PrefixOrigin is PrefixOrigin.Dhcp && q.SuffixOrigin is SuffixOrigin.OriginDhcp);

        return publicIpV6Address.IpAddress;
    }

    [SupportedOSPlatform("windows")]
    private static IEnumerable<(IPAddress IpAddress, PrefixOrigin PrefixOrigin, SuffixOrigin SuffixOrigin)> GetWindowsIpV6Addresses()
        => GetIpV6UnicastAddresses()
        .Select(static q => (q.Address, q.PrefixOrigin, q.SuffixOrigin));

    private static IEnumerable<IPAddress> GetIpV6Addresses()
        => GetIpV6UnicastAddresses()
        .Select(static q => q.Address);

    private static IEnumerable<UnicastIPAddressInformation> GetIpV6UnicastAddresses()
        => NetworkInterface.GetAllNetworkInterfaces()
        .Where(static q => q.OperationalStatus is OperationalStatus.Up)
        .Select(static q => q.GetIPProperties())
        .Where(static q => q.GatewayAddresses.Count is not 0)
        .SelectMany(static q => q.UnicastAddresses)
        .Where(static q => q.Address.AddressFamily is AddressFamily.InterNetworkV6)
        .Where(static q => q.Address is { IsIPv6SiteLocal: false, IsIPv6UniqueLocal: false, IsIPv6LinkLocal: false });

    private async ValueTask ReceiveAsync(ReadOnlyMemory<byte> buffer, IPEndPoint remoteEp, CancellationToken cancellationToken)
    {
        (uint senderId, uint receiverId) = GetClientIds(buffer);

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

        if (!ValidateClientIds(senderId, receiverId, buffer, remoteEp))
            return;

        if (await HandlePingRequestAsync(senderId, receiverId, buffer, remoteEp, cancellationToken).ConfigureAwait(false))
            return;

        await HandlePacketAsync(senderId, receiverId, buffer, remoteEp, cancellationToken).ConfigureAwait(false);

        if (Logger.IsEnabled(LogLevel.Debug))
            Logger.LogDebug(FormattableString.Invariant($"V{Version} client {remoteEp} message handled."));
    }

    private async ValueTask<bool> HandlePingRequestAsync(
        uint senderId, uint receiverId, ReadOnlyMemory<byte> buffer, IPEndPoint remoteEp, CancellationToken cancellationToken)
    {
        if (senderId is not 0u || receiverId is not 0u)
            return false;

        if (buffer.Length is PingRequestPacketSize)
        {
            if (!IsPingLimitReached(remoteEp.Address))
            {
                if (Logger.IsEnabled(LogLevel.Debug))
                {
                    Logger.LogDebug(
                        FormattableString.Invariant($"V{Version} client {remoteEp} replying to ping ") +
                        FormattableString.Invariant($"({pingCounter!.Count}/{ServiceOptions.Value.MaxPingsGlobal})."));
                }
                else if (Logger.IsEnabled(LogLevel.Trace))
                {
                    Logger.LogTrace(
                        FormattableString.Invariant($"V{Version} client {remoteEp} replying to ping ") +
                        FormattableString.Invariant($"({pingCounter!.Count}/{ServiceOptions.Value.MaxPingsGlobal}):") +
                        FormattableString.Invariant($" {Convert.ToHexString(buffer.Span[..PingResponsePacketSize])}."));
                }

                _ = await Client!.SendToAsync(
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

    private async Task DoReceiveAsync(ReadOnlyMemory<byte> buffer, IPEndPoint remoteEp, CancellationToken cancellationToken)
    {
        try
        {
            if (buffer.Length < MinimumPacketSize || buffer.Length > ServiceOptions.Value.MaxPacketSize)
            {
                if (Logger.IsEnabled(LogLevel.Debug))
                    Logger.LogDebug(FormattableString.Invariant($"V{Version} Tunnel invalid UDP packet size {buffer.Length}."));

                return;
            }

            await ReceiveAsync(buffer, remoteEp, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (ex.CancellationToken == cancellationToken)
        {
            // ignore, shut down signal
        }
        catch (Exception ex)
        {
            await Logger.LogExceptionDetailsAsync(ex).ConfigureAwait(false);
        }
    }

    private async ValueTask SendMasterServerHeartbeatAsync(int clients, CancellationToken cancellationToken)
    {
        var path = new Uri(
            FormattableString.Invariant($"?version={Version}&name={Uri.EscapeDataString(ServiceOptions.Value.Name!)}") +
            FormattableString.Invariant($"&port={Port}&clients={clients}&maxclients={ServiceOptions.Value.MaxClients}") +
            FormattableString.Invariant($"&masterpw={Uri.EscapeDataString(ServiceOptions.Value.MasterPassword ?? string.Empty)}") +
            FormattableString.Invariant($"&maintenance={(MaintenanceModeEnabled ? 1 : 0)}") +
            FormattableString.Invariant($"&address2={secondaryIpAddress}"),
            UriKind.Relative);
        HttpResponseMessage? httpResponseMessage = null;

        try
        {
            httpResponseMessage = await httpClientFactory.CreateClient(Options.DefaultName)
                .GetAsync(path, cancellationToken).ConfigureAwait(false);

            string responseContent = await httpResponseMessage.EnsureSuccessStatusCode().Content
                .ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!"OK".Equals(responseContent, StringComparison.OrdinalIgnoreCase))
                throw new MasterServerException(responseContent);

            if (Logger.IsEnabled(LogLevel.Information))
                Logger.LogInfo(FormattableString.Invariant($"V{Version} Tunnel Heartbeat sent."));
        }
        catch (HttpRequestException ex)
        {
            await Logger.LogExceptionDetailsAsync(ex, httpResponseMessage).ConfigureAwait(false);
        }
        finally
        {
            httpResponseMessage?.Dispose();
        }
    }

    private bool IsPingLimitReached(IPAddress address)
    {
        if (pingCounter!.Count >= ServiceOptions.Value.MaxPingsGlobal)
            return true;

        int hashCode = address.GetHashCode();

        if (pingCounter.TryGetValue(hashCode, out int count) && count >= ServiceOptions.Value.MaxPingsPerIp)
            return true;

        pingCounter[hashCode] = ++count;

        return false;
    }

    private async Task StartHeartbeatAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (ServiceOptions.Value is { AnnounceIpV4: true, AnnounceIpV6: true })
                    secondaryIpAddress = GetPublicIpV6Address();

                while (await heartbeatTimer!.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                {
                    int clients = CleanupConnections();

                    if (!ServiceOptions.Value.NoMasterAnnounce)
                        await SendMasterServerHeartbeatAsync(clients, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException ex) when (ex.CancellationToken == cancellationToken)
            {
                // ignore, shut down signal
            }
            catch (Exception ex)
            {
                await Logger.LogExceptionDetailsAsync(ex).ConfigureAwait(false);
            }
        }
    }

#if EnableLegacyVersion
#pragma warning disable SA1202 // Elements should be ordered by access
    protected virtual int CleanupConnections()
#pragma warning restore SA1202 // Elements should be ordered by access
#else
    private int CleanupConnections()
#endif
    {
        foreach (KeyValuePair<uint, TunnelClient> mapping in Mappings!.Where(static x => x.Value.TimedOut).ToList())
        {
            CleanupConnection(mapping.Value);
            _ = Mappings!.Remove(mapping.Key, out _);

            if (Logger.IsEnabled(LogLevel.Information))
            {
                Logger.LogInfo(
                    FormattableString.Invariant($"Removed V{Version} client from ") +
                    FormattableString.Invariant($"{mapping.Value.RemoteEp?.ToString() ?? "(not connected)"}, "));
            }

            if (Logger.IsEnabled(LogLevel.Debug))
            {
                Logger.LogDebug(
                    FormattableString.Invariant($"{Mappings!.Count} clients from {Mappings.Values
                        .Select(static q => q.RemoteEp?.Address).Where(static q => q is not null).Distinct().Count()} IPs."));
            }
        }

        pingCounter!.Clear();

        return Mappings!.Count;
    }
}