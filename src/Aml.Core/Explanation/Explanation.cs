using System.ComponentModel;
using System.Globalization;
using System.Text;
using Aml.Core.Cases;
using Aml.Core.Identity;
using Aml.Core.Normalization;
using Aml.Core.Screening;
using Aml.Core.Shared;
using Aml.Core.Subjects;

namespace Aml.Core.Explanation;

/// <summary>One row of the fixed-order comparison table. Difference holds codes (joined with "；"); Note holds the reviewer text.</summary>
public sealed record ComparisonRow(
    [property: Description("比對項目（固定順序：中文姓名、英文姓名／拼音、國籍、出生日期、性別、證件、地址、職業／雇主、其他識別碼）")] string Field,
    [property: Description("受檢對象的值；證件與參考號碼已遮罩")][property: Sensitivity(Sensitivity.Identifier)] string? SubjectValue,
    [property: Description("名單的值；證件已遮罩")][property: Sensitivity(Sensitivity.Identifier)] string? ProfileValue,
    [property: Description("比對狀態，見 /api/codes")] ComparisonStatus Status,
    [property: Description("差異代碼，多個以「；」連接，見 /api/codes")] string? Difference,
    [property: Description("給複核人員的說明文字，例如報導年齡的推算")][property: Sensitivity(Sensitivity.Personal)] string? Note = null);

/// <summary>A relative or related company named by the bank data (SUBJECT) or the Profile (PROFILE).</summary>
public sealed record RelatedParty(
    [property: Description("SUBJECT：銀行資料記載；PROFILE：名單記載")] string Side,
    [property: Description("關係人姓名或公司名稱")][property: Sensitivity(Sensitivity.Personal)] string Name,
    [property: Description("關係，依來源原文")] string Relation,
    [property: Description("是否為公司或組織")] bool IsEntity,
    [property: Description("名單記載的關係人名單編號")] string? ProfileId = null);

/// <summary>Five-part summary. It restates recorded facts only and adds none.</summary>
public sealed record ExplanationSummary(
    [property: Description("內部資料：受檢對象的姓名、出生日期、國籍、性別與證件是否提供")][property: Sensitivity(Sensitivity.Personal)] string InternalData,
    [property: Description("外部資料：名單類別、條件、出生日期、國籍、性別與新聞，[S#] 引用來源")][property: Sensitivity(Sensitivity.Personal)] string ExternalData,
    [property: Description("相符、衝突與無法比對的項目")] string AgreementsAndDifferences,
    [property: Description("關鍵佐證或缺口")] string KeyEvidenceOrGaps,
    [property: Description("結論：身分判斷、對外結果、理由碼與第 22 條建議")] string Conclusion);

public sealed record HitExplanation(
    [property: Description("hit 編號，從 1 開始")] int HitNumber,
    [property: Description("受檢對象：客戶或關聯人（關係）與姓名")][property: Sensitivity(Sensitivity.Personal)] string ScreenedSubject,
    [property: Description("名單鍵（DJ:<名單編號>）")] string ProfileKey,
    [property: Description("名單主要姓名")][property: Sensitivity(Sensitivity.Personal)] string? ProfileName,
    [property: Description("疑似名單表中命中的姓名")][property: Sensitivity(Sensitivity.Personal)] IReadOnlyList<string> MatchedNames,
    [property: Description("疑似名單表標示 Is RCA? = YES")] bool IsRca,
    [property: Description("名單類別／子類別")] IReadOnlyList<string> SourceCategories,
    [property: Description("名單條件（與身分判斷各自獨立）")] IReadOnlyList<SourceCondition> SourceConditions,
    [property: Description("是否附有新聞")] bool AttachedNews,
    [property: Description("固定順序的比對表")] IReadOnlyList<ComparisonRow> Comparison,
    [property: Description("雙方記載的關係人")] IReadOnlyList<RelatedParty> RelatedParties,
    [property: Description("支持同一人的證據")] IReadOnlyList<Evidence> Support,
    [property: Description("衝突的證據")] IReadOnlyList<Evidence> Conflicts,
    [property: Description("缺值或不確定的比對項目")] IReadOnlyList<string> UnknownFields,
    [property: Description("第一階段硬衝突代碼")] string? HardConflict,
    [property: Description("第三階段決策表列號（1～6）")] int? Stage3Row,
    [property: Description("對比式解釋：若證件或完整出生日期可比對，身分判斷會移到哪一列；不代表對外結果")] IReadOnlyList<WouldChangeIf> WouldChangeIf,
    [property: Description("系統身分判斷")] IdentityDecision IdentityDecision,
    [property: Description("此組合的對外結果")] ScreeningAssessment PairResult,
    [property: Description("複核優先序")] ReviewPriority? ReviewPriority,
    [property: Description("對外結果的理由碼")] IReadOnlyList<string> PairReasonCodes,
    [property: Description("第 22 條建議，僅供參考")] DispositionRecommendation Disposition,
    [property: Description("身分判斷的理由碼")] IReadOnlyList<string> ReasonCodes,
    [property: Description("資料品質 finding，見 /api/codes")] IReadOnlyList<string> DataQualityFindings,
    [property: Description("來源；Ref 在摘要中以 [S#] 引用")] IReadOnlyList<ExplanationSource> Sources,
    [property: Description("五段式摘要")] ExplanationSummary Summary);

