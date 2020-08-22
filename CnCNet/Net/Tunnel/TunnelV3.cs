using System;
using System.Collections.Generic;
using System.Text;
using System.Net;
using System.Net.Sockets;
using System.Timers;
using System.Linq;
using System.Web;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Security.Cryptography;

namespace CnCNet.Net.Tunnel
{
    class TunnelV3
    {
        const int VERSION = 3;
        const int MASTER_ANNOUNCE_INTERVAL = 60 * 1000;
        const int COMMAND_RATE_LIMIT = 60; //1 per X seconds
        const int MAX_PINGS_PER_IP = 20;
        const int MAX_PINGS_GLOBAL = 5000;

        enum TunnelCommand : byte { MaintenanceMode };

        public readonly int Port;
        public readonly int MaxClients;
        public readonly int IpLimit;
        public readonly bool NoMasterAnnounce;
        public readonly string Name;
        public readonly string MasterServerURL;
        public readonly string MasterPassword;
        public readonly string MaintenancePassword;

        byte[] MaintenancePasswordSha1;
        UdpClient Client;
        Dictionary<uint, TunnelClient> Mappings;
        Dictionary<int, int> ConnectionCounter;
        Task ReceiveTask;
        Timer HeartbeatTimer = new Timer(MASTER_ANNOUNCE_INTERVAL);
        object _lock = new object();
        List<uint> ExpiredMappings = new List<uint>(25);
        Dictionary<int, int> PingCounter = new Dictionary<int, int>(MAX_PINGS_GLOBAL);
        bool MaintenanceModeEnabled;
        long LastCommandTick;

        public TunnelV3(
            int port, int maxClients, string name, bool noMasterAnnounce, string masterPassword,
            string maintenancePassword, string masterServerURL, int ipLimit)
        {
            Port = port <= 1024 ? 50001 : port;
            Name = name.Length == 0 ? "Unnamed server" : name.Replace(";", "");
            MaxClients = maxClients < 2 ? 200 : maxClients;
            IpLimit = ipLimit < 1 ? 8 : ipLimit;
            NoMasterAnnounce = noMasterAnnounce;
            MasterServerURL = masterServerURL;
            MasterPassword = masterPassword;
            MaintenancePassword = maintenancePassword;
            if (maintenancePassword.Length > 0)
                using (var sha1 = SHA1CryptoServiceProvider.Create())
                    MaintenancePasswordSha1 = sha1.ComputeHash(Encoding.UTF8.GetBytes(maintenancePassword));

            Mappings = new Dictionary<uint, TunnelClient>(maxClients);
            ConnectionCounter = new Dictionary<int, int>(maxClients);
            LastCommandTick = DateTime.UtcNow.Ticks;
            HeartbeatTimer.Elapsed += new ElapsedEventHandler((sender, e) => SendHeartbeat());
            ReceiveTask = new Task(() => SyncReceive(), TaskCreationOptions.LongRunning);
        }

        public Task Start()
        {
            if (!ReceiveTask.Status.HasFlag(TaskStatus.Running))
            {
                HeartbeatTimer.Enabled = true;
                SendHeartbeat();
                ReceiveTask.Start();
            }
            return ReceiveTask;
        }

        private void SendHeartbeat()
        {
            int clients = 0;

            lock (_lock)
            {
                ExpiredMappings.Clear();
                foreach (var mapping in Mappings)
                {
                    if (mapping.Value.TimedOut)
                    {
                        ExpiredMappings.Add(mapping.Key);

                        int ipHash = mapping.Value.RemoteEP.Address.GetHashCode();
                        if (--ConnectionCounter[ipHash] <= 0)
                            ConnectionCounter.Remove(ipHash);
                    }
                }

                foreach (var mapping in ExpiredMappings)
                    Mappings.Remove(mapping);

                clients = Mappings.Count;

                PingCounter.Clear();
            }

            if (NoMasterAnnounce)
                return;

            try
            {
                Uri uri = new Uri(
                    string.Format(
                        "{0}?version={1}&name={2}&port={3}&clients={4}&maxclients={5}&masterpw={6}&maintenance={7}",
                        MasterServerURL, 
                        VERSION, 
                        Uri.EscapeDataString(Name), 
                        Port, 
                        clients, 
                        MaxClients, 
                        Uri.EscapeDataString(MasterPassword),
                        MaintenanceModeEnabled ? "1" : "0"));

                string ipv4 = Array.Find(
                        Dns.GetHostAddresses(uri.DnsSafeHost), 
                        x => x.AddressFamily == AddressFamily.InterNetwork).ToString();

                var request = (HttpWebRequest)WebRequest.Create(new UriBuilder(uri) { Host = ipv4 }.Uri);
                request.Timeout = 10000;
                request.Host = uri.Host;
                request.GetResponse().Close();
            }
            catch (Exception ex)
            {
                Console.WriteLine("[{0} UTC] Tunnel Heartbeat: {1}", DateTime.UtcNow, ex.Message);
            }
        }

