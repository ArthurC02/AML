namespace Aml.Core.Ports;

public enum JobStatus { Pending, Running, Succeeded, DeadLetter }

public sealed record ClaimedJob(string JobId, int AttemptCount, string Payload);

/// <summary>Progress of one batch item, with lineage to the Case and revision it produced.</summary>
public sealed record JobItem(string JobId, string IdempotencyKey, JobStatus Status, int AttemptCount,
    string? CaseId, int? AnalysisRevision, string? ErrorCode, DateTimeOffset? CompletedAt);

/// <summary>Durable job table (ADR-0011). Every state change after the claim requires the caller to own the lease.</summary>
public interface IJobStore
{
    /// <summary>False when the idempotency key is already queued (in this or an earlier batch).</summary>
    Task<bool> EnqueueAsync(string jobId, string jobType, string batchId, string idempotencyKey, string payload, DateTimeOffset now, CancellationToken ct);

    /// <summary>Claims up to <paramref name="take"/> due jobs; each one is owned exclusively until the lease expires.</summary>
    Task<IReadOnlyList<ClaimedJob>> ClaimAsync(string leaseOwner, int take, DateTimeOffset now, DateTimeOffset leaseExpiry, CancellationToken ct);

    Task CompleteAsync(string jobId, string leaseOwner, string caseId, int revision, DateTimeOffset now, CancellationToken ct);

    /// <summary>Back to PENDING at <paramref name="nextAttemptAt"/>, or DEAD_LETTER.</summary>
    Task FailAsync(string jobId, string leaseOwner, bool deadLetter, string errorCode, DateTimeOffset nextAttemptAt, DateTimeOffset now, CancellationToken ct);

    /// <summary>Returns RUNNING jobs with an expired lease to the queue, or dead-letters them when the retry budget is spent.</summary>
    Task<int> ReclaimExpiredAsync(int maxAttempts, DateTimeOffset now, CancellationToken ct);

    Task<IReadOnlyList<JobItem>> ListItemsAsync(string batchId, CancellationToken ct);

    /// <summary>Operator re-run: dead-lettered items of the batch get a fresh retry budget.</summary>
    Task<int> RerunAsync(string batchId, DateTimeOffset now, CancellationToken ct);
}
