﻿namespace CnCNetServer;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

internal sealed class CnCNetBackgroundService : BackgroundService
{
    private const int STUN_PORT1 = 3478;
    private const int STUN_PORT2 = 8054;

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
        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInfo(FormattableString.Invariant($"{DateTimeOffset.Now} Server {options.Name} starting."));

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
            logger.LogInfo(FormattableString.Invariant($"{DateTimeOffset.Now} Server {options.Name} started."));
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInfo(FormattableString.Invariant($"{DateTimeOffset.Now} Server {options.Name} stopping."));

        try
        {
            await base.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogExceptionDetails(ex);
            throw;
        }

        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInfo(FormattableString.Invariant($"{DateTimeOffset.Now} Server {options.Name} stopped."));
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.TunnelV3Enabled && !options.TunnelV2Enabled && options.NoPeerToPeer)
            throw new ConfigurationException("No tunnel or peer to peer enabled.");

        var tasks = new List<Task>();

        if (options.TunnelV3Enabled)
            tasks.Add(CreateLongRunningTask(() => tunnelV3.StartAsync(stoppingToken), tunnelV3, stoppingToken));

        if (options.TunnelV2Enabled)
        {
            tasks.Add(CreateLongRunningTask(() => tunnelV2.StartAsync(stoppingToken), tunnelV2, stoppingToken));
            tasks.Add(CreateLongRunningTask(() => tunnelV2.StartHttpServerAsync(stoppingToken), tunnelV2, stoppingToken));
        }

        if (!options.NoPeerToPeer)
        {
            tasks.Add(CreateLongRunningTask(() => peerToPeerUtil1.StartAsync(STUN_PORT1, stoppingToken), peerToPeerUtil1, stoppingToken));
            tasks.Add(CreateLongRunningTask(() => peerToPeerUtil2.StartAsync(STUN_PORT2, stoppingToken), peerToPeerUtil2, stoppingToken));
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
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogExceptionDetails(ex);
                        await disposable.DisposeAsync().ConfigureAwait(false);
                    }
                }
            },
            null,
            cancellationToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();
    }
}