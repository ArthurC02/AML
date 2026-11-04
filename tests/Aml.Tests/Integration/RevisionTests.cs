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

/// <summary>Revision numbering, one-transaction writes, WAL, deadlines and comparison with the effective revision.</summary>
public sealed class RevisionTests : ServiceTestBase
{
    [Fact]
    public async Task Two_concurrent_analyses_of_one_case_both_succeed_as_revisions_1_and_2()
    {
        var ct = TestContext.Current.CancellationToken;
        var files = CaseFiles();
        var input = TxtCaseReader.ReadFiles(files);
        var c = await _cases.CreateAsync(new CaseRecord("case-p1", "s", "e", CaseStatus.Open, 0, null, T0, T0), ct);

        var results = await Task.WhenAll(
            Task.Run(() => _caseService.ReanalyzeAsync(c.CaseId, input, ct, files), ct),
            Task.Run(() => _caseService.ReanalyzeAsync(c.CaseId, input, ct, files), ct));

        Assert.Equal([1, 2], results.Select(r => r!.CurrentRevision).Order());
        Assert.Equal(2, (await _cases.GetAsync(c.CaseId, ct))!.CurrentRevision);
        Assert.NotNull(await _cases.GetAnalysisAsync(c.CaseId, 1, ct));
        Assert.NotNull(await _cases.GetAnalysisAsync(c.CaseId, 2, ct));
    }

