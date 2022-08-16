namespace CnCNetServer;

using System.Text;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Net.Http;
using Microsoft.Extensions.Logging;

internal sealed class TunnelV3 : Tunnel
{
    private const int TunnelCommandRequestPacketSize = 8 + 1 + 20; // 8=receiver+sender ids, 1=command, 20=sha1 pass

    private readonly SemaphoreSlim clientsSemaphoreSlim = new(1, 1);
    private readonly byte[]? maintenancePasswordSha1;
    private long lastCommandTick;

    public TunnelV3(ILogger<TunnelV3> logger, Options options, IHttpClientFactory httpClientFactory)
        : base(logger, options, httpClientFactory)
    {
        if (Options.MaintenancePassword!.Any())
            maintenancePasswordSha1 = SHA1.HashData(Encoding.UTF8.GetBytes(Options.MaintenancePassword!));

        lastCommandTick = DateTime.UtcNow.Ticks;
    }

    protected override int Version => 3;

    protected override int DefaultPort => 50001;

    protected override int DefaultIpLimit => 8;

    protected override int Port => Options.TunnelPort;

    private enum TunnelCommand : byte
    {
        MaintenanceMode
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync().ConfigureAwait(false);
        clientsSemaphoreSlim.Dispose();
    }

    protected override void CleanupConnection(TunnelClient tunnelClient)
    {
        int ipHash = tunnelClient.RemoteEp!.Address.GetHashCode();

        if (--ConnectionCounter[ipHash] <= 0)
            ConnectionCounter.Remove(ipHash);
    }

    protected override async Task ReceiveAsync(
        ReadOnlyMemory<byte> buffer, IPEndPoint remoteEp, CancellationToken cancellationToken)
    {
        uint senderId = BitConverter.ToUInt32(buffer[..4].Span);
        uint receiverId = BitConverter.ToUInt32(buffer[4..8].Span);

        if (Logger.IsEnabled(LogLevel.Debug))
        {
            Logger.LogDebug(
                FormattableString.Invariant($"V{Version} client {remoteEp} ({senderId} -> {receiverId}) received") +
                FormattableString.Invariant($" {buffer.Length}: {Convert.ToHexString(buffer.Span)}"));
        }

        if (senderId == 0)
        {
            if (receiverId == uint.MaxValue && buffer.Length >= TunnelCommandRequestPacketSize)
                ExecuteCommand((TunnelCommand)buffer.Span[8..9][0], buffer, remoteEp);

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

        await clientsSemaphoreSlim.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (await HandlePingRequestAsync(senderId, receiverId, buffer, remoteEp, cancellationToken).ConfigureAwait(false))
                return;

            if (Mappings.TryGetValue(senderId, out TunnelClient? sender))
            {
                if (!remoteEp.Equals(sender.RemoteEp))
                {
                    if (sender.TimedOut && !MaintenanceModeEnabled
                        && IsNewConnectionAllowed(remoteEp.Address, sender.RemoteEp!.Address))
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
                    else
                    {
                        if (Logger.IsEnabled(LogLevel.Debug))
                        {
                            Logger.LogDebug(
                                FormattableString.Invariant($"V{Version} client {remoteEp}") +
                                FormattableString.Invariant($" did not match {sender.RemoteEp}."));
                        }

                        return;
                    }
                }

                sender.SetLastReceiveTick();
            }
            else
            {
                if (Mappings.Count >= MaxClients || MaintenanceModeEnabled || !IsNewConnectionAllowed(remoteEp.Address))
                    return;

                sender = new TunnelClient(new IPEndPoint(remoteEp.Address, remoteEp.Port));

                Mappings.Add(senderId, sender);

                if (Logger.IsEnabled(LogLevel.Information))
                {
                    Logger.LogInfo(
                        FormattableString.Invariant($"{DateTimeOffset.Now} New V{Version} client from {remoteEp}, ") +
                        FormattableString.Invariant($"{ConnectionCounter.Values.Sum()} clients from ") +
                        FormattableString.Invariant($"{ConnectionCounter.Count} IPs."));
                }
            }

            if (Mappings.TryGetValue(receiverId, out TunnelClient? receiver)
                && !receiver.RemoteEp!.Equals(sender.RemoteEp))
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
            else if (Logger.IsEnabled(LogLevel.Debug))
            {
                Logger.LogDebug(
                    FormattableString.Invariant($"V{Version} client {remoteEp} mapping not found or receiver") +
                    FormattableString.Invariant($" {receiver?.RemoteEp!} is sender") +
                    FormattableString.Invariant($" {sender.RemoteEp!}."));
            }
        }
        finally
        {
            int locks = clientsSemaphoreSlim.Release();

            if (Logger.IsEnabled(LogLevel.Debug))
            {
                Logger.LogDebug(
                    FormattableString.Invariant($"V{Version} client {remoteEp} message handled,") +
                    FormattableString.Invariant($" pending receive threads: {locks}."));
            }
        }
    }

    private bool IsNewConnectionAllowed(IPAddress newIp, IPAddress? oldIp = null)
    {
        int ipHash = newIp.GetHashCode();

        if (ConnectionCounter.TryGetValue(ipHash, out int count) && count >= GetIpLimit())
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
                ConnectionCounter.Remove(oldIpHash);
        }

        return true;
    }

    private void ExecuteCommand(TunnelCommand command, ReadOnlyMemory<byte> data, IPEndPoint remoteEp)
    {
        if (TimeSpan.FromTicks(DateTime.UtcNow.Ticks - lastCommandTick).TotalSeconds < CommandRateLimit
            || maintenancePasswordSha1 is null || !Options.MaintenancePassword!.Any())
        {
            return;
        }

        lastCommandTick = DateTime.UtcNow.Ticks;

        ReadOnlySpan<byte> commandPasswordSha1 = data.Slice(9, 20).Span;

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