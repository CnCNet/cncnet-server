namespace CnCNetServer;

using System.Text;
using System.Security.Cryptography;

internal sealed class TunnelV3 : Tunnel
{
    private const int PlayerIdSize = sizeof(int);
    private const int TunnelCommandSize = 1;
    private const int TunnelCommandHashSize = 20;
    private const int TunnelCommandRequestPacketSize = (PlayerIdSize * 2) + TunnelCommandSize + TunnelCommandHashSize;
    private const int CommandRateLimitInSeconds = 60;

    private byte[]? maintenancePasswordSha1;
    private long lastCommandTick;

    public TunnelV3(ILogger<TunnelV3> logger, IOptions<ServiceOptions> options, IHttpClientFactory httpClientFactory)
        : base(logger, options, httpClientFactory)
    {
    }

    protected override int Version => 3;

    protected override int Port => ServiceOptions.Value.TunnelPort;

    protected override int MinimumPacketSize => 8;

    private enum TunnelCommand : byte
    {
        MaintenanceMode
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        if (ServiceOptions.Value.MaintenancePassword?.Any() ?? false)
#pragma warning disable CA5350 // Do Not Use Weak Cryptographic Algorithms
            maintenancePasswordSha1 = SHA1.HashData(Encoding.UTF8.GetBytes(ServiceOptions.Value.MaintenancePassword));
#pragma warning restore CA5350 // Do Not Use Weak Cryptographic Algorithms

        lastCommandTick = DateTime.UtcNow.Ticks;

        return base.StartAsync(cancellationToken);
    }

    protected override void CleanupConnection(TunnelClient tunnelClient)
    {
        int ipHash = tunnelClient.RemoteEp!.Address.GetHashCode();

        if (--ConnectionCounter![ipHash] <= 0)
            ConnectionCounter.Remove(ipHash, out _);
    }

    protected override async ValueTask ReceiveAsync(
        ReadOnlyMemory<byte> buffer, IPEndPoint remoteEp, CancellationToken cancellationToken)
    {
        uint senderId = BitConverter.ToUInt32(buffer[..PlayerIdSize].Span);
        uint receiverId = BitConverter.ToUInt32(buffer[PlayerIdSize..(PlayerIdSize * 2)].Span);

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

        if (senderId == 0)
        {
            if (receiverId == uint.MaxValue && buffer.Length >= TunnelCommandRequestPacketSize)
                ExecuteCommand((TunnelCommand)buffer.Span[(PlayerIdSize * 2)..((PlayerIdSize * 2) + TunnelCommandSize)][0], buffer, remoteEp);

            if (receiverId != 0)
                return;
        }

        if ((senderId == receiverId && senderId != 0) || remoteEp.Address.Equals(IPAddress.Loopback)
            || remoteEp.Address.Equals(IPAddress.Any) || remoteEp.Address.Equals(IPAddress.Broadcast)
            || remoteEp.Port == 0)
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
            if (!remoteEp.Equals(sender.RemoteEp))
            {
                if (sender.TimedOut && !MaintenanceModeEnabled
                    && IsNewConnectionAllowed(remoteEp.Address, sender.RemoteEp!.Address))
                {
                    sender.RemoteEp = remoteEp;

                    if (Logger.IsEnabled(LogLevel.Information))
                    {
                        Logger.LogInfo(
                            FormattableString.Invariant($"{DateTimeOffset.Now} Reconnected V{Version} client from {remoteEp}, "));
                    }

                    if (Logger.IsEnabled(LogLevel.Debug))
                    {
                        Logger.LogDebug(
                            FormattableString.Invariant($"{Mappings.Count} clients from ") +
                            FormattableString.Invariant($"{Mappings.Values.Select(q => q.RemoteEp?.Address)
                                .Where(q => q is not null).Distinct().Count()} IPs."));
                    }
                }
                else
                {
                    if (Logger.IsEnabled(LogLevel.Debug))
                    {
                        Logger.LogDebug(
                            FormattableString.Invariant($"V{Version} client {remoteEp} denied {sender.TimedOut}") +
                            FormattableString.Invariant($" {MaintenanceModeEnabled} {remoteEp.Address} {sender.RemoteEp}."));
                    }

                    return;
                }
            }

            sender.SetLastReceiveTick();
        }
        else
        {
            sender = new(ServiceOptions.Value.ClientTimeout, remoteEp);

            if (Mappings.Count < ServiceOptions.Value.MaxClients && !MaintenanceModeEnabled
                && IsNewConnectionAllowed(remoteEp.Address) && Mappings.TryAdd(senderId, sender))
            {
                if (Logger.IsEnabled(LogLevel.Information))
                {
                    Logger.LogInfo(
                        FormattableString.Invariant($"{DateTimeOffset.Now} New V{Version} client from {remoteEp}."));
                }

                if (Logger.IsEnabled(LogLevel.Debug))
                {
                    Logger.LogDebug(
                        FormattableString.Invariant($"{ConnectionCounter!.Values.Sum()} clients from ") +
                        FormattableString.Invariant($"{ConnectionCounter.Count} IPs."));
                }
            }
            else if (Logger.IsEnabled(LogLevel.Information))
            {
                Logger.LogInfo(
                    FormattableString.Invariant($"{DateTimeOffset.Now} Denied new V{Version} client from {remoteEp}"));
            }
        }

        if (Mappings.TryGetValue(receiverId, out TunnelClient? receiver)
            && !receiver.RemoteEp!.Equals(sender.RemoteEp))
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
        else if (Logger.IsEnabled(LogLevel.Debug))
        {
            Logger.LogDebug(
                FormattableString.Invariant($"V{Version} client {remoteEp} mapping not found or receiver") +
                FormattableString.Invariant($" {receiver?.RemoteEp!} is sender") +
                FormattableString.Invariant($" {sender.RemoteEp!}."));
        }

        if (Logger.IsEnabled(LogLevel.Debug))
        {
            Logger.LogDebug(
                FormattableString.Invariant($"V{Version} client {remoteEp} message handled,"));
        }
    }

