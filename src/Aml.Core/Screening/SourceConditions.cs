using System.ComponentModel;
using System.Text.RegularExpressions;
using Aml.Core.Subjects;

namespace Aml.Core.Screening;

/// <summary>Bank list types (ADR-0016). UNKNOWN: an unmapped source category, left for the reviewer.</summary>
public enum ListType { Sanction, Pep, Rca, Sie, OfficialWantedList, OtherOfficialList, AdverseMedia, BankInternalList, Unknown }

public enum ConditionStatus { Hit, NotHit, Unknown }

public enum ConditionBasis { Flag, CategoryMapping, Unmapped }

public enum LegalStage { Unknown, Alleged, Indicted, Wanted, Convicted, Acquitted, Dismissed }

public enum AmlRelevance { Uncertain, Related, Unrelated }

/// <summary>SourceConditionResult (data-contracts.md). Independent of identity.</summary>
public sealed record SourceCondition(
    [property: Description("名單類型")] ListType Type,
    [property: Description("條件狀態：HIT、NOT_HIT 或 UNKNOWN")] ConditionStatus Status,
    [property: Description("依據：旗標、類別對應或未對應")] ConditionBasis Basis,
    [property: Description("風險類別，見 /api/codes")] IReadOnlyList<string> RiskCategories,
    [property: Description("法律階段")] LegalStage LegalStage,
    [property: Description("與洗錢資恐的關聯")] AmlRelevance? AmlRelevance,
    [property: Description("名單條件規則版本")] string PolicyVersion,
    [property: Description("條件的理由碼")] IReadOnlyList<string> ReasonCodes,
    [property: Description("PEP 子類型，見 /api/codes")] string? PepType = null,
    [property: Description("制裁計畫")] string? Program = null,
    [property: Description("管轄地")] string? Jurisdiction = null);

/// <summary>List-type determination (ADR-0016): a flag with a value decides its types; a mapped category whose flag is MISSING keeps its category condition.</summary>
public static partial class SourceConditions
{
    public const string PolicyVersion = "source-policy-0.2";
    public const string SanctionMeasureNotConfirmed = "SANCTION_MEASURE_NOT_CONFIRMED";
    public const string UnmappedCategory = "UNMAPPED_CATEGORY";

