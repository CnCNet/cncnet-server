namespace CnCNetServer;

internal sealed class TunnelClient
{
    private readonly int timeout;

    private long lastReceiveTick;

    public TunnelClient(int timeout, IPEndPoint? remoteEndPoint = null)
    {
        this.timeout = timeout;
        RemoteEp = remoteEndPoint;

        SetLastReceiveTick();
    }

    public IPEndPoint? RemoteEp { get; set; }

    public bool TimedOut => TimeSpan.FromTicks(DateTime.UtcNow.Ticks - lastReceiveTick).TotalSeconds >= timeout;

    public void SetLastReceiveTick() => lastReceiveTick = DateTime.UtcNow.Ticks;
}