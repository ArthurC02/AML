using System.ComponentModel;
using Aml.Core.Codes;
using Aml.Core.Explanation;
using Aml.Core.Identity;
using Aml.Core.Retrieval;
using Aml.Core.Screening;
using Aml.Core.Shared;

namespace Aml.Core.Cases;

public sealed record HitLine(
    [property: Description("hit 編號")] int HitNumber,
    [property: Description("名單編號（/api/profiles 路由使用）")] string ProfileId,
    [property: Description("名單鍵（DJ:<名單編號>）")] string ProfileKey,
    [property: Description("名單主要姓名")][property: Sensitivity(Sensitivity.Personal)] string? ProfileName,
    [property: Description("此組合的對外結果")] ScreeningAssessment PairResult,
    [property: Description("複核優先序")] ReviewPriority? ReviewPriority,
    [property: Description("系統身分判斷")] IdentityDecision IdentityDecision,
    [property: Description("此 revision 的 hit 摘要連結")] string Link);

public sealed record CaseDetail(
    [property: Description("案件")] CaseRecord Case,
    [property: Description("生效 revision 的完整紀錄；尚無生效 revision 時為 null")] AnalysisRecord? Effective,
    [property: Description("revision 清單的連結")] string Revisions);

public sealed record CaseSummary(
    [property: Description("案件編號")] string CaseId,
    [property: Description("受檢對象參考碼：呼叫端的不透明參考，不得為客戶編號或證件號碼")] string SubjectRef,
    [property: Description("觸發事件編號")] string TriggerEventId,
    [property: Description("案件狀態")] CaseStatus Status,
    [property: Description("最新 revision")] int CurrentRevision,
    [property: Description("生效 revision；尚無完整分析時為 null")] int? EffectiveRevision,
    [property: Description("生效 revision 的分析時間")] DateTimeOffset? EffectiveAt,
    [property: Description("有比生效 revision 更新但未完成的 revision")] bool NewerRevisionIncomplete,
    [property: Description("案件層級結果；尚無生效 revision 時為 null")] ScreeningAssessment? Result,
    [property: Description("結論句")] string Conclusion,
    [property: Description("各 hit 一行，依優先序再依編號，最多 10 筆")] IReadOnlyList<HitLine>? Hits,
    [property: Description("未列出的 hit 數")] int? Omitted,
    [property: Description("缺名單資料而無法評估的名單鍵")] IReadOnlyList<string>? NotEvaluatedProfiles,
    [property: Description("完整紀錄：生效 revision，沒有時為最新 revision")] string? Revision,
    [property: Description("revision 清單的連結")] string Revisions);

public sealed record EvidenceLine(
    [property: Description("證據群組")] EvidenceGroup Group,
    [property: Description("比對狀態")] ComparisonStatus Status,
    [property: Description("強度")] Strength Strength,
    [property: Description("細節代碼，見 /api/codes")] string? Detail);

public sealed record HitSummary(
    [property: Description("hit 編號")] int HitNumber,
    [property: Description("所屬 revision")] int Revision,
    [property: Description("名單編號（/api/profiles 路由使用）")] string ProfileId,
    [property: Description("名單鍵（DJ:<名單編號>）")] string ProfileKey,
    [property: Description("名單主要姓名")][property: Sensitivity(Sensitivity.Personal)] string? ProfileName,
    [property: Description("成立（HIT）的名單條件類型")] IReadOnlyList<ListType>? SourceConditions,
    [property: Description("系統身分判斷")] IdentityDecision IdentityDecision,
    [property: Description("第一階段硬衝突代碼；是否因來源品質降級見理由碼")] string? HardConflict,
    [property: Description("第三階段決策表列號")] int? Stage3Row,
    [property: Description("此組合的對外結果")] ScreeningAssessment PairResult,
    [property: Description("複核優先序")] ReviewPriority? ReviewPriority,
    [property: Description("身分判斷的理由碼，見 /api/codes")] IReadOnlyList<string>? ReasonCodes,
    [property: Description("決定性證據，最多 3 筆：第三階段第 2～5 列為依強度排序的支持，其他情況為細節代碼列在理由碼中的衝突，依理由碼順序")] IReadOnlyList<EvidenceLine>? DecisiveEvidence,
    [property: Description("第三階段第 6 列且沒有衝突時的說明")] string? EvidenceNote,
    [property: Description("對比式解釋，見 hit 明細")] IReadOnlyList<WouldChangeIf>? WouldChangeIf,
    [property: Description("hit 明細的連結")] string Detail);

public sealed record RevisionLine(
    [property: Description("revision 編號")] int Revision,
    [property: Description("分析時間")] DateTimeOffset CreatedAt,
    [property: Description("執行狀態")] ExecutionStatus ExecutionStatus,
    [property: Description("案件層級結果")] ScreeningAssessment CaseResult,
    [property: Description("是否為生效 revision")] bool Effective,
    [property: Description("與前一個生效 revision 的變更數；第一次分析或沒有解釋的 revision 為 null")] int? Changes);

public sealed record CasePage(
    [property: Description("Case，依建立時間再依案件編號")] IReadOnlyList<CaseRecord> Items,
    [property: Description("下一頁的 pageToken；最後一頁為 null")] string? NextPageToken);

// A summary only leaves detail out, never adds any, and never includes an Identifier field.
public static class CaseViews
{
    public const int MaxHits = 10;
    public const int MaxEvidence = 3;

