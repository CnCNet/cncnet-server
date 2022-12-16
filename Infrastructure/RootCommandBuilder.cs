namespace CnCNetServer;

using System.CommandLine;
using System.CommandLine.NamingConventionBinder;
using System.CommandLine.Parsing;

internal static class RootCommandBuilder
{
    public static RootCommand Build()
    {
        var nameOption = new Option<string>(new[] { "--name", "--n" }, "Name of the server") { IsRequired = true };
        var maxClientsOption = new Option<int>(new[] { "--maxclients", "--m" }, () => 200, "Maximum clients allowed on the tunnel server");
        var ipLimitOption = new Option<int>(new[] { "--iplimit", "--i" }, () => 8, "Maximum clients allowed per IP address");
        var tunnelPortOption = new Option<int>(new[] { "--tunnelport", "--p" }, () => 50001, "Port used for the V3 tunnel server");
        var tunnelV2PortOption = new Option<int>(new[] { "--tunnelv2port", "--p2" }, () => 50000, "Port used for the V2 tunnel server");
        var announceIpV6Option = new Option<bool>(new[] { "--announceipv6", "--6" }, () => true, "Announce IPv6 address to master server");
        var announceIpV4Option = new Option<bool>(new[] { "--announceipv4", "--4" }, () => true, "Announce IPv4 address to master server");
        var maxPacketSizeOption = new Option<int>(new[] { "--maxpacketsize", "--mps" }, () => 2048, "Maximum accepted packet size");
        var maxPingsGlobalOption = new Option<ushort>(new[] { "--maxpingsglobal", "--mpg" }, () => 1024, "Maximum accepted ping requests globally");
        var maxPingsPerIpOption = new Option<ushort>(new[] { "--maxpingsperip", "--mpi" }, () => 20, "Maximum accepted ping requests per IP");
        var masterAnnounceIntervalOption = new Option<ushort>(new[] { "--masterannounceinterval", "--ai" }, () => 60, "Master server announce interval in seconds");
        var clientTimeoutOption = new Option<int>(new[] { "--clienttimeout", "--c" }, () => 60, "Client timeout in seconds");

        nameOption.AddValidator(result =>
        {
            if (result.GetValueOrDefault<string>()!.Any(q => q == ';'))
                result.ErrorMessage = $"{nameof(ServiceOptions.Name)} cannot contain the character ;";
        });
        maxClientsOption.AddValidator(result =>
        {
            const int minMaxClients = 2;

            if (result.GetValueOrDefault<int>() < minMaxClients)
                result.ErrorMessage = $"{nameof(ServiceOptions.MaxClients)} minimum is {minMaxClients}";
        });
        ipLimitOption.AddValidator(result =>
        {
            const int minIpLimit = 1;

            if (result.GetValueOrDefault<int>() < minIpLimit)
                result.ErrorMessage = $"{nameof(ServiceOptions.IpLimit)} minimum is {minIpLimit}";
        });
        maxPacketSizeOption.AddValidator(result =>
        {
            const int maxPacketSizeLimit = 512;

            if (result.GetValueOrDefault<int>() < maxPacketSizeLimit)
                result.ErrorMessage = $"{nameof(ServiceOptions.MaxPacketSize)} minimum is {maxPacketSizeLimit}";
        });
        clientTimeoutOption.AddValidator(result =>
        {
            const int minClientTimeout = 30;

            if (result.GetValueOrDefault<int>() < minClientTimeout)
                result.ErrorMessage = $"{nameof(ServiceOptions.ClientTimeout)} minimum is {minClientTimeout}";
        });
        tunnelPortOption.AddValidator(ValidatePort);
        tunnelV2PortOption.AddValidator(ValidatePort);
        announceIpV6Option.AddValidator(result => ValidateIpAnnounce(result, Socket.OSSupportsIPv6));
        announceIpV4Option.AddValidator(result => ValidateIpAnnounce(result, Socket.OSSupportsIPv4));

        var rootCommand = new RootCommand("CnCNet tunnel server")
        {
            nameOption,
            tunnelPortOption,
            tunnelV2PortOption,
            maxClientsOption,
            new Option<bool>(new[] { "--nomasterannounce", "--nm" }, () => false, "Don't register to master"),
            new Option<string?>(new[] { "--masterpassword", "--masp" }, () => null, "Master password"),
            new Option<string?>(new[] { "--maintenancepassword", "--maip" }, () => null, "Maintenance password"),
            new Option<Uri>(new[] { "--masterserverurl", "--mu" }, () => new($"{Uri.UriSchemeHttps}://cncnet.org/master-announce"), "Master server URL"),
            ipLimitOption,
            new Option<bool>(new[] { "--nopeertopeer", "--np" }, () => false, "Disable STUN NAT traversal server (UDP 8054 & 3478)"),
            new Option<bool>(new[] { "--tunnelv3enabled", "--3" }, () => true, "Start a V3 tunnel server"),
            new Option<bool>(new[] { "--tunnelv2enabled", "--2" }, () => true, "Start a V2 tunnel server"),
            new Option<LogLevel>(new[] { "--serverloglevel", "--sel" }, () => LogLevel.Warning, "CnCNet server messages log level"),
            new Option<LogLevel>(new[] { "--systemloglevel", "--syl" }, () => LogLevel.Warning, "Low level system messages log level"),
            announceIpV6Option,
            announceIpV4Option,
            new Option<bool>(new[] { "--tunnelv2https", "--h" }, () => false, $"Use {Uri.UriSchemeHttps} Tunnel V2 web server"),
            maxPacketSizeOption,
            maxPingsGlobalOption,
            maxPingsPerIpOption,
            masterAnnounceIntervalOption,
            clientTimeoutOption
        };

        rootCommand.Handler = CommandHandler.Create<IHost>(host => host.WaitForShutdownAsync());

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