using Aml.Services;

namespace Aml.Api.Workers;

/// <summary>Keeps up to MaxWorkers jobs running: a finished job's slot is refilled at once; an empty queue is polled.</summary>
internal sealed partial class JobWorker(BatchService batches, ILogger<JobWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        batches.Warn = message => JobWarning(log, message);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var started = 0;
                try
                {
                    started = await batches.FillAsync(stoppingToken).ConfigureAwait(false);
                }
#pragma warning disable CA1031 // a database hiccup must not stop the worker; the next round retries
                catch (Exception e) when (!stoppingToken.IsCancellationRequested)
#pragma warning restore CA1031
                {
                    RoundFailed(log, e);
                }
                if (started == 0) await Task.Delay(batches.Options.PollInterval, stoppingToken).ConfigureAwait(false);
            }
        }
        finally
        {
            // Stopping: running jobs record their outcome or go back to the queue before the database closes. The host's
            // shutdown timeout (Batch:ShutdownTimeout) bounds the wait; a drain logged as started but never as done
            // outlived it, and its jobs return to the queue when their lease expires.
            DrainStarted(log);
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            await batches.DrainAsync(CancellationToken.None).ConfigureAwait(false);
            var seconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds;
            Drained(log, seconds);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Job worker stopping: waiting for running jobs")]
    private static partial void DrainStarted(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Job worker stopped: running jobs settled in {Seconds:0.0} s")]
    private static partial void Drained(ILogger logger, double seconds);

    [LoggerMessage(Level = LogLevel.Error, Message = "Job worker round failed")]
    private static partial void RoundFailed(ILogger logger, Exception e);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Message}")]
    private static partial void JobWarning(ILogger logger, string message);
}
