using System.Text;
using Aml.Core.Cases;
using Aml.Core.Codes;
using Aml.Core.Identity;
using Aml.Core.Normalization;
using Aml.Core.Ports;
using Aml.Core.Retrieval;
using Aml.Core.Screening;
using Aml.Core.Screening.Stage;
using Aml.Core.Shared;
using Aml.Core.Subjects;

namespace Aml.Core.Explanation;

/// <summary>Builds the reviewer and audit view (pipeline-api-and-batch.md, explanation contents).</summary>
public static class ExplanationBuilder
{
    private const int MaskKeptPrefix = 3;
    private const int MaskKeptSuffix = 2;

    /// <param name="asOf">The analysis date: with it the age row also says the age on that day; without it only the birth date.</param>
    public static CaseExplanation Build(CaseInput input, CaseAnalysis analysis, IReadOnlyList<PairChange>? changes = null, INewsModel? newsModel = null, DateOnly? asOf = null) =>
        new(CaseLifecycle.Aggregate(analysis),
            analysis.Hits.Select((h, i) => Hit(i + 1, input, h, newsModel, asOf)).ToList(),
            analysis.NotEvaluatedProfiles,
            input.ImportedDecisions,
            changes);

    private static HitExplanation Hit(int number, CaseInput input, HitResult h, INewsModel? newsModel, DateOnly? asOf)
    {
        var s = input.Subject;
        var p = h.Profile;
        var e = h.Identity.Evidence;
        var rows = Comparison(s, p, e, asOf);
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
                AgreementsAndDifferences: AgreementsAndDifferences(rows, unknown, h.Identity.Evidence, h.PairReasonCodes.Contains(AssessmentPolicy.AttachedNewsDisagreesWithProfile)),
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

    // What attached news states next to the name has no row of its own: a conflict there is named in the summary.
    private static readonly (EvidenceGroup Group, string Label)[] NewsStated =
        [(EvidenceGroup.StatedAge, "報導年齡"), (EvidenceGroup.StatedBirthDate, "新聞所載出生日期"), (EvidenceGroup.StatedGender, "新聞所載性別")];

    private static string AgreementsAndDifferences(List<ComparisonRow> rows, List<string> unknown, IReadOnlyDictionary<EvidenceGroup, Evidence> e, bool newsDisagrees) =>
        $"相符：{OrMissing(Join(rows.Where(r => r.Status is ComparisonStatus.Exact or ComparisonStatus.Partial).Select(r => r.Field)), "無")}；"
        + $"衝突：{OrMissing(Join(rows.Where(r => r.Status == ComparisonStatus.Conflict).Select(r => r.Field)
            .Concat(NewsStated.Where(n => e.GetValueOrDefault(n.Group)?.Status == ComparisonStatus.Conflict).Select(n => n.Label))), "無")}；"
        + $"無法比對：{OrMissing(Join(unknown), "無")}。"
        // The Profile's own field agrees with the customer and the news next to the name says otherwise: said in words,
        // since the rows above show no conflict. Only with the pair's reason code, so the two never part.
        + (!newsDisagrees ? "" : Join(NewsStated.Where(n => e.GetValueOrDefault(n.Group) is { Status: ComparisonStatus.Uncertain, Detail: ReasonCodes.NewsBirthDateDifference or ReasonCodes.NewsGenderDifference })
            .Select(n => n.Label)) is { Length: > 0 } disagreeing ? $"來源不一致：{disagreeing}與名單記錄不同，附加新聞可能不是這位名單人物的。" : "");

    // Said in words first; every code stays, in brackets, so the sentence can still be traced to /api/codes.
    private static string Conclusion(HitResult h) =>
        $"身分判斷：{IdentityWords(h.Identity.Decision)}（{CaseJson.Code(h.Identity.Decision)}，"
        + (h.Identity.HardConflict is { } hc ? $"第一階段硬衝突 {hc}）"
            : h.Identity.Stage3Row is { } row ? $"第三階段第 {row} 列）" : $"{h.Identity.ReasonCodes[0]}）")
        + $"；對外結果：{ResultWords(h.PairResult)}（{CaseJson.Code(h.PairResult)}）"
        + (h.ReviewPriority is { } pr ? $"，複核優先序{PriorityWords(pr)}（{CaseJson.Code(pr)}）" : "")
        + $"；理由：{Join(h.Identity.ReasonCodes.Concat(h.PairReasonCodes).Select(Named))}。名單條件與身分判斷各自獨立。"
        + Disposition(h.Disposition);

    /// <summary>A code with its catalog name in front: 在可得精度內一致（CONSISTENT_AT_AVAILABLE_PRECISION).</summary>
    private static string Named(string code) => CodeCatalog.Find(code) is { } entry ? $"{entry.Name}（{code}）" : code;

    private static string IdentityWords(IdentityDecision d) => d switch
    {
        IdentityDecision.ProbableMatch => "很可能是同一人",
        IdentityDecision.NoMatch => "不是同一人",
        _ => "無法確定是否同一人",
    };

    private static string ResultWords(ScreeningAssessment r) => r switch
    {
        ScreeningAssessment.Hit => "命中",
        ScreeningAssessment.NoHit => "未命中",
        ScreeningAssessment.Review => "需人工複核",
        ScreeningAssessment.NotEvaluated => "未評估",
        _ => "不適用",
    };

    private static string GroupWords(EvidenceGroup g) => g switch
    {
        EvidenceGroup.Name => "姓名",
        EvidenceGroup.Gender => "性別",
        EvidenceGroup.Nationality => "國籍",
        EvidenceGroup.DateOfBirth => "出生日期",
        EvidenceGroup.Document => "證件",
        EvidenceGroup.OtherIdentifier => "其他識別碼",
        EvidenceGroup.Employment => "任職機構",
        EvidenceGroup.Relationship => "關係人",
        EvidenceGroup.CompanyAssociation => "關聯公司",
        EvidenceGroup.Address => "地址",
        EvidenceGroup.StatedAge => "報導年齡",
        EvidenceGroup.TemporalRole => "時序",
        EvidenceGroup.EntityIdentifier => "登記號碼",
        EvidenceGroup.StatedBirthDate => "新聞所載出生日期",
        _ => "新聞所載性別",
    };

    private static string PriorityWords(ReviewPriority p) => p switch { ReviewPriority.High => "高", ReviewPriority.Normal => "一般", _ => "低" };

    /// <summary>The nine comparison fields of the bank's screening specification, in its order.</summary>
    public static readonly IReadOnlyList<string> ComparisonFields =
        ["中文姓名", "英文姓名", "國籍／所在地國家", "年齡（出生日期）", "性別", "證件號碼／統編", "地址", "職業／任職機構", "其他可識別身分資訊"];

    // The full list of related people is in RelatedParties; the row names the first few.
    private const int RelatedShownInRow = 5;

    private static List<ComparisonRow> Comparison(ScreenedSubject s, SourceProfile p, IReadOnlyDictionary<EvidenceGroup, Evidence> e, DateOnly? asOf)
    {
        ComparisonStatus StatusOf(EvidenceGroup g) => e.TryGetValue(g, out var v) ? v.Status : ComparisonStatus.SubjectMissing;
        string? DetailOf(EvidenceGroup g) => e.TryGetValue(g, out var v) ? v.Detail : null;
        var f = ComparisonFields;
        var subjectCountries = s.Addresses.Select(a => a.Country).OfType<string>().Distinct().ToList();
        // An entity record is compared on its registration or IMO number, a person on the identity document.
        var identifier = e.ContainsKey(EvidenceGroup.EntityIdentifier) ? EvidenceGroup.EntityIdentifier : EvidenceGroup.Document;
        var other = OtherGroups.Select(g => e.GetValueOrDefault(g)).OfType<Evidence>().ToList();
        var related = p.Relationships.Where(r => !EvidenceBuilder.IsEmployer(r.Relation)).ToList();

        return
        [
            NameRow(f[0], s.Names.Where(IsCjk), p.Names.Where(IsCjk)),
            NameRow(f[1], s.Names.Where(n => !IsCjk(n)), p.Names.Where(n => !IsCjk(n))),
            new(f[2], WithLocation(s.Nationalities, subjectCountries), WithLocation(p.Citizenship, p.AddressCountries), StatusOf(EvidenceGroup.Nationality), null,
                subjectCountries.Count == 0 || p.AddressCountries.Count == 0 ? null : subjectCountries.Intersect(p.AddressCountries).Any() ? "所在地國家相符" : "所在地國家不同"),
            new(f[3], s.DateOfBirth is { } own ? WithAge(own, asOf) : null, NullIfEmpty(Join(p.DatesOfBirth.Select(d => WithAge(d, asOf)))), StatusOf(EvidenceGroup.DateOfBirth),
                DobDifference(e, p), NullIfEmpty(string.Join("；", new[] { EvidenceGroup.StatedAge, EvidenceGroup.StatedBirthDate }.Select(g => e.GetValueOrDefault(g)?.Note).OfType<string>()))),
            new(f[4], Gender(s.Gender), Gender(p.Gender), StatusOf(EvidenceGroup.Gender), DetailOf(EvidenceGroup.StatedGender), e.GetValueOrDefault(EvidenceGroup.StatedGender)?.Note),
            new(f[5], NullIfEmpty(Join(new[] { s.Document is { } d ? $"{d.Type} {Mask(d.Number)}" : null, // Only a legal person's tax number is compared (as its registration number), so only that one is shown.
                    s.IsEntity && s.TaxId is { State: ValueState.Present, Value: { } tax } ? $"統編 {Mask(tax)}" : null }.OfType<string>())),
                NullIfEmpty(Join((p.Documents ?? []).Select(x => $"{x.Type} {Mask(x.Number)}"))), StatusOf(identifier), DetailOf(identifier)),
            new(f[6], NullIfEmpty(Join(SubjectAddresses(s))), NullIfEmpty(Join(ProfileAddresses(p))),
                StatusOf(EvidenceGroup.Address), DetailOf(EvidenceGroup.Address)),
            new(f[7], NullIfEmpty(Join(SubjectOccupations(s))), NullIfEmpty(Join(ProfileRoles(p))),
                StatusOf(EvidenceGroup.Employment), DetailOf(EvidenceGroup.Employment)),
            // Reference numbers, relatives, related companies and (for a legal person) its senior managers.
            new(f[8], NullIfEmpty(Join(new[] { s.ReferenceNumber is { } r ? $"參考號碼 {Mask(r)}" : null }.OfType<string>().Concat((s.Relatives ?? []).Select(x => $"{x.Relation} {x.Name}")))),
                NullIfEmpty(Join((p.OtherIdentifiers ?? []).Select(x => $"{x.Label} {Mask(x.Number)}").Concat(related.Take(RelatedShownInRow).Select(x => $"{x.Relation} {x.Name}"))
                    .Concat(related.Count > RelatedShownInRow ? [$"共 {related.Count} 筆"] : []))),
                other.Count == 0 ? ComparisonStatus.SubjectMissing : other.Select(x => x.Status).MinBy(x => Array.IndexOf(MostTellingFirst, x)),
                NullIfEmpty(string.Join("；", new[] { s.ReferenceNumber is null ? null : EvidenceDetails.NotADocument }.Concat(other.Select(x => x.Detail)).OfType<string>().Distinct()))),
        ];
    }

    private static readonly EvidenceGroup[] OtherGroups = [EvidenceGroup.OtherIdentifier, EvidenceGroup.Relationship, EvidenceGroup.CompanyAssociation];

    // The ninth row holds several groups: it shows the one that says the most.
    private static readonly ComparisonStatus[] MostTellingFirst =
        [ComparisonStatus.Exact, ComparisonStatus.Partial, ComparisonStatus.Conflict, ComparisonStatus.Uncertain, ComparisonStatus.ProfileMissing, ComparisonStatus.SubjectMissing];

    private static string? WithLocation(IReadOnlyList<string> nationalities, IReadOnlyList<string> located) =>
        NullIfEmpty(string.Join("；", new[] { Join(nationalities), located.Count == 0 ? "" : $"所在地 {Join(located)}" }.Where(x => x.Length > 0)));

    // A birth date known to the year gives two possible ages.
    private static string WithAge(PartialDate dob, DateOnly? asOf)
    {
        if (asOf is not { } day) return dob.ToString();
        var at = new PartialDate(day.Year, day.Month, day.Day);
        var (min, max) = (HardConflictGate.MinAgeAt(dob, at), HardConflictGate.MaxAgeAt(dob, at));
        // A birth date in the analysis year or later gives no age worth showing.
        return min < 0 ? dob.ToString() : $"{dob}（{(min == max ? $"{max}" : $"{min}～{max}")} 歲）";
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
        if (e.TryGetValue(EvidenceGroup.StatedBirthDate, out var stated) && stated.Detail is not null) parts.Add(stated.Detail);
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
            : "佐證：" + Join(support.Select(v => $"{GroupWords(v.Group)}（{CaseJson.Code(v.Group)}，強度 {CaseJson.Code(v.Strength)}{(v.Detail is null ? "" : "，" + Named(v.Detail))}）")) + "。");
        if (h.Identity.ReasonCodes.Contains(ReasonCodes.NoBilateralDirectIdentifier))
            sb.Append("雙方沒有可共同比對的直接識別資料（完整出生日期或證件）。");
        return sb.ToString();
    }

    // Advisory only: the final selection happens outside the system (ADR-0004).
    private static string Disposition(DispositionRecommendation d) =>
        (d.Recommendation is { } r
            ? $"第 22 條建議：{(r == Recommendation.OnList ? "列為名單對象" : "不列為名單對象")}（{CaseJson.Code(r)}；{Join(d.ReasonCodes.Select(Named))}），僅供參考。"
            : $"第 22 條無法給建議（{Join(d.ReasonCodes.Select(Named))}）。")
        + (d.PendingChecks.Count > 0 ? $"待查核（尚未執行）：{Join(d.PendingChecks.Select(Named))}。" : "");

    private static string Condition(SourceCondition c)
    {
        var parts = new List<string>();
        if (c.RiskCategories.Count > 0) parts.Add(Join(c.RiskCategories));
        if (c.LegalStage != LegalStage.Unknown || c.StageReaders is not null)
            parts.Add("法律階段 " + CaseJson.Code(c.LegalStage) + (c.LegalStageBasis is { } b ? $"（依據：{Named(b)}）" : ""));
        if (c.StageReaders is { } readers)
        {
            parts.Add("各讀取者：" + Join(readers.Select(r => $"{r.Reader} {(r.Stage is { } s ? CaseJson.Code(s) : "未回答")}"
                + (r.Quote is { } q ? $"（引文「{q}」，{(r.QuoteInText == true ? "原文可查到" : "原文查無")}）" : ""))));
            foreach (var code in new[] { StageVote.ReadersDisagree, StageVote.ReaderNoAnswer })
                if (c.Status == ConditionStatus.Hit && c.ReasonCodes.Contains(code)) parts.Add(Named(code));
        }
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
