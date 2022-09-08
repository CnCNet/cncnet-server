using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Hosting;
using System.CommandLine.Parsing;
using CnCNetServer;
using Microsoft.Extensions.DependencyInjection;

return await new CommandLineBuilder(RootCommandBuilder.Build())
    .UseDefaults()
    .UseHost(Host.CreateDefaultBuilder, hostBuilder =>
    {
        hostBuilder
            .UseWindowsService(o => o.ServiceName = "CnCNetServer")
            .UseSystemd()
            .ConfigureServices(services =>
            {
                services.AddOptions<ServiceOptions>().BindCommandLine();
                services
                    .AddHostedService<CnCNetBackgroundService>()
                    .AddSingleton<TunnelV3>()
                    .AddSingleton<TunnelV2>()
                    .AddTransient<PeerToPeerUtil>()
                    .AddHttpClient(nameof(Tunnel))
                    .ConfigureHttpClient((serviceProvider, httpClient) =>
                    {
                        ServiceOptions options = serviceProvider.GetRequiredService<IOptions<ServiceOptions>>().Value;

                        httpClient.BaseAddress = options.MasterServerUrl;
                        httpClient.Timeout = TimeSpan.FromMilliseconds(10000);
                        httpClient.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher;
                    })
                    .ConfigurePrimaryHttpMessageHandler(serviceProvider =>
                    {
                        ServiceOptions options = serviceProvider.GetRequiredService<IOptions<ServiceOptions>>().Value;

                        return new SocketsHttpHandler
                        {
                            AutomaticDecompression = DecompressionMethods.All,
                            ConnectCallback = async (context, token) =>
                            {
                                Socket? socket = null;

                                try
                                {
                                    socket = options.ForceIpV4Announce
                                        ? new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
                                        : new(SocketType.Stream, ProtocolType.Tcp);

                                    socket.NoDelay = true;

                                    await socket.ConnectAsync(context.DnsEndPoint, token).ConfigureAwait(false);
                                    return new NetworkStream(socket, ownsSocket: true);
                                }
                                catch
                                {
                                    socket?.Dispose();
                                    throw;
                                }
                            }
                        };
                    });
            })
            .ConfigureLogging((context, builder) =>
            {
                Option serverLogLevelOption = context.GetInvocationContext().BindingContext.ParseResult.RootCommandResult.Command.Options
                    .Single(q => q.Name.Equals(nameof(ServiceOptions.ServerLogLevel), StringComparison.OrdinalIgnoreCase));
                var serverLogLevel = (LogLevel)context.GetInvocationContext().ParseResult.GetValueForOption(serverLogLevelOption)!;
                Option systemLogLevelOption = context.GetInvocationContext().BindingContext.ParseResult.RootCommandResult.Command.Options
                    .Single(q => q.Name.Equals(nameof(ServiceOptions.SystemLogLevel), StringComparison.OrdinalIgnoreCase));
                var systemLogLevel = (LogLevel)context.GetInvocationContext().ParseResult.GetValueForOption(systemLogLevelOption)!;

                builder.ConfigureLogging(serverLogLevel, systemLogLevel);
            });
    })
    .Build()
    .InvokeAsync(args)
    .ConfigureAwait(false);