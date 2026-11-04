using System.Text.RegularExpressions;
using Aml.Core.Subjects;

namespace Aml.Core.Screening;

/// <summary>Bank list types (ADR-0016). UNKNOWN: an unmapped source category, left for the reviewer.</summary>
public enum ListType { Sanction, Pep, Rca, Sie, OfficialWantedList, OtherOfficialList, AdverseMedia, BankInternalList, Unknown }

public enum ConditionStatus { Hit, NotHit, Unknown }

public enum ConditionBasis { Flag, CategoryMapping, Unmapped }

public enum LegalStage { Unknown, Alleged, Indicted, Wanted, Convicted, Acquitted }

public enum AmlRelevance { Uncertain, Related, Unrelated }

/// <summary>SourceConditionResult (data-contracts.md). Independent of identity.</summary>
public sealed record SourceCondition(
    ListType Type,
    ConditionStatus Status,
    ConditionBasis Basis,
    IReadOnlyList<string> RiskCategories,
    LegalStage LegalStage,
    AmlRelevance? AmlRelevance,
    string PolicyVersion,
    IReadOnlyList<string> ReasonCodes,
    string? PepType = null,
    string? Program = null,
    string? Jurisdiction = null);

/// <summary>List-type determination (ADR-0016): flags first; category mapping only when all flags are MISSING.</summary>
public static partial class SourceConditions
{
    public const string PolicyVersion = "source-policy-0.2";
    public const string SanctionMeasureNotConfirmed = "SANCTION_MEASURE_NOT_CONFIRMED";
    public const string UnmappedCategory = "UNMAPPED_CATEGORY";

