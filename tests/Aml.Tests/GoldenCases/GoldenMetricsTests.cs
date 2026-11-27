using Aml.Core.Cases;
using Aml.Core.Explanation;
using Aml.Core.Identity;
using Aml.Core.Scoring;
using Aml.Core.Screening;
using Aml.Core.Shared;
using Aml.Infra.Txt;

namespace Aml.Tests.GoldenCases;

/// <summary>
/// PoC metrics over the Golden Cases (docs/plan/poc-delivery-plan.md#metrics). Prints the numbers, fails on the safety
/// invariants, and pins Review Rate and Probable-Match Yield on this set; no target percentage is committed before
/// representative data exists.
/// </summary>
public class GoldenMetricsTests
{
    // Identity truth from each expected.yaml finalDisposition.humanDecision (or disposition basis IDENTITY); null = UNRESOLVED.
    private static readonly Dictionary<string, bool?> SamePerson = new()
    {
        ["GC-01"] = false,
        ["GC-02"] = false,
        ["GC-03"] = true,
        ["GC-11"] = true,
        ["GC-12"] = null,
    };

    [Fact]
    public void Golden_case_metrics()
    {
        var pairs = new List<(string Gc, HitResult Hit, HitExplanation Explanation, bool? Truth)>();
        int rows = 0, stableCases = 0;
        foreach (var (gc, truth) in SamePerson)
        {
            var input = TxtCaseReader.Read(GoldenCaseTests.Folder(gc));
            var a = CaseAnalyzer.Analyze(input);
            var e = ExplanationBuilder.Build(input, a);
            rows += input.Rows.Count;
            pairs.AddRange(a.Hits.Zip(e.Hits, (h, x) => (gc, h, x, truth)));
            if (RevisionChanges.Compare(RevisionChanges.Snapshot(a), RevisionChanges.Snapshot(CaseAnalyzer.Analyze(input))).Count == 0) stableCases++;
        }

        var autoNoMatch = pairs.Where(p => p.Hit.PairResult == ScreeningAssessment.NoHit).ToList();
        var probable = pairs.Where(p => p.Hit.Identity.Decision == IdentityDecision.ProbableMatch && p.Truth is not null).ToList();
        var quality = AnalysisQuality.Of(pairs.Select(p => p.Explanation).ToList(), [], 0);
        double Rate(int n, int d) => d == 0 ? double.NaN : (double)n / d;

        var metrics = new (string Name, double Value)[]
        {
            ("Duplicate Hit Rate", Rate(pairs.Count - pairs.Select(p => (p.Gc, p.Hit.Profile.ProfileKey)).Distinct().Count(), pairs.Count)),
            ("Candidate rows folded into one hit", Rate(rows - pairs.Count, rows)),
            ("Auto-NO_MATCH Precision", Rate(autoNoMatch.Count(p => p.Truth == false), autoNoMatch.Count)),
            ("Auto-MATCH Precision (no auto MATCH in Phase 1)", Rate(0, 0)),
            ("Review Rate", Rate(pairs.Count(p => p.Hit.PairResult == ScreeningAssessment.Review), pairs.Count)),
            ("Probable-Match Yield", Rate(probable.Count(p => p.Truth == true), probable.Count)),
            ("Evidence Completeness", Rate(pairs.Count(p => p.Explanation.Comparison.Count == 9), pairs.Count)),
            ("Decision Traceability", Rate(pairs.Count(p => p.Explanation.ReasonCodes.Count > 0 && p.Explanation.Summary.Conclusion.Length > 0), pairs.Count)),
            ("Missing rate (comparison cells)", quality.MissingRate ?? double.NaN),
            ("Ambiguous rate (comparison cells)", quality.UncertainRate ?? double.NaN),
            ("Conflict rate (comparison cells)", quality.ConflictRate ?? double.NaN),
            ("Re-adjudication Stability", Rate(stableCases, SamePerson.Count)),
        };
        foreach (var (name, value) in metrics)
            TestContext.Current.TestOutputHelper?.WriteLine($"{name}: {(double.IsNaN(value) ? "n/a" : value.ToString("P0", System.Globalization.CultureInfo.InvariantCulture))}");

        // Safety invariants.
        Assert.All(autoNoMatch, p => Assert.False(p.Truth, $"{p.Gc} auto-closed a confirmed match"));
        Assert.All(pairs, p => Assert.Equal(9, p.Explanation.Comparison.Count));
        Assert.All(pairs, p => Assert.NotEmpty(p.Explanation.ReasonCodes));
        Assert.Equal(pairs.Count, pairs.Select(p => (p.Gc, p.Hit.Profile.ProfileKey)).Distinct().Count());
        Assert.Equal(SamePerson.Count, stableCases);

        // Review Rate and Probable-Match Yield on this set, as counts so a moved pair shows which way it went.
        Assert.Equal((4, 6), (pairs.Count(p => p.Hit.PairResult == ScreeningAssessment.Review), pairs.Count));
        Assert.Equal((2, 2), (probable.Count(p => p.Truth == true), probable.Count));
    }

