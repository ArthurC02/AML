using System.Text;
using Aml.Core.Cases;
using Aml.Core.Identity;
using Aml.Core.Normalization;
using Aml.Core.Ports;
using Aml.Core.Retrieval;
using Aml.Core.Screening;
using Aml.Core.Shared;
using Aml.Core.Subjects;

namespace Aml.Core.Explanation;

/// <summary>Builds the reviewer and audit view (pipeline-api-and-batch.md, explanation contents).</summary>
public static class ExplanationBuilder
{
    private const int MaskKeptPrefix = 3;
    private const int MaskKeptSuffix = 2;

    public static CaseExplanation Build(CaseInput input, CaseAnalysis analysis, IReadOnlyList<PairChange>? changes = null, INewsModel? newsModel = null) =>
        new(CaseLifecycle.Aggregate(analysis),
            analysis.Hits.Select((h, i) => Hit(i + 1, input, h, newsModel)).ToList(),
            analysis.NotEvaluatedProfiles,
            input.ImportedDecisions,
            changes);

    private static HitExplanation Hit(int number, CaseInput input, HitResult h, INewsModel? newsModel)
    {
        var s = input.Subject;
        var p = h.Profile;
        var e = h.Identity.Evidence;
        var rows = Comparison(s, p, e);
        var subject = ScreenedSubjectLabel(s);
        var candidateRows = input.Rows.Where(r => ProfileKeys.KeyOf(r.ProfileId) == p.ProfileKey).ToList();
        var categories = p.Categories.Select(c => c.Subcategory is null ? c.Category : $"{c.Category} / {c.Subcategory}").ToList();
        var support = e.Values.Where(v => v.IsSupport).ToList();
        var unknown = rows.Where(r => IsUnknown(r.Status)).Select(r => r.Field).ToList();
        var news = input.NewsByProfileId.GetValueOrDefault(ProfileKeys.IdOf(p.ProfileKey));
        var sources = Sources(p, news, h.Conditions);

        return new HitExplanation(
            HitNumber: number,
            ScreenedSubject: subject,
            ProfileId: ProfileKeys.IdOf(p.ProfileKey),
            ProfileKey: p.ProfileKey,
            ProfileName: p.Names.Count > 0 ? p.Names[0] : null,
            MatchedNames: candidateRows.Select(r => r.MatchedName).Distinct().ToList(),
            IsRca: candidateRows.Any(r => r.IsRca),
            SourceCategories: categories,
            SourceConditions: h.Conditions,
            AttachedNews: news is not null,
            Comparison: rows,
            RelatedParties: RelatedParties(s, p),
            Support: support,
            Conflicts: e.Values.Where(v => v.Status == ComparisonStatus.Conflict).ToList(),
            UnknownFields: unknown,
            HardConflict: h.Identity.HardConflict,
            Stage3Row: h.Identity.Stage3Row,
            WouldChangeIf: IdentityDecisionPolicy.Contrast(h.Identity),
            IdentityDecision: h.Identity.Decision,
            PairResult: h.PairResult,
            ReviewPriority: h.ReviewPriority,
            PairReasonCodes: h.PairReasonCodes,
            Disposition: h.Disposition,
            ReasonCodes: h.Identity.ReasonCodes,
            DataQualityFindings: DataQualityFindings(s, p),
            Sources: sources,
            Summary: new ExplanationSummary(
                InternalData: InternalData(s, subject),
                ExternalData: ExternalData(h, categories, sources, hasNews: news is not null),
                AgreementsAndDifferences: AgreementsAndDifferences(rows, unknown),
                KeyEvidenceOrGaps: KeyEvidence(h, support),
                Conclusion: Conclusion(h)),
            NewsDigest: NewsDigests.Build(news, p.Names, sources.LastOrDefault(x => x.UsedFor != SourceUse.ProfileRecord)?.Ref ?? "", newsModel));
    }

    private static string ScreenedSubjectLabel(ScreenedSubject s)
    {
        var role = s.Role == SubjectRole.Customer ? "客戶" : $"關聯人（{s.Relationship}）";
        return $"{role} {string.Join(" / ", s.Names)}";
    }

    private static bool IsUnknown(ComparisonStatus status) =>
        status is ComparisonStatus.SubjectMissing or ComparisonStatus.ProfileMissing or ComparisonStatus.Uncertain;

    private static List<string> DataQualityFindings(ScreenedSubject s, SourceProfile p) =>
        s.Findings.Concat(s.Addresses.Select(a => a.Detail).OfType<string>()).Concat(p.Findings).Distinct().ToList();

