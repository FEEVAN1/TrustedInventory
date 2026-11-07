using TrustedInventory.Application.Jobs;

namespace TrustedInventory.Worker;

/// <summary>
/// Runs one job on a fixed interval. A failed pass is logged and retried on the next tick; the job's own
/// per-item retry and dead-letter rules (NFR-JOBS) live in the job class, not here.
/// </summary>
public sealed partial class ScheduledJob<TJob>(IServiceProvider services, TimeSpan interval) : BackgroundService
    where TJob : IJob
{
    private readonly ILogger _log = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(TJob).Name);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var job = services.GetRequiredService<TJob>();
        using var timer = new PeriodicTimer(interval);
        do
        {
            try
            {
                await job.RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFailed(_log, ex, typeof(TJob).Name);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Job {Job} pass failed; it will run again on the next interval")]
    private static partial void LogFailed(ILogger logger, Exception exception, string job);
}
