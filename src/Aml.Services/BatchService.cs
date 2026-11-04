using System.Text.Json;
using Aml.Core.Cases;
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

/// <summary>
/// Batch and Work Management: queues Case analyses in the job table and runs them with at most MaxWorkers in flight;
/// a finished job frees its slot for the next one at once.
/// </summary>
/// <param name="lists">Runs REANALYZE_CASE jobs (queued by a Profile import).</param>
public sealed class BatchService(IJobStore queue, CaseService cases, Func<IReadOnlyDictionary<string, string>, CaseInput> read, BatchOptions options, TimeProvider clock,
    ListService? lists = null) : IDisposable
{
    public const string AnalyzeCase = "ANALYZE_CASE";
    public const string ReanalyzeCase = "REANALYZE_CASE";
    public const string InvalidInput = "INVALID_INPUT";
    public const string InvalidPayload = "INVALID_PAYLOAD";
    public const string Timeout = "TIMEOUT";

    private readonly string _owner = $"{Environment.MachineName}:{Guid.NewGuid():N}";
    private readonly SemaphoreSlim _slots = new(options.MaxWorkers, options.MaxWorkers);

    public BatchOptions Options => options;

    /// <summary>Receives warnings: a job that lost its lease, or whose outcome could not be recorded.</summary>
    public Action<string>? Warn { get; set; }

    /// <summary>Queues every item; an item whose trigger and subject are already queued is reported as a duplicate, not queued again.</summary>
    public async Task<BatchSubmitted> SubmitAsync(IReadOnlyList<BatchItemRequest> items, CancellationToken ct)
    {
        var batchId = Guid.CreateVersion7().ToString();
        var duplicates = new List<string>();
        foreach (var item in items)
        {
            var key = $"{AnalyzeCase}:{item.TriggerEventId}:{item.SubjectRef}";
            if (!await queue.EnqueueAsync(Guid.CreateVersion7().ToString(), AnalyzeCase, batchId, key,
                    JsonSerializer.Serialize(item, CaseJson.Options), clock.GetUtcNow(), ct).ConfigureAwait(false))
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

    /// <summary>Waits for a free slot, claims jobs for every free slot and starts them. Returns how many started.</summary>
    public async Task<int> FillAsync(CancellationToken ct) => (await StartAsync(ct).ConfigureAwait(false)).Count;

    /// <summary>Claims jobs for the free slots and waits for them to finish. Returns how many ran.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var running = await StartAsync(ct).ConfigureAwait(false);
        await Task.WhenAll(running).ConfigureAwait(false);
        return running.Count;
    }

    public void Dispose() => _slots.Dispose();

    private async Task<IReadOnlyList<Task>> StartAsync(CancellationToken ct)
    {
        await _slots.WaitAsync(ct).ConfigureAwait(false);
        var free = 1;
        while (_slots.Wait(0, CancellationToken.None)) free++;
        IReadOnlyList<ClaimedJob> jobs;
        try
        {
            var now = clock.GetUtcNow();
            await queue.ReclaimExpiredAsync(options.MaxAttempts, now, ct).ConfigureAwait(false);
            jobs = await queue.ClaimAsync(_owner, free, now, now + options.Lease, ct).ConfigureAwait(false);
        }
        catch
        {
            _slots.Release(free);
            throw;
        }
        if (jobs.Count < free) _slots.Release(free - jobs.Count);
        // Task.Run: reading and analysis are CPU-bound and SQLite's async calls complete synchronously, so without it each
        // job would run to the end before the next one starts. No token: a job that never starts would never free its slot.
        return jobs.Select(j => Task.Run(() => RunInSlotAsync(j, ct), CancellationToken.None)).ToList();
    }

    private async Task RunInSlotAsync(ClaimedJob job, CancellationToken ct)
    {
        try
        {
            await RunAsync(job, ct).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // a database hiccup while recording the outcome must not stop the worker; the lease expires and the job is retried
        catch (Exception e) when (!ct.IsCancellationRequested)
#pragma warning restore CA1031
        {
            Warn?.Invoke($"Job {job.JobId}: outcome not recorded ({e.GetType().Name}); it returns to the queue when its lease expires");
        }
        finally
        {
            _slots.Release();
        }
    }

    private async Task RunAsync(ClaimedJob job, CancellationToken ct)
    {
        var (item, reanalysis) = ReadPayload(job);
        if (item is null && reanalysis is null)
        {
            // A payload that cannot be read now never will be: no retries.
            await FailAsync(job, InvalidPayload, deadLetter: true, ct).ConfigureAwait(false);
            return;
        }

        CaseInput? input = null;
        if (item is not null)
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
        timeout.CancelAfter(options.Timeout); // the analysis itself is not interruptible; CaseService checks the deadline before writing
        try
        {
            // Checkpoint: Case creation is idempotent on (trigger, subject), so a retry after a crash reuses the stored revision.
            var c = item is not null
                ? await cases.CreateAsync(item.SubjectRef, item.TriggerEventId, input!, timeout.Token, item.Files, () => RenewLeaseAsync(job, ct)).ConfigureAwait(false)
                : await (lists ?? throw new InvalidOperationException("REANALYZE_CASE needs the list service"))
                    .ReanalyzeWithLatestProfilesAsync(reanalysis!.CaseId, timeout.Token, () => RenewLeaseAsync(job, ct)).ConfigureAwait(false);
            if (!await queue.CompleteAsync(job.JobId, _owner, c.CaseId, c.CurrentRevision, clock.GetUtcNow(), ct).ConfigureAwait(false))
                Warn?.Invoke($"Job {job.JobId}: completed, but its lease had passed to another worker; the job state was left to that worker");
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

    // The payload of a known job type with every required field, or (null, null).
    private static (BatchItemRequest? Item, ReanalysisJob? Reanalysis) ReadPayload(ClaimedJob job)
    {
        try
        {
            return job.JobType switch
            {
                AnalyzeCase when JsonSerializer.Deserialize<BatchItemRequest>(job.Payload, CaseJson.Options)
                    is { SubjectRef: not null, TriggerEventId: not null, Files: not null } item => (item, null),
                ReanalyzeCase when JsonSerializer.Deserialize<ReanalysisJob>(job.Payload, CaseJson.Options) is { CaseId: not null } r => (null, r),
                _ => (null, null),
            };
        }
        catch (Exception e) when (e is JsonException or ArgumentNullException or NotSupportedException)
        {
            return (null, null);
        }
    }

    // Before the revision is written: a job whose lease was reclaimed by another worker must not write.
    private async Task RenewLeaseAsync(ClaimedJob job, CancellationToken ct)
    {
        if (!await queue.RenewLeaseAsync(job.JobId, _owner, clock.GetUtcNow() + options.Lease, ct).ConfigureAwait(false))
            throw new InvalidOperationException($"Job {job.JobId} lost its lease");
    }

    // Exponential backoff: RetryDelay, 2×, 4×, …
    private async Task FailAsync(ClaimedJob job, string errorCode, bool deadLetter, CancellationToken ct)
    {
        AmlTelemetry.JobAttempts.Add(1, new("outcome", deadLetter ? "DEAD_LETTER" : "RETRY"), new("error_code", errorCode));
        var now = clock.GetUtcNow();
        var next = now + options.RetryDelay * Math.Pow(2, Math.Max(0, job.AttemptCount - 1));
        if (!await queue.FailAsync(job.JobId, _owner, deadLetter, errorCode, next, now, ct).ConfigureAwait(false))
            Warn?.Invoke($"Job {job.JobId}: failure {errorCode} not recorded, its lease had passed to another worker");
    }
}
