using System;
using System.Collections.Generic;
using System.Text;
using System.Net;
using System.Net.Sockets;
using System.Timers;
using System.Threading.Tasks;
using System.Linq;

namespace CnCNet.Net.Tunnel
{
    class TunnelV2
    {
        const int VERSION = 2;
        const int MASTER_ANNOUNCE_INTERVAL = 60 * 1000;
        const int MAX_PINGS_PER_IP = 20;
        const int MAX_PINGS_GLOBAL = 5000;
        const int MAX_REQUESTS_GLOBAL = 1000;

        public readonly int Port;
        public readonly int MaxClients;
        public readonly int IpLimit;
        public readonly bool NoMasterAnnounce;
        public readonly string Name;
        public readonly string MasterServerURL;
        public readonly string MasterPassword;
        public readonly string MaintenancePassword;

        UdpClient Client;
        Dictionary<short, TunnelClient> Mappings;
        Dictionary<int, int> ConnectionCounter;
        Task ReceiveTask;
        Timer HeartbeatTimer = new Timer(MASTER_ANNOUNCE_INTERVAL);
        object _lock = new object();
        Dictionary<int, int> PingCounter = new Dictionary<int, int>(MAX_PINGS_GLOBAL);
        HttpListener Listener = new HttpListener();
        bool MaintenanceModeEnabled;

        public TunnelV2(
            int port, int maxClients, string name, bool noMasterAnnounce, string masterPassword,
            string maintenancePassword, string masterServerURL, int ipLimit)
        {
            Port = port <= 1024 ? 50000 : port;
            Name = name.Length == 0 ? "Unnamed server" : name.Replace(";", "");
            MaxClients = maxClients < 2 ? 200 : maxClients;
            IpLimit = ipLimit < 1 ? 4 : ipLimit;
            NoMasterAnnounce = noMasterAnnounce;
            MasterServerURL = masterServerURL;
            MasterPassword = masterPassword;
            MaintenancePassword = maintenancePassword;
            Mappings = new Dictionary<short, TunnelClient>(maxClients);
            ConnectionCounter = new Dictionary<int, int>(maxClients);
            HeartbeatTimer.Elapsed += new ElapsedEventHandler((sender, e) => SendHeartbeat());
            ReceiveTask = new Task(() => SyncReceive(), TaskCreationOptions.LongRunning);
            Listener.IgnoreWriteExceptions = true;
            Listener.Prefixes.Add("http://*:" + Port.ToString() + "/");
        }
        
        public Task Start()
        {
            if (!ReceiveTask.Status.HasFlag(TaskStatus.Running))
            {
                HeartbeatTimer.Enabled = true;
                SendHeartbeat();
                ReceiveTask.Start();
                Listener.Start();
                Listener.BeginGetContext(new AsyncCallback(ListenerReceive), Listener);
            }
            return ReceiveTask;
        }

        private void SendHeartbeat()
        {
            int clients = 0;

            lock (_lock)
            {
                foreach (var mapping in Mappings.Where(x => x.Value.TimedOut).ToList())
                    Mappings.Remove(mapping.Key);

                clients = Mappings.Count;

                PingCounter.Clear();
            }

            lock (ConnectionCounter)
                ConnectionCounter.Clear();

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
                Console.WriteLine("[{0} UTC] Tunnel V2 Heartbeat: {1}", DateTime.UtcNow, ex.Message);
            }
        }