    private static readonly Dictionary<string, string> SipRiskCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Financial Crime"] = "FINANCIAL_CRIME",
        ["Tax Crime"] = "TAX_CRIME",
        ["Trafficking"] = "TRAFFICKING",
        ["Human Trafficking"] = "HUMAN_TRAFFICKING",
        ["Narcotics"] = "DRUG_TRAFFICKING",
        ["Drug Trafficking"] = "DRUG_TRAFFICKING",
        ["Organised Crime"] = "ORGANISED_CRIME",
        ["Terror"] = "TERRORISM",
        ["Terrorism"] = "TERRORISM",
        ["Corruption"] = "CORRUPTION",
        ["Bribery & Corruption"] = "BRIBERY",
        ["Money Laundering"] = "MONEY_LAUNDERING",
        ["Fraud"] = "FRAUD",
        ["War Crimes"] = "WAR_CRIMES",
        ["Cybercrime"] = "CYBERCRIME",
        ["Environmental Crime"] = "ENVIRONMENTAL_CRIME",
        ["Corporate Crime"] = "CORPORATE_CRIME",
    };

    private static readonly Dictionary<string, string> KeywordRiskCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Fraud"] = "FRAUD",
        ["Tax Evasion"] = "TAX_EVASION",
        ["Bribery"] = "BRIBERY",
        ["Corruption"] = "CORRUPTION",
        ["Forgery"] = "FORGERY",
        ["Breach of Trust"] = "BREACH_OF_TRUST",
        ["Money Laundering"] = "MONEY_LAUNDERING",
    };

    public const string FlagUnparseable = "FLAG_UNPARSEABLE";
    public const string FlagCategoryConflict = "FLAG_CATEGORY_CONFLICT";
    public const string BlacklistTypeUndecided = "BLACKLIST_TYPE_UNDECIDED";
    public const string FlagIsRca = "IS_RCA_FLAG";
    public const string RcaOutsideStatutoryScope = "RCA_OUTSIDE_STATUTORY_SCOPE";
    public const string PepFamily = "PEP_FAMILY";
    public const string PepCloseAssociate = "PEP_CLOSE_ASSOCIATE";
    public const string RcaUnconfirmed = "RCA_UNCONFIRMED";
    public const string FormerPep = "FORMER_PEP";

    /// <summary>
    /// A flag with a value (NO counts) decides its own list types; a mapped category whose flag is MISSING keeps its category
    /// condition. A flag that contradicts the category wins and the conflict is recorded, except that 黑名單 = NO never clears
    /// a Sanction category: that condition is UNKNOWN for review, and Is RCA? = NO never clears an RCA category the same way.
    /// Is RCA? = YES always adds RCA.
    /// </summary>
    public static IReadOnlyList<SourceCondition> Determine(SourceProfile profile, bool isRca, string? news, ListFlags? flags = null)
    {
        flags ??= ListFlags.None;
        var mapped = profile.Categories.Select(MapCategory).Distinct().ToList();
        var decided = new List<(ListType Type, ConditionStatus Status, ConditionBasis Basis, string? Code)>();
        if (!flags.AllMissing)
        {
            var blacklist = BlacklistType(profile, mapped);
            FromFlag(decided, ListType.Pep, flags.Pep, null);
            if (mapped.Contains(ListType.Sanction) && flags.Blacklist == FlagValue.No)
                decided.Add((ListType.Sanction, ConditionStatus.Unknown, ConditionBasis.Flag, FlagCategoryConflict));
            else
                FromFlag(decided, blacklist, flags.Blacklist, blacklist == ListType.Unknown ? BlacklistTypeUndecided : null);
            FromFlag(decided, ListType.AdverseMedia, flags.AdverseNews, null);
        }
        // A mapped category whose flag is MISSING (every one when all flags are MISSING) keeps its category condition.
        foreach (var t in mapped.Where(t => FlagOf(t, flags) == FlagValue.Missing && !decided.Any(d => d.Type == t)
                     && !(t == ListType.Rca && flags.Rca == FlagValue.No)))
            decided.Add(t == ListType.Unknown
                ? (t, ConditionStatus.Unknown, ConditionBasis.Unmapped, UnmappedCategory)
                : (t, ConditionStatus.Hit, ConditionBasis.CategoryMapping, null));
        if (mapped.Contains(ListType.Rca) && flags.Rca == FlagValue.No)
            decided.Add((ListType.Rca, ConditionStatus.Unknown, ConditionBasis.Flag, FlagCategoryConflict));
        if (isRca && !decided.Any(d => d.Type == ListType.Rca)) decided.Add((ListType.Rca, ConditionStatus.Hit, ConditionBasis.Flag, FlagIsRca));

        // The category disagrees with the flags: a mapped type the flags did not raise as HIT.
        var conflict = !flags.AllMissing && mapped.Any(t => t is not ListType.Unknown
            && !decided.Any(d => d.Status == ConditionStatus.Hit && d.Type == t));

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
            FlagValue.Yes => ConditionStatus.Hit, // an undecided (UNKNOWN) type is shaped UNKNOWN
            FlagValue.No => ConditionStatus.NotHit,
            _ => ConditionStatus.Unknown,
        };
        decided.Add((t, status, ConditionBasis.Flag, v == FlagValue.Unparseable ? FlagUnparseable : v == FlagValue.Yes ? code : null));
    }

    // The flag that speaks for a mapped type. RCA, SIE and unmapped types have none of the three (Is RCA? = NO is handled apart).
    private static FlagValue FlagOf(ListType t, ListFlags f) => t switch
    {
        ListType.Pep => f.Pep,
        ListType.AdverseMedia => f.AdverseNews,
        ListType.Sanction or ListType.OtherOfficialList => f.Blacklist,
        _ => FlagValue.Missing,
    };

    // One condition of a decided type, with the attributes its policy reads.
    private static SourceCondition Shape(ListType t, SourceProfile profile, IReadOnlyList<string> risks, string? news) => t switch
    {
        ListType.AdverseMedia => Hit(t) with
        {
            RiskCategories = risks,
            LegalStage = LegalStageFromNews(news, profile.Names),
            AmlRelevance = Relevance(risks, news, profile),
        },
        ListType.OtherOfficialList when IsActive(profile.OfficialList, "Wanted", "通緝") =>
            Hit(ListType.OfficialWantedList) with { LegalStage = LegalStage.Wanted },
        ListType.Sanction => Sanction(profile),
        ListType.Pep or ListType.Rca => Hit(t) with
        {
            PepType = PepType(profile, t),
            ReasonCodes = OutsideStatutoryScope(profile) ? [RcaOutsideStatutoryScope] : [],
        },
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

    /// <summary>
    /// Blacklist YES: the type comes from approved source fields (official-list entry, then category), else UNKNOWN.
    /// An active wanted list is shaped OFFICIAL_WANTED_LIST from OTHER_OFFICIAL_LIST.
    /// </summary>
    private static ListType BlacklistType(SourceProfile p, IReadOnlyList<ListType> mapped) =>
        p.OfficialList?.List.Contains("Sanction", StringComparison.OrdinalIgnoreCase) == true || mapped.Contains(ListType.Sanction) ? ListType.Sanction
        : mapped.Contains(ListType.OtherOfficialList) || p.OfficialList is not null ? ListType.OtherOfficialList
        : ListType.Unknown;

    private static SourceCondition Hit(ListType t) =>
        new(t, ConditionStatus.Hit, ConditionBasis.CategoryMapping, [], LegalStage.Unknown, null, PolicyVersion, []);

    /// <summary>A generic Active Profile is not an active sanction measure: HIT needs an active, named measure.</summary>
    // An active official-list entry confirms the measure only when it is not a wanted list (that is OFFICIAL_WANTED_LIST).
    private static SourceCondition Sanction(SourceProfile p) =>
        p.OfficialList is { } e && e.Status.Equals("Active", StringComparison.OrdinalIgnoreCase) && !IsActive(e, "Wanted", "通緝")
            ? Hit(ListType.Sanction) with { Program = e.List, Jurisdiction = e.Authority }
            : Hit(ListType.Sanction) with { Status = ConditionStatus.Unknown, ReasonCodes = [SanctionMeasureNotConfirmed] };

    // ponytail: PEP subtypes read from the category's subcategory text; the approved source fields arrive with GC-04.
    private static string? PepType(SourceProfile p, ListType type)
    {
        bool Says(params string[] words) => Subcategories(p, type).Any(s => words.Any(w => s.Contains(w, StringComparison.OrdinalIgnoreCase)));
        if (!OutsideStatutoryScope(p) && Says("Family", "Relative", "Spouse", "Child", "Parent", "Sibling")) return PepFamily;
        if (Says("Associate")) return PepCloseAssociate;
        if (type == ListType.Rca) return RcaUnconfirmed;
        return IsFormerPep(p, ownRoles: true) ? FormerPep : null;
    }

    private static IEnumerable<string> Subcategories(SourceProfile p, ListType type)
    {
        var prefix = type == ListType.Pep ? "Politically Exposed" : "Relative or Close Associate";
        return p.Categories.Where(c => c.Category.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Select(c => c.Subcategory).OfType<string>();
    }

    /// <summary>
    /// A PEP who has left office (認定標準第 5 條): a Former or Inactive PEP / RCA subcategory or, for the PEP's own Profile
    /// (<paramref name="ownRoles"/>), every recorded role ended; an RCA's own roles say nothing about the PEP's office.
    /// A role has ended when the last day its end date can stand for lies before the analysis date (<paramref name="asOf"/>,
    /// today by default): an end date of 2026 is still current during 2026. Still a HIT; lasting influence is a pending check
    /// (FORMER_PEP_INFLUENCE) with no fixed cooling-off period.
    /// </summary>
    // ponytail: "every role ended" reads all roles, not only public functions; approved PEP role fields arrive with GC-04.
    public static bool IsFormerPep(SourceProfile p, bool ownRoles, DateOnly? asOf = null)
    {
        var today = asOf ?? DateOnly.FromDateTime(DateTime.UtcNow);
        return Subcategories(p, ListType.Pep).Concat(Subcategories(p, ListType.Rca))
                .Any(s => s.Contains("Former", StringComparison.OrdinalIgnoreCase) || s.Contains("Inactive", StringComparison.OrdinalIgnoreCase))
            || (ownRoles && p.Roles.Count > 0 && p.Roles.All(r => r.To is { } to && to.LatestDay < today));
    }

    // ponytail: relation words in the subcategory text. 認定標準第 6 條 family: first-degree relatives by blood or marriage,
    // siblings, spouse or cohabiting partner, and the spouse's siblings. A named relation beyond that (cousin, uncle,
    // grandchild …) is outside the statutory scope; an unnamed "Family Member" / "Relative" is not judged here.
    private static readonly string[] OutsideScopeRelations =
        ["Cousin", "Uncle", "Aunt", "Nephew", "Niece", "Grand", "堂兄", "堂弟", "堂姊", "堂姐", "堂妹", "表兄", "表弟", "表姊", "表姐", "表妹", "表親",
        "叔", "伯父", "伯母", "姑", "舅", "姨", "姪", "甥", "祖父", "祖母", "孫"];

    private static bool OutsideStatutoryScope(SourceProfile p) =>
        Subcategories(p, ListType.Pep).Concat(Subcategories(p, ListType.Rca))
            .Any(s => OutsideScopeRelations.Any(w => s.Contains(w, StringComparison.OrdinalIgnoreCase)));

    // ponytail: controlled conduct lists. Only UNRELATED is extracted, and only when the source states both a conduct that
    // is no predicate offence and a jurisdiction; RELATED is left to the reviewer, as GC-02 and GC-11 keep UNCERTAIN.
    // Predicate offences follow 洗錢防制法第 3 條 (minimum sentence of six months or more, plus the listed statutes).
    private static readonly string[] PredicateConduct =
    [
        "詐欺", "詐騙", "詐貸", "詐取", "洗錢", "逃漏稅", "逃稅", "貪污", "賄賂", "收賄", "行賄", "背信", "侵占", "毒品", "販毒", "走私",
        "人口販運", "組織犯罪", "恐怖", "內線交易", "操縱股價", "地下匯兌", "吸金", "銀行法", "不實發票", "假發票", "偽造",
        "殺人", "強盜", "搶奪", "擄人勒贖", "恐嚇取財", "竊盜", "槍砲", "妨害性自主", "強制性交", "證券交易法", "證交法", "智慧財產",
        "著作權", "商標",
        "fraud", "laundering", "bribery", "corruption", "trafficking", "tax evasion", "murder", "robbery", "kidnap", "extortion",
        "theft", "firearm", "sexual assault", "securities",
    ];

    // Drunk driving or an accident that kills, gravely injures or is followed by flight (刑法 185-3 II, 185-4) carries a
    // minimum sentence of six months or more: a predicate offence whatever the conduct word.
    private static readonly string[] SeriousConsequence = ["致死", "致人於死", "致重傷", "逃逸", "hit-and-run", "death"];

    private static readonly string[] NonPredicateConduct =
    [
        "酒駕", "酒後駕車", "交通事故", "過失傷害", "傷害罪", "妨害名譽", "誹謗", "公然侮辱", "性騷擾", "家暴",
        "drunk driving", "defamation", "traffic accident",
    ];

    /// <summary>
    /// ② input. Any SIP crime category or risk keyword is a predicate offence, so it never becomes UNRELATED; without news
    /// there is no stated conduct. The non-predicate conduct must be in a sentence naming the Profile person; a predicate
    /// offence or a serious consequence anywhere in the news blocks UNRELATED.
    /// </summary>
    private static AmlRelevance Relevance(IReadOnlyList<string> risks, string? news, SourceProfile p)
    {
        if (news is null || risks.Count > 0) return AmlRelevance.Uncertain;
        var jurisdiction = (p.AllegationCountries ?? []).Count > 0 || Dateline().IsMatch(news);
        var predicate = PredicateConduct.Concat(SeriousConsequence).Any(w => news.Contains(w, StringComparison.OrdinalIgnoreCase));
        var other = Sentences(news).Where(s => Names(s, p.Names))
            .Any(s => NonPredicateConduct.Any(w => s.Contains(w, StringComparison.OrdinalIgnoreCase)));
        return jurisdiction && other && !predicate ? AmlRelevance.Unrelated : AmlRelevance.Uncertain;
    }

    private static string[] Sentences(string news) => news.Split('。', '！', '？', '\n', '；');

    private static bool Names(string sentence, IReadOnlyList<string> names) =>
        names.Any(n => n.Length > 1 && sentence.Contains(n, StringComparison.OrdinalIgnoreCase));

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

    // ponytail: keyword heuristic on attached news. Replace with approved legal-stage extraction when judgment wording
    // appears in a Golden Case.
    // - A judgment (conviction, acquittal) counts only in a sentence naming the Profile person: co-defendants' sentences
    //   are not theirs (GC-12).
    // - A dismissal or indictment counts in such a sentence, or in one that points at nobody else: a subjectless 檢方已起訴
    //   or a collective 起訴七人 (GC-12) is the person's; 同案, 另一, 其他 … mark someone else. Negation goes first:
    //   不起訴, 緩起訴 and 撤回起訴 are DISMISSED, never INDICTED.
    // - A modal, request or presumption before the keyword in its clause (可, 恐, 求, 最重, 推定) is no stage: 最重可判處,
    //   請求從重判處, 求刑. 無罪 is an acquittal only after 判決, 改判 or 宣判 in its clause (堅稱無罪 is a claim).
    // - The last stage in the text wins; 偵辦 or 調查 give ALLEGED only when there is no judicial stage.
    private static LegalStage LegalStageFromNews(string? news, IReadOnlyList<string> names)
    {
        if (news is null) return LegalStage.Unknown;
        var stage = LegalStage.Unknown;
        foreach (var sentence in Sentences(news))
        {
            var own = Names(sentence, names);
            var attributable = own || !OtherParty.Any(m => sentence.Contains(m, StringComparison.Ordinal));
            foreach (var clause in sentence.Split('，', ','))
            {
                var found = new List<(int End, LegalStage Stage)>();
                if (attributable)
                {
                    Find(found, clause, Dismissal(), LegalStage.Dismissed);
                    Find(found, clause, Indictment(), LegalStage.Indicted);
                }
                if (own)
                {
                    Find(found, clause, Conviction(), LegalStage.Convicted);
                    Find(found, clause, Acquittal(), LegalStage.Acquitted);
                }
                if (found.Count > 0) stage = found.MaxBy(f => f.End).Stage;
            }
        }
        if (stage == LegalStage.Unknown && (news.Contains("偵辦", StringComparison.Ordinal) || news.Contains("調查", StringComparison.Ordinal)))
            return LegalStage.Alleged;
        return stage;
    }

    private static void Find(List<(int, LegalStage)> found, string clause, Regex pattern, LegalStage stage)
    {
        foreach (Match m in pattern.Matches(clause))
            if (!Modals.Any(w => clause.AsSpan(0, m.Index).Contains(w, StringComparison.Ordinal))) found.Add((m.Index + m.Length, stage));
    }

    private static readonly string[] Modals = ["可", "恐", "求", "最重", "推定"];

    private static readonly string[] OtherParty = ["同案", "另一", "另名", "其他", "其餘", "共犯", "共同被告"];

    [GeneratedRegex("不起訴|緩起訴|撤回起訴")]
    private static partial Regex Dismissal();

    [GeneratedRegex("(?<!不|緩|撤回)起訴")]
    private static partial Regex Indictment();

    [GeneratedRegex("判處|判刑|判決有罪|有罪確定|(?<!無罪)定讞")]
    private static partial Regex Conviction();

    [GeneratedRegex("(判決|改判|宣判)[^，,]{0,10}?無罪(?!推定)")]
    private static partial Regex Acquittal();
}
