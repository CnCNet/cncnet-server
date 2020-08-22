using CommandLine;
using CommandLine.Text;
using System.Collections.Generic;
using System.ComponentModel;

namespace CnCNetServer
{
    class Options
    {
        [Option("port", Default = 50001, HelpText = "Port used for the tunnel server")]
        public int TunnelPort { get; set; }

        [Option("portv2", Default = 50000, HelpText = "Port used for the V2 tunnel server")]
        public int TunnelV2Port { get; set; }

        [Option("name", Default = "Unnamed server", HelpText = "Name of the server")]
        public string Name { get; set; }

        [Option("maxclients", Default = 200, HelpText = "Maximum clients allowed on the tunnel server")]
        public int MaxClients { get; set; }

        [Option("nomaster", Default = false, HelpText = "Don't register to master")]
        public bool NoMasterAnnounce { get; set; }

        [Option("masterpw", Default = "", HelpText = "Master password")]
        public string MasterPassword { get; set; }

        [Option("maintpw", Default = "", HelpText = "Maintenance password")]
        public string MaintenancePassword { get; set; }

        [Option("master", Default = "http://cncnet.org/master-announce", HelpText = "Master server URL")]
        public string MasterServerURL { get; set; }

        [Option("iplimit", Default = 8, HelpText = "Maximum clients allowed per IP address")]
        public int IpLimit { get; set; }

        [Option("iplimitv2", Default = 4, HelpText = "Max game request allowed per IP address on V2 tunnel")]
        public int IpLimitV2 { get; set; }

        [Option("nop2p", Default = false, HelpText = "Disable NAT traversal ports (8054, 3478 UDP)")]
        public bool NoPeerToPeer { get; set; }
    }
}