/// <summary>A source behind a hit: what it is, where it is and what the system used it for. Ref (S1, S2, …) is cited as [S1] in the summary.</summary>
public sealed record ExplanationSource(
    [property: Description("摘要中的引用編號（S1、S2…）")] string Ref,
    [property: Description("來源編號")] string Id,
    [property: Description("來源名稱或新聞出處與標題")] string Name,
    [property: Description("來源網址")] string? Url,
    [property: Description("發布日期")] string? PublishedOn,
    [property: Description("取得日期")] string? RetrievedOn,
    [property: Description("用途：PROFILE_RECORD、LEGAL_STAGE、CONTEXT")] string UsedFor,
    [property: Description("是否附於名單")] bool AttachedToProfile);

public sealed record CaseExplanation(
    [property: Description("案件層級結果")] ScreeningAssessment CaseResult,
    [property: Description("每組受檢對象與名單的解釋")] IReadOnlyList<HitExplanation> Hits,
    [property: Description("缺名單資料而無法評估的名單鍵")] IReadOnlyList<string> NotEvaluatedProfiles,
    [property: Description("自輸入檔讀入的舊決定，從不成為系統判斷")] IReadOnlyList<ImportedDecision> ImportedDecisions,
    [property: Description("與前一個生效 revision 的差異；第一次分析為 null")] IReadOnlyList<PairChange>? ChangesFromPreviousRevision)
{
    [property: Description("儲存格式版本")]
    public int SchemaVersion { get; init; } = CaseJson.SchemaVersion;
}

/// <summary>What the system used a source for.</summary>
public static class SourceUse
{
    public const string ProfileRecord = "PROFILE_RECORD";
    public const string LegalStage = "LEGAL_STAGE";
    public const string Context = "CONTEXT";
}

/// <summary>Builds the reviewer and audit view (pipeline-api-and-batch.md, explanation contents).</summary>
public static class ExplanationBuilder
{
    /// <param name="changes">Differences from the previous revision; null on the first analysis.</param>
    public static CaseExplanation Build(CaseInput input, CaseAnalysis analysis, IReadOnlyList<PairChange>? changes = null) =>
        new(CaseLifecycle.Aggregate(analysis),
            analysis.Hits.Select((h, i) => Hit(i + 1, input, h)).ToList(),
            analysis.NotEvaluatedProfiles,
            input.LegacyDecisions,
            changes);

