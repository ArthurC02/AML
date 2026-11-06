using System.Text.Json;
using Aml.Core.Cases;
using Aml.Core.Ports;
using Aml.Core.Subjects;

namespace Aml.Services;

public sealed record BatchItemRequest(string SubjectRef, string TriggerEventId, IReadOnlyDictionary<string, string> Files);

public sealed record BatchSubmitted(string BatchId, int Queued, IReadOnlyList<string> Duplicates);

public enum BatchView { NotYetSucceeded, Every }

public sealed record BatchStatus(string BatchId, int Pending, int Running, int Succeeded, int DeadLetter, IReadOnlyList<JobItem> Items, string? NextPageToken = null);

public sealed record BatchOptions
{
    public int MaxWorkers { get; init; } = 32; // analysis is about 0.6 ms of CPU; the jobs mostly wait on commits, which group commit shares
    public int MaxAttempts { get; init; } = 3;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan Lease { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);
}

/// <summary>At most MaxWorkers jobs in flight; a finished job frees its slot for the next one at once.</summary>
public sealed class BatchService(IJobStore queue, CaseService cases, Func<IReadOnlyDictionary<string, string>, CaseInput> read, BatchOptions options, TimeProvider clock,
    ListService? lists = null) : IDisposable
{
    public const string AnalyzeCase = "ANALYZE_CASE";
    public const string ReanalyzeCase = "REANALYZE_CASE";
    public const string InvalidInput = "INVALID_INPUT";
    public const string InvalidPayload = "INVALID_PAYLOAD";
    public const string Timeout = "TIMEOUT";
    public const string LeaseExpired = "LEASE_EXPIRED"; // written by the job store when it reclaims an expired lease

    public static string ExceptionCode(Exception e) => "EXCEPTION:" + e.GetType().Name;

    private readonly string _owner = $"{Environment.MachineName}:{Guid.NewGuid():N}";
    private readonly SemaphoreSlim _slots = new(options.MaxWorkers, options.MaxWorkers);

    public BatchOptions Options => options;

    public Action<string>? Warn { get; set; }

    public async Task<BatchSubmitted> SubmitAsync(IReadOnlyList<BatchItemRequest> items, CancellationToken ct)
    {
        var batchId = Guid.CreateVersion7().ToString();
        var jobs = items.Select(item => new NewJob(Guid.CreateVersion7().ToString(), AnalyzeCase, batchId,
            $"{AnalyzeCase}:{item.TriggerEventId}:{item.SubjectRef}", JsonSerializer.Serialize(item, CaseJson.Options))).ToList();
        var queued = (await queue.EnqueueAsync(jobs, clock.GetUtcNow(), ct).ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);
        var duplicates = jobs.Where(j => !queued.Contains(j.JobId)).Select(j => j.IdempotencyKey).ToList();
        return new BatchSubmitted(batchId, items.Count - duplicates.Count, duplicates);
    }

    public async Task<BatchStatus?> GetAsync(string batchId, CancellationToken ct, BatchView view = BatchView.Every, JobStatus? status = null,
        int pageSize = int.MaxValue - 1, (DateTimeOffset At, string Id)? after = null)
    {
        var counts = await queue.CountsAsync(batchId, ct).ConfigureAwait(false);
        if (counts.Count == 0) return null;
        var rows = await queue.ListItemsAsync(batchId, ct, StatusesToList(view, status), after, pageSize + 1).ConfigureAwait(false);
        var (page, next) = PageToken.Page(rows, pageSize, j => (j.CreatedAt, j.JobId));
        int Count(JobStatus st) => counts.GetValueOrDefault(st);
        return new BatchStatus(batchId, Count(JobStatus.Pending), Count(JobStatus.Running), Count(JobStatus.Succeeded), Count(JobStatus.DeadLetter), page, next);
    }

    private static IReadOnlyCollection<JobStatus>? StatusesToList(BatchView view, JobStatus? status) =>
        status is { } only ? [only]
        : view == BatchView.NotYetSucceeded ? [JobStatus.Pending, JobStatus.Running, JobStatus.DeadLetter]
        : null;

    public Task<int> RerunAsync(string batchId, CancellationToken ct) => queue.RerunAsync(batchId, clock.GetUtcNow(), ct);

    public async Task<int> FillAsync(CancellationToken ct) => (await StartAsync(ct).ConfigureAwait(false)).Count;

    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var running = await StartAsync(ct).ConfigureAwait(false);
        await Task.WhenAll(running).ConfigureAwait(false);
        return running.Count;
    }

    public void Dispose() => _slots.Dispose();

    // Every write of a job (claim, lease, Case, outcome) is replayable (ADR-0026): if a power loss takes the newest of them,
    // the job is still queued or its lease expires, and it runs again; a batch item's Case takes the job's id, so the re-run
    // stores the same Case. The task started for each job inherits the scope.
    private async Task<IReadOnlyList<Task>> StartAsync(CancellationToken ct)
    {
        using var replayable = WriteDurability.Replayable();
        var free = await TakeEveryFreeSlotAsync(ct).ConfigureAwait(false);
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
        // Task.Run: CPU-bound work and synchronous SQLite would otherwise run jobs one by one; no token, or an unstarted job keeps its slot.
        return jobs.Select(j => Task.Run(() => RunInSlotAsync(j, ct), CancellationToken.None)).ToList();
    }

    private async Task<int> TakeEveryFreeSlotAsync(CancellationToken ct)
    {
        await _slots.WaitAsync(ct).ConfigureAwait(false);
        var free = 1;
        while (_slots.Wait(0, CancellationToken.None)) free++;
        return free;
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
            await DeadLetterAsync(job, InvalidPayload, ct).ConfigureAwait(false);
            return;
        }

        var input = item is null ? null : await ReadInputOrDeadLetterAsync(job, item, ct).ConfigureAwait(false);
        if (item is not null && input is null) return;

        // CaseService checks the deadline before writing. The constructor, unlike CancelAfter, cancels at once for a zero timeout.
        using var deadline = new CancellationTokenSource(options.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        try
        {
            var record = await AnalyzeOrReanalyzeAsync(job, item, input, reanalysis, linked.Token, ct).ConfigureAwait(false);
            if (!await queue.CompleteAsync(job.JobId, _owner, record.CaseId, record.CurrentRevision, clock.GetUtcNow(), ct).ConfigureAwait(false))
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
            await FailAsync(job, e is OperationCanceledException ? Timeout : ExceptionCode(e), job.AttemptCount >= options.MaxAttempts, ct).ConfigureAwait(false);
        }
    }

    private async Task<CaseInput?> ReadInputOrDeadLetterAsync(ClaimedJob job, BatchItemRequest item, CancellationToken ct)
    {
        try
        {
            return read(item.Files) ?? throw new InvalidDataException("The reader returned no input");
        }
#pragma warning disable CA1031 // any reader failure means the files are unusable; retrying the same files cannot help
        catch (Exception)
#pragma warning restore CA1031
        {
            await DeadLetterAsync(job, InvalidInput, ct).ConfigureAwait(false);
            return null;
        }
    }

    // Case creation is idempotent on (trigger, subject), so a retry after a crash reuses the stored revision.
    private Task<CaseRecord> AnalyzeOrReanalyzeAsync(ClaimedJob job, BatchItemRequest? item, CaseInput? input, ReanalysisJob? reanalysis,
        CancellationToken deadline, CancellationToken ct) =>
        item is not null
            ? cases.CreateAsync(item.SubjectRef, item.TriggerEventId, input!, deadline, item.Files, () => RenewLeaseBeforeWriteAsync(job, ct), caseId: job.JobId)
            : (lists ?? throw new InvalidOperationException("REANALYZE_CASE needs the list service"))
                .ReanalyzeWithLatestProfilesAsync(reanalysis!.CaseId, deadline, () => RenewLeaseBeforeWriteAsync(job, ct));

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

    private async Task RenewLeaseBeforeWriteAsync(ClaimedJob job, CancellationToken ct)
    {
        if (!await queue.RenewLeaseAsync(job.JobId, _owner, clock.GetUtcNow() + options.Lease, ct).ConfigureAwait(false))
            throw new InvalidOperationException($"Job {job.JobId} lost its lease");
    }

    private Task DeadLetterAsync(ClaimedJob job, string errorCode, CancellationToken ct) => FailAsync(job, errorCode, deadLetter: true, ct);

    private TimeSpan ExponentialBackoff(int attemptCount) => options.RetryDelay * Math.Pow(2, Math.Max(0, attemptCount - 1));

    private async Task FailAsync(ClaimedJob job, string errorCode, bool deadLetter, CancellationToken ct)
    {
        AmlTelemetry.JobAttempts.Add(1, new("outcome", deadLetter ? "DEAD_LETTER" : "RETRY"), new("error_code", errorCode));
        var now = clock.GetUtcNow();
        var next = now + ExponentialBackoff(job.AttemptCount);
        if (!await queue.FailAsync(job.JobId, _owner, deadLetter, errorCode, next, now, ct).ConfigureAwait(false))
            Warn?.Invoke($"Job {job.JobId}: failure {errorCode} not recorded, its lease had passed to another worker");
    }
}
