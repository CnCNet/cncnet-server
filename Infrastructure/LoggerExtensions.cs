namespace CnCNetServer;

internal static partial class LoggerExtensions
{
    public static async ValueTask LogExceptionDetailsAsync(this ILogger logger, Exception exception, HttpResponseMessage? httpResponseMessage = null)
    {
        logger.LogException(exception.GetDetailedExceptionInfo());

        if (httpResponseMessage is not null)
            logger.LogException(await httpResponseMessage.GetHttpResponseMessageInfoAsync().ConfigureAwait(false));
    }

    public static void ConfigureLogging(this ILoggingBuilder loggingBuilder, LogLevel serverLogLevel, LogLevel systemLogLevel)
        => _ = loggingBuilder
                .SetMinimumLevel(systemLogLevel)
                .AddFilter(nameof(CnCNetServer), serverLogLevel);

    [LoggerMessage(EventId = 4, Level = LogLevel.Trace, Message = "{message}")]
    public static partial void LogTrace(this ILogger logger, string message);

    [LoggerMessage(EventId = 3, Level = LogLevel.Debug, Message = "{message}")]
    public static partial void LogDebug(this ILogger logger, string message);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "{message}")]
    public static partial void LogInfo(this ILogger logger, string message);

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "{message}")]
    public static partial void LogWarning(this ILogger logger, string message);

    [LoggerMessage(EventId = 0, Level = LogLevel.Error, Message = "{message}")]
    private static partial void LogException(this ILogger logger, string message);
}