    private static HitExplanation Hit(int number, CaseInput input, HitResult h)
    {
        var s = input.Subject;
        var p = h.Profile;
        var e = h.Identity.Evidence;
        var rows = Comparison(s, p, e);
        var subjectLabel = s.Role == SubjectRole.Customer ? "客戶" : $"關聯人（{s.Relationship}）";
        var subject = $"{subjectLabel} {string.Join(" / ", s.Names)}";
        var matched = input.Rows.Where(r => "DJ:" + r.ProfileId == p.ProfileKey).Select(r => r.MatchedName).Distinct().ToList();
        var categories = p.Categories.Select(c => c.Subcategory is null ? c.Category : $"{c.Category} / {c.Subcategory}").ToList();

        var support = e.Values.Where(v => v.IsSupport).ToList();
        var conflicts = e.Values.Where(v => v.Status == ComparisonStatus.Conflict).ToList();
        var unknown = rows.Where(r => r.Status is ComparisonStatus.SubjectMissing or ComparisonStatus.ProfileMissing or ComparisonStatus.Uncertain)
            .Select(r => r.Field).ToList();
        var findings = s.Findings.Concat(s.Addresses.Select(a => a.Detail).OfType<string>()).Concat(p.Findings).Distinct().ToList();
        var newsItem = input.NewsByProfileId.GetValueOrDefault(p.ProfileKey["DJ:".Length..]);
        var news = newsItem is not null;
        var sources = p.Sources.Select((x, i) => new ExplanationSource($"S{i + 1}", x.Id, x.Name, x.Url, x.PublishedOn, x.RetrievedOn, SourceUse.ProfileRecord, true)).ToList();
        if (newsItem is not null)
            sources.Add(new ExplanationSource($"S{sources.Count + 1}", newsItem.DocumentId ?? $"{p.ProfileKey}#news", $"{newsItem.Publisher}：{newsItem.Title}", null,
                newsItem.PublishedOn?.ToString(), null,
                h.Conditions.Any(c => c.Type == ListType.AdverseMedia) ? SourceUse.LegalStage : SourceUse.Context, true));

        var summary = new ExplanationSummary(
            InternalData: $"受檢對象為{subject}；出生日期 {Show(s.DateOfBirth?.ToString())}；國籍 {Show(Join(s.Nationalities))}；性別 {Show(Gender(s.Gender))}；證件 {(s.Document is null ? "未提供" : "已提供")}。",
            ExternalData: $"名單 {p.ProfileKey}（{Join(categories)}）{Cite(sources.Where(x => x.UsedFor == SourceUse.ProfileRecord))}判定為 {Join(h.Conditions.Select(Condition))}；"
                + $"出生日期 {Show(Join(p.DatesOfBirth.Select(d => d.ToString())))}；國籍 {Show(Join(p.Citizenship))}；性別 {Show(Gender(p.Gender))}；"
                + $"附加新聞{(news ? "有" + Cite(sources.Where(x => x.UsedFor != SourceUse.ProfileRecord)) : "無")}。",
            AgreementsAndDifferences: $"相符：{Show(Join(rows.Where(r => r.Status is ComparisonStatus.Exact or ComparisonStatus.Partial).Select(r => r.Field)), "無")}；"
                + $"衝突：{Show(Join(rows.Where(r => r.Status == ComparisonStatus.Conflict).Select(r => r.Field)), "無")}；"
                + $"無法比對：{Show(Join(unknown), "無")}。",
            KeyEvidenceOrGaps: KeyEvidence(h, support),
            Conclusion: $"系統身分判斷 {Snake(h.Identity.Decision)}"
                + (h.Identity.HardConflict is { } hc ? $"（第一階段硬衝突 {hc}）"
                    : h.Identity.Stage3Row is { } row ? $"（第三階段第 {row} 列）" : $"（{h.Identity.ReasonCodes[0]}）")
                + $"；對外結果 {Snake(h.PairResult)}" + (h.ReviewPriority is { } pr ? $"（複核優先序 {Snake(pr)}）" : "")
                + $"；理由碼 {Join(h.Identity.ReasonCodes.Concat(h.PairReasonCodes))}。名單條件與身分判斷各自獨立。"
                + Disposition(h.Disposition));

        var related = (s.Relatives ?? []).Select(r => new RelatedParty("SUBJECT", r.Name, r.Relation, r.IsEntity))
            .Concat(p.Relationships.Select(r => new RelatedParty("PROFILE", r.Name, r.Relation, r.IsEntity, r.ProfileId))).ToList();

        return new HitExplanation(number, subject, p.ProfileKey, p.Names.Count > 0 ? p.Names[0] : null, matched, input.Rows.Any(r => "DJ:" + r.ProfileId == p.ProfileKey && r.IsRca),
            categories, h.Conditions, news, rows, related, support, conflicts, unknown, h.Identity.HardConflict, h.Identity.Stage3Row,
            IdentityDecisionPolicy.Contrast(h.Identity), h.Identity.Decision, h.PairResult, h.ReviewPriority, h.PairReasonCodes, h.Disposition, h.Identity.ReasonCodes, findings, sources, summary);
    }