    private static List<(string Gc, HitResult Hit, IdentityScore Score)> ScoredPairs() =>
        SamePerson.Keys.SelectMany(gc => CaseAnalyzer.Analyze(TxtCaseReader.Read(GoldenCaseTests.Folder(gc))).Hits
            .Select(h => (gc, h, ScoringRule.Identity(h.Identity, !IdentityDecisionPolicy.IsPersonRecord(h.Profile))))).ToList();

    /// <summary>The golden-case lines of docs/feature/scoring-rule.md#identity-score: the score band beside the stage 3 decision.</summary>
    [Theory]
    [InlineData("GC-01", "0", IdentityBand.NoMatch, IdentityDecision.NoMatch)]   // veto, both Profiles
    [InlineData("GC-02", "0", IdentityBand.Review, IdentityDecision.Review)]
    [InlineData("GC-12", "0", IdentityBand.Review, IdentityDecision.Review)]
    [InlineData("GC-03", "8", IdentityBand.ProbableMatch, IdentityDecision.ProbableMatch)]   // on T_probable_exact, zero margin
    [InlineData("GC-11", "8", IdentityBand.ProbableMatch, IdentityDecision.ProbableMatch)]
    public void Golden_case_scores(string gc, string total, IdentityBand band, IdentityDecision stage3)
    {
        var pairs = ScoredPairs().Where(p => p.Gc == gc).ToList();
        Assert.NotEmpty(pairs);
        Assert.All(pairs, p => Assert.Equal((decimal.Parse(total, System.Globalization.CultureInfo.InvariantCulture), band, stage3),
            (p.Score.Total, p.Score.Band, p.Hit.Identity.Decision)));
        if (gc == "GC-01") Assert.All(pairs, p => Assert.Equal([ReasonCodes.ImpossibleAgeAtReportedEvent], p.Score.Applied));
    }

    /// <summary>
    /// The trial calculation beside the stage 3 table (docs/plan/poc-delivery-plan.md#golden-case-results): agreement,
    /// decided rate (MATCH or NO_MATCH without a reviewer), why each undecided pair stays undecided, and the highest score
    /// the data each pair carries could ever give.
    /// </summary>
    [Fact]
    public void Score_bands_beside_the_stage_3_table()
    {
        var pairs = ScoredPairs();
        var agree = pairs.Count(p => p.Score.Band.ToString() == p.Hit.Identity.Decision.ToString());
        var disagreements = pairs.Where(p => p.Score.Band.ToString() != p.Hit.Identity.Decision.ToString())
            .GroupBy(p => $"{p.Hit.Identity.Decision}→{p.Score.Band}").ToDictionary(g => g.Key, g => g.Count());
        var decidedStage3 = pairs.Count(p => p.Hit.Identity.Decision == IdentityDecision.NoMatch);
        var decidedScore = pairs.Count(p => p.Score.Band is IdentityBand.Match or IdentityBand.NoMatch);
        var undecided = pairs.Where(p => p.Score.Band is not (IdentityBand.Match or IdentityBand.NoMatch))
            .GroupBy(p => Undecided(p.Score)).ToDictionary(g => g.Key, g => g.Count());
        var canReachMatch = pairs.Count(p => HighestReachable(p.Hit.Identity) >= ScoringPolicy.Proposed.Match);

        var output = TestContext.Current.TestOutputHelper;
        output?.WriteLine($"Pairs {pairs.Count}; score band agrees with stage 3 on {agree}; disagreements: {string.Join(", ", disagreements.Select(d => $"{d.Key} {d.Value}"))}");
        output?.WriteLine($"Decided (MATCH or NO_MATCH): stage 3 {decidedStage3}, score {decidedScore}");
        foreach (var (reason, n) in undecided) output?.WriteLine($"Undecided {n}: {reason}");
        foreach (var p in pairs)
            output?.WriteLine($"{p.Gc}: total {p.Score.Total}, band {p.Score.Band}, highest reachable {HighestReachable(p.Hit.Identity)}; "
                + $"Profile lacks {Lacking(p.Hit.Identity, ComparisonStatus.ProfileMissing)}; customer lacks {Lacking(p.Hit.Identity, ComparisonStatus.SubjectMissing)}");
        output?.WriteLine($"Could reach T_match {ScoringPolicy.Proposed.Match} with the data they carry: {canReachMatch}");

        Assert.Equal((6, 6), (agree, pairs.Count));
        Assert.Empty(disagreements);
        Assert.Equal((2, 2), (decidedStage3, decidedScore));
        Assert.Equal(new Dictionary<string, int> { ["only prerequisites agree"] = 2, ["support below T_match (8)"] = 2 }, undecided);
        Assert.Equal(0, canReachMatch);
    }

