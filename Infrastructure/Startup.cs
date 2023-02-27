namespace CnCNetServer;

using System.CommandLine;
using System.CommandLine.Hosting;
using System.CommandLine.Invocation;
using Microsoft.Extensions.DependencyInjection;

internal static class Startup
{
    public static HttpMessageHandler ConfigurePrimaryHttpMessageHandler(IServiceProvider serviceProvider)
        => new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectCallback = async (context, token) =>
            {
                Socket? socket = null;

                try
                {
                    socket = serviceProvider.GetRequiredService<IOptions<ServiceOptions>>().Value.AnnounceIpV4
                        ? new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
                        : new(SocketType.Stream, ProtocolType.Tcp);

                    socket.NoDelay = true;

                    await socket.ConnectAsync(context.DnsEndPoint, token).ConfigureAwait(false);

                    return new NetworkStream(socket, true);
                }
                catch
                {
                    socket?.Dispose();

                    throw;
                }
            }
        };

    public static void ConfigureHttpClient(IServiceProvider serviceProvider, HttpClient httpClient)
    {
        httpClient.BaseAddress = serviceProvider.GetRequiredService<IOptions<ServiceOptions>>().Value.MasterServerUrl;
        httpClient.Timeout = TimeSpan.FromMilliseconds(10000);
        httpClient.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher;
    }

    public static void ConfigureLogging(HostBuilderContext context, ILoggingBuilder builder)
    {
        InvocationContext invocationContext = context.GetInvocationContext();
        IReadOnlyList<Option> options = invocationContext.ParseResult.RootCommandResult.Command.Options;
        Option serverLogLevelOption = options.Single(static q => q.Name.Equals(nameof(ServiceOptions.ServerLogLevel), StringComparison.OrdinalIgnoreCase));
        Option systemLogLevelOption = options.Single(static q => q.Name.Equals(nameof(ServiceOptions.SystemLogLevel), StringComparison.OrdinalIgnoreCase));
        var serverLogLevel = (LogLevel)invocationContext.ParseResult.GetValueForOption(serverLogLevelOption)!;
        var systemLogLevel = (LogLevel)invocationContext.ParseResult.GetValueForOption(systemLogLevelOption)!;

        builder.ConfigureLogging(serverLogLevel, systemLogLevel);
    }
}