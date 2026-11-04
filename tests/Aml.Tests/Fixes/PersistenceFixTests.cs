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

namespace Aml.Tests.Fixes;

/// <summary>WP-E: revision numbering, one-transaction writes, queued re-analysis, WAL, job controls, Profile hashing, retrieval cache.</summary>
public sealed class PersistenceFixTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"aml-fix-e-{Guid.NewGuid():N}.db");
    private readonly SqliteDatabase _db;
    private readonly CaseStore _cases;
    private readonly ListStore _lists;
    private readonly JobStore _jobs;
    private readonly CaseService _caseService;
    private readonly ListService _listService;
    private static readonly DateTimeOffset T0 = new(2026, 11, 4, 0, 0, 0, TimeSpan.Zero);

    private static readonly string Gc11 = GoldenCases.GoldenCaseTests.Folder("GC-11");
    private const string ProfileName = "名單資料_90000011.txt";

    public PersistenceFixTests()
    {
        _db = new SqliteDatabase($"Data Source={_file};Pooling=False");
        _db.Upgrade();
        _cases = new CaseStore(_db);
        _lists = new ListStore(_db);
        _jobs = new JobStore(_db);
        _caseService = new CaseService(_cases, TimeProvider.System);
        _listService = new ListService(_lists, _cases, _caseService, TxtCaseReader.ParseProfile, TxtCaseReader.ReadFiles, TimeProvider.System,
            c => c.TrimStart().TrimStart('﻿').StartsWith('<') ? HtmlExport.ToExportText(c, HtmlExport.Kind.Profile) : c);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_file);
    }

    private static Dictionary<string, string> CaseFiles() =>
        Directory.GetFiles(Gc11, "*.txt").ToDictionary(p => Path.GetFileName(p), File.ReadAllText);

    private BatchService Batch(BatchOptions? options = null) =>
        new(_jobs, _caseService, TxtCaseReader.ReadFiles, options ?? new BatchOptions(), TimeProvider.System, _listService);

    private static AnalysisRecord Failed(string caseId, int revision) =>
        new(caseId, revision, ExecutionStatus.Failed, ScreeningAssessment.NotEvaluated, "{}", null, T0);

    // P1
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
    public async Task A_taken_revision_number_is_refused_and_nothing_is_stored()
    {
        var ct = TestContext.Current.CancellationToken;
        var c = await _cases.CreateAsync(new CaseRecord("case-p1b", "s", "e", CaseStatus.Open, 0, null, T0, T0), ct);
        Assert.True(await _cases.AcceptAnalysisAsync(Failed(c.CaseId, 1), CaseStatus.Open, new Dictionary<string, string> { ["a"] = "first" }, ct));
        Assert.False(await _cases.AcceptAnalysisAsync(Failed(c.CaseId, 1), CaseStatus.Open, new Dictionary<string, string> { ["a"] = "second" }, ct));
        Assert.Equal("first", (await _cases.LatestInputAsync(c.CaseId, ct))!["a"]);
    }

    // P2
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

    // P3
    [Fact]
    public async Task A_changed_profile_queues_one_reanalysis_job_per_case_and_the_job_adds_a_revision()
    {
        var ct = TestContext.Current.CancellationToken;
        var files = CaseFiles();
        var c = await _caseService.CreateAsync("subject-11", "event-11", TxtCaseReader.ReadFiles(files), ct, files);

        var updated = files[ProfileName].Replace("Date of Birth\t1973", "Date of Birth\t1973/6/12", StringComparison.Ordinal);
        var report = await _listService.ImportAsync(new Dictionary<string, string> { [ProfileName] = updated }, ct);
        var job = Assert.Single(report.QueuedReanalyses);
        Assert.Equal((c.CaseId, "90000011", 1), (job.CaseId, job.ProfileId, job.Version));
        var item = Assert.Single(await _jobs.ListItemsAsync(report.BatchId!, ct));
        Assert.Equal(($"REANALYZE:{c.CaseId}:90000011:1", JobStatus.Pending), (item.IdempotencyKey, item.Status));
        Assert.Equal(1, (await _cases.GetAsync(c.CaseId, ct))!.CurrentRevision); // nothing runs inside the import

        using var batch = Batch();
        Assert.Equal(1, await batch.RunOnceAsync(ct));
        Assert.Equal(2, (await _cases.GetAsync(c.CaseId, ct))!.CurrentRevision);
        Assert.Equal((JobStatus.Succeeded, 2), ((await _jobs.ListItemsAsync(report.BatchId!, ct))[0].Status, (await _jobs.ListItemsAsync(report.BatchId!, ct))[0].AnalysisRevision));
        Assert.Equal(updated, (await _cases.LatestInputAsync(c.CaseId, ct))![ProfileName]);

        // The same file again: unchanged, nothing queued.
        var again = await _listService.ImportAsync(new Dictionary<string, string> { [ProfileName] = updated }, ct);
        Assert.Equal((false, 0, null), (again.Profiles[0].Changed, again.QueuedReanalyses.Count, again.BatchId));
    }

    // P4
    [Fact]
    public async Task The_database_runs_in_wal_mode()
    {
        await using var conn = await _db.OpenAsync(TestContext.Current.CancellationToken);
        Assert.Equal("wal", await conn.ExecuteScalarAsync<string>("PRAGMA journal_mode;"));
    }

    // P5
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

    // P6
    private const string ProfileTxt = "Profile_ID:\t123\nGender:\tMale\n";
    private const string ProfileHtml = "<html><body><table><tr><td>Profile_ID</td><td>123</td></tr><tr><td>Gender</td><td>Male</td></tr></table></body></html>";

    [Fact]
    public async Task Crlf_lf_bom_and_html_copies_of_one_profile_are_one_version()
    {
        var ct = TestContext.Current.CancellationToken;
        await _listService.ImportAsync(new Dictionary<string, string> { ["名單資料_123.txt"] = ProfileTxt }, ct);
        await _listService.ImportAsync(new Dictionary<string, string> { ["名單資料_123.txt"] = "﻿" + ProfileTxt.Replace("\n", "\r\n", StringComparison.Ordinal) }, ct);
        var html = await _listService.ImportAsync(new Dictionary<string, string> { ["名單資料_123.html"] = ProfileHtml }, ct);

        Assert.Equal((1, false), (html.Profiles[0].Version, html.Profiles[0].Changed));
        Assert.Single(await _listService.HistoryAsync("123", ct));
    }

    [Fact]
    public async Task A_profile_id_mismatch_or_two_formats_in_one_import_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var mismatch = await _listService.ImportAsync(new Dictionary<string, string> { ["名單資料_999.txt"] = ProfileTxt }, ct);
        Assert.Equal(["名單資料_999.txt"], mismatch.Rejected);

        var twoFormats = await _listService.ImportAsync(new Dictionary<string, string> { ["名單資料_123.txt"] = ProfileTxt, ["名單資料_123.html"] = ProfileHtml }, ct);
        Assert.Equal(2, twoFormats.Rejected.Count);
        Assert.Empty(twoFormats.Profiles);
        Assert.Empty(await _listService.HistoryAsync("123", ct));
    }

    [Fact]
    public async Task Reanalysis_replaces_the_profile_by_id_whatever_its_file_name()
    {
        var ct = TestContext.Current.CancellationToken;
        var files = CaseFiles();
        var html = files[ProfileName];
        files.Remove(ProfileName);
        files["名單資料_90000011.TXT"] = html; // same Profile under another spelling of the name
        var c = await _caseService.CreateAsync("subject-11", "event-11", TxtCaseReader.ReadFiles(files), ct, files);

        var updated = html.Replace("Date of Birth\t1973", "Date of Birth\t1973/6/12", StringComparison.Ordinal);
        var report = await _listService.ImportAsync(new Dictionary<string, string> { [ProfileName] = updated }, ct);
        Assert.Single(report.QueuedReanalyses);
        var after = await _listService.ReanalyzeWithLatestProfilesAsync(c.CaseId, ct);

        Assert.Equal(2, after.CurrentRevision);
        var input = (await _cases.LatestInputAsync(c.CaseId, ct))!;
        Assert.Equal(updated, input[ProfileName]);
        Assert.DoesNotContain("名單資料_90000011.TXT", input.Keys);
    }

    // P7
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

    // R-cache
    [Fact]
    public async Task Direct_reference_accepts_the_dj_prefix_and_the_index_follows_new_captures()
    {
        var ct = TestContext.Current.CancellationToken;
        var profile = CaseFiles()[ProfileName];
        await _listService.ImportAsync(new Dictionary<string, string> { [ProfileName] = profile }, ct);

        var direct = await _listService.RetrieveAsync(RetrievalMode.DirectReference, null, "DJ:90000011", null, 10, ct);
        Assert.Equal("DJ:90000011", Assert.Single(direct).ProfileKey);

        Assert.Empty(await _listService.RetrieveAsync(RetrievalMode.IndexSearch, "WANG Wei", null, null, 10, ct));
        await _listService.ImportAsync(new Dictionary<string, string> { ["名單資料_123.txt"] = "Profile_ID:\t123\n主要名稱:\n1\tWei\tWANG\n" }, ct);
        var search = await _listService.RetrieveAsync(RetrievalMode.IndexSearch, "WANG Wei", null, null, 10, ct);
        Assert.Contains(search, s => s.ProfileKey == "DJ:123");
    }
}
