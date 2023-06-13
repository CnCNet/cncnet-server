namespace CnCNetServer;

using System.Security.Cryptography;
using System.Text;

internal sealed class TunnelV3(ILogger<TunnelV3> logger, IOptions<ServiceOptions> options, IHttpClientFactory httpClientFactory)
    : Tunnel(logger, options, httpClientFactory)
{
    private const int PlayerIdSize = sizeof(int);
    private const int TunnelCommandSize = 1;
    private const int TunnelCommandHashSize = 20;
    private const int TunnelCommandRequestPacketSize = (PlayerIdSize * 2) + TunnelCommandSize + TunnelCommandHashSize;
    private const double CommandRateLimitInSeconds = 60d;

    private byte[]? maintenancePasswordSha1;
    private long lastCommandTick;

    private enum TunnelCommand : byte
    {
        MaintenanceMode
    }

    protected override int Version => 3;

    protected override int Port => ServiceOptions.Value.TunnelPort;

    protected override int MinimumPacketSize => 8;

    public override ValueTask StartAsync(CancellationToken cancellationToken)
    {
        if (ServiceOptions.Value.MaintenancePassword?.Length is not null and not 0)
#pragma warning disable CA5350 // Do Not Use Weak Cryptographic Algorithms
            maintenancePasswordSha1 = SHA1.HashData(Encoding.UTF8.GetBytes(ServiceOptions.Value.MaintenancePassword));
#pragma warning restore CA5350 // Do Not Use Weak Cryptographic Algorithms

        lastCommandTick = DateTime.UtcNow.Ticks;

        return base.StartAsync(cancellationToken);
    }

    protected override void CleanupConnection(TunnelClient tunnelClient)
    {
        int hashCode = tunnelClient.RemoteEp!.Address.GetHashCode();

        if (--ConnectionCounter![hashCode] <= 0)
            _ = ConnectionCounter.Remove(hashCode, out _);
    }

    protected override (uint SenderId, uint ReceiverId) GetClientIds(ReadOnlyMemory<byte> buffer)
    {
        uint senderId = BitConverter.ToUInt32(buffer[..PlayerIdSize].Span);
        uint receiverId = BitConverter.ToUInt32(buffer[PlayerIdSize..(PlayerIdSize * 2)].Span);

        return (senderId, receiverId);
    }

    protected override bool ValidateClientIds(uint senderId, uint receiverId, ReadOnlyMemory<byte> buffer, IPEndPoint remoteEp)
    {
        if (senderId is 0u)
        {
            if (receiverId is uint.MaxValue && buffer.Length >= TunnelCommandRequestPacketSize)
                ExecuteCommand((TunnelCommand)buffer.Span[(PlayerIdSize * 2)..((PlayerIdSize * 2) + TunnelCommandSize)][0], buffer, remoteEp);

            if (receiverId is not 0u)
                return false;
        }

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
        if (Mappings!.TryGetValue(senderId, out TunnelClient? sender))
        {
            if (!HandleExistingClient(remoteEp, sender))
                return;
        }
        else
        {
            sender = HandleNewClient(senderId, remoteEp);
        }

        if (Mappings.TryGetValue(receiverId, out TunnelClient? receiver) && !receiver.RemoteEp!.Equals(sender.RemoteEp))
        {
            await ForwardPacketAsync(senderId, receiverId, buffer, remoteEp, receiver, cancellationToken).ConfigureAwait(false);
        }
        else if (Logger.IsEnabled(LogLevel.Debug))
        {
            Logger.LogDebug(
                FormattableString.Invariant($"V{Version} client {remoteEp} mapping not found or receiver") +
                FormattableString.Invariant($" {receiver?.RemoteEp!} equals sender {sender.RemoteEp!}."));
        }
    }

    private async ValueTask ForwardPacketAsync(
        uint senderId, uint receiverId, ReadOnlyMemory<byte> buffer, IPEndPoint remoteEp, TunnelClient receiver, CancellationToken cancellationToken)
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
                FormattableString.Invariant($"{receiver.RemoteEp} ({receiverId}):  {Convert.ToHexString(buffer.Span)}."));
        }

        _ = await Client!.SendToAsync(buffer, SocketFlags.None, receiver.RemoteEp!, cancellationToken).ConfigureAwait(false);
    }

    private TunnelClient HandleNewClient(uint senderId, IPEndPoint remoteEp)
    {
        TunnelClient sender = new(ServiceOptions.Value.ClientTimeout, remoteEp);

        if (Mappings!.Count < ServiceOptions.Value.MaxClients && !MaintenanceModeEnabled
            && IsNewConnectionAllowed(remoteEp.Address) && Mappings.TryAdd(senderId, sender))
        {
            if (Logger.IsEnabled(LogLevel.Information))
                Logger.LogInfo(FormattableString.Invariant($"New V{Version} client from {remoteEp}."));

            if (Logger.IsEnabled(LogLevel.Debug))
            {
                Logger.LogDebug(
                    FormattableString.Invariant($"{ConnectionCounter!.Values.Sum()} clients from ") +
                    FormattableString.Invariant($"{ConnectionCounter.Count} IPs."));
            }
        }
        else if (Logger.IsEnabled(LogLevel.Information))
        {
            Logger.LogInfo(FormattableString.Invariant($"Denied new V{Version} client from {remoteEp}"));
        }

        return sender;
    }

    private bool HandleExistingClient(IPEndPoint remoteEp, TunnelClient sender)
    {
        if (!remoteEp.Equals(sender.RemoteEp))
        {
            if (sender.TimedOut && !MaintenanceModeEnabled && IsNewConnectionAllowed(remoteEp.Address, sender.RemoteEp!.Address))
            {
                sender.RemoteEp = remoteEp;

                if (Logger.IsEnabled(LogLevel.Information))
                    Logger.LogInfo(FormattableString.Invariant($"Reconnected V{Version} client from {remoteEp}."));

                if (Logger.IsEnabled(LogLevel.Debug))
                {
                    Logger.LogDebug(
                        FormattableString.Invariant($"{Mappings!.Count} clients from ") +
                        FormattableString.Invariant($"{Mappings.Values.Select(static q => q.RemoteEp?.Address)
                            .Where(static q => q is not null).Distinct().Count()} IPs."));
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

                return false;
            }
        }

        sender.SetLastReceiveTick();

        return true;
    }

    private bool IsNewConnectionAllowed(IPAddress newIp, IPAddress? oldIp = null)
    {
        int hashCode = newIp.GetHashCode();

        if (ConnectionCounter!.TryGetValue(hashCode, out int count) && count >= ServiceOptions.Value.IpLimit)
            return false;

        if (oldIp is null)
        {
            ConnectionCounter[hashCode] = ++count;
        }
        else if (!newIp.Equals(oldIp))
        {
            ConnectionCounter[hashCode] = ++count;

            int oldIpHash = oldIp.GetHashCode();

            if (--ConnectionCounter[oldIpHash] <= 0)
                _ = ConnectionCounter.Remove(oldIpHash, out _);
        }

        return true;
    }

    private void ExecuteCommand(TunnelCommand command, ReadOnlyMemory<byte> data, IPEndPoint remoteEp)
    {
        if (TimeSpan.FromTicks(DateTime.UtcNow.Ticks - lastCommandTick).TotalSeconds < CommandRateLimitInSeconds
            || maintenancePasswordSha1 is null || ServiceOptions.Value.MaintenancePassword!.Length is 0)
        {
            return;
        }

        lastCommandTick = DateTime.UtcNow.Ticks;

        ReadOnlySpan<byte> commandPasswordSha1 = data.Slice((PlayerIdSize * 2) + TunnelCommandSize, TunnelCommandHashSize).Span;

        if (!commandPasswordSha1.SequenceEqual(maintenancePasswordSha1))
        {
            if (Logger.IsEnabled(LogLevel.Warning))
                Logger.LogWarning(FormattableString.Invariant($"Invalid Maintenance mode request by {remoteEp}."));

            return;
        }

        MaintenanceModeEnabled = command switch
        {
            TunnelCommand.MaintenanceMode => !MaintenanceModeEnabled,
            _ => MaintenanceModeEnabled
        };

        if (Logger.IsEnabled(LogLevel.Warning))
            Logger.LogWarning(FormattableString.Invariant($"Maintenance mode set to {MaintenanceModeEnabled} by {remoteEp}."));
    }
}