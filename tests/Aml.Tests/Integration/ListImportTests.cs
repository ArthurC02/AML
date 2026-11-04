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

/// <summary>Profile import: hashing across formats, queued re-analysis and the retrieval cache.</summary>
public sealed class ListImportTests : ServiceTestBase
{
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

    [Fact]
    public async Task Direct_reference_accepts_the_dj_prefix_and_the_index_follows_new_captures()
    {
        var ct = TestContext.Current.CancellationToken;
        var profile = CaseFiles()[ProfileName];
        await _listService.ImportAsync(new Dictionary<string, string> { [ProfileName] = profile }, ct);

        var direct = (await _listService.GetProfileAsync("DJ:90000011", null, ct))!;
        Assert.Equal(("90000011", "DJ:90000011", 1, 1), (direct.ProfileId, direct.ProfileKey, direct.Version, direct.LatestVersion));
        Assert.Null(await _listService.GetProfileAsync("90000011", 2, ct));

        Assert.Empty(await _listService.SearchAsync("WANG Wei", 10, ct));
        await _listService.ImportAsync(new Dictionary<string, string> { ["名單資料_123.txt"] = "Profile_ID:\t123\n主要名稱:\n1\tWei\tWANG\n" }, ct);
        var search = await _listService.SearchAsync("WANG Wei", 10, ct);
        Assert.Contains(search, s => (s.ProfileKey, s.ProfileId) == ("DJ:123", "123"));
    }

    [Fact]
    public async Task An_import_compares_with_the_latest_input_of_a_case_not_an_earlier_one()
    {
        var ct = TestContext.Current.CancellationToken;
        var files = CaseFiles();
        var original = files[ProfileName];
        var c = await _caseService.CreateAsync("subject-11", "event-11", TxtCaseReader.ReadFiles(files), ct, files);
        files[ProfileName] = original.Replace("Date of Birth\t1973", "Date of Birth\t1973/6/12", StringComparison.Ordinal);
        await _caseService.ReanalyzeAsync(c.CaseId, TxtCaseReader.ReadFiles(files), ct, files);

        // The imported content equals revision 1's Profile but differs from revision 2's, the latest.
        var report = await _listService.ImportAsync(new Dictionary<string, string> { [ProfileName] = original }, ct);

        var job = Assert.Single(report.QueuedReanalyses);
        Assert.Equal((c.CaseId, "90000011", 1), (job.CaseId, job.ProfileId, job.Version));
        using var batch = Batch();
        Assert.Equal(1, await batch.RunOnceAsync(ct));
        Assert.Equal(3, (await _cases.GetAsync(c.CaseId, ct))!.CurrentRevision);
        Assert.Equal(original, (await _cases.LatestInputAsync(c.CaseId, ct))![ProfileName]);
    }
}
