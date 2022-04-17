using System.Net;
using CnCNetServer;
using CommandLine;
using CommandLine.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

IHost host = Host.CreateDefaultBuilder(args)
    .UseWindowsService(o => o.ServiceName = "CnCNetServer")
    .ConfigureServices((__, services) =>
    {
        ParserResult<Options> result = Parser.Default.ParseArguments<Options>(args);

        Console.WriteLine(HelpText.AutoBuild(result, null, null));

        Options options = ((Parsed<Options>)result).Value;

        _ = services.AddSingleton(options);
        _ = services.AddSingleton<TunnelV3>();
        _ = services.AddSingleton<TunnelV2>();
        _ = services.AddTransient<PeerToPeerUtil>();
        _ = services.AddHttpClient(nameof(Tunnel))
                .ConfigureHttpClient((_, httpClient) =>
                {
                    httpClient.BaseAddress = new Uri(options.MasterServerUrl);
                    httpClient.Timeout = TimeSpan.FromMilliseconds(10000);
                })
                .ConfigurePrimaryHttpMessageHandler(_ => new HttpClientHandler
                {
                    AutomaticDecompression = DecompressionMethods.All
                });
        _ = services.AddHostedService<CnCNetBackgroundService>();
    })
    .Build();

await host.RunAsync().ConfigureAwait(false);