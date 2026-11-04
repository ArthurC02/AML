using System.Buffers.Text;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Aml.Core.Codes;
using Aml.Core.Explanation;
using Aml.Core.Identity;
using Aml.Core.Screening;
using Aml.Core.Shared;

namespace Aml.Core.Cases;

/// <summary>One hit in a Case summary.</summary>
public sealed record HitLine(
    [property: Description("hit 編號")] int HitNumber,
    [property: Description("名單鍵（DJ:<名單編號>）")] string ProfileKey,
    [property: Description("名單主要姓名")][property: Sensitivity(Sensitivity.Personal)] string? ProfileName,
    [property: Description("此組合的對外結果")] ScreeningAssessment PairResult,
    [property: Description("複核優先序")] ReviewPriority? ReviewPriority,
    [property: Description("系統身分判斷")] IdentityDecision IdentityDecision,
    [property: Description("此 revision 的 hit 摘要連結")] string Link);

/// <summary>What a Case concluded, from its effective revision; the full record is at the revision link.</summary>
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

/// <summary>A piece of evidence without its values.</summary>
public sealed record EvidenceLine(
    [property: Description("證據群組")] EvidenceGroup Group,
    [property: Description("比對狀態")] ComparisonStatus Status,
    [property: Description("強度")] Strength Strength,
    [property: Description("細節代碼，見 /api/codes")] string? Detail);

