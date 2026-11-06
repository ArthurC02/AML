using System.Text;
using Aml.Core.Cases;
using Aml.Core.Identity;
using Aml.Core.Normalization;
using Aml.Core.Retrieval;
using Aml.Core.Screening;
using Aml.Core.Shared;
using Aml.Core.Subjects;

namespace Aml.Core.Explanation;

/// <summary>Builds the reviewer and audit view (pipeline-api-and-batch.md, explanation contents).</summary>
public static class ExplanationBuilder
{
    /// <param name="changes">Differences from the previous revision; null on the first analysis.</param>
    public static CaseExplanation Build(CaseInput input, CaseAnalysis analysis, IReadOnlyList<PairChange>? changes = null) =>
        new(CaseLifecycle.Aggregate(analysis),
            analysis.Hits.Select((h, i) => Hit(i + 1, input, h)).ToList(),
            analysis.NotEvaluatedProfiles,
            input.ImportedDecisions,
            changes);

    private static HitExplanation Hit(int number, CaseInput input, HitResult h)
    {
        var s = input.Subject;
        var p = h.Profile;
        var e = h.Identity.Evidence;
        var rows = Comparison(s, p, e);
        var subjectLabel = s.Role == SubjectRole.Customer ? "客戶" : $"關聯人（{s.Relationship}）";
        var subject = $"{subjectLabel} {string.Join(" / ", s.Names)}";
        var matched = input.Rows.Where(r => ProfileKeys.KeyOf(r.ProfileId) == p.ProfileKey).Select(r => r.MatchedName).Distinct().ToList();
        var categories = p.Categories.Select(c => c.Subcategory is null ? c.Category : $"{c.Category} / {c.Subcategory}").ToList();

        var support = e.Values.Where(v => v.IsSupport).ToList();
        var conflicts = e.Values.Where(v => v.Status == ComparisonStatus.Conflict).ToList();
        var unknown = rows.Where(r => r.Status is ComparisonStatus.SubjectMissing or ComparisonStatus.ProfileMissing or ComparisonStatus.Uncertain)
            .Select(r => r.Field).ToList();
        var findings = s.Findings.Concat(s.Addresses.Select(a => a.Detail).OfType<string>()).Concat(p.Findings).Distinct().ToList();
        var news = input.NewsByProfileId.GetValueOrDefault(ProfileKeys.IdOf(p.ProfileKey));
        var hasNews = news is not null;
        var sources = p.Sources.Select((x, i) => new ExplanationSource($"S{i + 1}", x.Id, x.Name, x.Url, x.PublishedOn, x.RetrievedOn, SourceUse.ProfileRecord, true)).ToList();
        if (news is not null)
            sources.Add(new ExplanationSource($"S{sources.Count + 1}", news.DocumentId ?? $"{p.ProfileKey}#news", $"{news.Publisher}：{news.Title}", null,
                news.PublishedOn?.ToString(), null,
                h.Conditions.Any(c => c.Type == ListType.AdverseMedia) ? SourceUse.LegalStage : SourceUse.Context, true));

        var summary = new ExplanationSummary(
            InternalData: $"受檢對象為{subject}；出生日期 {OrMissing(s.DateOfBirth?.ToString())}；國籍 {OrMissing(Join(s.Nationalities))}；性別 {OrMissing(Gender(s.Gender))}；證件 {(s.Document is null ? "未提供" : "已提供")}。",
            ExternalData: $"名單 {p.ProfileKey}（{Join(categories)}）{Cite(sources.Where(x => x.UsedFor == SourceUse.ProfileRecord))}判定為 {Join(h.Conditions.Select(Condition))}；"
                + $"出生日期 {OrMissing(Join(p.DatesOfBirth.Select(d => d.ToString())))}；國籍 {OrMissing(Join(p.Citizenship))}；性別 {OrMissing(Gender(p.Gender))}；"
                + $"附加新聞{(hasNews ? "有" + Cite(sources.Where(x => x.UsedFor != SourceUse.ProfileRecord)) : "無")}。",
            AgreementsAndDifferences: $"相符：{OrMissing(Join(rows.Where(r => r.Status is ComparisonStatus.Exact or ComparisonStatus.Partial).Select(r => r.Field)), "無")}；"
                + $"衝突：{OrMissing(Join(rows.Where(r => r.Status == ComparisonStatus.Conflict).Select(r => r.Field)), "無")}；"
                + $"無法比對：{OrMissing(Join(unknown), "無")}。",
            KeyEvidenceOrGaps: KeyEvidence(h, support),
            Conclusion: $"系統身分判斷 {CaseJson.Code(h.Identity.Decision)}"
                + (h.Identity.HardConflict is { } hc ? $"（第一階段硬衝突 {hc}）"
                    : h.Identity.Stage3Row is { } row ? $"（第三階段第 {row} 列）" : $"（{h.Identity.ReasonCodes[0]}）")
                + $"；對外結果 {CaseJson.Code(h.PairResult)}" + (h.ReviewPriority is { } pr ? $"（複核優先序 {CaseJson.Code(pr)}）" : "")
                + $"；理由碼 {Join(h.Identity.ReasonCodes.Concat(h.PairReasonCodes))}。名單條件與身分判斷各自獨立。"
                + Disposition(h.Disposition));

        var related = (s.Relatives ?? []).Select(r => new RelatedParty("SUBJECT", r.Name, r.Relation, r.IsEntity))
            .Concat(p.Relationships.Select(r => new RelatedParty("PROFILE", r.Name, r.Relation, r.IsEntity, r.ProfileId))).ToList();

        return new HitExplanation(number, subject, ProfileKeys.IdOf(p.ProfileKey), p.ProfileKey, p.Names.Count > 0 ? p.Names[0] : null, matched, input.Rows.Any(r => ProfileKeys.KeyOf(r.ProfileId) == p.ProfileKey && r.IsRca),
            categories, h.Conditions, hasNews, rows, related, support, conflicts, unknown, h.Identity.HardConflict, h.Identity.Stage3Row,
            IdentityDecisionPolicy.Contrast(h.Identity), h.Identity.Decision, h.PairResult, h.ReviewPriority, h.PairReasonCodes, h.Disposition, h.Identity.ReasonCodes, findings, sources, summary);
    }

