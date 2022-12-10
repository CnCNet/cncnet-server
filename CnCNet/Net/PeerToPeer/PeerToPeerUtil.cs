namespace CnCNetServer;

using System.Buffers;

internal sealed class PeerToPeerUtil : IAsyncDisposable
{
    private const int CounterResetInterval = 60 * 1000; // Reset counter every X ms
    private const int MaxRequestsPerIp = 20; // Max requests during one CounterResetInterval period
    private const int MaxConnectionsGlobal = 5000; // Max amount of different ips sending requests during one CounterResetInterval period
    private const short StunId = 26262;

    private readonly Dictionary<int, int> connectionCounter = new(MaxConnectionsGlobal);
    private readonly System.Timers.Timer connectionCounterTimer = new(CounterResetInterval);
    private readonly IMemoryOwner<byte> memoryOwner = MemoryPool<byte>.Shared.Rent(40);
    private readonly SemaphoreSlim connectionCounterSemaphoreSlim = new(1, 1);
    private readonly ILogger logger;

    private Memory<byte> sendBuffer;

    public PeerToPeerUtil(ILogger<PeerToPeerUtil> logger)
    {
        this.logger = logger;
    }

    public Task StartAsync(int listenPort, CancellationToken cancellationToken)
    {
        sendBuffer = memoryOwner.Memory[..40];

        new Random().NextBytes(sendBuffer.Span);
        BitConverter.GetBytes(IPAddress.HostToNetworkOrder(StunId)).AsSpan(..2).CopyTo(sendBuffer.Span[6..8]);

#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
        connectionCounterTimer.Elapsed += (_, _) => ResetConnectionCounterAsync(cancellationToken);
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
        connectionCounterTimer.Enabled = true;

        return StartReceiverAsync(listenPort, cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        connectionCounterSemaphoreSlim.Dispose();
        connectionCounterTimer.Dispose();
        memoryOwner.Dispose();

        return ValueTask.CompletedTask;
    }

    private static bool IsInvalidRemoteIpEndPoint(IPEndPoint remoteEp)
        => remoteEp.Address.Equals(IPAddress.Loopback) || remoteEp.Address.Equals(IPAddress.Any)
        || remoteEp.Address.Equals(IPAddress.Broadcast) || remoteEp.Port == 0;

    private async Task ResetConnectionCounterAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (cancellationToken.IsCancellationRequested)
                connectionCounterTimer.Enabled = false;

            await connectionCounterSemaphoreSlim.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                connectionCounter.Clear();
            }
            finally
            {
                connectionCounterSemaphoreSlim.Release();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await logger.LogExceptionDetailsAsync(ex).ConfigureAwait(false);
        }
    }

    private async Task StartReceiverAsync(int listenPort, CancellationToken cancellationToken)
    {
        using var client = new Socket(SocketType.Dgram, ProtocolType.Udp);
        using IMemoryOwner<byte> receiveMemoryOwner = MemoryPool<byte>.Shared.Rent(64);
        Memory<byte> buffer = receiveMemoryOwner.Memory[..64];
        var remoteEp = new IPEndPoint(IPAddress.Any, 0);

        client.Bind(new IPEndPoint(IPAddress.IPv6Any, listenPort));

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInfo(
                FormattableString.Invariant($"{DateTimeOffset.Now} PeerToPeer UDP server started on port {listenPort}."));
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            SocketReceiveFromResult socketReceiveFromResult = await client.ReceiveFromAsync(
                buffer, SocketFlags.None, remoteEp, cancellationToken).ConfigureAwait(false);

            if (socketReceiveFromResult.ReceivedBytes == 48)
            {
                await ReceiveAsync(client, buffer, (IPEndPoint)socketReceiveFromResult.RemoteEndPoint, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private async ValueTask ReceiveAsync(
        Socket client, ReadOnlyMemory<byte> buffer, IPEndPoint remoteEp, CancellationToken cancellationToken)
    {
        logger.LogDebug(
            FormattableString.Invariant($"P2P client {remoteEp} connected."));

        if (IsInvalidRemoteIpEndPoint(remoteEp)
            || await IsConnectionLimitReachedAsync(remoteEp.Address, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        if (IPAddress.NetworkToHostOrder(BitConverter.ToInt16(buffer.Span)) != StunId)
            return;

        remoteEp.Address.GetAddressBytes().AsSpan(..4).CopyTo(sendBuffer.Span[..4]);
        BitConverter.GetBytes(IPAddress.HostToNetworkOrder((short)remoteEp.Port)).AsSpan(..2).CopyTo(sendBuffer.Span[4..6]);

        // obfuscate
        for (int i = 0; i < 6; i++)
            sendBuffer.Span[i] ^= 0x20;

        await client.SendToAsync(sendBuffer, SocketFlags.None, remoteEp, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<bool> IsConnectionLimitReachedAsync(IPAddress address, CancellationToken cancellationToken)
    {
        await connectionCounterSemaphoreSlim.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (connectionCounter.Count >= MaxConnectionsGlobal)
                return true;

            int ipHash = address.GetHashCode();

            if (connectionCounter.TryGetValue(ipHash, out int count) && count >= MaxRequestsPerIp)
                return true;

            connectionCounter[ipHash] = ++count;

            return false;
        }
        finally
        {
            connectionCounterSemaphoreSlim.Release();
        }
    }
}