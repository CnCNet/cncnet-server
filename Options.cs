namespace CnCNetServer;

using CommandLine;
using Microsoft.Extensions.Logging;

internal sealed record Options
{
    [Option("port", Default = 50001, HelpText = "Port used for the V3 tunnel server")]
    public int TunnelPort { get; set; }

    [Option("portv2", Default = 50000, HelpText = "Port used for the V2 tunnel server")]
    public int TunnelV2Port { get; set; }

    [Option("name", Default = "Unnamed server", HelpText = "Name of the server")]
    public string? Name { get; set; }

    [Option("maxclients", Default = 200, HelpText = "Maximum clients allowed on the tunnel server")]
    public int MaxClients { get; set; }

    [Option("nomaster", Default = false, HelpText = "Don't register to master")]
    public bool NoMasterAnnounce { get; set; }

    [Option("masterpw", Default = "", HelpText = "Master password")]
    public string? MasterPassword { get; set; }

    [Option("maintpw", Default = "", HelpText = "Maintenance password")]
    public string? MaintenancePassword { get; set; }

    [Option("master", Default = "http://cncnet.org/master-announce", HelpText = "Master server URL")]
    public string? MasterServerUrl { get; set; }

    [Option("iplimit", Default = 8, HelpText = "Maximum clients allowed per IP address")]
    public int IpLimit { get; set; }

    [Option("nop2p", Default = false, HelpText = "Disable NAT traversal ports (8054, 3478 UDP)")]
    public bool NoPeerToPeer { get; set; }

    [Option("tunnelv3", Default = true, HelpText = "Start a V3 tunnel server")]
    public bool TunnelV3Enabled { get; set; }

    [Option("tunnelv2", Default = true, HelpText = "Start a V2 tunnel server")]
    public bool TunnelV2Enabled { get; set; }

    [Option("loglevel", Default = LogLevel.Information, HelpText = "CnCNet server messages log level")]
    public LogLevel ServerLogLevel { get; set; }

    [Option("systemloglevel", Default = LogLevel.Warning, HelpText = "Low level system messages log level")]
    public LogLevel SystemLogLevel { get; set; }
}