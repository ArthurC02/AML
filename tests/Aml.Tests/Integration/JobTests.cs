using System.Text.Json;
using Aml.Core.Cases;
using Aml.Core.Ports;
using Aml.Core.Retrieval;
using Aml.Core.Screening;
using Aml.Infra.Html;
using Aml.Infra.Jobs;
using Aml.Infra.Persistence;
using Aml.Infra.Persistence.Repositories;
using Aml.Infra.Txt;
using Aml.Services;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Aml.Tests.Integration;

/// <summary>Job controls on the job table: unreadable payloads, deadlines and lease ownership.</summary>
public sealed class JobTests : ServiceTestBase
{
    [Theory]
    [InlineData(BatchService.AnalyzeCase, "not json")]
    [InlineData(BatchService.AnalyzeCase, "{}")]
    [InlineData(BatchService.ReanalyzeCase, "null")]
    [InlineData("UNKNOWN_KIND", "{}")]
    public async Task An_unreadable_payload_dead_letters_at_once(string jobType, string payload)
    {
        var ct = TestContext.Current.CancellationToken;
        await _jobs.EnqueueAsync("j1", jobType, "b1", "k1", payload, T0, ct);
        using var batch = Batch();
        Assert.Equal(1, await batch.RunOnceAsync(ct));
        var item = Assert.Single(await _jobs.ListItemsAsync("b1", ct));
        Assert.Equal((JobStatus.DeadLetter, BatchService.InvalidPayload, 1), (item.Status, item.ErrorCode, item.AttemptCount));
    }

