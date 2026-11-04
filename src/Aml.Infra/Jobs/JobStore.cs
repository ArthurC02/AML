using Aml.Core.Ports;
using Aml.Infra.Persistence;
using Dapper;
using Microsoft.Data.Sqlite;
using static Aml.Infra.Persistence.Repositories.CaseStore;

namespace Aml.Infra.Jobs;

public sealed class JobStore(SqliteDatabase db) : IJobStore
{
    private const int SqliteConstraintUnique = 2067;

    private sealed record ClaimedRow(string JobId, string JobType, long AttemptCount, string Payload);

    private sealed record ItemRow(string JobId, string IdempotencyKey, string Status, long AttemptCount,
        string? CaseId, long? AnalysisRevision, string? ErrorCode, string? CompletedAt);

    public async Task<bool> EnqueueAsync(string jobId, string jobType, string batchId, string idempotencyKey, string payload, DateTimeOffset now, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        try
        {
            await c.ExecuteAsync(new CommandDefinition(db.Sql["Batch.Enqueue"],
                new { jobId, jobType, batchId, idempotencyKey, payload, now = Time(now) }, cancellationToken: ct)).ConfigureAwait(false);
            return true;
        }
        catch (SqliteException e) when (e.SqliteExtendedErrorCode == SqliteConstraintUnique)
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<ClaimedJob>> ClaimAsync(string leaseOwner, int take, DateTimeOffset now, DateTimeOffset leaseExpiry, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        var ids = await c.QueryAsync<string>(new CommandDefinition(db.Sql["Batch.ListClaimable"], new { now = Time(now), take }, cancellationToken: ct)).ConfigureAwait(false);
        var claimed = new List<ClaimedJob>();
        foreach (var jobId in ids)
        {
            // Optimistic claim: another worker may have taken it since the list was read.
            var won = await c.ExecuteAsync(new CommandDefinition(db.Sql["Batch.Claim"],
                new { jobId, leaseOwner, leaseExpiry = Time(leaseExpiry) }, cancellationToken: ct)).ConfigureAwait(false);
            if (won != 1) continue;
            var row = await c.QuerySingleAsync<ClaimedRow>(new CommandDefinition(db.Sql["Batch.GetClaimed"], new { jobId }, cancellationToken: ct)).ConfigureAwait(false);
            claimed.Add(new ClaimedJob(row.JobId, row.JobType, (int)row.AttemptCount, row.Payload));
        }
        return claimed;
    }

    public async Task<bool> RenewLeaseAsync(string jobId, string leaseOwner, DateTimeOffset leaseExpiry, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        return await c.ExecuteAsync(new CommandDefinition(db.Sql["Batch.RenewLease"],
            new { jobId, leaseOwner, leaseExpiry = Time(leaseExpiry) }, cancellationToken: ct)).ConfigureAwait(false) == 1;
    }

    public async Task<bool> CompleteAsync(string jobId, string leaseOwner, string caseId, int revision, DateTimeOffset now, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        return await c.ExecuteAsync(new CommandDefinition(db.Sql["Batch.Complete"],
            new { jobId, leaseOwner, caseId, revision, now = Time(now) }, cancellationToken: ct)).ConfigureAwait(false) == 1;
    }

    public async Task<bool> FailAsync(string jobId, string leaseOwner, bool deadLetter, string errorCode, DateTimeOffset nextAttemptAt, DateTimeOffset now, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        return await c.ExecuteAsync(new CommandDefinition(db.Sql["Batch.Fail"], new
        {
            jobId, leaseOwner, errorCode,
            status = Enum(deadLetter ? JobStatus.DeadLetter : JobStatus.Pending),
            nextAttemptAt = Time(nextAttemptAt),
            completedAt = deadLetter ? Time(now) : null,
        }, cancellationToken: ct)).ConfigureAwait(false) == 1;
    }

    public async Task<int> ReclaimExpiredAsync(int maxAttempts, DateTimeOffset now, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        return await c.ExecuteAsync(new CommandDefinition(db.Sql["Batch.ReclaimExpired"], new { maxAttempts, now = Time(now) }, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<JobItem>> ListItemsAsync(string batchId, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        var rows = await c.QueryAsync<ItemRow>(new CommandDefinition(db.Sql["Batch.ListItems"], new { batchId }, cancellationToken: ct)).ConfigureAwait(false);
        return rows.Select(r => new JobItem(r.JobId, r.IdempotencyKey, Parse<JobStatus>(r.Status), (int)r.AttemptCount,
            r.CaseId, (int?)r.AnalysisRevision, r.ErrorCode, r.CompletedAt is null ? null : ParseTime(r.CompletedAt))).ToList();
    }

    public async Task<int> RerunAsync(string batchId, DateTimeOffset now, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        return await c.ExecuteAsync(new CommandDefinition(db.Sql["Batch.Rerun"], new { batchId, now = Time(now) }, cancellationToken: ct)).ConfigureAwait(false);
    }
}
