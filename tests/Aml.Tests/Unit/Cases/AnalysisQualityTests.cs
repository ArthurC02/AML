using Aml.Core.Cases;
using Aml.Core.Explanation;
using Aml.Infra.Txt;
using Aml.Tests.GoldenCases;

namespace Aml.Tests.Unit.Cases;

/// <summary>A revision without hits has no rates and counts the subject's findings once each, by catalog key.</summary>
public class AnalysisQualityTests
{
    [Fact]
    public void No_hits_gives_null_rates_and_counts_subject_findings_by_key()
    {
        var q = AnalysisQuality.Of([], ["ENCODING_FALLBACK:a.txt", "ENCODING_FALLBACK:a.txt", "PLACEHOLDER_DASH"], notEvaluated: 2);

        Assert.Equal(0, q.Hits);
        Assert.Equal(0, q.ComparisonCells);
        Assert.Null(q.MissingRate);
        Assert.Null(q.UncertainRate);
        Assert.Null(q.ConflictRate);
        Assert.Equal(2, q.NotEvaluatedProfiles);
        Assert.Equal(new Dictionary<string, int> { ["ENCODING_FALLBACK"] = 1, ["PLACEHOLDER_DASH"] = 1 }, q.FindingCounts);
    }

    [Fact]
    public void Each_finding_key_counts_once_per_hit()
    {
        var input = TxtCaseReader.Read(GoldenCaseTests.Folder("GC-11"));
        var golden = ExplanationBuilder.Build(input, CaseAnalyzer.Analyze(input)).Hits[0];
        var hit = golden with { DataQualityFindings = ["ENCODING_FALLBACK:a.txt", "ENCODING_FALLBACK:b.txt", "PLACEHOLDER_DASH"] };

        var q = AnalysisQuality.Of([hit, hit], ["SUBJECT_ONLY"], notEvaluated: 0);

        Assert.Equal(new Dictionary<string, int> { ["ENCODING_FALLBACK"] = 2, ["PLACEHOLDER_DASH"] = 2 }, q.FindingCounts);
    }
}
