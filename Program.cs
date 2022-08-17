using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using CnCNetServer;
using CommandLine;
using CommandLine.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

IHost? host = null;

try
{
    ParserResult<Options> result = Parser.Default.ParseArguments<Options>(args);

    Console.WriteLine(HelpText.AutoBuild(result, null, null));

    if (result.Errors.Any())
        return 1;

    Options options = ((Parsed<Options>)result).Value;

    host = Host.CreateDefaultBuilder(args)
        .UseWindowsService(o => o.ServiceName = "CnCNetServer")
        .ConfigureServices((_, services) =>
        {
            services
                .AddHostedService<CnCNetBackgroundService>()
                .AddSingleton(options)
                .AddSingleton<TunnelV3>()
                .AddSingleton<TunnelV2>()
                .AddTransient<PeerToPeerUtil>()
                .AddHttpClient(nameof(Tunnel))
                .ConfigureHttpClient((_, httpClient) =>
                {
                    httpClient.BaseAddress = new Uri(options.MasterServerUrl!);
                    httpClient.Timeout = TimeSpan.FromMilliseconds(10000);
                    httpClient.DefaultRequestVersion = HttpVersion.Version11;
                    httpClient.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher;
                })
                .ConfigurePrimaryHttpMessageHandler(_ => new SocketsHttpHandler
                {
                    AutomaticDecompression = DecompressionMethods.All,
                    SslOptions = new SslClientAuthenticationOptions
                    {
                        EnabledSslProtocols = SslProtocols.Tls13 | SslProtocols.Tls12
                    },
                    ConnectCallback = async (context, token) =>
                    {
                        AddressFamily addressFamily = options.AnnounceIpV6 ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork;
                        var socket = new Socket(addressFamily, SocketType.Stream, ProtocolType.Tcp)
                        {
                            NoDelay = true
                        };

                        try
                        {
                            await socket.ConnectAsync(context.DnsEndPoint, token).ConfigureAwait(false);
                            return new NetworkStream(socket, ownsSocket: true);
                        }
                        catch
                        {
                            socket.Dispose();
                            throw;
                        }
                    }
                });
        })
        .ConfigureLogging((_, loggingBuilder) => loggingBuilder.ConfigureLogging(options))
        .Build();

    await host.RunAsync().ConfigureAwait(false);
}
catch (Exception ex)
{
    ILogger logger = host!.Services.GetRequiredService<ILogger<Program>>();

    logger.LogExceptionDetails(ex);
}

return 0;