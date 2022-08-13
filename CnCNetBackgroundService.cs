namespace CnCNetServer;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

internal sealed class CnCNetBackgroundService : BackgroundService
{
    private readonly ILogger logger;
    private readonly Options options;
    private readonly TunnelV3 tunnelV3;
    private readonly TunnelV2 tunnelV2;
    private readonly PeerToPeerUtil peerToPeerUtil1;
    private readonly PeerToPeerUtil peerToPeerUtil2;

    public CnCNetBackgroundService(ILogger<CnCNetBackgroundService> logger, Options options, TunnelV3 tunnelV3,
        TunnelV2 tunnelV2, PeerToPeerUtil peerToPeerUtil1, PeerToPeerUtil peerToPeerUtil2)
    {
        this.logger = logger;
        this.options = options;
        this.tunnelV3 = tunnelV3;
        this.tunnelV2 = tunnelV2;
        this.peerToPeerUtil1 = peerToPeerUtil1;
        this.peerToPeerUtil2 = peerToPeerUtil2;
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInfo("Server starting.");

        try
        {
            await base.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogExceptionDetails(ex);
            throw;
        }

        logger.LogInfo("Server started.");
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInfo("Server stopping.");

        try
        {
            await base.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogExceptionDetails(ex);
            throw;
        }

        logger.LogInfo("Server stopped.");
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var tasks = new List<Task>
            {
                CreateLongRunningTask(() => tunnelV3.StartAsync(stoppingToken), stoppingToken),
                CreateLongRunningTask(() => tunnelV2.StartAsync(stoppingToken), stoppingToken),
                CreateLongRunningTask(tunnelV2.StartHttpServerAsync, stoppingToken),
            };

        if (!options.NoPeerToPeer)
        {
            tasks.Add(CreateLongRunningTask(() => peerToPeerUtil1.StartAsync(8054, stoppingToken), stoppingToken));
            tasks.Add(CreateLongRunningTask(() => peerToPeerUtil2.StartAsync(3478, stoppingToken), stoppingToken));
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

    private Task CreateLongRunningTask(Func<Task> task, CancellationToken cancellationToken)
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
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogExceptionDetails(ex);
                    }

                    await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                }
            },
            null,
            cancellationToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();
    }
}