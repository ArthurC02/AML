using Aml.Core.Cases;
using Aml.Core.Screening;
using Aml.Infra.Persistence;
using Aml.Infra.Persistence.Repositories;
using Microsoft.Data.Sqlite;

namespace Aml.Tests.Persistence;

/// <summary>Repository contract: the same suite must pass on SQL Server or Oracle before either replaces SQLite.</summary>
public sealed class CaseStoreTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"aml-test-{Guid.NewGuid():N}.db");
    private readonly CaseStore _store;
    private static readonly DateTimeOffset T0 = new(2026, 11, 2, 0, 0, 0, TimeSpan.Zero);

    public CaseStoreTests()
    {
        var db = new SqliteDatabase($"Data Source={_file};Pooling=False");
        db.Upgrade();
        _store = new CaseStore(db);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_file);
    }

    private static CaseRecord NewCase(string trigger = "evt-1") =>
        new(Guid.CreateVersion7().ToString(), "subject-1", trigger, CaseStatus.Open, 0, null, T0, T0);

    private static AnalysisRecord Analysis(string caseId, int revision) =>
        new(caseId, revision, ExecutionStatus.Completed, ScreeningAssessment.Review, "{\"hits\":[]}", "{}", T0.AddMinutes(revision));

    [Fact]
    public async Task Round_trip_and_idempotent_create()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = await _store.CreateAsync(NewCase(), ct);
        var again = await _store.CreateAsync(NewCase(), ct);

        Assert.Equal(first.CaseId, again.CaseId);
        var loaded = await _store.GetAsync(first.CaseId, ct);
        Assert.Equal(first, loaded);
    }

    [Fact]
    public async Task Accepting_a_revision_moves_the_case_and_a_late_revision_never_overwrites()
    {
        var ct = TestContext.Current.CancellationToken;
        var c = await _store.CreateAsync(NewCase(), ct);

        Assert.True(await _store.AcceptAnalysisAsync(Analysis(c.CaseId, 2), CaseStatus.InReview, ct));
        Assert.False(await _store.AcceptAnalysisAsync(Analysis(c.CaseId, 1), CaseStatus.Closed, ct));

        var loaded = (await _store.GetAsync(c.CaseId, ct))!;
        Assert.Equal(2, loaded.CurrentRevision);
        Assert.Equal(2, loaded.EffectiveRevision);
        Assert.Equal(CaseStatus.InReview, loaded.Status);
        // The late revision is kept as history.
        Assert.NotNull(await _store.GetAnalysisAsync(c.CaseId, 1, ct));
        Assert.Equal(ScreeningAssessment.Review, (await _store.GetAnalysisAsync(c.CaseId, 2, ct))!.CaseResult);
    }
}
