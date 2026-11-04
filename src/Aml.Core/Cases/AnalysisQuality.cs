using System.ComponentModel;
using Aml.Core.Codes;
using Aml.Core.Explanation;
using Aml.Core.Shared;

namespace Aml.Core.Cases;

/// <summary>Data quality of one revision: comparison cells by status, findings by code, Profiles not evaluated.</summary>
public sealed record AnalysisQuality(
    [property: Description("hit 數")] int Hits,
    [property: Description("比對格數（每個 hit 9 格）")] int ComparisonCells,
    [property: Description("一方缺值的格數")] int MissingCells,
    [property: Description("UNCERTAIN 的格數")] int UncertainCells,
    [property: Description("CONFLICT 的格數")] int ConflictCells,
    [property: Description("缺值率；沒有比對格時為 null")] double? MissingRate,
    [property: Description("不確定率；沒有比對格時為 null")] double? UncertainRate,
    [property: Description("衝突率；沒有比對格時為 null")] double? ConflictRate,
    [property: Description("無法評估的名單數")] int NotEvaluatedProfiles,
    [property: Description("資料品質 finding 依代碼計數（每個 hit 計一次）")] IReadOnlyDictionary<string, int> FindingCounts)
{
    /// <summary>
    /// Counts over the given hits; each finding is counted once per hit (subject findings once more when there are no hits),
    /// by its catalog key. The Golden metrics call this over every Golden hit.
    /// </summary>
    public static AnalysisQuality Of(IReadOnlyList<HitExplanation> hits, IEnumerable<string> subjectFindings, int notEvaluated)
    {
        var cells = hits.SelectMany(h => h.Comparison).ToList();
        int Count(params ComparisonStatus[] s) => cells.Count(c => s.Contains(c.Status));
        double? Rate(int n) => cells.Count == 0 ? null : (double)n / cells.Count;
        var (missing, uncertain, conflict) = (Count(ComparisonStatus.SubjectMissing, ComparisonStatus.ProfileMissing), Count(ComparisonStatus.Uncertain), Count(ComparisonStatus.Conflict));
        var findings = (hits.Count == 0 ? [subjectFindings.Distinct().ToList()] : hits.Select(h => h.DataQualityFindings))
            .SelectMany(f => f.Select(CodeCatalog.Key).Distinct())
            .GroupBy(k => k, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        return new(hits.Count, cells.Count, missing, uncertain, conflict, Rate(missing), Rate(uncertain), Rate(conflict), notEvaluated, findings);
    }
}
