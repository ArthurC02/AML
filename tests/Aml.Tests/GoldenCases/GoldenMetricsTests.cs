using Aml.Core.Cases;
using Aml.Core.Explanation;
using Aml.Core.Identity;
using Aml.Core.Screening;
using Aml.Core.Shared;
using Aml.Infra.Txt;

namespace Aml.Tests.GoldenCases;

/// <summary>
/// PoC metrics over the Golden Cases (docs/plan/poc-delivery-plan.md#metrics). Prints the numbers and fails only on the
/// safety invariants; no target percentage is committed before representative data exists.
/// </summary>
public class GoldenMetricsTests
{
    // Identity truth from each expected.yaml finalDisposition.humanDecision (or disposition basis IDENTITY); null = UNRESOLVED.
    private static readonly Dictionary<string, bool?> SamePerson = new()
    {
        ["GC-01"] = false, ["GC-02"] = false, ["GC-03"] = true, ["GC-11"] = true, ["GC-12"] = null,
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
        var cells = pairs.SelectMany(p => p.Explanation.Comparison).ToList();
        double Rate(int n, int d) => d == 0 ? double.NaN : (double)n / d;
        double CellRate(params ComparisonStatus[] s) => Rate(cells.Count(c => s.Contains(c.Status)), cells.Count);

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
            ("Missing rate (comparison cells)", CellRate(ComparisonStatus.SubjectMissing, ComparisonStatus.ProfileMissing)),
            ("Ambiguous rate (comparison cells)", CellRate(ComparisonStatus.Uncertain)),
            ("Conflict rate (comparison cells)", CellRate(ComparisonStatus.Conflict)),
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
    }
}
