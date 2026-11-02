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
    PairResult PairResult,
    IReadOnlyList<string> ReasonCodes,
    IReadOnlyList<string> DataQualityFindings,
    ExplanationSummary Summary);

public sealed record CaseExplanation(CaseResult CaseResult, IReadOnlyList<HitExplanation> Hits, IReadOnlyList<ImportedDecision> ImportedDecisions);

/// <summary>Builds the reviewer and audit view (pipeline-api-and-batch.md, explanation contents).</summary>
public static class ExplanationBuilder
{
    public static CaseExplanation Build(CaseInput input, CaseAnalysis analysis) =>
        new(CaseLifecycle.Aggregate(analysis),
            analysis.Hits.Select((h, i) => Hit(i + 1, input, h)).ToList(),
            input.LegacyDecisions);

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
        var news = input.NewsByProfileId.ContainsKey(p.ProfileKey["DJ:".Length..]);

        var summary = new ExplanationSummary(
            InternalData: $"受檢對象為{subject}；出生日期 {Show(s.DateOfBirth?.ToString())}；國籍 {Show(Join(s.Nationalities))}；性別 {Show(Gender(s.Gender))}；證件 {(s.Document is null ? "未提供" : "已提供")}。",
            ExternalData: $"名單 {p.ProfileKey}（{Join(categories)}）判定為 {Join(h.Conditions.Select(Condition))}；"
                + $"出生日期 {Show(Join(p.DatesOfBirth.Select(d => d.ToString())))}；國籍 {Show(Join(p.Citizenship))}；性別 {Show(Gender(p.Gender))}；附加新聞{(news ? "有" : "無")}。",
            AgreementsAndDifferences: $"相符：{Show(Join(rows.Where(r => r.Status is ComparisonStatus.Exact or ComparisonStatus.Partial).Select(r => r.Field)), "無")}；"
                + $"衝突：{Show(Join(rows.Where(r => r.Status == ComparisonStatus.Conflict).Select(r => r.Field)), "無")}；"
                + $"無法比對：{Show(Join(unknown), "無")}。",
            KeyEvidenceOrGaps: KeyEvidence(h, support),
            Conclusion: $"系統身分判斷 {Snake(h.Identity.Decision)}"
                + (h.Identity.HardConflict is { } hc ? $"（第一階段硬衝突 {hc}）" : $"（第三階段第 {h.Identity.Stage3Row} 列）")
                + $"；對外結果 {Snake(h.PairResult)}；理由碼 {Join(h.Identity.ReasonCodes)}。名單條件與身分判斷各自獨立。");

        return new HitExplanation(number, subject, p.ProfileKey, matched, input.Rows.Any(r => "DJ:" + r.ProfileId == p.ProfileKey && r.IsRca),
            categories, h.Conditions, news, rows, support, conflicts, unknown, h.Identity.HardConflict, h.Identity.Stage3Row,
            h.Identity.Decision, h.PairResult, h.Identity.ReasonCodes, findings, summary);
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
                Detail(EvidenceGroup.DateOfBirth) ?? (p.Findings.Contains(Findings.ProfileMultipleDob) ? Findings.ProfileMultipleDob : null)),
            new("性別", Gender(s.Gender), Gender(p.Gender), St(EvidenceGroup.Gender), null),
            new("證件", s.Document is { } d ? $"{d.Type} {Mask(d.Number)}" : null, null, St(EvidenceGroup.Document), null),
            new("地址", Opt(Join(addresses)), Opt(Join(p.AddressCountries)), St(EvidenceGroup.Address), Detail(EvidenceGroup.Address)),
            new("職業／雇主", s.Occupation, Opt(Join(roles)), St(EvidenceGroup.Employment), null),
            new("其他識別碼", s.ReferenceNumber is { } r ? $"參考號碼 {Mask(r)}" : null, null, St(EvidenceGroup.OtherIdentifier),
                s.ReferenceNumber is null ? null : "NOT_A_DOCUMENT"),
        ];
    }

    private static ComparisonRow NameRow(string field, IEnumerable<string> subject, IEnumerable<string> profile)
    {
        var sv = subject.ToList();
        var pv = profile.ToList();
        var status = sv.Count == 0 ? ComparisonStatus.SubjectMissing
            : pv.Count == 0 ? ComparisonStatus.ProfileMissing
            : sv.SelectMany(Normalize.NameKeys).Intersect(pv.SelectMany(Normalize.NameKeys)).Any() ? ComparisonStatus.Exact
            : ComparisonStatus.Partial;
        return new(field, Opt(Join(sv)), Opt(Join(pv)), status, status == ComparisonStatus.Partial ? "NOT_IDENTICAL_AFTER_NORMALIZATION" : null);
    }

    private static string KeyEvidence(HitResult h, List<Evidence> support)
    {
        var sb = new StringBuilder();
        if (h.Identity.HardConflict is { } hc) sb.Append(CultureInfo.InvariantCulture, $"硬衝突 {hc}，足以排除。");
        sb.Append(support.Count == 0
            ? "除姓名、國籍、性別等前提條件外，沒有任何佐證。"
            : "佐證：" + Join(support.Select(v => $"{Snake(v.Group)}（{Snake(v.Strength)}{(v.Detail is null ? "" : "，" + v.Detail)}）")) + "。");
        if (h.Identity.ReasonCodes.Contains(ReasonCodes.NoBilateralDirectIdentifier))
            sb.Append("雙方沒有可共同比對的直接識別資料（完整出生日期或證件）。");
        return sb.ToString();
    }

    private static string Condition(SourceCondition c)
    {
        var parts = new List<string>();
        if (c.RiskCategories.Count > 0) parts.Add(Join(c.RiskCategories));
        if (c.LegalStage != LegalStage.Unknown) parts.Add("法律階段 " + Snake(c.LegalStage));
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
