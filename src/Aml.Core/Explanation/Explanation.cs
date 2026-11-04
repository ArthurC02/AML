using System.Globalization;
using System.Text;
using Aml.Core.Cases;
using Aml.Core.Identity;
using Aml.Core.Normalization;
using Aml.Core.Screening;
using Aml.Core.Shared;
using Aml.Core.Subjects;

namespace Aml.Core.Explanation;

/// <summary>One row of the fixed-order comparison table.</summary>
public sealed record ComparisonRow(string Field, string? SubjectValue, string? ProfileValue, ComparisonStatus Status, string? Difference);

/// <summary>Five-part summary. It restates recorded facts only and adds none.</summary>
public sealed record ExplanationSummary(string InternalData, string ExternalData, string AgreementsAndDifferences, string KeyEvidenceOrGaps, string Conclusion);

public sealed record HitExplanation(
    int HitNumber,
    string ScreenedSubject,
    string ProfileKey,
    IReadOnlyList<string> MatchedNames,
    bool IsRca,
    IReadOnlyList<string> SourceCategories,
    IReadOnlyList<SourceCondition> SourceConditions,
    bool AttachedNews,
    IReadOnlyList<ComparisonRow> Comparison,
    IReadOnlyList<Evidence> Support,
    IReadOnlyList<Evidence> Conflicts,
    IReadOnlyList<string> UnknownFields,
    string? HardConflict,
    int? Stage3Row,
    IdentityDecision IdentityDecision,
    ScreeningAssessment PairResult,
    ReviewPriority? ReviewPriority,
    IReadOnlyList<string> PairReasonCodes,
    DispositionRecommendation Disposition,
    IReadOnlyList<string> ReasonCodes,
    IReadOnlyList<string> DataQualityFindings,
    IReadOnlyList<ExplanationSource> Sources,
    ExplanationSummary Summary);

/// <summary>A source behind a hit: what it is, where it is and what the system used it for.</summary>
public sealed record ExplanationSource(string Id, string Name, string? Url, string? PublishedOn, string? RetrievedOn, string UsedFor, bool AttachedToProfile);

public sealed record CaseExplanation(
    ScreeningAssessment CaseResult,
    IReadOnlyList<HitExplanation> Hits,
    IReadOnlyList<string> NotEvaluatedProfiles,
    IReadOnlyList<ImportedDecision> ImportedDecisions,
    IReadOnlyList<PairChange>? ChangesFromPreviousRevision);

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
        var sources = p.Sources.Select(x => new ExplanationSource(x.Id, x.Name, x.Url, x.PublishedOn, x.RetrievedOn, "PROFILE_RECORD", true)).ToList();
        if (newsItem is not null)
            sources.Add(new ExplanationSource(newsItem.DocumentId ?? $"{p.ProfileKey}#news", $"{newsItem.Publisher}：{newsItem.Title}", null,
                newsItem.PublishedOn?.ToString(), null,
                h.Conditions.Any(c => c.Type == ListType.AdverseMedia) ? "LEGAL_STAGE" : "CONTEXT", true));

        var summary = new ExplanationSummary(
            InternalData: $"受檢對象為{subject}；出生日期 {Show(s.DateOfBirth?.ToString())}；國籍 {Show(Join(s.Nationalities))}；性別 {Show(Gender(s.Gender))}；證件 {(s.Document is null ? "未提供" : "已提供")}。",
            ExternalData: $"名單 {p.ProfileKey}（{Join(categories)}）判定為 {Join(h.Conditions.Select(Condition))}；"
                + $"出生日期 {Show(Join(p.DatesOfBirth.Select(d => d.ToString())))}；國籍 {Show(Join(p.Citizenship))}；性別 {Show(Gender(p.Gender))}；附加新聞{(news ? "有" : "無")}。",
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

        return new HitExplanation(number, subject, p.ProfileKey, matched, input.Rows.Any(r => "DJ:" + r.ProfileId == p.ProfileKey && r.IsRca),
            categories, h.Conditions, news, rows, support, conflicts, unknown, h.Identity.HardConflict, h.Identity.Stage3Row,
            h.Identity.Decision, h.PairResult, h.ReviewPriority, h.PairReasonCodes, h.Disposition, h.Identity.ReasonCodes, findings, sources, summary);
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
                DobDifference(e, p)),
            new("性別", Gender(s.Gender), Gender(p.Gender), St(EvidenceGroup.Gender), null),
            new("證件", s.Document is { } d ? $"{d.Type} {Mask(d.Number)}" : null,
                Opt(Join((p.Documents ?? []).Select(x => $"{x.Type} {Mask(x.Number)}"))), St(EvidenceGroup.Document), Detail(EvidenceGroup.Document)),
            new("地址", Opt(Join(addresses)), Opt(Join(p.AddressLines is { Count: > 0 } lines ? lines.Count == p.AddressCountries.Count ? lines.Zip(p.AddressCountries, (l, c) => $"{l}（{c}）") : lines : p.AddressCountries)),
                St(EvidenceGroup.Address), Detail(EvidenceGroup.Address)),
            new("職業／雇主", Opt(Join(new[] { s.Occupation }.OfType<string>()
                    .Concat((s.Employments ?? []).Select(x => $"{x.Title}，{x.Organization}" + (x.From is { } f ? $"（{f} 起）" : ""))))),
                Opt(Join(roles)), St(EvidenceGroup.Employment), Detail(EvidenceGroup.Employment)),
            new("其他識別碼", s.ReferenceNumber is { } r ? $"參考號碼 {Mask(r)}" : null, null, St(EvidenceGroup.OtherIdentifier),
                s.ReferenceNumber is null ? null : "NOT_A_DOCUMENT"),
        ];
    }

    // DOB detail, plus any age stated in news (the table's "DOB or age" row).
    private static string? DobDifference(IReadOnlyDictionary<EvidenceGroup, Evidence> e, SourceProfile p)
    {
        var parts = new List<string>();
        if (e.TryGetValue(EvidenceGroup.DateOfBirth, out var dob) && dob.Detail is not null) parts.Add(dob.Detail);
        else if (p.Findings.Contains(Findings.ProfileMultipleDob)) parts.Add(Findings.ProfileMultipleDob);
        if (e.TryGetValue(EvidenceGroup.StatedAge, out var age)) parts.Add($"{age.Detail}（{age.Note}）");
        return parts.Count == 0 ? null : string.Join("；", parts);
    }

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
