namespace CnCNetServer;

using System.Net;

internal sealed class TunnelClient
{
    private readonly int timeout;

    private long lastReceiveTick;

    public TunnelClient(IPEndPoint remoteEndPoint, int timeout = 30)
    {
        RemoteEp = remoteEndPoint;
        this.timeout = timeout;
    }

    public IPEndPoint RemoteEp { get; set; }

    public bool TimedOut { get => TimeSpan.FromTicks(DateTime.UtcNow.Ticks - lastReceiveTick).TotalSeconds >= timeout; }

    public void SetLastReceiveTick()
    {
        lastReceiveTick = DateTime.UtcNow.Ticks;
    }
}