    private static readonly Dictionary<string, string> SipRiskCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Financial Crime"] = "FINANCIAL_CRIME", ["Tax Crime"] = "TAX_CRIME", ["Trafficking"] = "TRAFFICKING",
        ["Human Trafficking"] = "HUMAN_TRAFFICKING", ["Narcotics"] = "DRUG_TRAFFICKING", ["Drug Trafficking"] = "DRUG_TRAFFICKING",
        ["Organised Crime"] = "ORGANISED_CRIME", ["Terror"] = "TERRORISM", ["Terrorism"] = "TERRORISM",
        ["Corruption"] = "CORRUPTION", ["Bribery & Corruption"] = "BRIBERY", ["Money Laundering"] = "MONEY_LAUNDERING",
        ["Fraud"] = "FRAUD", ["War Crimes"] = "WAR_CRIMES", ["Cybercrime"] = "CYBERCRIME",
        ["Environmental Crime"] = "ENVIRONMENTAL_CRIME", ["Corporate Crime"] = "CORPORATE_CRIME",
    };

    private static readonly Dictionary<string, string> KeywordRiskCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Fraud"] = "FRAUD", ["Tax Evasion"] = "TAX_EVASION", ["Bribery"] = "BRIBERY", ["Corruption"] = "CORRUPTION",
        ["Forgery"] = "FORGERY", ["Breach of Trust"] = "BREACH_OF_TRUST", ["Money Laundering"] = "MONEY_LAUNDERING",
    };

    public const string FlagUnparseable = "FLAG_UNPARSEABLE";
    public const string FlagCategoryConflict = "FLAG_CATEGORY_CONFLICT";
    public const string BlacklistTypeUndecided = "BLACKLIST_TYPE_UNDECIDED";
    public const string FlagIsRca = "IS_RCA_FLAG";

    /// <summary>
    /// Flags decide when any of PEP, 黑名單 and 負面新聞 has a value (NO counts); the category mapping runs only when all
    /// three are MISSING. A flag that contradicts the category wins and the conflict is recorded. Is RCA? = YES always adds RCA.
    /// </summary>
    public static IReadOnlyList<SourceCondition> Determine(SourceProfile profile, bool isRca, string? news, ListFlags? flags = null)
    {
        flags ??= ListFlags.None;
        var mapped = profile.Categories.Select(MapCategory).Distinct().ToList();
        var decided = new List<(ListType Type, ConditionStatus Status, ConditionBasis Basis, string? Code)>();
        if (flags.AllMissing)
        {
            foreach (var t in mapped)
                decided.Add(t == ListType.Unknown
                    ? (t, ConditionStatus.Unknown, ConditionBasis.Unmapped, UnmappedCategory)
                    : (t, ConditionStatus.Hit, ConditionBasis.CategoryMapping, null));
        }
        else
        {
            var blacklist = BlacklistType(profile, mapped);
            FromFlag(decided, ListType.Pep, flags.Pep, null);
            FromFlag(decided, blacklist, flags.Blacklist, blacklist == ListType.Unknown ? BlacklistTypeUndecided : null);
            FromFlag(decided, ListType.AdverseMedia, flags.AdverseNews, null);
        }
        if (isRca && !decided.Any(d => d.Type == ListType.Rca)) decided.Add((ListType.Rca, ConditionStatus.Hit, ConditionBasis.Flag, FlagIsRca));

        // The category disagrees with the flags: a mapped type the flags did not raise as HIT.
        var conflict = !flags.AllMissing && mapped.Any(t => t is not ListType.Unknown and not ListType.Rca
            && !decided.Any(d => d.Status == ConditionStatus.Hit && Same(d.Type, t)));

        var risks = RiskCategories(profile);
        return decided.Select(d =>
        {
            var c = Shape(d.Type, profile, risks, news);
            if (d.Status != ConditionStatus.Hit) c = c with { Status = d.Status };
            var codes = c.ReasonCodes.Concat(new[] { d.Code, conflict ? FlagCategoryConflict : null }.OfType<string>()).Distinct().ToList();
            return c with { Basis = d.Basis, ReasonCodes = codes };
        }).ToList();
    }

    private static void FromFlag(List<(ListType, ConditionStatus, ConditionBasis, string?)> decided, ListType t, FlagValue v, string? code)
    {
        if (v == FlagValue.Missing) return;
        var status = v switch
        {
            FlagValue.Yes => t == ListType.Unknown ? ConditionStatus.Unknown : ConditionStatus.Hit,
            FlagValue.No => ConditionStatus.NotHit,
            _ => ConditionStatus.Unknown,
        };
        decided.Add((t, status, ConditionBasis.Flag, v == FlagValue.Unparseable ? FlagUnparseable : v == FlagValue.Yes ? code : null));
    }

    private static bool Same(ListType a, ListType b) =>
        a == b || (a, b) is (ListType.OfficialWantedList, ListType.OtherOfficialList) or (ListType.OtherOfficialList, ListType.OfficialWantedList);

    // One condition of a decided type, with the attributes its policy reads.
    private static SourceCondition Shape(ListType t, SourceProfile profile, IReadOnlyList<string> risks, string? news) => t switch
    {
        ListType.AdverseMedia => Hit(t) with
        {
            RiskCategories = risks, LegalStage = LegalStageFromNews(news, profile.Names), AmlRelevance = Relevance(risks, news, profile),
        },
        ListType.OtherOfficialList or ListType.OfficialWantedList when IsActive(profile.OfficialList, "Wanted", "通緝") =>
            Hit(ListType.OfficialWantedList) with { LegalStage = LegalStage.Wanted },
        ListType.OfficialWantedList => Hit(ListType.OtherOfficialList),
        ListType.Sanction => Sanction(profile),
        ListType.Pep => Hit(t) with { PepType = PepType(profile, ListType.Pep) },
        ListType.Rca => Hit(t) with { PepType = PepType(profile, ListType.Rca) },
        ListType.Unknown => new SourceCondition(t, ConditionStatus.Unknown, ConditionBasis.Unmapped, [], LegalStage.Unknown, null, PolicyVersion, []),
        _ => Hit(t),
    };

    // SIP crime subcategories, then keywords; only an ADVERSE_MEDIA condition carries them.
    private static List<string> RiskCategories(SourceProfile profile)
    {
        var risks = new List<string>();
        foreach (var c in profile.Categories)
            if (MapCategory(c) == ListType.AdverseMedia && c.Subcategory is { } sub && SipRiskCategories.TryGetValue(sub, out var r) && !risks.Contains(r))
                risks.Add(r);
        foreach (var k in profile.Keywords)
            if (KeywordRiskCategories.TryGetValue(k, out var r) && !risks.Contains(r)) risks.Add(r);
        return risks;
    }

    /// <summary>Blacklist YES: the type comes from approved source fields (official-list entry, then category), else UNKNOWN.</summary>
    private static ListType BlacklistType(SourceProfile p, IReadOnlyList<ListType> mapped) =>
        p.OfficialList?.List.Contains("Sanction", StringComparison.OrdinalIgnoreCase) == true || mapped.Contains(ListType.Sanction) ? ListType.Sanction
        : IsActive(p.OfficialList, "Wanted", "通緝") ? ListType.OfficialWantedList
        : mapped.Contains(ListType.OtherOfficialList) || p.OfficialList is not null ? ListType.OtherOfficialList
        : ListType.Unknown;

    private static SourceCondition Hit(ListType t) =>
        new(t, ConditionStatus.Hit, ConditionBasis.CategoryMapping, [], LegalStage.Unknown, null, PolicyVersion, []);

    /// <summary>A generic Active Profile is not an active sanction measure: HIT needs an active, named measure.</summary>
    private static SourceCondition Sanction(SourceProfile p) =>
        p.OfficialList is { } e && e.Status.Equals("Active", StringComparison.OrdinalIgnoreCase)
            ? Hit(ListType.Sanction) with { Program = e.List, Jurisdiction = e.Authority }
            : Hit(ListType.Sanction) with { Status = ConditionStatus.Unknown, ReasonCodes = [SanctionMeasureNotConfirmed] };

    // ponytail: PEP subtypes read from the category's subcategory text; the approved source fields arrive with GC-04.
    private static string? PepType(SourceProfile p, ListType type)
    {
        var prefix = type == ListType.Pep ? "Politically Exposed" : "Relative or Close Associate";
        var subs = p.Categories.Where(c => c.Category.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Select(c => c.Subcategory ?? "").ToList();
        bool Says(params string[] words) => subs.Any(s => words.Any(w => s.Contains(w, StringComparison.OrdinalIgnoreCase)));
        if (Says("Family", "Relative", "Spouse", "Child", "Parent", "Sibling")) return "PEP_FAMILY";
        if (Says("Associate")) return "PEP_CLOSE_ASSOCIATE";
        if (type == ListType.Rca) return "RCA_UNCONFIRMED";
        return Says("Former") ? "FORMER_PEP" : null;
    }

    // ponytail: controlled conduct lists. Only UNRELATED is extracted, and only when the source states both a conduct that
    // is no predicate offence and a jurisdiction; RELATED is left to the reviewer, as GC-02 and GC-11 keep UNCERTAIN.
    private static readonly string[] PredicateConduct =
    [
        "詐欺", "詐騙", "詐貸", "詐取", "洗錢", "逃漏稅", "逃稅", "貪污", "賄賂", "收賄", "行賄", "背信", "侵占", "毒品", "販毒", "走私",
        "人口販運", "組織犯罪", "恐怖", "內線交易", "操縱股價", "地下匯兌", "吸金", "銀行法", "不實發票", "假發票", "偽造",
        "fraud", "laundering", "bribery", "corruption", "trafficking", "tax evasion",
    ];

    private static readonly string[] NonPredicateConduct =
    [
        "酒駕", "酒後駕車", "公共危險", "肇事", "交通事故", "過失致死", "過失傷害", "傷害罪", "妨害名譽", "誹謗", "公然侮辱", "性騷擾",
        "家暴", "drunk driving", "defamation", "traffic accident",
    ];

    /// <summary>
    /// ② input. Any SIP crime category or risk keyword is a predicate offence, so it never becomes UNRELATED; without news
    /// there is no stated conduct.
    /// </summary>
    private static AmlRelevance Relevance(IReadOnlyList<string> risks, string? news, SourceProfile p)
    {
        if (news is null || risks.Count > 0) return AmlRelevance.Uncertain;
        var jurisdiction = (p.AllegationCountries ?? []).Count > 0 || Dateline().IsMatch(news);
        var predicate = PredicateConduct.Any(w => news.Contains(w, StringComparison.OrdinalIgnoreCase));
        var other = NonPredicateConduct.Any(w => news.Contains(w, StringComparison.OrdinalIgnoreCase));
        return jurisdiction && other && !predicate ? AmlRelevance.Unrelated : AmlRelevance.Uncertain;
    }

    // "〔記者丙／台北報導〕", "（中央社台北6日電）": the report states where it happened.
    [GeneratedRegex("／\\p{IsCJKUnifiedIdeographs}{2,4}報導|中央社\\p{IsCJKUnifiedIdeographs}{2,4}\\d{1,2}日電")]
    private static partial Regex Dateline();

    private static ListType MapCategory(SourceCategory c)
    {
        var cat = c.Category;
        if (cat.StartsWith("Special Interest Person", StringComparison.OrdinalIgnoreCase))
            return c.Subcategory switch
            {
                null => ListType.Unknown,
                var s when s.Contains("Official List", StringComparison.OrdinalIgnoreCase) || s.Contains("Exclusion List", StringComparison.OrdinalIgnoreCase) => ListType.OtherOfficialList,
                var s when SipRiskCategories.ContainsKey(s) => ListType.AdverseMedia,
                _ => ListType.Unknown,
            };
        if (cat.StartsWith("Politically Exposed", StringComparison.OrdinalIgnoreCase)) return ListType.Pep;
        if (cat.StartsWith("Relative or Close Associate", StringComparison.OrdinalIgnoreCase)) return ListType.Rca;
        if (cat.StartsWith("Sanction", StringComparison.OrdinalIgnoreCase)) return ListType.Sanction;
        if (cat.StartsWith("State Owned", StringComparison.OrdinalIgnoreCase) || cat.StartsWith("State Invested", StringComparison.OrdinalIgnoreCase)) return ListType.Sie;
        if (cat.StartsWith("Adverse Media", StringComparison.OrdinalIgnoreCase)) return ListType.AdverseMedia;
        if (cat.Contains("Official List", StringComparison.OrdinalIgnoreCase) || cat.Contains("Exclusion List", StringComparison.OrdinalIgnoreCase)) return ListType.OtherOfficialList;
        return ListType.Unknown;
    }

    private static bool IsActive(OfficialListEntry? e, string english, string chinese) =>
        e is not null
        && e.Status.Equals("Active", StringComparison.OrdinalIgnoreCase)
        && (e.List.Contains(english, StringComparison.OrdinalIgnoreCase) || e.List.Contains(chinese, StringComparison.Ordinal));

    // ponytail: keyword heuristic on attached news; 求刑 is a sentencing request and never a judgment.
    // Replace with approved legal-stage extraction when judgment wording appears in a Golden Case.
    // A judgment counts only in a sentence that names the Profile person: co-defendants' sentences are not theirs (GC-12).
    private static LegalStage LegalStageFromNews(string? news, IReadOnlyList<string> names)
    {
        if (news is null) return LegalStage.Unknown;
        var own = news.Split('。', '！', '？', '\n', '；')
            .Where(s => names.Any(n => n.Length > 1 && s.Contains(n, StringComparison.OrdinalIgnoreCase))).ToList();
        if (own.Any(s => Conviction.Any(w => s.Contains(w, StringComparison.Ordinal)))) return LegalStage.Convicted;
        if (own.Any(s => s.Contains("無罪", StringComparison.Ordinal))) return LegalStage.Acquitted;
        if (news.Contains("起訴", StringComparison.Ordinal)) return LegalStage.Indicted;
        if (news.Contains("偵辦", StringComparison.Ordinal) || news.Contains("調查", StringComparison.Ordinal)) return LegalStage.Alleged;
        return LegalStage.Unknown;
    }

    private static readonly string[] Conviction = ["判處", "判刑", "判決有罪", "有罪確定", "定讞"];
}