    private bool IsNewConnectionAllowed(IPAddress newIp, IPAddress? oldIp = null)
    {
        int ipHash = newIp.GetHashCode();

        if (ConnectionCounter!.TryGetValue(ipHash, out int count) && count >= ServiceOptions.Value.IpLimit)
            return false;

        if (oldIp == null)
        {
            ConnectionCounter[ipHash] = ++count;
        }
        else if (!newIp.Equals(oldIp))
        {
            ConnectionCounter[ipHash] = ++count;

            int oldIpHash = oldIp.GetHashCode();

            if (--ConnectionCounter[oldIpHash] <= 0)
                ConnectionCounter.Remove(oldIpHash, out _);
        }

        return true;
    }

    private void ExecuteCommand(TunnelCommand command, ReadOnlyMemory<byte> data, IPEndPoint remoteEp)
    {
        if (TimeSpan.FromTicks(DateTime.UtcNow.Ticks - lastCommandTick).TotalSeconds < CommandRateLimitInSeconds
            || maintenancePasswordSha1 is null || !ServiceOptions.Value.MaintenancePassword!.Any())
        {
            return;
        }

        lastCommandTick = DateTime.UtcNow.Ticks;

        ReadOnlySpan<byte> commandPasswordSha1 = data.Slice((PlayerIdSize * 2) + TunnelCommandSize, TunnelCommandHashSize).Span;

        if (!commandPasswordSha1.SequenceEqual(maintenancePasswordSha1))
        {
            if (Logger.IsEnabled(LogLevel.Warning))
            {
                Logger.LogWarning(FormattableString.Invariant(
                    $"{DateTimeOffset.Now} Invalid Maintenance mode request by {remoteEp}."));
            }

            return;
        }

        MaintenanceModeEnabled = command switch
        {
            TunnelCommand.MaintenanceMode => !MaintenanceModeEnabled,
            _ => MaintenanceModeEnabled
        };

        if (Logger.IsEnabled(LogLevel.Warning))
        {
            Logger.LogWarning(FormattableString.Invariant(
                $"{DateTimeOffset.Now} Maintenance mode set to {MaintenanceModeEnabled} by {remoteEp}."));
        }
    }
}