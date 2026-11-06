using System.Text.Json;
using Aml.Core.Cases;
using Aml.Core.Screening;
using Aml.Infra.Persistence;
using Aml.Infra.Persistence.Repositories;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Aml.Tests.Integration;

/// <summary>Repository contract: the same suite must pass on SQL Server or Oracle before either replaces SQLite.</summary>
public sealed class CaseStoreTests : IDisposable
{
    private readonly TestDatabase _test = new();
    private readonly CaseStore _store;
    private static readonly DateTimeOffset T0 = new(2026, 11, 2, 0, 0, 0, TimeSpan.Zero);

    public CaseStoreTests()
    {
        _store = new CaseStore(_test.Db);
    }

    public void Dispose() => _test.Dispose();

    private static CaseRecord NewCase(string trigger = "evt-1") =>
        new(Guid.CreateVersion7().ToString(), "subject-1", trigger, CaseStatus.Open, 0, null, T0, T0);

    private static AnalysisRecord Analysis(string caseId, int revision) =>
        new(caseId, revision, ExecutionStatus.Completed, ScreeningAssessment.Review, new CaseAnalysis([], [], []), null, null, null, T0.AddMinutes(revision));

    [Theory]
    [InlineData(0)] // an earlier release, before the format was recorded
    [InlineData(CaseJson.SchemaVersion + 1)]
    public void A_database_of_another_format_is_refused_and_this_format_upgrades_again(int other)
    {
        _test.Db.Upgrade();
        using (var c = new SqliteConnection(_test.ConnectionString))
        {
            c.Open();
            Assert.Equal(CaseJson.SchemaVersion, c.ExecuteScalar<long>("PRAGMA user_version;"));
            c.Execute($"PRAGMA user_version = {other};");
        }

        var e = Assert.Throws<InvalidOperationException>(_test.Db.Upgrade);
        Assert.Contains("Delete the database file", e.Message, StringComparison.Ordinal);
    }

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

        Assert.True(await _store.AcceptAnalysisAsync(Analysis(c.CaseId, 2), CaseStatus.InReview, null, ct));
        Assert.False(await _store.AcceptAnalysisAsync(Analysis(c.CaseId, 1), CaseStatus.Closed, null, ct));

        var loaded = (await _store.GetAsync(c.CaseId, ct))!;
        Assert.Equal(2, loaded.CurrentRevision);
        Assert.Equal(2, loaded.EffectiveRevision);
        Assert.Equal(CaseStatus.InReview, loaded.Status);
        // The late revision is kept as history.
        Assert.NotNull(await _store.GetAnalysisAsync(c.CaseId, 1, ct));
        Assert.Equal(ScreeningAssessment.Review, (await _store.GetAnalysisAsync(c.CaseId, 2, ct))!.CaseResult);
    }

    [Fact]
    public async Task Lineage_quality_revisions_and_search_round_trip()
    {
        var ct = TestContext.Current.CancellationToken;
        var c = await _store.CreateAsync(NewCase(), ct);
        var other = await _store.CreateAsync(NewCase("evt-2"), ct);
        var lineage = new AnalysisLineage([new InputFile("a.txt", "h1")], [new ProfileUse("1", AnalysisLineage.ListDatabase, 2, "h2")],
            "policy", "codes", CaseJson.SchemaVersion, "app", new DateOnly(2026, 11, 4));
        var quality = new AnalysisQuality(1, 9, 3, 1, 0, 3 / 9.0, 1 / 9.0, 0, 0, new Dictionary<string, int> { ["F"] = 2 });
        var stored = Analysis(c.CaseId, 1) with { Lineage = lineage, Quality = quality };
        Assert.True(await _store.AcceptAnalysisAsync(stored, CaseStatus.InReview, null, ct));
        Assert.True(await _store.AcceptAnalysisAsync(Analysis(c.CaseId, 2) with { ExecutionStatus = ExecutionStatus.Partial }, CaseStatus.InReview, null, ct));

        var loaded = (await _store.GetAnalysisAsync(c.CaseId, 1, ct))!;
        Assert.Equal(JsonSerializer.Serialize(stored, CaseJson.Options), JsonSerializer.Serialize(loaded, CaseJson.Options));
        Assert.Equal([(1, true), (2, false)], (await _store.RevisionsAsync(c.CaseId, ct)).Select(r => (r.Revision, r.Effective)));

        Assert.Equal(other.CaseId, Assert.Single(await _store.SearchAsync(null, "evt-2", null, 10, ct)).CaseId);
        var first = Assert.Single(await _store.SearchAsync("subject-1", null, null, 1, ct));
        var next = Assert.Single(await _store.SearchAsync("subject-1", null, (first.CreatedAt, first.CaseId), 10, ct));
        Assert.Equal(new[] { c.CaseId, other.CaseId }.Order(StringComparer.Ordinal), new[] { first.CaseId, next.CaseId });
    }
}