/// <summary>Why one hit was raised and decided, without the compared values; the full hit is at the detail link.</summary>
public sealed record HitSummary(
    [property: Description("hit 編號")] int HitNumber,
    [property: Description("所屬 revision")] int Revision,
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

/// <summary>One revision of a Case.</summary>
public sealed record RevisionLine(
    [property: Description("revision 編號")] int Revision,
    [property: Description("分析時間")] DateTimeOffset CreatedAt,
    [property: Description("執行狀態")] ExecutionStatus ExecutionStatus,
    [property: Description("案件層級結果")] ScreeningAssessment CaseResult,
    [property: Description("是否為生效 revision")] bool Effective,
    [property: Description("與前一個生效 revision 的變更數；第一次分析或沒有解釋的 revision 為 null")] int? Changes);

/// <summary>One page of Cases and the token for the next.</summary>
public sealed record CasePage(
    [property: Description("Case，依建立時間再依案件編號")] IReadOnlyList<CaseRecord> Items,
    [property: Description("下一頁的 pageToken；最後一頁為 null")] string? NextPageToken);

/// <summary>Summaries over the stored records: they leave detail out and never add any. Identifier fields are never included.</summary>
public static class CaseViews
{
    public const int MaxHits = 10;
    public const int MaxEvidence = 3;

    public static string HitLink(string caseId, int hit, int revision) => $"/api/cases/{caseId}/hits/{hit}?revision={revision}";

    public static string RevisionLink(string caseId, int revision) => $"/api/cases/{caseId}/revisions/{revision}";

    private static string Code<T>(T value) where T : struct, Enum => JsonNamingPolicy.SnakeCaseUpper.ConvertName(value.ToString());

    private static IReadOnlyList<T>? NullIfEmpty<T>(IReadOnlyList<T> items) => items.Count == 0 ? null : items;

    /// <summary>The Case and its effective revision (null when it has none).</summary>
    public static CaseSummary Summary(CaseRecord c, AnalysisRecord? effective)
    {
        var revisions = $"/api/cases/{c.CaseId}/revisions";
        var newer = c.EffectiveRevision is { } e && c.CurrentRevision > e;
        if (effective is null)
            return new(c.CaseId, c.SubjectRef, c.TriggerEventId, c.Status, c.CurrentRevision, null, null, false, null,
                c.CurrentRevision == 0 ? "尚未分析" : $"尚無生效的分析結果：最新 revision {c.CurrentRevision} 未完成",
                null, null, null, c.CurrentRevision == 0 ? null : RevisionLink(c.CaseId, c.CurrentRevision), revisions);

        var hits = effective.Explanation?.Hits ?? [];
        var lines = hits.OrderBy(h => h.ReviewPriority ?? (ReviewPriority)int.MaxValue).ThenBy(h => h.HitNumber)
            .Select(h => new HitLine(h.HitNumber, h.ProfileKey, h.ProfileName, h.PairResult, h.ReviewPriority, h.IdentityDecision, HitLink(c.CaseId, h.HitNumber, effective.Revision)))
            .ToList();
        var toReview = hits.Count(h => h.PairResult is ScreeningAssessment.Hit or ScreeningAssessment.Review);
        var conclusion = $"案件結果 {Code(effective.CaseResult)}（revision {effective.Revision}）：{hits.Count} 個 hit，{toReview} 個需複核"
            + (newer ? $"；revision {c.CurrentRevision} 未完成，未取代此結果" : "");
        return new(c.CaseId, c.SubjectRef, c.TriggerEventId, c.Status, c.CurrentRevision, effective.Revision, effective.CreatedAt, newer,
            effective.CaseResult, conclusion, NullIfEmpty(lines.Take(MaxHits).ToList()), lines.Count > MaxHits ? lines.Count - MaxHits : null,
            NullIfEmpty(effective.Result.NotEvaluatedProfiles), RevisionLink(c.CaseId, effective.Revision), revisions);
    }

    /// <summary>
    /// One hit without its compared values. The decisive evidence is what decided it: on stage 3 rows 2 to 5 the strongest
    /// support; otherwise (row 1, row 6, a hard conflict, a non-Person rule) the conflicts whose code is one of the reason
    /// codes, in reason-code order, so the deciding conflict comes first. A rule with no evidence of its own (a death before
    /// the subject's birth, a non-Person subject) has none; the reason codes say which rule decided.
    /// </summary>
    public static HitSummary Hit(string caseId, int revision, HitExplanation h)
    {
        static EvidenceLine Line(Evidence e) => new(e.Group, e.Status, e.Strength, e.Detail);
        var codes = h.ReasonCodes.ToList();
        // A hard conflict or a non-Person rule leaves the stage 3 row empty.
        var decisive = h.Stage3Row is >= 2 and <= 5
            ? h.Support.OrderByDescending(e => e.Strength).Take(MaxEvidence).Select(Line).ToList()
            : h.Conflicts.Select(e => (e, Rank: e.Detail is { } d ? codes.IndexOf(CodeCatalog.Key(d)) : -1))
                .Where(x => x.Rank >= 0).OrderBy(x => x.Rank).Take(MaxEvidence).Select(x => Line(x.e)).ToList();
        return new(h.HitNumber, revision, h.ProfileKey, h.ProfileName,
            NullIfEmpty(h.SourceConditions.Where(s => s.Status == ConditionStatus.Hit).Select(s => s.Type).ToList()),
            h.IdentityDecision, h.HardConflict, h.Stage3Row, h.PairResult, h.ReviewPriority, NullIfEmpty(h.ReasonCodes), NullIfEmpty(decisive),
            decisive.Count == 0 && h.Stage3Row == 6 ? "僅前提條件相符" : null, NullIfEmpty(h.WouldChangeIf),
            $"/api/cases/{caseId}/hits/{h.HitNumber}?view=detail&revision={revision}");
    }
}

/// <summary>Opaque keyset page token: the sort key (created at, id) of the last item on the page.</summary>
public static class PageToken
{
    public const int DefaultSize = 20;
    public const int MaxSize = 100;

    public static string Encode(DateTimeOffset at, string id) =>
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{at.UtcTicks}|{id}")));

    /// <summary>False for a token this API did not issue (not base64url, or not a tick count and an id).</summary>
    public static bool TryDecode(string token, out (DateTimeOffset At, string Id) key)
    {
        key = default;
        if (!Base64Url.IsValid(token)) return false;
        string text;
        try
        {
            text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(Base64Url.DecodeFromChars(token));
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
        var bar = text.IndexOf('|', StringComparison.Ordinal);
        if (bar <= 0 || bar == text.Length - 1
            || !long.TryParse(text.AsSpan(0, bar), NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            || ticks > DateTimeOffset.MaxValue.UtcTicks) return false;
        key = (new DateTimeOffset(ticks, TimeSpan.Zero), text[(bar + 1)..]);
        return true;
    }
}
