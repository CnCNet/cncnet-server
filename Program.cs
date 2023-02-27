using System.CommandLine.Builder;
using System.CommandLine.Hosting;
using System.CommandLine.Parsing;
using CnCNetServer;
using Microsoft.Extensions.DependencyInjection;

return await new CommandLineBuilder(RootCommandBuilder.Build())
    .UseDefaults()
    .UseHost(Host.CreateDefaultBuilder, static hostBuilder =>
        hostBuilder
            .UseWindowsService(static o => o.ServiceName = "CnCNetServer")
            .UseSystemd()
            .ConfigureServices(static services =>
            {
                services.AddOptions<ServiceOptions>().BindCommandLine();
                services
                    .AddHostedService<CnCNetBackgroundService>()
                    .AddSingleton<TunnelV3>()
#if EnableLegacyVersion
                    .AddSingleton<TunnelV2>()
#endif
                    .AddTransient<PeerToPeerUtil>()
                    .AddHttpClient(Options.DefaultName)
                    .ConfigureHttpClient(Startup.ConfigureHttpClient)
                    .ConfigurePrimaryHttpMessageHandler(Startup.ConfigurePrimaryHttpMessageHandler);
            })
            .ConfigureLogging(Startup.ConfigureLogging))
    .Build()
    .InvokeAsync(args)
    .ConfigureAwait(false);