    private static List<ComparisonRow> Comparison(ScreenedSubject s, SourceProfile p, IReadOnlyDictionary<EvidenceGroup, Evidence> e)
    {
        ComparisonStatus St(EvidenceGroup g) => e.TryGetValue(g, out var v) ? v.Status : ComparisonStatus.SubjectMissing;
        string? Detail(EvidenceGroup g) => e.TryGetValue(g, out var v) ? v.Detail : null;

        var roles = p.Roles.Select(r => $"{r.Title}，{r.Organization}" + (r.From is { } f ? $"（{f} 起）" : ""))
            .Concat(p.Relationships.Where(r => r.Relation.Equals("Employer", StringComparison.OrdinalIgnoreCase)).Select(r => $"受僱於 {r.Name}"));
        var addresses = s.Addresses.Select(a => a.State == ValueState.Present ? a.Normalized ?? a.Raw : $"{a.Raw}（{a.Detail}）");

        return
        [
            NameRow("中文姓名", s.Names.Where(IsCjk), p.Names.Where(IsCjk)),
            NameRow("英文姓名／拼音", s.Names.Where(n => !IsCjk(n)), p.Names.Where(n => !IsCjk(n))),
            new("國籍", Opt(Join(s.Nationalities)), Opt(Join(p.Citizenship)), St(EvidenceGroup.Nationality), null),
            new("出生日期", s.DateOfBirth?.ToString(), Opt(Join(p.DatesOfBirth.Select(d => d.ToString()))), St(EvidenceGroup.DateOfBirth),
                DobDifference(e, p), e.GetValueOrDefault(EvidenceGroup.StatedAge)?.Note),
            new("性別", Gender(s.Gender), Gender(p.Gender), St(EvidenceGroup.Gender), null),
            new("證件", s.Document is { } d ? $"{d.Type} {Mask(d.Number)}" : null,
                Opt(Join((p.Documents ?? []).Select(x => $"{x.Type} {Mask(x.Number)}"))), St(EvidenceGroup.Document), Detail(EvidenceGroup.Document)),
            new("地址", Opt(Join(addresses)), Opt(Join(p.AddressLines is { Count: > 0 } lines ? lines.Count == p.AddressCountries.Count ? lines.Zip(p.AddressCountries, (l, c) => $"{l}（{c}）") : lines : p.AddressCountries)),
                St(EvidenceGroup.Address), Detail(EvidenceGroup.Address)),
            new("職業／雇主", Opt(Join(new[] { s.Occupation }.OfType<string>()
                    .Concat((s.Employments ?? []).Select(x => $"{x.Title}，{x.Organization}" + (x.From is { } f ? $"（{f} 起）" : ""))))),
                Opt(Join(roles)), St(EvidenceGroup.Employment), Detail(EvidenceGroup.Employment)),
            new("其他識別碼", s.ReferenceNumber is { } r ? $"參考號碼 {Mask(r)}" : null, null, St(EvidenceGroup.OtherIdentifier),
                s.ReferenceNumber is null ? null : EvidenceDetails.NotADocument),
        ];
    }

