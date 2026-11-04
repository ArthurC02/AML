using Aml.Core.Ports;
using Aml.Core.Subjects;
using Aml.Infra.Jobs;
using Aml.Infra.Persistence;
using Aml.Infra.Persistence.Repositories;
using Aml.Services;
using Microsoft.Data.Sqlite;

namespace Aml.Tests.Persistence;

/// <summary>Job table contract and batch controls: exclusive claim, idempotency, retry budget, dead-letter, lease reclaim, re-run.</summary>
public sealed class BatchTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"aml-batch-{Guid.NewGuid():N}.db");
    private readonly SqliteDatabase _db;
    private readonly JobStore _jobs;
    private static readonly DateTimeOffset T0 = new(2026, 11, 3, 0, 0, 0, TimeSpan.Zero);

    public BatchTests()
    {
        _db = new SqliteDatabase($"Data Source={_file};Pooling=False");
        _db.Upgrade();
        _jobs = new JobStore(_db);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_file);
    }

    [Fact]
    public async Task Claim_is_exclusive_and_idempotency_key_is_unique()
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.True(await _jobs.EnqueueAsync("j1", "T", "b1", "k1", "{}", T0, ct));
        Assert.False(await _jobs.EnqueueAsync("j2", "T", "b2", "k1", "{}", T0, ct));

        Assert.Single(await _jobs.ClaimAsync("a", 10, T0, T0.AddMinutes(5), ct));
        Assert.Empty(await _jobs.ClaimAsync("b", 10, T0, T0.AddMinutes(5), ct));
        Assert.Equal(1, (await _jobs.ListItemsAsync("b1", ct))[0].AttemptCount);
    }

    [Fact]
    public async Task Expired_lease_returns_to_queue_then_dead_letters_and_rerun_resets()
    {
        var ct = TestContext.Current.CancellationToken;
        await _jobs.EnqueueAsync("j1", "T", "b1", "k1", "{}", T0, ct);
        await _jobs.ClaimAsync("dead-worker", 1, T0, T0.AddMinutes(1), ct);

        Assert.Equal(1, await _jobs.ReclaimExpiredAsync(maxAttempts: 2, T0.AddMinutes(2), ct));
        Assert.Equal(JobStatus.Pending, (await _jobs.ListItemsAsync("b1", ct))[0].Status);

        await _jobs.ClaimAsync("dead-worker", 1, T0.AddMinutes(2), T0.AddMinutes(3), ct); // attempt 2 of 2
        await _jobs.ReclaimExpiredAsync(maxAttempts: 2, T0.AddMinutes(4), ct);
        var item = (await _jobs.ListItemsAsync("b1", ct))[0];
        Assert.Equal((JobStatus.DeadLetter, "LEASE_EXPIRED"), (item.Status, item.ErrorCode));

        Assert.Equal(1, await _jobs.RerunAsync("b1", T0.AddMinutes(5), ct));
        item = (await _jobs.ListItemsAsync("b1", ct))[0];
        Assert.Equal((JobStatus.Pending, 0, null), (item.Status, item.AttemptCount, item.ErrorCode));
    }

    private sealed class UnavailableStore : ICaseStore
    {
        public Task<Aml.Core.Cases.CaseRecord> CreateAsync(Aml.Core.Cases.CaseRecord record, CancellationToken ct) => throw new TimeoutException("database unavailable");
        public Task<Aml.Core.Cases.CaseRecord?> GetAsync(string caseId, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> AcceptAnalysisAsync(Aml.Core.Cases.AnalysisRecord analysis, Aml.Core.Cases.CaseStatus status, CancellationToken ct) => throw new NotSupportedException();
        public Task<Aml.Core.Cases.AnalysisRecord?> GetAnalysisAsync(string caseId, int revision, CancellationToken ct) => throw new NotSupportedException();
        public Task SaveInputAsync(string caseId, int revision, IReadOnlyDictionary<string, string> files, DateTimeOffset now, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<(string CaseId, int Revision, IReadOnlyDictionary<string, string> Files)>> LatestInputsAsync(CancellationToken ct) => throw new NotSupportedException();
    }

    private static BatchItemRequest Item(string file) => new("s1", "e1", new Dictionary<string, string> { [file] = "" });

    [Fact]
    public async Task Invalid_input_dead_letters_at_once_and_duplicates_are_not_queued()
    {
        var ct = TestContext.Current.CancellationToken;
        var batch = new BatchService(_jobs, new CaseService(new CaseStore(_db), TimeProvider.System),
            _ => throw new InvalidOperationException("unusable"), new BatchOptions(), TimeProvider.System);

        var b = await batch.SubmitAsync([Item("a.txt")], ct);
        Assert.Equal(1, await batch.RunOnceAsync(ct));
        var item = (await batch.GetAsync(b.BatchId, ct))!.Items[0];
        Assert.Equal((JobStatus.DeadLetter, BatchService.InvalidInput, 1), (item.Status, item.ErrorCode, item.AttemptCount));

        var again = await batch.SubmitAsync([Item("a.txt")], ct);
        Assert.Equal((0, "ANALYZE_CASE:e1:s1"), (again.Queued, Assert.Single(again.Duplicates)));
    }

    [Fact]
    public async Task Transient_failures_retry_until_the_budget_is_spent()
    {
        var ct = TestContext.Current.CancellationToken;
        var input = Aml.Infra.Txt.TxtCaseReader.Read(GoldenCases.GoldenCaseTests.Folder("GC-01"));
        var batch = new BatchService(_jobs, new CaseService(new UnavailableStore(), TimeProvider.System), _ => input,
            new BatchOptions { MaxAttempts = 2, RetryDelay = TimeSpan.Zero }, TimeProvider.System);

        var b = await batch.SubmitAsync([Item("a.txt")], ct);
        await batch.RunOnceAsync(ct);
        var item = (await batch.GetAsync(b.BatchId, ct))!.Items[0];
        Assert.Equal((JobStatus.Pending, "TimeoutException", 1), (item.Status, item.ErrorCode, item.AttemptCount));

        await batch.RunOnceAsync(ct);
        item = (await batch.GetAsync(b.BatchId, ct))!.Items[0];
        Assert.Equal((JobStatus.DeadLetter, 2), (item.Status, item.AttemptCount));
        Assert.Equal(0, await batch.RunOnceAsync(ct));
    }
}
