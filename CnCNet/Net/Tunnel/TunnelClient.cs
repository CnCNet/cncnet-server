namespace CnCNetServer;

using System.Net;

internal sealed class TunnelClient
{
    private const int timeout = 30;

    private long lastReceiveTick = DateTime.UtcNow.Ticks;

    public TunnelClient(IPEndPoint? remoteEndPoint = null)
    {
        RemoteEp = remoteEndPoint;
    }

    public IPEndPoint? RemoteEp { get; set; }

    public bool TimedOut { get => TimeSpan.FromTicks(DateTime.UtcNow.Ticks - lastReceiveTick).TotalSeconds >= timeout; }

    public void SetLastReceiveTick()
        => lastReceiveTick = DateTime.UtcNow.Ticks;
}