    [Fact]
    public async Task An_analysis_past_its_deadline_is_not_written()
    {
        var ct = TestContext.Current.CancellationToken;
        using var batch = Batch(new BatchOptions { Timeout = TimeSpan.Zero, RetryDelay = TimeSpan.Zero });
        var b = await batch.SubmitAsync([new BatchItemRequest("subject-11", "event-11", CaseFiles())], ct);
        await batch.RunOnceAsync(ct);

        var item = (await batch.GetAsync(b.BatchId, ct))!.Items[0];
        Assert.Equal((JobStatus.Pending, BatchService.Timeout), (item.Status, item.ErrorCode));
        await using var conn = await _db.OpenAsync(ct);
        Assert.Equal(0, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM case_analysis"));
    }

    [Fact]
    public async Task Batch_items_page_oldest_first_and_filter_by_view_or_status()
    {
        var ct = TestContext.Current.CancellationToken;
        using var batch = Batch();
        string[] ids = ["j0", "j1", "j2", "j3", "j4"];
        int[] seconds = [0, 1, 2, 2, 3]; // j2 and j3 tie on created_at: the job id breaks it
        foreach (var (id, s) in ids.Zip(seconds))
            await _jobs.EnqueueAsync(id, "T", "b1", id, "{}", T0.AddSeconds(s), ct);
        await using (var conn = await _db.OpenAsync(ct))
            await conn.ExecuteAsync("UPDATE job SET status = 'SUCCEEDED' WHERE job_id = 'j0'; UPDATE job SET status = 'DEAD_LETTER' WHERE job_id = 'j3';");

        async Task<List<string>> All(BatchView view, JobStatus? status = null)
        {
            var seen = new List<string>();
            (DateTimeOffset, string)? after = null;
            while (true)
            {
                var page = (await batch.GetAsync("b1", ct, view, status, 2, after))!;
                Assert.InRange(page.Items.Count, 1, 2);
                Assert.Equal((3, 0, 1, 1), (page.Pending, page.Running, page.Succeeded, page.DeadLetter));
                seen.AddRange(page.Items.Select(i => i.JobId));
                if (page.NextPageToken is null) return seen;
                Assert.True(PageToken.TryDecode(page.NextPageToken, out var key));
                after = key;
            }
        }

        Assert.Equal(ids, await All(BatchView.Full));
        Assert.Equal(["j1", "j2", "j3", "j4"], await All(BatchView.Summary));
        Assert.Equal(["j3"], await All(BatchView.Full, JobStatus.DeadLetter));
        Assert.Null(await batch.GetAsync("unknown", ct));
    }

    [Fact]
    public async Task Only_the_lease_owner_can_renew_complete_or_fail()
    {
        var ct = TestContext.Current.CancellationToken;
        await _jobs.EnqueueAsync("j1", "T", "b1", "k1", "{}", T0, ct);
        await _jobs.ClaimAsync("a", 1, T0, T0.AddMinutes(1), ct);
        Assert.False(await _jobs.RenewLeaseAsync("j1", "b", T0.AddMinutes(5), ct));
        Assert.False(await _jobs.CompleteAsync("j1", "b", "c", 1, T0, ct));
        Assert.False(await _jobs.FailAsync("j1", "b", true, "X", T0, T0, ct));
        Assert.True(await _jobs.RenewLeaseAsync("j1", "a", T0.AddMinutes(5), ct));
        Assert.Equal(0, await _jobs.ReclaimExpiredAsync(3, T0.AddMinutes(2), ct)); // renewed past the original expiry
    }

    [Fact]
    public async Task A_job_another_worker_claimed_after_the_list_was_read_is_not_claimed_again()
    {
        var ct = TestContext.Current.CancellationToken;
        await _jobs.EnqueueAsync("j1", "T", "b1", "k1", "{}", T0, ct);
        await _jobs.EnqueueAsync("j2", "T", "b1", "k2", "{}", T0.AddSeconds(1), ct);
        // Worker "b" takes j2 between worker "a" reading the claimable list and claiming j2.
        await using (var conn = await _db.OpenAsync(ct))
            await conn.ExecuteAsync("""
                CREATE TRIGGER other_worker AFTER UPDATE OF status ON job WHEN NEW.job_id = 'j1' AND NEW.status = 'RUNNING'
                BEGIN UPDATE job SET status = 'RUNNING', lease_owner = 'b', lease_expiry = NEW.lease_expiry, attempt_count = attempt_count + 1 WHERE job_id = 'j2'; END;
                """);

        var claimed = await _jobs.ClaimAsync("a", 2, T0.AddMinutes(1), T0.AddMinutes(5), ct);

        Assert.Equal(["j1"], claimed.Select(j => j.JobId));
        Assert.All(await _jobs.ListItemsAsync("b1", ct), i => Assert.Equal((JobStatus.Running, 1), (i.Status, i.AttemptCount)));
        Assert.False(await _jobs.RenewLeaseAsync("j2", "a", T0.AddMinutes(6), ct));
        Assert.True(await _jobs.RenewLeaseAsync("j2", "b", T0.AddMinutes(6), ct));
    }

    [Fact]
    public async Task A_job_that_lost_its_lease_before_writing_stores_no_revision()
    {
        var ct = TestContext.Current.CancellationToken;
        using var batch = new BatchService(new LeaseLostQueue(_jobs), _caseService, TxtCaseReader.ReadFiles,
            new BatchOptions { RetryDelay = TimeSpan.Zero }, TimeProvider.System, _listService);
        var b = await batch.SubmitAsync([new BatchItemRequest("subject-11", "event-11", CaseFiles())], ct);

        Assert.Equal(1, await batch.RunOnceAsync(ct));

        var item = Assert.Single(await _jobs.ListItemsAsync(b.BatchId, ct));
        Assert.Equal((JobStatus.Pending, 1, null), (item.Status, item.AttemptCount, item.AnalysisRevision));
        await using var conn = await _db.OpenAsync(ct);
        Assert.Equal(0, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM case_analysis"));
        Assert.Equal(0, await conn.ExecuteScalarAsync<long>("SELECT current_revision FROM aml_case"));
    }

    /// <summary>The job table as seen by a worker whose lease has passed to another worker: renewing it fails.</summary>
    private sealed class LeaseLostQueue(IJobStore inner) : IJobStore
    {
        public Task<bool> RenewLeaseAsync(string jobId, string leaseOwner, DateTimeOffset leaseExpiry, CancellationToken ct) => Task.FromResult(false);
        public Task<bool> EnqueueAsync(string jobId, string jobType, string batchId, string idempotencyKey, string payload, DateTimeOffset now, CancellationToken ct) =>
            inner.EnqueueAsync(jobId, jobType, batchId, idempotencyKey, payload, now, ct);
        public Task<IReadOnlyList<ClaimedJob>> ClaimAsync(string leaseOwner, int take, DateTimeOffset now, DateTimeOffset leaseExpiry, CancellationToken ct) =>
            inner.ClaimAsync(leaseOwner, take, now, leaseExpiry, ct);
        public Task<bool> CompleteAsync(string jobId, string leaseOwner, string caseId, int revision, DateTimeOffset now, CancellationToken ct) =>
            inner.CompleteAsync(jobId, leaseOwner, caseId, revision, now, ct);
        public Task<bool> FailAsync(string jobId, string leaseOwner, bool deadLetter, string errorCode, DateTimeOffset nextAttemptAt, DateTimeOffset now, CancellationToken ct) =>
            inner.FailAsync(jobId, leaseOwner, deadLetter, errorCode, nextAttemptAt, now, ct);
        public Task<int> ReclaimExpiredAsync(int maxAttempts, DateTimeOffset now, CancellationToken ct) => inner.ReclaimExpiredAsync(maxAttempts, now, ct);
        public Task<IReadOnlyList<JobItem>> ListItemsAsync(string batchId, CancellationToken ct,
            IReadOnlyCollection<JobStatus>? statuses = null, (DateTimeOffset At, string Id)? after = null, int limit = int.MaxValue) =>
            inner.ListItemsAsync(batchId, ct, statuses, after, limit);
        public Task<IReadOnlyDictionary<JobStatus, int>> CountsAsync(string batchId, CancellationToken ct) => inner.CountsAsync(batchId, ct);
        public Task<int> RerunAsync(string batchId, DateTimeOffset now, CancellationToken ct) => inner.RerunAsync(batchId, now, ct);
    }
}
