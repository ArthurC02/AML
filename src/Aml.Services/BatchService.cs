using System.Text.Json;
using Aml.Core.Ports;
using Aml.Core.Subjects;

namespace Aml.Services;

/// <summary>One Case to create and analyze: the same fields and files as POST /api/cases.</summary>
public sealed record BatchItemRequest(string SubjectRef, string TriggerEventId, IReadOnlyDictionary<string, string> Files);

public sealed record BatchSubmitted(string BatchId, int Queued, IReadOnlyList<string> Duplicates);

public sealed record BatchStatus(string BatchId, int Pending, int Running, int Succeeded, int DeadLetter, IReadOnlyList<JobItem> Items);

public sealed record BatchOptions
{
    public int MaxWorkers { get; init; } = 4;
    public int MaxAttempts { get; init; } = 3;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan Lease { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);
}

/// <summary>Batch and Work Management: queues Case analyses in the job table and runs them with bounded workers.</summary>
public sealed class BatchService(IJobStore queue, CaseService cases, Func<IReadOnlyDictionary<string, string>, CaseInput> read, BatchOptions options, TimeProvider clock)
{
    public const string AnalyzeCase = "ANALYZE_CASE";
    public const string InvalidInput = "INVALID_INPUT";
    public const string Timeout = "TIMEOUT";

    private readonly string _owner = $"{Environment.MachineName}:{Guid.NewGuid():N}";

    public BatchOptions Options => options;

    /// <summary>Queues every item; an item whose trigger and subject are already queued is reported as a duplicate, not queued again.</summary>
    public async Task<BatchSubmitted> SubmitAsync(IReadOnlyList<BatchItemRequest> items, CancellationToken ct)
    {
        var batchId = Guid.CreateVersion7().ToString();
        var duplicates = new List<string>();
        foreach (var item in items)
        {
            var key = $"{AnalyzeCase}:{item.TriggerEventId}:{item.SubjectRef}";
            if (!await queue.EnqueueAsync(Guid.CreateVersion7().ToString(), AnalyzeCase, batchId, key,
                    JsonSerializer.Serialize(item, CaseService.Json), clock.GetUtcNow(), ct).ConfigureAwait(false))
                duplicates.Add(key);
        }
        return new BatchSubmitted(batchId, items.Count - duplicates.Count, duplicates);
    }

    public async Task<BatchStatus?> GetAsync(string batchId, CancellationToken ct)
    {
        var items = await queue.ListItemsAsync(batchId, ct).ConfigureAwait(false);
        if (items.Count == 0) return null;
        int Count(JobStatus s) => items.Count(i => i.Status == s);
        return new BatchStatus(batchId, Count(JobStatus.Pending), Count(JobStatus.Running), Count(JobStatus.Succeeded), Count(JobStatus.DeadLetter), items);
    }

    public Task<int> RerunAsync(string batchId, CancellationToken ct) => queue.RerunAsync(batchId, clock.GetUtcNow(), ct);

    /// <summary>One worker round: reclaim abandoned jobs, claim up to MaxWorkers, run them in parallel. Returns how many ran.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        await queue.ReclaimExpiredAsync(options.MaxAttempts, now, ct).ConfigureAwait(false);
        var jobs = await queue.ClaimAsync(_owner, options.MaxWorkers, now, now + options.Lease, ct).ConfigureAwait(false);
        await Task.WhenAll(jobs.Select(j => RunAsync(j, ct))).ConfigureAwait(false);
        return jobs.Count;
    }

    private async Task RunAsync(ClaimedJob job, CancellationToken ct)
    {
        var item = JsonSerializer.Deserialize<BatchItemRequest>(job.Payload, CaseService.Json)!;
        CaseInput input;
        try
        {
            input = read(item.Files);
        }
#pragma warning disable CA1031 // any reader failure means the files are unusable; retrying the same files cannot help
        catch (Exception)
#pragma warning restore CA1031
        {
            await FailAsync(job, InvalidInput, deadLetter: true, ct).ConfigureAwait(false);
            return;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(options.Timeout); // ponytail: cancels the I/O, not the CPU-bound analyzer; enough at PoC volumes
        try
        {
            // Checkpoint: Case creation is idempotent on (trigger, subject), so a retry after a crash reuses the stored revision.
            var c = await cases.CreateAsync(item.SubjectRef, item.TriggerEventId, input, timeout.Token, item.Files).ConfigureAwait(false);
            await queue.CompleteAsync(job.JobId, _owner, c.CaseId, c.CurrentRevision, clock.GetUtcNow(), ct).ConfigureAwait(false);
            AmlTelemetry.JobAttempts.Add(1, new KeyValuePair<string, object?>("outcome", "SUCCEEDED"));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Host stopping: the lease expires and another round picks the job up again.
        }
#pragma warning disable CA1031 // failure isolation: one item's error must not stop the batch
        catch (Exception e)
#pragma warning restore CA1031
        {
            await FailAsync(job, e is OperationCanceledException ? Timeout : e.GetType().Name, job.AttemptCount >= options.MaxAttempts, ct).ConfigureAwait(false);
        }
    }

    // Exponential backoff: RetryDelay, 2×, 4×, …
    private Task FailAsync(ClaimedJob job, string errorCode, bool deadLetter, CancellationToken ct)
    {
        AmlTelemetry.JobAttempts.Add(1, new("outcome", deadLetter ? "DEAD_LETTER" : "RETRY"), new("error_code", errorCode));
        var now = clock.GetUtcNow();
        var next = now + options.RetryDelay * Math.Pow(2, Math.Max(0, job.AttemptCount - 1));
        return queue.FailAsync(job.JobId, _owner, deadLetter, errorCode, next, now, ct);
    }
}
