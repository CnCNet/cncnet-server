namespace CnCNetServer;

using System.CommandLine;
using System.CommandLine.NamingConventionBinder;
using System.CommandLine.Parsing;

internal static class RootCommandBuilder
{
    private static readonly string[] NameOptionAliases = { "--name", "--n" };
    private static readonly string[] MaxClientsOptionAliases = { "--maxclients", "--m" };
    private static readonly string[] AddressLimitOptionAliases = { "--iplimit", "--i" };
    private static readonly string[] TunnelPortOptionAliases = { "--tunnelport", "--p" };
#if EnableLegacyVersion
    private static readonly string[] TunnelV2PortOptionAliases = { "--tunnelv2port", "--p2" };
#endif
    private static readonly string[] AnnounceIpV6OptionAliases = { "--announceipv6", "--6" };
    private static readonly string[] AnnounceIpV4OptionAliases = { "--announceipv4", "--4" };
    private static readonly string[] MaxPacketSizeOptionAliases = { "--maxpacketsize", "--mps" };
    private static readonly string[] MaxPingsGlobalOptionAliases = { "--maxpingsglobal", "--mpg" };
    private static readonly string[] MaxPingsPerIpOptionAliases = { "--maxpingsperip", "--mpi" };
    private static readonly string[] MasterAnnounceIntervalOptionAliases = { "--masterannounceinterval", "--ai" };
    private static readonly string[] ClientTimeoutOptionAliases = { "--clienttimeout", "--c" };
    private static readonly string[] NoMasterAnnounceAliases = { "--nomasterannounce", "--nm" };
    private static readonly string[] MasterPasswordAliases = { "--masterpassword", "--masp" };
    private static readonly string[] MaintenancePasswordAliases = { "--maintenancepassword", "--maip" };
    private static readonly string[] MasterServerUrlAliases = { "--masterserverurl", "--mu" };
    private static readonly string[] NoPeerToPeerAliases = { "--nopeertopeer", "--np" };
    private static readonly string[] TunnelV3EnabledAliases = { "--tunnelv3enabled", "--3" };
#if EnableLegacyVersion
    private static readonly string[] TunnelV2EnabledAliases = { "--tunnelv2enabled", "--2" };
#endif
    private static readonly string[] ServerLogLevelAliases = { "--serverloglevel", "--sel" };
    private static readonly string[] SystemLogLevelAliases = { "--systemloglevel", "--syl" };
#if EnableLegacyVersion
    private static readonly string[] TunnelV2HttpsAliases = { "--tunnelv2https", "--h" };
#endif