        private void ListenerReceive(IAsyncResult result)
        {
            HttpListener listener = (HttpListener)result.AsyncState;
            HttpListenerContext context = null;
            
            try
            {
                context = listener.EndGetContext(result);
            }
            finally
            {
                listener.BeginGetContext(new AsyncCallback(ListenerReceive), listener);
            }

            if (context == null)
                return;

            var response = context.Response;
            var request = context.Request;

            response.KeepAlive = false;

            if (!NewConnectionAllowed(request.RemoteEndPoint.Address))
            {
                response.StatusCode = 429; // TooManyRequests
                response.Close();
                return;
            }

            if (request.Url.AbsolutePath.StartsWith("/maintenance/"))
            {
                if (MaintenancePassword.Length > 0 && request.Url.AbsolutePath.Split('/')[2] == MaintenancePassword)
                {
                    MaintenanceModeEnabled = true;
                    response.Close();
                    return;
                }

                response.StatusCode = (int)HttpStatusCode.Unauthorized;
                response.Close();
            }
            else if (request.Url.AbsolutePath.Equals("/status"))
            {
                string status = "";

                lock (_lock)
                {
                    status = 
                        string.Format(
                            "{0} slots free.\n{1} slots in use.\n", MaxClients - Mappings.Count, Mappings.Count);
                }

                byte[] buf = Encoding.UTF8.GetBytes(status);

                response.ContentLength64 = buf.Length;
                response.OutputStream.Write(buf, 0, buf.Length);
                response.Close();
            }
            else if (request.Url.AbsolutePath.Equals("/request"))
            {
                if (MaintenanceModeEnabled)
                {
                    response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                    response.Close();
                    return;
                }

                int clients = 0;

                if (!int.TryParse(request.QueryString["clients"], out clients) || clients < 2 || clients > 8)
                {
                    response.StatusCode = (int)HttpStatusCode.BadRequest;
                    response.Close();
                    return;
                }

                var clientIds = new List<string>(clients);

                lock (_lock)
                {
                    if (Mappings.Count + clients <= MaxClients)
                    {
                        var rand = new Random();

                        while (clients > 0)
                        {
                            short clientId = (short)rand.Next(short.MinValue, short.MaxValue);

                            if (!Mappings.ContainsKey(clientId))
                            {
                                clients--;

                                var client = new TunnelClient();
                                client.SetLastReceiveTick();
                                Mappings.Add(clientId, client);

                                clientIds.Add(clientId.ToString());
                            }
                        }
                    }
                }

                if (clientIds.Count < 2)
                {
                    response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                    response.Close();
                    return;
                }


                string msg = string.Format("[{0}]", string.Join(",", clientIds));

                byte[] buffer = Encoding.UTF8.GetBytes(msg);

                response.ContentLength64 = buffer.Length;
                response.OutputStream.Write(buffer, 0, buffer.Length);
                response.Close();
            }
            else
            {
                response.StatusCode = (int)HttpStatusCode.BadRequest;
                response.Close();
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
                if (size >= 4)
                    OnReceive(buffer, size, (IPEndPoint)remoteEP);
            }
        }

        private void OnReceive(byte[] buffer, int size, IPEndPoint remoteEP)
        {
            short senderId = IPAddress.NetworkToHostOrder(BitConverter.ToInt16(buffer, 0));
            short receiverId = IPAddress.NetworkToHostOrder(BitConverter.ToInt16(buffer, 2));

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
                    if (sender.RemoteEP == null)
                    {
                        sender.RemoteEP = new IPEndPoint(remoteEP.Address, remoteEP.Port);
                    }
                    else if (!remoteEP.Equals(sender.RemoteEP))
                    {
                        return;
                    }

                    sender.SetLastReceiveTick();

                    TunnelClient receiver;
                    if (Mappings.TryGetValue(receiverId, out receiver) && 
                        receiver.RemoteEP != null && 
                        !receiver.RemoteEP.Equals(sender.RemoteEP))
                    {
                        Client.Client.SendTo(buffer, 0, size, SocketFlags.None, receiver.RemoteEP);
                    }
                }
            }
        }

        private bool NewConnectionAllowed(IPAddress address)
        {
            lock (ConnectionCounter)
            {
                if (ConnectionCounter.Count >= MAX_REQUESTS_GLOBAL)
                    return false;

                int ipHash = address.GetHashCode();

                int count = 0;
                if (ConnectionCounter.TryGetValue(ipHash, out count) && count >= IpLimit)
                    return false;

                ConnectionCounter[ipHash] = ++count;

                return true;
            }
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
    }
}
