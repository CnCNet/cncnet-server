namespace CnCNetServer;

using System.CommandLine.Parsing;

internal sealed class CnCNetBackgroundService : BackgroundService
{
    private const int StunPort1 = 3478;
    private const int StunPort2 = 8054;

    private readonly ILogger logger;
    private readonly IOptions<ServiceOptions> options;
    private readonly TunnelV3 tunnelV3;
    private readonly TunnelV2 tunnelV2;
    private readonly PeerToPeerUtil peerToPeerUtil1;
    private readonly PeerToPeerUtil peerToPeerUtil2;
    private readonly ParseResult parseResult;

    private bool started;
    private bool stopping;

    public CnCNetBackgroundService(ILogger<CnCNetBackgroundService> logger, IOptions<ServiceOptions> options, TunnelV3 tunnelV3,
        TunnelV2 tunnelV2, PeerToPeerUtil peerToPeerUtil1, PeerToPeerUtil peerToPeerUtil2, ParseResult parseResult)
    {
        this.logger = logger;
        this.options = options;
        this.tunnelV3 = tunnelV3;
        this.tunnelV2 = tunnelV2;
        this.peerToPeerUtil1 = peerToPeerUtil1;
        this.peerToPeerUtil2 = peerToPeerUtil2;
        this.parseResult = parseResult;
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        if (parseResult.Errors.Any())
            return;

        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInfo(FormattableString.Invariant($"{DateTimeOffset.Now} Server {options.Value.Name} starting."));

        try
        {
            await base.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogExceptionDetails(ex);
            throw;
        }

        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInfo(FormattableString.Invariant($"{DateTimeOffset.Now} Server {options.Value.Name} started."));

        started = true;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (stopping || !started)
            return;

        stopping = true;

        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInfo(FormattableString.Invariant($"{DateTimeOffset.Now} Server {options.Value.Name} stopping."));

        try
        {
            await base.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogExceptionDetails(ex);
            throw;
        }

        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInfo(FormattableString.Invariant($"{DateTimeOffset.Now} Server {options.Value.Name} stopped."));
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.Value is { TunnelV3Enabled: false, TunnelV2Enabled: false, NoPeerToPeer: true })
            throw new ConfigurationException("No tunnel or peer to peer enabled.");

        var tasks = new List<Task>();

        if (options.Value.TunnelV3Enabled)
            tasks.Add(CreateLongRunningTask(() => tunnelV3.StartAsync(stoppingToken), tunnelV3, stoppingToken));

        if (options.Value.TunnelV2Enabled)
        {
            tasks.Add(CreateLongRunningTask(() => tunnelV2.StartAsync(stoppingToken), tunnelV2, stoppingToken));
            tasks.Add(CreateLongRunningTask(() => tunnelV2.StartHttpServerAsync(stoppingToken), tunnelV2, stoppingToken));
        }

        if (!options.Value.NoPeerToPeer)
        {
            tasks.Add(CreateLongRunningTask(() => peerToPeerUtil1.StartAsync(StunPort1, stoppingToken), peerToPeerUtil1, stoppingToken));
            tasks.Add(CreateLongRunningTask(() => peerToPeerUtil2.StartAsync(StunPort2, stoppingToken), peerToPeerUtil2, stoppingToken));
        }

        return WhenAllSafe(tasks);
    }

    private static async Task WhenAllSafe(IEnumerable<Task> tasks)
    {
        var whenAllTask = Task.WhenAll(tasks);

        try
        {
            await whenAllTask.ConfigureAwait(false);
            return;
        }
        catch
        {
            // Intentionally left empty
        }

        if (whenAllTask.Exception is not null)
            throw whenAllTask.Exception;
    }

    private Task CreateLongRunningTask(Func<Task> task, IAsyncDisposable disposable, CancellationToken cancellationToken)
    {
        return Task.Factory.StartNew(
            async _ =>
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        await task().ConfigureAwait(false);
                    }
                    catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
                    {
                        await LogExceptionAsync(disposable, ex).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // ignored, shutdown signal
                    }
                    catch (Exception ex)
                    {
                        await LogExceptionAsync(disposable, ex).ConfigureAwait(false);
                    }
                }
            },
            null,
            cancellationToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();
    }

    private async ValueTask LogExceptionAsync(IAsyncDisposable disposable, Exception ex)
    {
        logger.LogExceptionDetails(ex);
        await disposable.DisposeAsync().ConfigureAwait(false);
    }
}