    public static RootCommand Build()
    {
        var nameOption = new Option<string>(NameOptionAliases, "Name of the server") { IsRequired = true };
        var maxClientsOption = new Option<int>(MaxClientsOptionAliases, static () => 200, "Maximum clients allowed on the tunnel server");
        var addressLimitOption = new Option<int>(AddressLimitOptionAliases, static () => 8, "Maximum clients allowed per IP address");
        var tunnelPortOption = new Option<int>(TunnelPortOptionAliases, static () => 50001, "Port used for the V3 tunnel server");
#if EnableLegacyVersion
        var tunnelV2PortOption = new Option<int>(TunnelV2PortOptionAliases, static () => 50000, "Port used for the V2 tunnel server");
#endif
        var announceIpV6Option = new Option<bool>(AnnounceIpV6OptionAliases, static () => true, "Announce IPv6 address to master server");
        var announceIpV4Option = new Option<bool>(AnnounceIpV4OptionAliases, static () => true, "Announce IPv4 address to master server");
        var maxPacketSizeOption = new Option<int>(MaxPacketSizeOptionAliases, static () => 2048, "Maximum accepted packet size");
        var maxPingsGlobalOption = new Option<ushort>(MaxPingsGlobalOptionAliases, static () => 1024, "Maximum accepted ping requests globally");
        var maxPingsPerIpOption = new Option<ushort>(MaxPingsPerIpOptionAliases, static () => 20, "Maximum accepted ping requests per IP");
        var masterAnnounceIntervalOption = new Option<ushort>(MasterAnnounceIntervalOptionAliases, static () => 60, "Master server announce interval in seconds");
        var clientTimeoutOption = new Option<int>(ClientTimeoutOptionAliases, static () => 60, "Client timeout in seconds");

        nameOption.AddValidator(static result =>
        {
            if (result.GetValueOrDefault<string>()!.Any(static q => q is ';'))
                result.ErrorMessage = $"{nameof(ServiceOptions.Name)} cannot contain the character ;";
        });
        maxClientsOption.AddValidator(static result =>
        {
            const int minMaxClients = 2;

            if (result.GetValueOrDefault<int>() < minMaxClients)
                result.ErrorMessage = $"{nameof(ServiceOptions.MaxClients)} minimum is {minMaxClients}";
        });
        addressLimitOption.AddValidator(static result =>
        {
            const int minIpLimit = 1;

            if (result.GetValueOrDefault<int>() < minIpLimit)
                result.ErrorMessage = $"{nameof(ServiceOptions.IpLimit)} minimum is {minIpLimit}";
        });
        maxPacketSizeOption.AddValidator(static result =>
        {
            const int maxPacketSizeLimit = 512;

            if (result.GetValueOrDefault<int>() < maxPacketSizeLimit)
                result.ErrorMessage = $"{nameof(ServiceOptions.MaxPacketSize)} minimum is {maxPacketSizeLimit}";
        });
        clientTimeoutOption.AddValidator(static result =>
        {
            const int minClientTimeout = 30;

            if (result.GetValueOrDefault<int>() < minClientTimeout)
                result.ErrorMessage = $"{nameof(ServiceOptions.ClientTimeout)} minimum is {minClientTimeout}";
        });
        tunnelPortOption.AddValidator(ValidatePort);
#if EnableLegacyVersion
        tunnelV2PortOption.AddValidator(ValidatePort);
#endif
        announceIpV6Option.AddValidator(static result => ValidateIpAnnounce(result, Socket.OSSupportsIPv6));
        announceIpV4Option.AddValidator(static result => ValidateIpAnnounce(result, Socket.OSSupportsIPv4));

        var rootCommand = new RootCommand("CnCNet tunnel server")
        {
            nameOption,
            tunnelPortOption,
#if EnableLegacyVersion
            tunnelV2PortOption,
#endif
            maxClientsOption,
            new Option<bool>(NoMasterAnnounceAliases, static () => false, "Don't register to master"),
            new Option<string?>(MasterPasswordAliases, static () => null, "Master password"),
            new Option<string?>(MaintenancePasswordAliases, static () => null, "Maintenance password"),
            new Option<Uri>(MasterServerUrlAliases, static () => new($"{Uri.UriSchemeHttps}://cncnet.org/api/v1/master-announce"), "Master server URL"),
            addressLimitOption,
            new Option<bool>(NoPeerToPeerAliases, static () => false, "Disable STUN NAT traversal server (UDP 8054 & 3478)"),
            new Option<bool>(TunnelV3EnabledAliases, static () => true, "Start a V3 tunnel server"),
#if EnableLegacyVersion
            new Option<bool>(TunnelV2EnabledAliases, static () => true, "Start a V2 tunnel server"),
#endif
            new Option<LogLevel>(ServerLogLevelAliases, static () => LogLevel.Warning, "CnCNet server messages log level"),
            new Option<LogLevel>(SystemLogLevelAliases, static () => LogLevel.Warning, "Low level system messages log level"),
            announceIpV6Option,
            announceIpV4Option,
#if EnableLegacyVersion
            new Option<bool>(TunnelV2HttpsAliases, static () => false, $"Use {Uri.UriSchemeHttps} Tunnel V2 web server"),
#endif
            maxPacketSizeOption,
            maxPingsGlobalOption,
            maxPingsPerIpOption,
            masterAnnounceIntervalOption,
            clientTimeoutOption
        };

        rootCommand.Handler = CommandHandler.Create<IHost>(static host => host.WaitForShutdownAsync());

        return rootCommand;
    }

    private static void ValidatePort(OptionResult result)
    {
        const int minPort = 1024;
        const int maxPort = 65534;

        if (result.GetValueOrDefault<int>() is < minPort or > maxPort)
            result.ErrorMessage = $"{result.Option.Name} minimum is {minPort} and maximum is {maxPort}";
    }

    private static void ValidateIpAnnounce(OptionResult result, bool isSupported)
    {
        if (result.GetValueOrDefault<bool>() && !isSupported)
            result.ErrorMessage = $"{result.Option.Name} is not supported on this system";
    }
}