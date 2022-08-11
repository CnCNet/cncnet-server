namespace CnCNetServer;

using System.Text;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Net.Http;
using Microsoft.Extensions.Logging;

internal sealed class TunnelV3 : Tunnel
{
    private readonly SemaphoreSlim clientsSemaphoreSlim = new(1, 1);
    private readonly byte[]? maintenancePasswordSha1;
    private readonly List<uint> expiredMappings = new(25);
    private long lastCommandTick;

    public TunnelV3(ILogger<TunnelV3> logger, Options options, IHttpClientFactory httpClientFactory)
        : base(3, options.TunnelPort <= 1024 ? 50001 : options.TunnelPort,
            options.IpLimit < 1 ? 8 : options.IpLimit, logger, options, httpClientFactory)
    {
        if (MaintenancePassword.Length > 0)
            maintenancePasswordSha1 = SHA1.HashData(Encoding.UTF8.GetBytes(MaintenancePassword));

        lastCommandTick = DateTime.UtcNow.Ticks;
    }

    private enum TunnelCommand : byte
    {
        MaintenanceMode
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync().ConfigureAwait(false);
        clientsSemaphoreSlim.Dispose();
    }

    protected override async Task<int> CleanupConnectionsAsync(CancellationToken cancellationToken)
    {
        int clients;

        await clientsSemaphoreSlim.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            expiredMappings.Clear();

            foreach (KeyValuePair<uint, TunnelClient> mapping in Mappings)
            {
                if (mapping.Value.TimedOut)
                {
                    expiredMappings.Add(mapping.Key);

                    int ipHash = mapping.Value.RemoteEp.Address.GetHashCode();

                    if (--ConnectionCounter[ipHash] <= 0)
                        ConnectionCounter.Remove(ipHash);

                    Logger.LogMessage(
                        FormattableString.Invariant($"Removed V{Version} client from {mapping.Value.RemoteEp}") +
                        FormattableString.Invariant($", {Mappings.Count} clients from ") +
                        FormattableString.Invariant($"{Mappings.Values.Select(q => q.RemoteEp.Address).Distinct().Count()} IPs."));
                }
            }

            foreach (uint mapping in expiredMappings)
                Mappings.Remove(mapping);

            clients = Mappings.Count;

            PingCounter.Clear();
        }
        finally
        {
            clientsSemaphoreSlim.Release();
        }

        return clients;
    }

    protected override async Task ReceiveAsync(
        byte[] buffer, int size, IPEndPoint remoteEp, CancellationToken cancellationToken)
    {
        uint senderId = BitConverter.ToUInt32(buffer, 0);
        uint receiverId = BitConverter.ToUInt32(buffer, 4);

        if (senderId == 0)
        {
            if (receiverId == uint.MaxValue && size >= 8 + 1 + 20) // 8=receiver+sender ids, 1=command, 20=sha1 pass
                ExecuteCommand((TunnelCommand)buffer[8], buffer);

            if (receiverId != 0)
                return;
        }

        if ((senderId == receiverId && senderId != 0) || remoteEp.Address.Equals(IPAddress.Loopback)
            || remoteEp.Address.Equals(IPAddress.Any) || remoteEp.Address.Equals(IPAddress.Broadcast)
            || remoteEp.Port == 0)
        {
            return;
        }

        await clientsSemaphoreSlim.WaitAsync(cancellationToken).ConfigureAwait(false);

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
                {
                    if (sender.TimedOut && !MaintenanceModeEnabled
                        && IsNewConnectionAllowed(remoteEp.Address, sender.RemoteEp.Address))
                    {
                        sender.RemoteEp = new IPEndPoint(remoteEp.Address, remoteEp.Port);
                    }
                    else
                    {
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

                sender.SetLastReceiveTick();

                Mappings.Add(senderId, sender);
                Logger.LogMessage(
                    FormattableString.Invariant($"New V{Version} client from {remoteEp}, ") +
                    FormattableString.Invariant($"{ConnectionCounter.Values.Sum()} clients from ") +
                    FormattableString.Invariant($"{ConnectionCounter.Count} IPs."));
            }

            if (Mappings.TryGetValue(receiverId, out TunnelClient? receiver)
                && !receiver.RemoteEp.Equals(sender.RemoteEp))
            { 
                await Client!.Client.SendAsync(buffer.AsMemory()[..size], SocketFlags.None, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            clientsSemaphoreSlim.Release();
        }
    }

    private bool IsNewConnectionAllowed(IPAddress newIp, IPAddress? oldIp = null)
    {
        int ipHash = newIp.GetHashCode();

        if (ConnectionCounter.TryGetValue(ipHash, out int count) && count >= IpLimit)
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

    private void ExecuteCommand(TunnelCommand command, byte[] data)
    {
        if (TimeSpan.FromTicks(DateTime.UtcNow.Ticks - lastCommandTick).TotalSeconds < CommandRateLimit
            || maintenancePasswordSha1 is null || MaintenancePassword.Length == 0)
        { 
            return;
        }

        lastCommandTick = DateTime.UtcNow.Ticks;

        byte[] commandPasswordSha1 = new byte[20];
        Array.Copy(data, 9, commandPasswordSha1, 0, 20);

        if (!commandPasswordSha1.SequenceEqual(maintenancePasswordSha1))
            return;

        MaintenanceModeEnabled = command switch
        {
            TunnelCommand.MaintenanceMode => !MaintenanceModeEnabled,
            _ => MaintenanceModeEnabled
        };
    }
}