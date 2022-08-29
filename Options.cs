namespace CnCNetServer;

using CommandLine;
using Microsoft.Extensions.Logging;

internal sealed record Options
{
    public Options(int tunnelPort, int tunnelV2Port, string name, int maxClients, bool noMasterAnnounce, string? masterPassword, string? maintenancePassword, string masterServerUrl, int ipLimit, bool noPeerToPeer, bool tunnelV3Enabled, bool tunnelV2Enabled, LogLevel serverLogLevel, LogLevel systemLogLevel, bool announceIpV6)
    {
        TunnelPort = tunnelPort;
        TunnelV2Port = tunnelV2Port;
        Name = name;
        MaxClients = maxClients;
        NoMasterAnnounce = noMasterAnnounce;
        MasterPassword = masterPassword;
        MaintenancePassword = maintenancePassword;
        MasterServerUrl = masterServerUrl;
        IpLimit = ipLimit;
        NoPeerToPeer = noPeerToPeer;
        TunnelV3Enabled = tunnelV3Enabled;
        TunnelV2Enabled = tunnelV2Enabled;
        ServerLogLevel = serverLogLevel;
        SystemLogLevel = systemLogLevel;
        AnnounceIpV6 = announceIpV6;
    }

    [Option("port", Default = 50001, HelpText = "Port used for the V3 tunnel server")]
    public int TunnelPort { get; }

    [Option("portv2", Default = 50000, HelpText = "Port used for the V2 tunnel server")]
    public int TunnelV2Port { get; }

    [Option("name", Default = "Unnamed server", HelpText = "Name of the server")]
    public string Name { get; }

    [Option("maxclients", Default = 200, HelpText = "Maximum clients allowed on the tunnel server")]
    public int MaxClients { get; }

    [Option("nomaster", Default = false, HelpText = "Don't register to master")]
    public bool NoMasterAnnounce { get; }

    [Option("masterpw", Default = "", HelpText = "Master password")]
    public string? MasterPassword { get; }

    [Option("maintpw", Default = "", HelpText = "Maintenance password")]
    public string? MaintenancePassword { get; }

    [Option("master", Default = "http://cncnet.org/master-announce", HelpText = "Master server URL")]
    public string MasterServerUrl { get; }

    [Option("iplimit", Default = 8, HelpText = "Maximum clients allowed per IP address")]
    public int IpLimit { get; }

    [Option("nop2p", Default = false, HelpText = "Disable NAT traversal ports (8054, 3478 UDP)")]
    public bool NoPeerToPeer { get; }

    [Option("tunnelv3", Default = true, HelpText = "Start a V3 tunnel server")]
    public bool TunnelV3Enabled { get; }

    [Option("tunnelv2", Default = true, HelpText = "Start a V2 tunnel server")]
    public bool TunnelV2Enabled { get; }

    [Option("loglevel", Default = LogLevel.Information, HelpText = "CnCNet server messages log level")]
    public LogLevel ServerLogLevel { get; }

    [Option("systemloglevel", Default = LogLevel.Warning, HelpText = "Low level system messages log level")]
    public LogLevel SystemLogLevel { get; }

    [Option("announceipv6", Default = false, HelpText = "Announce IPv6 address instead of IPv4 address to master server")]
    public bool AnnounceIpV6 { get; }
}