using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net;
using System.Net.Sockets;
using System.Collections;
using System.Threading;
using System.IO;
using CommandLine;
using CommandLine.Text;
using System.Diagnostics;
using CnCNet.Net.Tunnel;
using CnCNet.Net.PeerToPeer;

namespace CnCNetServer
{
    class Program
    {
        static void Main(string[] args)
        {
            var result = Parser.Default.ParseArguments<Options>(args);

            Console.WriteLine(HelpText.AutoBuild(result, null, null));

            Options o = ((Parsed<Options>)result).Value;

            if (!o.NoPeerToPeer)
            {
                PeerToPeerUtil.StartNew(8054);
                PeerToPeerUtil.StartNew(3478);
            }

            var tunnelV3Task = 
                new TunnelV3(
                    o.TunnelPort, 
                    o.MaxClients, 
                    o.Name, 
                    o.NoMasterAnnounce,
                    o.MasterPassword, 
                    o.MaintenancePassword, 
                    o.MasterServerURL, 
                    o.IpLimit)
                .Start();

            var tunnelV2Task = 
                new TunnelV2(
                    o.TunnelV2Port, 
                    o.MaxClients, 
                    o.Name, 
                    o.NoMasterAnnounce,
                    o.MasterPassword, 
                    o.MaintenancePassword, 
                    o.MasterServerURL, 
                    o.IpLimitV2)
                .Start();

            Console.WriteLine("[{0} UTC] CnCNet server running...", DateTime.UtcNow);

            tunnelV3Task.Wait();
        }
    }
}