    private static List<RelatedParty> RelatedParties(ScreenedSubject s, SourceProfile p) =>
        (s.Relatives ?? []).Select(r => new RelatedParty("SUBJECT", r.Name, r.Relation, r.IsEntity))
            .Concat(p.Relationships.Select(r => new RelatedParty("PROFILE", r.Name, r.Relation, r.IsEntity, r.ProfileId))).ToList();

    private static List<ExplanationSource> Sources(SourceProfile p, NewsItem? news, IReadOnlyList<SourceCondition> conditions)
    {
        var sources = p.Sources.Select((x, i) => new ExplanationSource($"S{i + 1}", x.Id, x.Name, x.Url, x.PublishedOn, x.RetrievedOn, SourceUse.ProfileRecord, true)).ToList();
        if (news is not null)
            sources.Add(new ExplanationSource($"S{sources.Count + 1}", news.DocumentId ?? $"{p.ProfileKey}#news", $"{news.Publisher}：{news.Title}", null,
                news.PublishedOn?.ToString(), null,
                conditions.Any(c => c.Type == ListType.AdverseMedia) ? SourceUse.LegalStage : SourceUse.Context, true));
        return sources;
    }

    private static string InternalData(ScreenedSubject s, string subject) =>
        $"受檢對象為{subject}；出生日期 {OrMissing(s.DateOfBirth?.ToString())}；國籍 {OrMissing(Join(s.Nationalities))}；性別 {OrMissing(Gender(s.Gender))}；證件 {(s.Document is null ? "未提供" : "已提供")}。";

    private static string ExternalData(HitResult h, List<string> categories, List<ExplanationSource> sources, bool hasNews)
    {
        var p = h.Profile;
        return $"名單 {p.ProfileKey}（{Join(categories)}）{Cite(sources.Where(x => x.UsedFor == SourceUse.ProfileRecord))}判定為 {Join(h.Conditions.Select(Condition))}；"
            + $"出生日期 {OrMissing(Join(p.DatesOfBirth.Select(d => d.ToString())))}；國籍 {OrMissing(Join(p.Citizenship))}；性別 {OrMissing(Gender(p.Gender))}；"
            + $"附加新聞{(hasNews ? "有" + Cite(sources.Where(x => x.UsedFor != SourceUse.ProfileRecord)) : "無")}。";
    }

    private static string AgreementsAndDifferences(List<ComparisonRow> rows, List<string> unknown) =>
        $"相符：{OrMissing(Join(rows.Where(r => r.Status is ComparisonStatus.Exact or ComparisonStatus.Partial).Select(r => r.Field)), "無")}；"
        + $"衝突：{OrMissing(Join(rows.Where(r => r.Status == ComparisonStatus.Conflict).Select(r => r.Field)), "無")}；"
        + $"無法比對：{OrMissing(Join(unknown), "無")}。";

    private static string Conclusion(HitResult h) =>
        $"系統身分判斷 {CaseJson.Code(h.Identity.Decision)}"
        + (h.Identity.HardConflict is { } hc ? $"（第一階段硬衝突 {hc}）"
            : h.Identity.Stage3Row is { } row ? $"（第三階段第 {row} 列）" : $"（{h.Identity.ReasonCodes[0]}）")
        + $"；對外結果 {CaseJson.Code(h.PairResult)}" + (h.ReviewPriority is { } pr ? $"（複核優先序 {CaseJson.Code(pr)}）" : "")
        + $"；理由碼 {Join(h.Identity.ReasonCodes.Concat(h.PairReasonCodes))}。名單條件與身分判斷各自獨立。"
        + Disposition(h.Disposition);