    private static List<ComparisonRow> Comparison(ScreenedSubject s, SourceProfile p, IReadOnlyDictionary<EvidenceGroup, Evidence> e)
    {
        ComparisonStatus StatusOf(EvidenceGroup g) => e.TryGetValue(g, out var v) ? v.Status : ComparisonStatus.SubjectMissing;
        string? DetailOf(EvidenceGroup g) => e.TryGetValue(g, out var v) ? v.Detail : null;

        var roles = p.Roles.Select(r => $"{r.Title}，{r.Organization}" + (r.From is { } f ? $"（{f} 起）" : ""))
            .Concat(p.Relationships.Where(r => EvidenceBuilder.IsEmployer(r.Relation)).Select(r => $"受僱於 {r.Name}"));
        var addresses = s.Addresses.Select(a => a.State == ValueState.Present ? a.Normalized ?? a.Raw : $"{a.Raw}（{a.Detail}）");

        return
        [
            NameRow("中文姓名", s.Names.Where(IsCjk), p.Names.Where(IsCjk)),
            NameRow("英文姓名／拼音", s.Names.Where(n => !IsCjk(n)), p.Names.Where(n => !IsCjk(n))),
            new("國籍", NullIfEmpty(Join(s.Nationalities)), NullIfEmpty(Join(p.Citizenship)), StatusOf(EvidenceGroup.Nationality), null),
            new("出生日期", s.DateOfBirth?.ToString(), NullIfEmpty(Join(p.DatesOfBirth.Select(d => d.ToString()))), StatusOf(EvidenceGroup.DateOfBirth),
                DobDifference(e, p), e.GetValueOrDefault(EvidenceGroup.StatedAge)?.Note),
            new("性別", Gender(s.Gender), Gender(p.Gender), StatusOf(EvidenceGroup.Gender), null),
            new("證件", s.Document is { } d ? $"{d.Type} {Mask(d.Number)}" : null,
                NullIfEmpty(Join((p.Documents ?? []).Select(x => $"{x.Type} {Mask(x.Number)}"))), StatusOf(EvidenceGroup.Document), DetailOf(EvidenceGroup.Document)),
            new("地址", NullIfEmpty(Join(addresses)), NullIfEmpty(Join(p.AddressLines is { Count: > 0 } lines ? lines.Count == p.AddressCountries.Count ? lines.Zip(p.AddressCountries, (l, c) => $"{l}（{c}）") : lines : p.AddressCountries)),
                StatusOf(EvidenceGroup.Address), DetailOf(EvidenceGroup.Address)),
            new("職業／雇主", NullIfEmpty(Join(new[] { s.Occupation }.OfType<string>()
                    .Concat((s.Employments ?? []).Select(x => $"{x.Title}，{x.Organization}" + (x.From is { } f ? $"（{f} 起）" : ""))))),
                NullIfEmpty(Join(roles)), StatusOf(EvidenceGroup.Employment), DetailOf(EvidenceGroup.Employment)),
            new("其他識別碼", s.ReferenceNumber is { } r ? $"參考號碼 {Mask(r)}" : null, null, StatusOf(EvidenceGroup.OtherIdentifier),
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
        return new(field, NullIfEmpty(Join(sv)), NullIfEmpty(Join(pv)), status, detail);
    }

    private static string KeyEvidence(HitResult h, List<Evidence> support)
    {
        var sb = new StringBuilder();
        if (h.Identity.HardConflict is { } hc)
            sb.Append("硬衝突 " + hc + (HardConflictGate.Validated.Contains(hc) ? "，足以排除。" : "，規則尚待驗證，不自動排除。"));
        sb.Append(support.Count == 0
            ? "除姓名、國籍、性別等前提條件外，沒有任何佐證。"
            : "佐證：" + Join(support.Select(v => $"{CaseJson.Code(v.Group)}（{CaseJson.Code(v.Strength)}{(v.Detail is null ? "" : "，" + v.Detail)}）")) + "。");
        if (h.Identity.ReasonCodes.Contains(ReasonCodes.NoBilateralDirectIdentifier))
            sb.Append("雙方沒有可共同比對的直接識別資料（完整出生日期或證件）。");
        return sb.ToString();
    }

    // Advisory only: the final selection happens outside the system (ADR-0020).
    private static string Disposition(DispositionRecommendation d) =>
        (d.Recommendation is { } r ? $"第 22 條建議 {CaseJson.Code(r)}（{Join(d.ReasonCodes)}，僅供參考）。" : $"第 22 條無法給建議（{Join(d.ReasonCodes)}）。")
        + (d.PendingChecks.Count > 0 ? $"待查核：{Join(d.PendingChecks)}（尚未執行）。" : "");

    private static string Condition(SourceCondition c)
    {
        var parts = new List<string>();
        if (c.RiskCategories.Count > 0) parts.Add(Join(c.RiskCategories));
        if (c.LegalStage != LegalStage.Unknown) parts.Add("法律階段 " + CaseJson.Code(c.LegalStage));
        if (c.Status != ConditionStatus.Hit) parts.Add("條件狀態 " + CaseJson.Code(c.Status) + (c.ReasonCodes.Count > 0 ? "，" + Join(c.ReasonCodes) : ""));
        return CaseJson.Code(c.Type) + (parts.Count > 0 ? $"（{string.Join("；", parts)}）" : "");
    }

    /// <summary>Keeps the first 3 and last 2 characters: A12*****89.</summary>
    public static string Mask(string value) =>
        value.Length <= 5 ? new string('*', value.Length) : value[..3] + new string('*', value.Length - 5) + value[^2..];

    private static bool IsCjk(string s) => s.Any(Normalize.IsCjk);

    private static string? Gender(Gender? g) => g switch { Shared.Gender.Male => "男", Shared.Gender.Female => "女", _ => null };

    private static string Join(IEnumerable<string> values) => string.Join("、", values);

    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;

    private static string OrMissing(string? s, string missing = "未提供") => string.IsNullOrEmpty(s) ? missing : s;
}
