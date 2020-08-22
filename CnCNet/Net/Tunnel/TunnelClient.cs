using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net;

namespace CnCNet.Net.Tunnel
{
    class TunnelClient
    {
        public IPEndPoint RemoteEP;

        long LastReceiveTick;
        int Timeout;

        public TunnelClient(int timeout = 30)
        {
            Timeout = timeout;
        }

        public bool TimedOut {
            get { return TimeSpan.FromTicks(DateTime.UtcNow.Ticks - LastReceiveTick).TotalSeconds >= Timeout; } }

        public void SetLastReceiveTick()
        {
            LastReceiveTick = DateTime.UtcNow.Ticks;
        }
    }
}
