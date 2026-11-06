using Aml.Core.Cases;
using Aml.Core.Codes;
using Aml.Core.Screening;
using Aml.Infra.Txt;
using Dapper;

namespace Aml.Tests.Integration;

/// <summary>AI-ready data of a stored revision: the fact tables agree with the JSON, and the lineage names what it was computed from.</summary>
public sealed class AnalysisDataTests : ServiceTestBase
{
    private static Dictionary<string, string> Files(string gc) =>
        Directory.GetFiles(GoldenCases.GoldenCaseTests.Folder(gc), "*.txt").ToDictionary(p => Path.GetFileName(p), File.ReadAllText);

    private sealed record HitRow(long HitNumber, string ProfileId, string ConditionTypes, string LegalStage, string IdentityDecision, long? Stage3Row, string? HardConflict, string PairResult, string? ReviewPriority);

    private sealed record EvidenceRow(long HitNumber, string EvidenceGroup, string Direction, string ComparisonStatus, string Strength, string? DetailCode);

    private sealed record ReasonRow(long HitNumber, string ReasonKind, long Seq, string ReasonCode);

    [Theory]
    [InlineData("GC-01", "UNKNOWN,UNKNOWN")]
    [InlineData("GC-02", "ALLEGED")]
    [InlineData("GC-03", "WANTED")]
    [InlineData("GC-11", "UNKNOWN")]
    [InlineData("GC-12", "INDICTED")]
    public async Task Fact_tables_rebuild_every_decision_in_the_json(string gc, string legalStages)
    {
        var ct = TestContext.Current.CancellationToken;
        var files = Files(gc);
        var c = await _caseService.CreateAsync("s-" + gc, "e-" + gc, TxtCaseReader.ReadFiles(files), ct, files);
        var result = (await _cases.GetAnalysisAsync(c.CaseId, 1, ct))!.Result;

        await using var db = await _db.OpenAsync(ct);
        var key = new { caseId = c.CaseId };
        var hits = (await db.QueryAsync<HitRow>("SELECT hit_number, profile_id, condition_types, legal_stage, identity_decision, stage3_row, hard_conflict, pair_result, review_priority FROM case_hit WHERE case_id = @caseId ORDER BY hit_number", key)).ToList();
        var evidence = (await db.QueryAsync<EvidenceRow>("SELECT hit_number, evidence_group, direction, comparison_status, strength, detail_code FROM hit_evidence WHERE case_id = @caseId", key)).ToList();
        var reasons = (await db.QueryAsync<ReasonRow>("SELECT hit_number, reason_kind, seq, reason_code FROM hit_reason WHERE case_id = @caseId ORDER BY hit_number, reason_kind, seq", key)).ToList();
        static string E<T>(T v) where T : struct, Enum => System.Text.Json.JsonNamingPolicy.SnakeCaseUpper.ConvertName(v.ToString());
        string[] Codes(int n, string kind) => reasons.Where(r => r.HitNumber == n && r.ReasonKind == kind).Select(r => r.ReasonCode).ToArray();

        Assert.Equal(result.Hits.Count, hits.Count);
        Assert.Equal(legalStages, string.Join(",", hits.Select(h => h.LegalStage)));
        foreach (var (h, i) in result.Hits.Select((h, i) => (h, i + 1)))
        {
            Assert.Equal(new HitRow(i, h.Profile.ProfileKey[3..], string.Join(",", h.Conditions.Select(k => E(k.Type))),
                E(h.Conditions.Select(k => k.LegalStage).FirstOrDefault(s => s != LegalStage.Unknown)), E(h.Identity.Decision), h.Identity.Stage3Row, h.Identity.HardConflict, E(h.PairResult),
                h.ReviewPriority is { } p ? E(p) : null), hits[i - 1]);
            Assert.Equal(h.Identity.Evidence.Values.Select(e => new EvidenceRow(i, E(e.Group), E(e.Direction), E(e.Status), E(e.Strength),
                    e.Detail is null ? null : CodeCatalog.Key(e.Detail))).OrderBy(e => e.EvidenceGroup),
                evidence.Where(e => e.HitNumber == i).OrderBy(e => e.EvidenceGroup));
            Assert.Equal(h.Identity.ReasonCodes, Codes(i, "IDENTITY"));
            Assert.Equal(h.PairReasonCodes, Codes(i, "PAIR"));
            Assert.Equal(h.Disposition.ReasonCodes, Codes(i, "DISPOSITION"));
        }
    }

    [Fact]
    public async Task Lineage_names_the_input_files_rule_versions_and_the_list_version_of_each_profile()
    {
        var ct = TestContext.Current.CancellationToken;
        var files = Files("GC-03");
        const string profile = "名單資料_90000031.txt";
        var c = await _caseService.CreateAsync("s-3", "e-3", TxtCaseReader.ReadFiles(files), ct, files);

        var first = (await _cases.GetAnalysisAsync(c.CaseId, 1, ct))!;
        var lineage = first.Lineage!;
        Assert.Equal(files.Keys.Order(StringComparer.Ordinal), lineage.InputFiles.Select(f => f.Name));
        Assert.All(lineage.InputFiles, f => Assert.Matches("^[0-9A-F]{64}$", f.Sha256));
        Assert.Equal(("source-policy-0.2", CodeCatalog.Version, CaseJson.SchemaVersion),
            (lineage.SourcePolicyVersion, lineage.CodeCatalogVersion, lineage.SchemaVersion));
        Assert.Equal(new ProfileUse("90000031", AnalysisLineage.CaseFiles, null, lineage.Profiles[0].ContentHash), Assert.Single(lineage.Profiles));
        var stored = DateOnly.FromDateTime(first.CreatedAt.UtcDateTime); // the analysis date is the UTC day it ran, at most midnight before the write
        Assert.InRange(lineage.AsOf, stored.AddDays(-1), stored);
        Assert.Equal(9, first.Quality!.ComparisonCells);

        // The list database gets a changed version; its re-analysis records that version.
        var changed = files[profile].Replace("\n", "\r\n", StringComparison.Ordinal) + "\r\n補充:\t新版本";
        var report = await _listService.ImportAsync(new Dictionary<string, string> { [profile] = changed }, ct);
        Assert.Equal(1, Assert.Single(report.Profiles).Version);
        await _listService.ReanalyzeWithLatestProfilesAsync(c.CaseId, ct);

        var use = Assert.Single((await _cases.GetAnalysisAsync(c.CaseId, 2, ct))!.Lineage!.Profiles);
        Assert.Equal(("90000031", AnalysisLineage.ListDatabase, (int?)1), (use.ProfileId, use.Origin, use.ListVersion));
        Assert.NotEqual(lineage.Profiles[0].ContentHash, use.ContentHash);
    }
}