    [Fact]
    public async Task Concurrent_creates_for_one_trigger_store_one_first_revision()
    {
        var ct = TestContext.Current.CancellationToken;
        var files = CaseFiles();
        var input = TxtCaseReader.ReadFiles(files);

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            Task.Run(() => _caseService.CreateAsync("s", "evt-race", input, ct, files), ct)));

        Assert.Single(results.Select(r => r.CaseId).Distinct());
        Assert.All(results, r => Assert.Equal(1, r.CurrentRevision));
        Assert.Equal(1, (await _cases.GetAsync(results[0].CaseId, ct))!.CurrentRevision);
        Assert.Null(await _cases.GetAnalysisAsync(results[0].CaseId, 2, ct));
    }

    [Fact]
    public async Task A_case_left_at_revision_0_gets_its_first_analysis_on_retry()
    {
        var ct = TestContext.Current.CancellationToken;
        var files = CaseFiles();
        var interrupted = await _cases.CreateAsync(new CaseRecord("case-r0", "s", "evt-r0", CaseStatus.Open, 0, null, T0, T0), ct);

        var resumed = await _caseService.CreateAsync("s", "evt-r0", TxtCaseReader.ReadFiles(files), ct, files);

        Assert.Equal((interrupted.CaseId, 1), (resumed.CaseId, resumed.CurrentRevision));
    }

    [Fact]
    public async Task A_taken_revision_number_is_refused_and_nothing_is_stored()
    {
        var ct = TestContext.Current.CancellationToken;
        var c = await _cases.CreateAsync(new CaseRecord("case-p1b", "s", "e", CaseStatus.Open, 0, null, T0, T0), ct);
        Assert.True(await _cases.AcceptAnalysisAsync(Failed(c.CaseId, 1), CaseStatus.Open, new Dictionary<string, string> { ["a"] = "first" }, ct));
        Assert.False(await _cases.AcceptAnalysisAsync(Failed(c.CaseId, 1), CaseStatus.Open, new Dictionary<string, string> { ["a"] = "second" }, ct));
        Assert.Equal("first", (await _cases.LatestInputAsync(c.CaseId, ct))!["a"]);
    }

    [Fact]
    public async Task A_failed_input_write_leaves_no_revision()
    {
        var ct = TestContext.Current.CancellationToken;
        var c = await _cases.CreateAsync(new CaseRecord("case-p2", "s", "e", CaseStatus.Open, 0, null, T0, T0), ct);
        await using (var conn = await _db.OpenAsync(ct))
            await conn.ExecuteAsync("CREATE TRIGGER fail_input BEFORE INSERT ON case_input BEGIN SELECT RAISE(ABORT, 'injected'); END;");

        await Assert.ThrowsAsync<SqliteException>(() =>
            _cases.AcceptAnalysisAsync(Failed(c.CaseId, 1), CaseStatus.Open, new Dictionary<string, string> { ["a"] = "x" }, ct));

        Assert.Null(await _cases.GetAnalysisAsync(c.CaseId, 1, ct));
        Assert.Equal(0, (await _cases.GetAsync(c.CaseId, ct))!.CurrentRevision);
    }

    [Fact]
    public async Task The_database_runs_in_wal_mode()
    {
        await using var conn = await _db.OpenAsync(TestContext.Current.CancellationToken);
        Assert.Equal("wal", await conn.ExecuteScalarAsync<string>("PRAGMA journal_mode;"));
    }

    [Fact]
    public async Task Reanalysis_after_a_failed_revision_compares_with_the_effective_one()
    {
        var ct = TestContext.Current.CancellationToken;
        var files = CaseFiles();
        var input = TxtCaseReader.ReadFiles(files);
        var c = await _caseService.CreateAsync("subject-11", "event-11", input, ct, files);
        Assert.True(await _cases.AcceptAnalysisAsync(Failed(c.CaseId, 2), CaseStatus.InReview, files, ct));

        var after = await _caseService.ReanalyzeAsync(c.CaseId, input, ct, files);

        Assert.Equal((3, 3), (after!.CurrentRevision, after.EffectiveRevision));
        using var explanation = JsonDocument.Parse((await _cases.GetAnalysisAsync(c.CaseId, 3, ct))!.ExplanationJson!);
        Assert.Equal(0, explanation.RootElement.GetProperty("changesFromPreviousRevision").GetArrayLength()); // same input as revision 1
    }

    [Fact]
    public async Task Reanalysis_when_the_only_previous_revision_failed_does_not_throw()
    {
        var ct = TestContext.Current.CancellationToken;
        var files = CaseFiles();
        var c = await _cases.CreateAsync(new CaseRecord("case-p7", "s", "e", CaseStatus.Open, 0, null, T0, T0), ct);
        Assert.True(await _cases.AcceptAnalysisAsync(Failed(c.CaseId, 1), CaseStatus.Open, files, ct));

        var after = await _caseService.ReanalyzeAsync(c.CaseId, TxtCaseReader.ReadFiles(files), ct, files);
        Assert.Equal(2, after!.CurrentRevision);
    }

    [Fact]
    public async Task A_partial_analysis_is_stored_but_does_not_move_the_effective_revision()
    {
        var ct = TestContext.Current.CancellationToken;
        var files = CaseFiles();
        var c = await _caseService.CreateAsync("subject-11", "event-11", TxtCaseReader.ReadFiles(files), ct, files);
        files.Remove(ProfileName); // the candidate Profile cannot be evaluated

        var after = await _caseService.ReanalyzeAsync(c.CaseId, TxtCaseReader.ReadFiles(files), ct, files);

        Assert.Equal((2, 1), (after!.CurrentRevision, after.EffectiveRevision));
        var stored = (await _cases.GetAsync(c.CaseId, ct))!;
        Assert.Equal((2, 1), (stored.CurrentRevision, stored.EffectiveRevision));
        Assert.Equal(ExecutionStatus.Partial, (await _cases.GetAnalysisAsync(c.CaseId, 2, ct))!.ExecutionStatus);
    }

    [Fact]
    public async Task Upgrading_a_migrated_database_again_changes_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var files = CaseFiles();
        var c = await _caseService.CreateAsync("subject-11", "event-11", TxtCaseReader.ReadFiles(files), ct, files);
        async Task<(string Schema, string Journal)> SnapshotAsync()
        {
            await using var conn = await _db.OpenAsync(ct);
            var schema = await conn.QueryAsync<string>("SELECT type || ' ' || name || ' ' || COALESCE(sql, '') FROM sqlite_master ORDER BY type, name");
            var journal = await conn.QueryAsync<string>("SELECT ScriptName FROM SchemaVersions ORDER BY SchemaVersionID");
            return (string.Join('\n', schema), string.Join('\n', journal));
        }
        var before = await SnapshotAsync();

        _db.Upgrade();

        Assert.Equal(before, await SnapshotAsync());
        Assert.Equal(c, await _cases.GetAsync(c.CaseId, ct));
        Assert.NotNull(await _cases.GetAnalysisAsync(c.CaseId, 1, ct));
    }
}
