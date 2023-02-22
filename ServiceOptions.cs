namespace CnCNetServer;

internal sealed record ServiceOptions
{
    public int TunnelPort { get; set; }
#if EnableLegacyVersion

    public int TunnelV2Port { get; set; }
#endif

    public string? Name { get; set; }

    public int MaxClients { get; set; }

    public bool NoMasterAnnounce { get; set; }

    public string? MasterPassword { get; set; }

    public string? MaintenancePassword { get; set; }

    public Uri? MasterServerUrl { get; set; }

    public int IpLimit { get; set; }

    public bool NoPeerToPeer { get; set; }

    public bool TunnelV3Enabled { get; set; }
#if EnableLegacyVersion

    public bool TunnelV2Enabled { get; set; }
#endif

    public LogLevel ServerLogLevel { get; set; }

    public LogLevel SystemLogLevel { get; set; }

    public bool AnnounceIpV6 { get; set; }

    public bool AnnounceIpV4 { get; set; }
#if EnableLegacyVersion

    public bool TunnelV2Https { get; set; }
#endif

    public int MaxPacketSize { get; set; }

    public ushort MaxPingsGlobal { get; set; }

    public ushort MaxPingsPerIp { get; set; }

    public ushort MasterAnnounceInterval { get; set; }

    public int ClientTimeout { get; set; }
}