    private static List<ComparisonRow> Comparison(ScreenedSubject s, SourceProfile p, IReadOnlyDictionary<EvidenceGroup, Evidence> e)
    {
        ComparisonStatus StatusOf(EvidenceGroup g) => e.TryGetValue(g, out var v) ? v.Status : ComparisonStatus.SubjectMissing;
        string? DetailOf(EvidenceGroup g) => e.TryGetValue(g, out var v) ? v.Detail : null;

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
            new("地址", NullIfEmpty(Join(SubjectAddresses(s))), NullIfEmpty(Join(ProfileAddresses(p))),
                StatusOf(EvidenceGroup.Address), DetailOf(EvidenceGroup.Address)),
            new("職業／雇主", NullIfEmpty(Join(SubjectOccupations(s))), NullIfEmpty(Join(ProfileRoles(p))),
                StatusOf(EvidenceGroup.Employment), DetailOf(EvidenceGroup.Employment)),
            new("其他識別碼", s.ReferenceNumber is { } r ? $"參考號碼 {Mask(r)}" : null, null, StatusOf(EvidenceGroup.OtherIdentifier),
                s.ReferenceNumber is null ? null : EvidenceDetails.NotADocument),
        ];
    }

    private static IEnumerable<string> SubjectAddresses(ScreenedSubject s) =>
        s.Addresses.Select(a => a.State == ValueState.Present ? a.Normalized ?? a.Raw : $"{a.Raw}（{a.Detail}）");

    private static IEnumerable<string> ProfileAddresses(SourceProfile p)
    {
        if (p.AddressLines is not { Count: > 0 } lines) return p.AddressCountries;
        var oneCountryPerLine = lines.Count == p.AddressCountries.Count;
        return oneCountryPerLine ? lines.Zip(p.AddressCountries, (l, c) => $"{l}（{c}）") : lines;
    }

    private static IEnumerable<string> SubjectOccupations(ScreenedSubject s) =>
        new[] { s.Occupation }.OfType<string>().Concat((s.Employments ?? []).Select(x => Position(x.Title, x.Organization, x.From)));

    private static IEnumerable<string> ProfileRoles(SourceProfile p) =>
        p.Roles.Select(r => Position(r.Title, r.Organization, r.From))
            .Concat(p.Relationships.Where(r => EvidenceBuilder.IsEmployer(r.Relation)).Select(r => $"受僱於 {r.Name}"));

    private static string Position(string? title, string organization, PartialDate? from) =>
        $"{title}，{organization}" + (from is { } f ? $"（{f} 起）" : "");

    // The table's "DOB or age" row: the DOB code, then the code of any age stated in news; the age itself is the row's Note.
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
        var (status, detail) = NameComparison.CompareNames(sv, pv);
        return new(field, NullIfEmpty(Join(sv)), NullIfEmpty(Join(pv)), status, detail);
    }

    private static string KeyEvidence(HitResult h, List<Evidence> support)
    {
        var sb = new StringBuilder();
        if (h.Identity.HardConflict is { } hc)
            sb.Append("硬衝突 " + hc + (!HardConflictGate.ValidatedReasonCodes.Contains(hc) ? "，規則尚待驗證，不自動排除。"
                : h.Identity.ReasonCodes.Contains(ReasonCodes.SourceQualityDowngrade) ? "，但來源品質有疑慮（SOURCE_QUALITY_DOWNGRADE），維持人工複核，不自動排除。"
                // The identity is excluded, but a raised Sanction, PEP or RCA condition keeps the pair for a person to close.
                : h.PairReasonCodes.Contains(AssessmentPolicy.ReleaseControlHumanReview) ? "，身分可排除，但名單條件依放行控制（RELEASE_CONTROL_HUMAN_REVIEW）仍需人工複核。"
                : "，足以排除。"));
        sb.Append(support.Count == 0
            ? "除姓名、國籍、性別等前提條件外，沒有任何佐證。"
            : "佐證：" + Join(support.Select(v => $"{CaseJson.Code(v.Group)}（{CaseJson.Code(v.Strength)}{(v.Detail is null ? "" : "，" + v.Detail)}）")) + "。");
        if (h.Identity.ReasonCodes.Contains(ReasonCodes.NoBilateralDirectIdentifier))
            sb.Append("雙方沒有可共同比對的直接識別資料（完整出生日期或證件）。");
        return sb.ToString();
    }

    // Advisory only: the final selection happens outside the system (ADR-0004).
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

    public static string Mask(string value) =>
        value.Length <= MaskKeptPrefix + MaskKeptSuffix
            ? new string('*', value.Length)
            : value[..MaskKeptPrefix] + new string('*', value.Length - MaskKeptPrefix - MaskKeptSuffix) + value[^MaskKeptSuffix..];

    private static bool IsCjk(string s) => s.Any(CjkText.IsCjk);

    private static string? Gender(Gender? g) => g switch { Shared.Gender.Male => "男", Shared.Gender.Female => "女", _ => null };

    private static string Join(IEnumerable<string> values) => string.Join("、", values);

    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;

    private static string OrMissing(string? s, string missing = "未提供") => string.IsNullOrEmpty(s) ? missing : s;
}
