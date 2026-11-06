namespace Aml.Core.Ports;

public enum JobStatus { Pending, Running, Succeeded, DeadLetter }

public sealed record ClaimedJob(string JobId, string JobType, int AttemptCount, string Payload);

/// <summary>A job to queue; the idempotency key is unique across the job table.</summary>
public sealed record NewJob(string JobId, string JobType, string BatchId, string IdempotencyKey, string Payload);

public sealed record JobItem(string JobId, string IdempotencyKey, JobStatus Status, int AttemptCount,
    string? CaseId, int? AnalysisRevision, string? ErrorCode, DateTimeOffset? CompletedAt, DateTimeOffset CreatedAt);

/// <summary>Durable job table (ADR-0011). Every state change after the claim requires the caller to own the lease.</summary>
public interface IJobStore
{
    /// <summary>False when the idempotency key is already queued (in this or an earlier batch).</summary>
    Task<bool> EnqueueAsync(string jobId, string jobType, string batchId, string idempotencyKey, string payload, DateTimeOffset now, CancellationToken ct);

    /// <summary>Queues every job in one transaction; returns the ids of those queued, skipping keys already queued.</summary>
    Task<IReadOnlyList<string>> EnqueueAsync(IReadOnlyList<NewJob> jobs, DateTimeOffset now, CancellationToken ct);

    /// <summary>Claims up to <paramref name="take"/> due jobs; each one is owned exclusively until the lease expires.</summary>
    Task<IReadOnlyList<ClaimedJob>> ClaimAsync(string leaseOwner, int take, DateTimeOffset now, DateTimeOffset leaseExpiry, CancellationToken ct);

    /// <summary>Extends the lease of a running job. False when the caller no longer owns it.</summary>
    Task<bool> RenewLeaseAsync(string jobId, string leaseOwner, DateTimeOffset leaseExpiry, CancellationToken ct);

    /// <summary>False when the caller no longer owns the lease (nothing changed).</summary>
    Task<bool> CompleteAsync(string jobId, string leaseOwner, string caseId, int revision, DateTimeOffset now, CancellationToken ct);

    /// <summary>Back to PENDING at <paramref name="nextAttemptAt"/>, or DEAD_LETTER. False when the caller no longer owns the lease.</summary>
    Task<bool> FailAsync(string jobId, string leaseOwner, bool deadLetter, string errorCode, DateTimeOffset nextAttemptAt, DateTimeOffset now, CancellationToken ct);

    /// <summary>Returns RUNNING jobs with an expired lease to the queue, or dead-letters them when the retry budget is spent.</summary>
    Task<int> ReclaimExpiredAsync(int maxAttempts, DateTimeOffset now, CancellationToken ct);

    /// <summary>Null statuses means all; ordered by (created at, id), starting after <paramref name="after"/>.</summary>
    Task<IReadOnlyList<JobItem>> ListItemsAsync(string batchId, CancellationToken ct,
        IReadOnlyCollection<JobStatus>? statuses = null, (DateTimeOffset At, string Id)? after = null, int limit = int.MaxValue);

    /// <summary>Empty for an unknown batch.</summary>
    Task<IReadOnlyDictionary<JobStatus, int>> CountsAsync(string batchId, CancellationToken ct);

    /// <summary>Gives the batch's dead-lettered items a fresh retry budget.</summary>
    Task<int> RerunAsync(string batchId, DateTimeOffset now, CancellationToken ct);
}
