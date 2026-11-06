using System.Data.Common;
using Aml.Core.Ports;
using Aml.Infra.Persistence;
using Dapper;
using Microsoft.Data.Sqlite;
using static Aml.Core.Cases.CaseJson;
using static Aml.Infra.Persistence.SqliteValues;

namespace Aml.Infra.Jobs;

public sealed class JobStore(SqliteDatabase db) : IJobStore
{
    private sealed record ClaimedRow(string JobId, string JobType, long AttemptCount, string Payload);

    private sealed record ItemRow(string JobId, string IdempotencyKey, string Status, long AttemptCount,
        string? CaseId, long? AnalysisRevision, string? ErrorCode, string? CompletedAt, string CreatedAt);

    // Settable properties: with no rows the COUNT column has no type a constructor match could use.
    private sealed class CountRow
    {
        public string Status { get; set; } = "";
        public long Items { get; set; }
    }

    public Task<bool> EnqueueAsync(string jobId, string jobType, string batchId, string idempotencyKey, string payload, DateTimeOffset now, CancellationToken ct) =>
        db.WriteAsync((c, tx) => TryEnqueueAsync(db, c, tx, new NewJob(jobId, jobType, batchId, idempotencyKey, payload), FormatTime(now), ct), ct);

    public Task<IReadOnlyList<string>> EnqueueAsync(IReadOnlyList<NewJob> jobs, DateTimeOffset now, CancellationToken ct) =>
        db.WriteAsync<IReadOnlyList<string>>(async (c, tx) =>
        {
            var at = FormatTime(now);
            var queued = new List<string>();
            foreach (var job in jobs)
                if (await TryEnqueueAsync(db, c, tx, job, at, ct).ConfigureAwait(false)) queued.Add(job.JobId);
            return queued;
        }, ct);

    /// <summary>Inserts a pending job; false when one is already queued under its idempotency key (only this statement is rolled back).</summary>
    internal static async Task<bool> TryEnqueueAsync(SqliteDatabase db, SqliteConnection c, DbTransaction? tx, NewJob job, string now, CancellationToken ct)
    {
        try
        {
            await c.ExecuteAsync(new CommandDefinition(db.Sql["Batch.Enqueue"], new
            {
                jobId = job.JobId,
                jobType = job.JobType,
                batchId = job.BatchId,
                idempotencyKey = job.IdempotencyKey,
                payload = job.Payload,
                now,
            }, tx, cancellationToken: ct)).ConfigureAwait(false);
            return true;
        }
        catch (SqliteException e) when (e.SqliteExtendedErrorCode == SqliteDatabase.SqliteUniqueViolation)
        {
            return false;
        }
    }

    public Task<IReadOnlyList<ClaimedJob>> ClaimAsync(string leaseOwner, int take, DateTimeOffset now, DateTimeOffset leaseExpiry, CancellationToken ct) =>
        db.WriteAsync<IReadOnlyList<ClaimedJob>>(async (c, tx) =>
        {
            var ids = await c.QueryAsync<string>(new CommandDefinition(db.Sql["Batch.ListClaimable"], new { now = FormatTime(now), take }, tx, cancellationToken: ct)).ConfigureAwait(false);
            var claimed = new List<ClaimedJob>();
            foreach (var jobId in ids)
            {
                // Optimistic claim: another process may have taken it since the list was read.
                var won = await c.ExecuteAsync(new CommandDefinition(db.Sql["Batch.Claim"],
                    new { jobId, leaseOwner, leaseExpiry = FormatTime(leaseExpiry) }, tx, cancellationToken: ct)).ConfigureAwait(false);
                if (won != 1) continue;
                var row = await c.QuerySingleAsync<ClaimedRow>(new CommandDefinition(db.Sql["Batch.GetClaimed"], new { jobId }, tx, cancellationToken: ct)).ConfigureAwait(false);
                claimed.Add(new ClaimedJob(row.JobId, row.JobType, (int)row.AttemptCount, row.Payload, leaseOwner));
            }
            return claimed;
        }, ct);

