using System.ComponentModel;
using Aml.Core.Identity;
using Aml.Core.Retrieval;
using Aml.Core.Screening;
using Aml.Core.Subjects;

namespace Aml.Core.Cases;

public sealed record HitResult(
    [property: Description("名單（讀入時的結構化內容）")] SourceProfile Profile,
    [property: Description("疑似名單表中合併為此 hit 的列數")] int CandidateRows,
    [property: Description("名單條件")] IReadOnlyList<SourceCondition> Conditions,
    [property: Description("身分判斷與證據")] IdentityResult Identity,
    [property: Description("此組合的對外結果")] ScreeningAssessment PairResult,
    [property: Description("複核優先序")] ReviewPriority? ReviewPriority,
    [property: Description("對外結果的理由碼")] IReadOnlyList<string> PairReasonCodes,
    [property: Description("第 22 條建議，僅供參考")] DispositionRecommendation Disposition,
    [property: Description("AML 風險評分；沒有核准的政策時為 null")] RiskAssessment? Risk = null);

/// <summary>Hits, plus candidate Profiles that could not be evaluated (no Profile data): those make the revision incomplete.</summary>
public sealed record CaseAnalysis(
    [property: Description("每筆名單一個 hit")] IReadOnlyList<HitResult> Hits,
    [property: Description("缺名單資料而無法評估的名單鍵；有任何一筆時 revision 不完整")] IReadOnlyList<string> NotEvaluatedProfiles,
    [property: Description("自輸入檔讀入的舊決定")] IReadOnlyList<ImportedDecision> ImportedDecisions)
{
    [property: Description("儲存格式版本")]
    public int SchemaVersion { get; init; } = CaseJson.SchemaVersion;
}

/// <summary>Pure AnalyzeCase flow: deduplicate rows by Profile, then source conditions, identity and assessment per hit.</summary>
public static class CaseAnalyzer
{
    /// <param name="risk">An approved AML risk policy; without one no pair is scored.</param>
    public static CaseAnalysis Analyze(CaseInput input, RiskPolicy? risk = null)
    {
        var groups = input.Rows.GroupBy(r => r.ProfileId).ToList();
        var hits = groups
            .Where(g => input.Profiles.ContainsKey(g.Key))
            .Select(g =>
            {
                var profile = input.Profiles[g.Key];
                var news = input.NewsByProfileId.GetValueOrDefault(g.Key);
                var conditions = SourceConditions.Determine(profile, g.Any(r => r.IsRca), news?.Text, ListFlags.Merge(g.Select(r => r.Flags ?? ListFlags.None)));
                // One identity comparison per pair, reused by every condition (PEP, Sanction, adverse media).
                var identity = IdentityDecisionPolicy.Decide(input.Subject, profile, news);
                var pair = AssessmentPolicy.Assess(conditions, identity);
                var disposition = DispositionPolicy.Recommend(input.Subject, profile, conditions, identity, news is not null);
                return new HitResult(profile, g.Count(), conditions, identity, pair.Result, pair.Priority, pair.ReasonCodes, disposition,
                    RiskScoring.Assess(conditions, identity, risk));
            })
            .ToList();
        var notEvaluated = groups.Where(g => !input.Profiles.ContainsKey(g.Key)).Select(g => ProfileKeys.KeyOf(g.Key)).ToList();
        return new CaseAnalysis(hits, notEvaluated, input.ImportedDecisions);
    }
}