    // DOB code, plus the code of any age stated in news (the table's "DOB or age" row); the age itself is the row's Note.
    private static string? DobDifference(IReadOnlyDictionary<EvidenceGroup, Evidence> e, SourceProfile p)
    {
        var parts = new List<string>();
        if (e.TryGetValue(EvidenceGroup.DateOfBirth, out var dob) && dob.Detail is not null) parts.Add(dob.Detail);
        else if (p.Findings.Contains(Findings.ProfileMultipleDob)) parts.Add(Findings.ProfileMultipleDob);
        if (e.TryGetValue(EvidenceGroup.StatedAge, out var age) && age.Detail is not null) parts.Add(age.Detail);
        return parts.Count == 0 ? null : string.Join("；", parts);
    }

    private static string Cite(IEnumerable<ExplanationSource> sources) => string.Concat(sources.Select(x => $"[{x.Ref}]"));

    private static ComparisonRow NameRow(string field, IEnumerable<string> subject, IEnumerable<string> profile)
    {
        var sv = subject.ToList();
        var pv = profile.ToList();
        var (status, detail) = Normalize.CompareNames(sv, pv);
        return new(field, Opt(Join(sv)), Opt(Join(pv)), status, detail);
    }

    private static string KeyEvidence(HitResult h, List<Evidence> support)
    {
        var sb = new StringBuilder();
        if (h.Identity.HardConflict is { } hc)
            sb.Append("硬衝突 " + hc + (HardConflictGate.Validated.Contains(hc) ? "，足以排除。" : "，規則尚待驗證，不自動排除。"));
        sb.Append(support.Count == 0
            ? "除姓名、國籍、性別等前提條件外，沒有任何佐證。"
            : "佐證：" + Join(support.Select(v => $"{Snake(v.Group)}（{Snake(v.Strength)}{(v.Detail is null ? "" : "，" + v.Detail)}）")) + "。");
        if (h.Identity.ReasonCodes.Contains(ReasonCodes.NoBilateralDirectIdentifier))
            sb.Append("雙方沒有可共同比對的直接識別資料（完整出生日期或證件）。");
        return sb.ToString();
    }

    // Advisory only: the final selection happens outside the system (ADR-0020).
    private static string Disposition(DispositionRecommendation d) =>
        (d.Recommendation is { } r ? $"第 22 條建議 {Snake(r)}（{Join(d.ReasonCodes)}，僅供參考）。" : $"第 22 條無法給建議（{Join(d.ReasonCodes)}）。")
        + (d.PendingChecks.Count > 0 ? $"待查核：{Join(d.PendingChecks)}（尚未執行）。" : "");

    private static string Condition(SourceCondition c)
    {
        var parts = new List<string>();
        if (c.RiskCategories.Count > 0) parts.Add(Join(c.RiskCategories));
        if (c.LegalStage != LegalStage.Unknown) parts.Add("法律階段 " + Snake(c.LegalStage));
        if (c.Status != ConditionStatus.Hit) parts.Add("條件狀態 " + Snake(c.Status) + (c.ReasonCodes.Count > 0 ? "，" + Join(c.ReasonCodes) : ""));
        return Snake(c.Type) + (parts.Count > 0 ? $"（{string.Join("；", parts)}）" : "");
    }

    /// <summary>Keeps the first 3 and last 2 characters: A12*****89.</summary>
    public static string Mask(string value) =>
        value.Length <= 5 ? new string('*', value.Length) : value[..3] + new string('*', value.Length - 5) + value[^2..];

    private static bool IsCjk(string s) => s.Any(c => c is >= '㐀' and <= '鿿');

    private static string? Gender(Gender? g) => g switch { Shared.Gender.Male => "男", Shared.Gender.Female => "女", _ => null };

    private static string Join(IEnumerable<string> values) => string.Join("、", values);

    private static string? Opt(string s) => s.Length == 0 ? null : s;

    private static string Show(string? s, string missing = "未提供") => string.IsNullOrEmpty(s) ? missing : s;

    // InReview → IN_REVIEW, matching the API's enum naming.
    private static string Snake<T>(T value) where T : struct, Enum =>
        string.Concat(value.ToString().Select((ch, i) => i > 0 && char.IsUpper(ch) ? "_" + ch : ch.ToString())).ToUpperInvariant();
}
