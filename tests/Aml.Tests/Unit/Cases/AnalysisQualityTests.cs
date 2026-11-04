using Aml.Core.Cases;

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
}
