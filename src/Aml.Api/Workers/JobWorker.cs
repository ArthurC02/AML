using Aml.Services;

namespace Aml.Api.Workers;

/// <summary>Polls the job table; each round runs at most MaxWorkers jobs (bounded concurrency).</summary>
internal sealed partial class JobWorker(BatchService batches, ILogger<JobWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var ran = 0;
            try
            {
                ran = await batches.RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // a database hiccup must not stop the worker; the next round retries
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
#pragma warning restore CA1031
            {
                RoundFailed(log, e);
            }
            if (ran == 0) await Task.Delay(batches.Options.PollInterval, stoppingToken).ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Job worker round failed")]
    private static partial void RoundFailed(ILogger logger, Exception e);
}