    public Task<bool> RenewLeaseAsync(string jobId, string leaseOwner, DateTimeOffset leaseExpiry, CancellationToken ct) =>
        ChangesOneAsync("Batch.RenewLease", new { jobId, leaseOwner, leaseExpiry = FormatTime(leaseExpiry) }, ct);

    public Task<bool> CompleteAsync(string jobId, string leaseOwner, string caseId, int revision, DateTimeOffset now, CancellationToken ct) =>
        ChangesOneAsync("Batch.Complete", new { jobId, leaseOwner, caseId, revision, now = FormatTime(now) }, ct);

    public Task<bool> ReleaseAsync(string jobId, string leaseOwner, DateTimeOffset now, CancellationToken ct) =>
        ChangesOneAsync("Batch.Release", new { jobId, leaseOwner, now = FormatTime(now) }, ct);

    public Task<bool> FailAsync(string jobId, string leaseOwner, bool deadLetter, string errorCode, DateTimeOffset nextAttemptAt, DateTimeOffset now, CancellationToken ct) =>
        ChangesOneAsync("Batch.Fail", new
        {
            jobId,
            leaseOwner,
            errorCode,
            status = Code(deadLetter ? JobStatus.DeadLetter : JobStatus.Pending),
            nextAttemptAt = FormatTime(nextAttemptAt),
            completedAt = deadLetter ? FormatTime(now) : null,
        }, ct);

    public Task<int> ReclaimExpiredAsync(int maxAttempts, DateTimeOffset now, CancellationToken ct) =>
        ExecuteAsync("Batch.ReclaimExpired", new { maxAttempts, now = FormatTime(now) }, ct);

    private async Task<bool> ChangesOneAsync(string sql, object args, CancellationToken ct) => await ExecuteAsync(sql, args, ct).ConfigureAwait(false) == 1;

    private Task<int> ExecuteAsync(string sql, object args, CancellationToken ct) =>
        db.WriteAsync((c, tx) => c.ExecuteAsync(new CommandDefinition(db.Sql[sql], args, tx, cancellationToken: ct)), ct);

    public async Task<IReadOnlyList<JobItem>> ListItemsAsync(string batchId, CancellationToken ct,
        IReadOnlyCollection<JobStatus>? statuses = null, (DateTimeOffset At, string Id)? after = null, int limit = int.MaxValue)
    {
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        var rows = await c.QueryAsync<ItemRow>(new CommandDefinition(db.Sql["Batch.ListItems"], new
        {
            batchId,
            allStatuses = statuses is null,
            statuses = (statuses ?? []).Select(s => Code(s)).DefaultIfEmpty("").ToArray(),
            afterAt = after is { } a ? FormatTime(a.At) : null,
            afterId = after?.Id,
            limit,
        }, cancellationToken: ct)).ConfigureAwait(false);
        return rows.Select(r => new JobItem(r.JobId, r.IdempotencyKey, ParseCode<JobStatus>(r.Status), (int)r.AttemptCount,
            r.CaseId, (int?)r.AnalysisRevision, r.ErrorCode, r.CompletedAt is null ? null : ParseTime(r.CompletedAt), ParseTime(r.CreatedAt))).ToList();
    }

    public async Task<IReadOnlyDictionary<JobStatus, int>> CountsAsync(string batchId, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        var rows = await c.QueryAsync<CountRow>(new CommandDefinition(db.Sql["Batch.Counts"], new { batchId }, cancellationToken: ct)).ConfigureAwait(false);
        return rows.ToDictionary(r => ParseCode<JobStatus>(r.Status), r => (int)r.Items);
    }

    public Task<int> RerunAsync(string batchId, DateTimeOffset now, CancellationToken ct) => ExecuteAsync("Batch.Rerun", new { batchId, now = FormatTime(now) }, ct);
}