        private void SyncReceive()
        {
            Client = new UdpClient(Port);
            try { Client.Client.IOControl(-1744830452, new byte[] { 0, 0, 0, 0 }, null); } // SIO_UDP_CONNRESET
            catch { } // Fails on mono

            byte[] buffer = new byte[1024];
            EndPoint remoteEP = (EndPoint)new IPEndPoint(IPAddress.Any, 0);

            while (true)
            {
                int size = Client.Client.ReceiveFrom(buffer, ref remoteEP);
                if (size >= 8)
                    OnReceive(buffer, size, (IPEndPoint)remoteEP);
            }
        }

        private void OnReceive(byte[] buffer, int size, IPEndPoint remoteEP)
        {
            uint senderId = BitConverter.ToUInt32(buffer, 0);
            uint receiverId = BitConverter.ToUInt32(buffer, 4);

            if (senderId == 0)
            {
                if (receiverId == uint.MaxValue && size >= 8 + 1 + 20) // 8=receiver+sender ids, 1=command, 20=sha1 pass
                    ExecuteCommand((TunnelCommand)buffer[8], buffer, size);

                if (receiverId != 0)
                    return;
            }

            if ((senderId == receiverId && senderId != 0) || 
                remoteEP.Address.Equals(IPAddress.Loopback) ||
                remoteEP.Address.Equals(IPAddress.Any) ||
                remoteEP.Address.Equals(IPAddress.Broadcast) ||
                remoteEP.Port == 0)
                return;

            lock (_lock)
            {
                if (senderId == 0 && receiverId == 0)
                {
                    if (size == 50 && !PingLimitReached(remoteEP.Address))
                    {
                        Client.Client.SendTo(buffer, 0, 12, SocketFlags.None, remoteEP);
                    }

                    return;
                }

                TunnelClient sender;
                if (Mappings.TryGetValue(senderId, out sender))
                {
                    if (!remoteEP.Equals(sender.RemoteEP))
                    {
                        if (sender.TimedOut && !MaintenanceModeEnabled && 
                            NewConnectionAllowed(remoteEP.Address, sender.RemoteEP.Address))
                            sender.RemoteEP = new IPEndPoint(remoteEP.Address, remoteEP.Port);
                        else
                            return;
                    }

                    sender.SetLastReceiveTick();
                }
                else
                {
                    if (Mappings.Count >= MaxClients || MaintenanceModeEnabled || !NewConnectionAllowed(remoteEP.Address))
                        return;

                    sender = new TunnelClient();
                    sender.RemoteEP = new IPEndPoint(remoteEP.Address, remoteEP.Port);
                    sender.SetLastReceiveTick();

                    Mappings.Add(senderId, sender);
                }

                TunnelClient receiver;
                if (Mappings.TryGetValue(receiverId, out receiver) && !receiver.RemoteEP.Equals(sender.RemoteEP))
                    Client.Client.SendTo(buffer, 0, size, SocketFlags.None, receiver.RemoteEP);
            }
        }

        private bool NewConnectionAllowed(IPAddress newIP, IPAddress oldIP = null)
        {
            int ipHash = newIP.GetHashCode();

            int count = 0;
            if (ConnectionCounter.TryGetValue(ipHash, out count) && count >= IpLimit)
                return false;

            if (oldIP == null)
            {
                ConnectionCounter[ipHash] = ++count;
            }
            else if (!newIP.Equals(oldIP))
            {
                ConnectionCounter[ipHash] = ++count;

                int oldIpHash = oldIP.GetHashCode();
                if (--ConnectionCounter[oldIpHash] <= 0)
                    ConnectionCounter.Remove(oldIpHash);
            }

            return true;
        }

        private bool PingLimitReached(IPAddress address)
        {
            if (PingCounter.Count >= MAX_PINGS_GLOBAL)
                return true;

            int ipHash = address.GetHashCode();

            int count = 0;
            if (PingCounter.TryGetValue(ipHash, out count) && count >= MAX_PINGS_PER_IP)
                return true;

            PingCounter[ipHash] = ++count;

            return false;
        }

        private void ExecuteCommand(TunnelCommand command, byte[] data, int size)
        {
            if (TimeSpan.FromTicks(DateTime.UtcNow.Ticks - LastCommandTick).TotalSeconds < COMMAND_RATE_LIMIT || 
                MaintenancePassword.Length == 0)
                return;

            LastCommandTick = DateTime.UtcNow.Ticks;

            byte[] commandPasswordSha1 = new byte[20];
            Array.Copy(data, 9, commandPasswordSha1, 0, 20);

            if (!commandPasswordSha1.SequenceEqual(MaintenancePasswordSha1))
                return;
            
            switch (command)
            {
                case TunnelCommand.MaintenanceMode:
                    MaintenanceModeEnabled = !MaintenanceModeEnabled;
                    break;
            }
        }


    }
}
