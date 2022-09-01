namespace CnCNetServer;

internal sealed class TunnelClient
{
    private const int Timeout = 30;

    private long lastReceiveTick;

    public TunnelClient(IPEndPoint? remoteEndPoint = null)
    {
        RemoteEp = remoteEndPoint;

        SetLastReceiveTick();
    }

    public IPEndPoint? RemoteEp { get; set; }

    public bool TimedOut { get => TimeSpan.FromTicks(DateTime.UtcNow.Ticks - lastReceiveTick).TotalSeconds >= Timeout; }

    public void SetLastReceiveTick()
        => lastReceiveTick = DateTime.UtcNow.Ticks;
}