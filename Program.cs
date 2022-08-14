using System.Net;
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

    Options options = ((Parsed<Options>)result).Value;

    host = Host.CreateDefaultBuilder(args)
        .UseWindowsService(o => o.ServiceName = "CnCNetServer")
        .ConfigureServices((__, services) =>
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
                })
                .ConfigurePrimaryHttpMessageHandler(_ => new HttpClientHandler
                {
                    AutomaticDecompression = DecompressionMethods.All
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