    private static string Undecided(IdentityScore s) =>
        s.Applied.Count > 0 ? $"held or capped ({string.Join(", ", s.Applied)})"
        : s.Items.Count > 0 ? $"support below T_match ({s.Total})"
        : "only prerequisites agree";

    private static string Lacking(IdentityResult r, ComparisonStatus side) =>
        string.Join(", ", new[] { EvidenceGroup.DateOfBirth, EvidenceGroup.Document, EvidenceGroup.Employment, EvidenceGroup.Relationship, EvidenceGroup.Address, EvidenceGroup.OtherIdentifier }
            .Where(g => r.Evidence.GetValueOrDefault(g)?.Status == side));

    /// <summary>
    /// Optimistic bound: a group already compared keeps its points, a group present on both sides but undecided
    /// (UNCERTAIN) gets its best proposed points, a group missing on either side stays 0.
    /// </summary>
    private static decimal HighestReachable(IdentityResult r)
    {
        var p = ScoringPolicy.Proposed;
        var scored = ScoringRule.Identity(r, entityRecord: false).Items.ToDictionary(i => i.Of, i => i.Points);
        decimal Best(EvidenceGroup g) => g switch
        {
            EvidenceGroup.Document => p.Document,
            EvidenceGroup.DateOfBirth => p.DobDay,
            EvidenceGroup.Address => p.AddressHouseNumber,
            EvidenceGroup.OtherIdentifier => p.OtherIdentifier,
            EvidenceGroup.EntityIdentifier => p.EntityIdentifier,
            EvidenceGroup.Employment or EvidenceGroup.Relationship or EvidenceGroup.CompanyAssociation => p.Association,
            _ => 0,
        };
        return r.Evidence.Values.Sum(v => v.Status == ComparisonStatus.Uncertain ? Math.Max(Best(v.Group), scored.GetValueOrDefault(v.Group.ToString()))
            : scored.GetValueOrDefault(v.Group.ToString()));
    }

    [Fact]
    public void GC03_quality_counts_cells_by_status_and_findings_by_code()
    {
        var input = TxtCaseReader.Read(GoldenCaseTests.Folder("GC-03"));
        var a = CaseAnalyzer.Analyze(input);
        var q = AnalysisQuality.Of(ExplanationBuilder.Build(input, a).Hits, input.Subject.Findings, a.NotEvaluatedProfiles.Count);

        // Nine rows: four with a side missing, the address UNCERTAIN, no conflict; one placeholder dash in the customer data.
        Assert.Equal((1, 9, 4, 1, 0, 0), (q.Hits, q.ComparisonCells, q.MissingCells, q.UncertainCells, q.ConflictCells, q.NotEvaluatedProfiles));
        Assert.Equal((4 / 9.0, 1 / 9.0, 0.0), (q.MissingRate!.Value, q.UncertainRate!.Value, q.ConflictRate!.Value));
        Assert.Equal(new Dictionary<string, int> { ["PLACEHOLDER_DASH"] = 1 }, q.FindingCounts);
    }
}