    private const int PrerequisitesOnlyRow = 6;

    public static string HitLink(string caseId, int hit, int revision) => $"/api/cases/{caseId}/hits/{hit}?revision={revision}&view=summary";

    public static string RevisionsLink(string caseId) => $"/api/cases/{caseId}/revisions";

    public static string RevisionLink(string caseId, int revision) => $"{RevisionsLink(caseId)}/{revision}";

    private static IReadOnlyList<T>? NullIfEmpty<T>(IReadOnlyList<T> items) => items.Count == 0 ? null : items;

    public static CaseSummary Summary(CaseRecord c, AnalysisRecord? effective)
    {
        if (effective is null) return SummaryWithoutEffective(c);

        var hits = effective.Explanation?.Hits ?? [];
        var lines = HitLinesByPriority(c.CaseId, effective.Revision, hits);
        var newer = c.EffectiveRevision is { } e && c.CurrentRevision > e;
        return new(c.CaseId, c.SubjectRef, c.TriggerEventId, c.Status, c.CurrentRevision, effective.Revision, effective.CreatedAt, newer,
            effective.CaseResult, Conclusion(c, effective, hits, newer), NullIfEmpty(lines.Take(MaxHits).ToList()), lines.Count > MaxHits ? lines.Count - MaxHits : null,
            NullIfEmpty(effective.Result.NotEvaluatedProfiles), RevisionLink(c.CaseId, effective.Revision), RevisionsLink(c.CaseId));
    }

    private static CaseSummary SummaryWithoutEffective(CaseRecord c)
    {
        var neverAnalyzed = c.CurrentRevision == 0;
        return new(c.CaseId, c.SubjectRef, c.TriggerEventId, c.Status, c.CurrentRevision, null, null, false, null,
            neverAnalyzed ? "尚未分析" : $"尚無生效的分析結果：最新 revision {c.CurrentRevision} 未完成",
            null, null, null, neverAnalyzed ? null : RevisionLink(c.CaseId, c.CurrentRevision), RevisionsLink(c.CaseId));
    }

    private static List<HitLine> HitLinesByPriority(string caseId, int revision, IReadOnlyList<HitExplanation> hits) =>
        hits.OrderBy(h => h.ReviewPriority ?? (ReviewPriority)int.MaxValue).ThenBy(h => h.HitNumber)
            .Select(h => new HitLine(h.HitNumber, ProfileKeys.IdOf(h.ProfileKey), h.ProfileKey, h.ProfileName, h.PairResult, h.ReviewPriority, h.IdentityDecision, HitLink(caseId, h.HitNumber, revision)))
            .ToList();

    private static string Conclusion(CaseRecord c, AnalysisRecord effective, IReadOnlyList<HitExplanation> hits, bool newerRevisionIncomplete)
    {
        var toReview = hits.Count(h => h.PairResult is ScreeningAssessment.Hit or ScreeningAssessment.Review);
        return $"案件結果 {CaseJson.Code(effective.CaseResult)}（revision {effective.Revision}）：{hits.Count} 個 hit，{toReview} 個需複核"
            + (newerRevisionIncomplete ? $"；revision {c.CurrentRevision} 未完成，未取代此結果" : "");
    }

    public static HitSummary Hit(string caseId, int revision, HitExplanation h)
    {
        var decisive = DecisiveEvidence(h);
        return new(h.HitNumber, revision, ProfileKeys.IdOf(h.ProfileKey), h.ProfileKey, h.ProfileName,
            NullIfEmpty(h.SourceConditions.Where(s => s.Status == ConditionStatus.Hit).Select(s => s.Type).ToList()),
            h.IdentityDecision, h.HardConflict, h.Stage3Row, h.PairResult, h.ReviewPriority, NullIfEmpty(h.ReasonCodes), NullIfEmpty(decisive),
            decisive.Count == 0 && h.Stage3Row == PrerequisitesOnlyRow ? "僅前提條件相符" : null, NullIfEmpty(h.WouldChangeIf),
            $"/api/cases/{caseId}/hits/{h.HitNumber}?view=detail&revision={revision}");
    }

    // A hard conflict or a non-Person rule leaves no stage 3 row; a rule with no evidence of its own (death before birth) yields none.
    private static List<EvidenceLine> DecisiveEvidence(HitExplanation h) =>
        IsDecidedBySupport(h.Stage3Row) ? StrongestSupport(h) : ConflictsInReasonCodeOrder(h);

    private static bool IsDecidedBySupport(int? stage3Row) => stage3Row is >= 2 and <= 5;

    private static List<EvidenceLine> StrongestSupport(HitExplanation h) =>
        h.Support.OrderByDescending(e => e.Strength).Take(MaxEvidence).Select(Line).ToList();

    private static List<EvidenceLine> ConflictsInReasonCodeOrder(HitExplanation h)
    {
        var codes = h.ReasonCodes.ToList();
        return h.Conflicts.Select(e => (e, Rank: e.Detail is { } d ? codes.IndexOf(CodeCatalog.Key(d)) : -1))
            .Where(x => x.Rank >= 0).OrderBy(x => x.Rank).Take(MaxEvidence).Select(x => Line(x.e)).ToList();
    }

    private static EvidenceLine Line(Evidence e) => new(e.Group, e.Status, e.Strength, e.Detail);
}
