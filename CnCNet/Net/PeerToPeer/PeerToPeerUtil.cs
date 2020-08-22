using System;
using System.Collections.Generic;
using System.Text;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Timers;
using System.Linq;

namespace CnCNet.Net.PeerToPeer
{
    class PeerToPeerUtil
    {
        const int COUNTER_RESET_INTERVAL = 60 * 1000; // Reset counter every X ms
        const int MAX_REQUESTS_PER_IP = 20; // Max requests during one COUNTER_RESET_INTERVAL period
        const int MAX_CONNECTIONS_GLOBAL = 5000; // Max amount of different ips sending requests during one COUNTER_RESET_INTERVAL period
        const int STUN_ID = 26262;

        int ListenPort;
        UdpClient Client;
        Task ReceiveTask;
        Dictionary<int, int> ConnectionCounter = new Dictionary<int, int>(MAX_CONNECTIONS_GLOBAL);
        Timer ConnectionCounterTimer = new Timer(COUNTER_RESET_INTERVAL);
        object _lock = new object();
        byte[] SendBuffer = new byte[40];


        public static Task StartNew(int listenPort) { return new PeerToPeerUtil(listenPort).Start(); }

        public PeerToPeerUtil(int listenPort)
        {
            ListenPort = listenPort;
            new Random().NextBytes(SendBuffer);

            Array.Copy(
                BitConverter.GetBytes(IPAddress.HostToNetworkOrder((short)STUN_ID)),
                0,
                SendBuffer,
                6,
                2);

            ConnectionCounterTimer.Elapsed += new ElapsedEventHandler((sender, e) => ResetCounter());
            ReceiveTask = new Task(() => SyncReceive(), TaskCreationOptions.LongRunning);
        }

        public Task Start()
        {
            if (!ReceiveTask.Status.HasFlag(TaskStatus.Running))
            {
                ReceiveTask.Start();
                ConnectionCounterTimer.Enabled = true;
            }

            return ReceiveTask;
        }

        private void ResetCounter()
        {
            lock (_lock)
            {
                ConnectionCounter.Clear();
            }
        }

        private void SyncReceive()
        {
            Client = new UdpClient(ListenPort);
            try { Client.Client.IOControl(-1744830452, new byte[] { 0, 0, 0, 0 }, null); } // SIO_UDP_CONNRESET
            catch { } // Fails on mono

            byte[] buffer = new byte[64];
            EndPoint remoteEP = (EndPoint)new IPEndPoint(IPAddress.Any, 0);

            while (true)
            {
                int size = Client.Client.ReceiveFrom(buffer, ref remoteEP);
                if (size == 48)
                    OnReceive(buffer, size, (IPEndPoint)remoteEP);
            }
        }

        private void OnReceive(byte[] buffer, int size, IPEndPoint remoteEP)
        {
            if (remoteEP.Address.Equals(IPAddress.Loopback) ||
                remoteEP.Address.Equals(IPAddress.Any) ||
                remoteEP.Address.Equals(IPAddress.Broadcast) ||
                remoteEP.Port == 0 ||
                ConnectionLimitReached(remoteEP.Address))
                return;


            if (IPAddress.NetworkToHostOrder(BitConverter.ToInt16(buffer, 0)) == STUN_ID)
            {
                Array.Copy(
                    remoteEP.Address.GetAddressBytes(), 
                    SendBuffer, 
                    4);

                Array.Copy(
                    BitConverter.GetBytes(IPAddress.HostToNetworkOrder((short)remoteEP.Port)), 
                    0,
                    SendBuffer, 
                    4,
                    2);


                //obfuscate
                for (int i = 0; i < 6; i++)
                    SendBuffer[i] ^= 0x20;

                Client.Client.SendTo(SendBuffer, remoteEP);
            }
        }

        private bool ConnectionLimitReached(IPAddress address)
        {
            lock (_lock)
            {
                if (ConnectionCounter.Count >= MAX_CONNECTIONS_GLOBAL)
                    return true;

                int ipHash = address.GetHashCode();

                int count = 0;
                if (ConnectionCounter.TryGetValue(ipHash, out count) && count >= MAX_REQUESTS_PER_IP)
                    return true;

                ConnectionCounter[ipHash] = ++count;

                return false;
            }
        }

    }
}
