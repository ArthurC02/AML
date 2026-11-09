using System.ComponentModel;
using System.Text.RegularExpressions;
using Aml.Core.Normalization;
using Aml.Core.Subjects;

namespace Aml.Core.Screening;

/// <summary>Bank list types (ADR-0005). UNKNOWN: an unmapped source category, left for the reviewer.</summary>
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

/// <summary>List-type determination and source conditions (ADR-0005).</summary>
public static partial class SourceConditions
{
    public const string PolicyVersion = "source-policy-0.2";
    public const string SanctionMeasureNotConfirmed = "SANCTION_MEASURE_NOT_CONFIRMED";
    public const string UnmappedCategory = "UNMAPPED_CATEGORY";

    private const string PepCategory = "Politically Exposed";
    private const string RcaCategory = "Relative or Close Associate";
    private const string SipCategory = "Special Interest Person";

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

    private readonly record struct Decision(ListType Type, ConditionStatus Status, ConditionBasis Basis, string? Code);

    /// <summary>
    /// A flag with a value (NO counts) decides its own list types and wins over a contradicting category, recording the
    /// conflict; a mapped category whose flag is MISSING keeps its category condition.
    /// </summary>
    /// <param name="asOf">The analysis date, for whether a PEP's roles have all ended; today (UTC) when not given.</param>
    public static IReadOnlyList<SourceCondition> Determine(SourceProfile profile, bool isRca, string? news, ListFlags? flags = null, DateOnly? asOf = null)
    {
        var today = asOf ?? DateOnly.FromDateTime(DateTime.UtcNow);
        flags ??= ListFlags.None;
        var mapped = profile.Categories.Select(MapCategory).Distinct().ToList();
        var decided = new List<Decision>();
        if (!flags.AllMissing) DecideFromFlags(decided, profile, mapped, flags);
        DecideFromUnflaggedCategories(decided, mapped, flags);
        DecideRca(decided, mapped, flags, isRca);
        var conflict = !flags.AllMissing && FlagsContradictCategories(decided, mapped);
        var risks = AdverseMediaRiskCategories(profile);
        return decided.Select(d => Condition(d, profile, risks, news, conflict, today)).ToList();
    }

    // 黑名單 = NO never clears a Sanction category: that condition is UNKNOWN for review.
    private static void DecideFromFlags(List<Decision> decided, SourceProfile profile, List<ListType> mapped, ListFlags flags)
    {
        var blacklist = BlacklistType(profile, mapped);
        FromFlag(decided, ListType.Pep, flags.Pep, null);
        if (mapped.Contains(ListType.Sanction) && flags.Blacklist == FlagValue.No)
            decided.Add(new(ListType.Sanction, ConditionStatus.Unknown, ConditionBasis.Flag, FlagCategoryConflict));
        else
            FromFlag(decided, blacklist, flags.Blacklist, blacklist == ListType.Unknown ? BlacklistTypeUndecided : null);
        FromFlag(decided, ListType.AdverseMedia, flags.AdverseNews, null);
    }

    private static void DecideFromUnflaggedCategories(List<Decision> decided, List<ListType> mapped, ListFlags flags)
    {
        foreach (var t in mapped.Where(t => FlagFor(t, flags) == FlagValue.Missing && !decided.Any(d => d.Type == t)
                     && !(t == ListType.Rca && flags.Rca == FlagValue.No)))
            decided.Add(t == ListType.Unknown
                ? new(t, ConditionStatus.Unknown, ConditionBasis.Unmapped, UnmappedCategory)
                : new(t, ConditionStatus.Hit, ConditionBasis.CategoryMapping, null));
    }

    // Is RCA? = NO never clears an RCA category: that condition is UNKNOWN for review. Is RCA? = YES always adds RCA.
    private static void DecideRca(List<Decision> decided, List<ListType> mapped, ListFlags flags, bool isRca)
    {
        if (mapped.Contains(ListType.Rca) && flags.Rca == FlagValue.No)
            decided.Add(new(ListType.Rca, ConditionStatus.Unknown, ConditionBasis.Flag, FlagCategoryConflict));
        if (isRca && !decided.Any(d => d.Type == ListType.Rca)) decided.Add(new(ListType.Rca, ConditionStatus.Hit, ConditionBasis.Flag, FlagIsRca));
    }

    private static bool FlagsContradictCategories(List<Decision> decided, List<ListType> mapped) =>
        mapped.Any(t => t is not ListType.Unknown && !decided.Any(d => d.Status == ConditionStatus.Hit && d.Type == t));

    private static SourceCondition Condition(Decision d, SourceProfile profile, IReadOnlyList<string> risks, string? news, bool conflict, DateOnly today)
    {
        var c = Shape(d.Type, profile, risks, news, today);
        if (d.Status != ConditionStatus.Hit) c = c with { Status = d.Status };
        var codes = c.ReasonCodes.Concat(new[] { d.Code, conflict ? FlagCategoryConflict : null }.OfType<string>()).Distinct().ToList();
        return c with { Basis = d.Basis, ReasonCodes = codes };
    }

    private static void FromFlag(List<Decision> decided, ListType t, FlagValue v, string? codeWhenYes)
    {
        if (v == FlagValue.Missing) return;
        var status = v switch
        {
            FlagValue.Yes => ConditionStatus.Hit,
            FlagValue.No => ConditionStatus.NotHit,
            _ => ConditionStatus.Unknown,
        };
        decided.Add(new(t, status, ConditionBasis.Flag, v == FlagValue.Unparseable ? FlagUnparseable : v == FlagValue.Yes ? codeWhenYes : null));
    }

    private static FlagValue FlagFor(ListType t, ListFlags f) => t switch
    {
        ListType.Pep => f.Pep,
        ListType.AdverseMedia => f.AdverseNews,
        ListType.Sanction or ListType.OtherOfficialList => f.Blacklist,
        _ => FlagValue.Missing,
    };

    private static SourceCondition Shape(ListType t, SourceProfile profile, IReadOnlyList<string> risks, string? news, DateOnly today) => t switch
    {
        ListType.AdverseMedia => Hit(t) with
        {
            RiskCategories = risks,
            LegalStage = LegalStageFromNews(news, profile.Names),
            AmlRelevance = Relevance(risks, news, profile),
        },
        ListType.OtherOfficialList when IsActiveWantedList(profile.OfficialList) =>
            Hit(ListType.OfficialWantedList) with { LegalStage = LegalStage.Wanted },
        ListType.Sanction => Sanction(profile),
        ListType.Pep or ListType.Rca => Hit(t) with
        {
            PepType = PepType(profile, t, today),
            ReasonCodes = OutsideStatutoryScope(profile) ? [RcaOutsideStatutoryScope] : [],
        },
        ListType.Unknown => new SourceCondition(t, ConditionStatus.Unknown, ConditionBasis.Unmapped, [], LegalStage.Unknown, null, PolicyVersion, []),
        _ => Hit(t),
    };

    private static List<string> AdverseMediaRiskCategories(SourceProfile profile) =>
        SipCrimeRisks(profile).Concat(KeywordRisks(profile)).Distinct().ToList();

    private static IEnumerable<string> SipCrimeRisks(SourceProfile profile)
    {
        foreach (var c in profile.Categories)
            if (MapCategory(c) == ListType.AdverseMedia && c.Subcategory is { } sub && SipRiskCategories.TryGetValue(sub, out var r))
                yield return r;
    }

    private static IEnumerable<string> KeywordRisks(SourceProfile profile) =>
        profile.Keywords.Select(k => KeywordRiskCategories.GetValueOrDefault(k)).OfType<string>();

    private static ListType BlacklistType(SourceProfile p, IReadOnlyList<ListType> mapped) =>
        p.OfficialList?.List.Contains("Sanction", StringComparison.OrdinalIgnoreCase) == true || mapped.Contains(ListType.Sanction) ? ListType.Sanction
        : mapped.Contains(ListType.OtherOfficialList) || p.OfficialList is not null ? ListType.OtherOfficialList
        : ListType.Unknown;

    private static SourceCondition Hit(ListType t) =>
        new(t, ConditionStatus.Hit, ConditionBasis.CategoryMapping, [], LegalStage.Unknown, null, PolicyVersion, []);

    /// <summary>A generic Active Profile is not an active sanction measure: HIT needs an active, named measure.</summary>
    private static SourceCondition Sanction(SourceProfile p) =>
        p.OfficialList is { } e && ConfirmsSanctionMeasure(e)
            ? Hit(ListType.Sanction) with { Program = e.List, Jurisdiction = e.Authority }
            : Hit(ListType.Sanction) with { Status = ConditionStatus.Unknown, ReasonCodes = [SanctionMeasureNotConfirmed] };

    private static bool ConfirmsSanctionMeasure(OfficialListEntry e) => IsActive(e) && !IsActiveWantedList(e);

    // ponytail: PEP subtypes read from the category's subcategory text; the approved source fields arrive with GC-04.
    private static string? PepType(SourceProfile p, ListType type, DateOnly today)
    {
        bool Says(params string[] words) => Subcategories(p, type).Any(s => words.Any(w => s.Contains(w, StringComparison.OrdinalIgnoreCase)));
        if (!OutsideStatutoryScope(p) && Says("Family", "Relative", "Spouse", "Child", "Parent", "Sibling")) return PepFamily;
        if (Says("Associate")) return PepCloseAssociate;
        if (type == ListType.Rca) return RcaUnconfirmed;
        return IsFormerPep(p, ownRoles: true, today) ? FormerPep : null;
    }

    private static IEnumerable<string> Subcategories(SourceProfile p, ListType type)
    {
        var prefix = type == ListType.Pep ? PepCategory : RcaCategory;
        return p.Categories.Where(c => c.Category.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Select(c => c.Subcategory).OfType<string>();
    }

    private static IEnumerable<string> PepAndRcaSubcategories(SourceProfile p) =>
        Subcategories(p, ListType.Pep).Concat(Subcategories(p, ListType.Rca));

    /// <summary>
    /// A PEP who has left office (認定標準第 5 條): a Former or Inactive subcategory or, for the PEP's own Profile, every
    /// role ended; an RCA's own roles say nothing about the PEP's office. Still a HIT; lasting influence is the
    /// FORMER_PEP_INFLUENCE pending check, with no fixed cooling-off period.
    /// </summary>
    // ponytail: "every role ended" reads all roles, not only public functions; approved PEP role fields arrive with GC-04.
    public static bool IsFormerPep(SourceProfile p, bool ownRoles, DateOnly asOf) =>
        HasFormerSubcategory(p) || (ownRoles && AllRolesEndedBefore(p, asOf));

    private static bool HasFormerSubcategory(SourceProfile p) =>
        PepAndRcaSubcategories(p).Any(s => s.Contains("Former", StringComparison.OrdinalIgnoreCase) || s.Contains("Inactive", StringComparison.OrdinalIgnoreCase));

    private static bool AllRolesEndedBefore(SourceProfile p, DateOnly today) =>
        p.Roles.Count > 0 && p.Roles.All(r => r.To is { } to && to.LatestDay < today);

    // ponytail: relation words in the subcategory text. 認定標準第 6 條 family: first-degree relatives by blood or marriage,
    // siblings, spouse or cohabiting partner, and the spouse's siblings. A named relation beyond that (cousin, uncle,
    // grandchild …) is outside the statutory scope; an unnamed "Family Member" / "Relative" is not judged here.
    private static readonly string[] OutsideScopeRelations =
        ["Cousin", "Uncle", "Aunt", "Nephew", "Niece", "Grand", "堂兄", "堂弟", "堂姊", "堂姐", "堂妹", "表兄", "表弟", "表姊", "表姐", "表妹", "表親",
        "叔", "伯父", "伯母", "姑", "舅", "姨", "姪", "甥", "祖父", "祖母", "孫"];

    private static bool OutsideStatutoryScope(SourceProfile p) =>
        PepAndRcaSubcategories(p).Any(s => OutsideScopeRelations.Any(w => s.Contains(w, StringComparison.OrdinalIgnoreCase)));

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

    // 刑法 185-3 II, 185-4: these consequences carry six months or more, so they are predicate offences whatever the conduct.
    private static readonly string[] SeriousConsequence = ["致死", "致人於死", "致重傷", "逃逸", "hit-and-run", "death"];

    private static readonly string[] NonPredicateConduct =
    [
        "酒駕", "酒後駕車", "交通事故", "過失傷害", "傷害罪", "妨害名譽", "誹謗", "公然侮辱", "性騷擾", "家暴",
        "drunk driving", "defamation", "traffic accident",
    ];

    /// <summary>Article 22 ② input: UNRELATED needs a jurisdiction and non-predicate conduct by the named Profile person.</summary>
    private static AmlRelevance Relevance(IReadOnlyList<string> risks, string? news, SourceProfile p)
    {
        var hasPredicateRiskCategory = risks.Count > 0;
        if (news is null || hasPredicateRiskCategory) return AmlRelevance.Uncertain;
        var statesJurisdiction = (p.AllegationCountries ?? []).Count > 0 || Dateline().IsMatch(news);
        var mentionsPredicateOffence = PredicateConduct.Concat(SeriousConsequence).Any(w => news.Contains(w, StringComparison.OrdinalIgnoreCase));
        var personNamedInNonPredicateConduct = CjkText.Sentences(news).Where(s => MentionsAnyName(s, p.Names))
            .Any(s => NonPredicateConduct.Any(w => s.Contains(w, StringComparison.OrdinalIgnoreCase)));
        return statesJurisdiction && personNamedInNonPredicateConduct && !mentionsPredicateOffence ? AmlRelevance.Unrelated : AmlRelevance.Uncertain;
    }

    /// <summary>An ADVERSE_MEDIA condition about conduct unrelated to money laundering (Article 22 ②): it is set aside.</summary>
    internal static bool IsUnrelatedAdverseMedia(SourceCondition c) => c.Type == ListType.AdverseMedia && c.AmlRelevance == AmlRelevance.Unrelated;

    // ponytail: the first 100 names, as in StatedAgeCheck: every sentence and clause is searched for each name.
    private static bool MentionsAnyName(string sentence, IReadOnlyList<string> names) =>
        names.Take(100).Any(n => n.Length > 1 && sentence.Contains(n, StringComparison.OrdinalIgnoreCase));

    // "〔記者丙／台北報導〕", "（中央社台北6日電）": the report states where it happened.
    [GeneratedRegex("／\\p{IsCJKUnifiedIdeographs}{2,4}報導|中央社\\p{IsCJKUnifiedIdeographs}{2,4}\\d{1,2}日電")]
    private static partial Regex Dateline();

    private static ListType MapCategory(SourceCategory c)
    {
        var cat = c.Category;
        if (cat.StartsWith(SipCategory, StringComparison.OrdinalIgnoreCase)) return SipListType(c.Subcategory);
        if (cat.StartsWith(PepCategory, StringComparison.OrdinalIgnoreCase)) return ListType.Pep;
        if (cat.StartsWith(RcaCategory, StringComparison.OrdinalIgnoreCase)) return ListType.Rca;
        if (cat.StartsWith("Sanction", StringComparison.OrdinalIgnoreCase)) return ListType.Sanction;
        if (cat.StartsWith("State Owned", StringComparison.OrdinalIgnoreCase) || cat.StartsWith("State Invested", StringComparison.OrdinalIgnoreCase)) return ListType.Sie;
        if (cat.StartsWith("Adverse Media", StringComparison.OrdinalIgnoreCase)) return ListType.AdverseMedia;
        if (NamesOfficialList(cat)) return ListType.OtherOfficialList;
        return ListType.Unknown;
    }

    private static ListType SipListType(string? subcategory) => subcategory switch
    {
        null => ListType.Unknown,
        var s when NamesOfficialList(s) => ListType.OtherOfficialList,
        var s when SipRiskCategories.ContainsKey(s) => ListType.AdverseMedia,
        _ => ListType.Unknown,
    };

    private static bool NamesOfficialList(string s) =>
        s.Contains("Official List", StringComparison.OrdinalIgnoreCase) || s.Contains("Exclusion List", StringComparison.OrdinalIgnoreCase);

    private static bool IsActive(OfficialListEntry e) => e.Status.Equals("Active", StringComparison.OrdinalIgnoreCase);

    private static bool IsActiveWantedList(OfficialListEntry? e) =>
        e is not null && IsActive(e)
        && (e.List.Contains("Wanted", StringComparison.OrdinalIgnoreCase) || e.List.Contains("通緝", StringComparison.Ordinal));

    // ponytail: keyword heuristic on attached news. Replace with approved legal-stage extraction when judgment wording
    // appears in a Golden Case.
    // - A judgment (conviction, acquittal) counts only in a sentence naming the Profile person: co-defendants' sentences
    //   are not theirs (GC-12).
    // - A dismissal or indictment counts in such a sentence, or in one that points at nobody else: a subjectless 檢方已起訴
    //   or a collective 起訴七人 (GC-12) is the person's; 同案, 另一, 其他 … mark someone else. Negation goes first:
    //   不起訴, 緩起訴 and 撤回起訴 are DISMISSED, never INDICTED.
    // - A modal, request, plan, condition or hearsay before the keyword in its clause (可, 恐, 求, 最重, 推定, 擬, 預計, 即將,
    //   將會, 若, 如果, 一旦, 據傳) is no stage: 最重可判處, 請求從重判處, 恐遭起訴, 擬起訴. Words that only hold those
    //   characters (恐嚇, 恐怖, 恐龍, 可疑, 可是, 許可, 求償, 要求, 模擬, 若干) are not modals.
    // - A negation or question just before the keyword (未, 沒有, 並未, 尚未, 從未, 不會, 無, 是否, with 被/遭/受 between)
    //   is no stage: 尚未起訴, 沒有被起訴, 是否起訴. 不予起訴 and 免予起訴 are DISMISSED.
    // - Quoted speech (「…」, 『…』, “…”) is a claim, not a report: no stage is read inside it, but names still are.
    // - A clause whose own subject is another person is not the Profile person's, even in a sentence that names them:
    //   王大明與李四涉詐欺，李四遭起訴. The clause must open (past 而, 但, 隨後 …) with a 2 or 3 character name of a common
    //   surname other than the person's own, or "X姓男子", right before 遭, 被 or 獲 (李四遭, 林姓男子被); 王男遭 for 王大明
    //   is the person, and 施用毒品遭 or 他曾涉案遭 open with no name.
    //   無罪 is an acquittal only after 判決, 改判 or 宣判 in its clause (堅稱無罪 is a claim).
    // - The last stage in the text wins; 偵辦 or 調查 give ALLEGED only when there is no judicial stage.
    private static LegalStage LegalStageFromNews(string? news, IReadOnlyList<string> names)
    {
        if (news is null) return LegalStage.Unknown;
        var stage = LegalStage.Unknown;
        var surnames = names.Where(n => n.Length > 1 && CjkText.IsCjk(n[0])).Select(n => n[0]).ToHashSet();
        // Quoted speech is blanked out, keeping every position: stages are looked for in the blanked text, names in the
        // original (綽號「阿明」的王大明 still names the person).
        var blanked = Quoted().Replace(news, m => new string('　', m.Length));
        foreach (var (start, length) in Spans(blanked, 0, blanked.Length, SentenceEnds))
        {
            var own = MentionsAnyName(news.Substring(start, length), names);
            var attributable = own || !OtherParty.Any(m => blanked.AsSpan(start, length).Contains(m, StringComparison.Ordinal));
            foreach (var (at, size) in Spans(blanked, start, length, ClauseEnds))
            {
                var clause = blanked.Substring(at, size);
                if (!MentionsAnyName(news.Substring(at, size), names) && NamesAnotherPerson(clause, surnames)) continue;
                if (LastStageIn(clause, attributable, own) is { } found) stage = found;
            }
        }
        var underInvestigation = blanked.Contains("偵辦", StringComparison.Ordinal) || blanked.Contains("調查", StringComparison.Ordinal);
        return stage == LegalStage.Unknown && underInvestigation ? LegalStage.Alleged : stage;
    }

    internal static readonly char[] SentenceEnds = ['。', '！', '？', '\n', '；'], ClauseEnds = ['，', ','];

    // The (start, length) of each piece of text[from..from+length] between separators, as string.Split cuts it.
    internal static IEnumerable<(int Start, int Length)> Spans(string text, int from, int length, char[] separators)
    {
        var end = from + length;
        for (var start = from; start <= end;)
        {
            var next = text.IndexOfAny(separators, start, end - start);
            if (next < 0) next = end;
            yield return (start, next - start);
            start = next + 1;
        }
    }

    // The clause's own subject is another person: it opens (past a connective) with a name of a common surname other
    // than the person's own, 2 or 3 characters, or "X姓男子", right before 遭, 被 or 獲. A clause that opens with
    // anything else (施用毒品遭, 高院二審被, 他曾涉案遭) is not taken as naming someone.
    private static bool NamesAnotherPerson(string clause, HashSet<char> ownSurnames) =>
        OtherPersonOpening().Match(clause) is { Success: true } m && CommonSurnames.Contains(m.Groups["surname"].Value[0], StringComparison.Ordinal)
        && !ownSurnames.Contains(m.Groups["surname"].Value[0]);

    private static LegalStage? LastStageIn(string clause, bool attributable, bool own)
    {
        var found = new List<(int End, LegalStage Stage)>();
        var modalEnd = FirstModalEnd(clause); // once per clause: a modal ending at or before a keyword qualifies it
        if (attributable)
        {
            Find(found, clause, Dismissal(), LegalStage.Dismissed, modalEnd);
            Find(found, clause, Indictment(), LegalStage.Indicted, modalEnd);
        }
        if (own)
        {
            Find(found, clause, Conviction(), LegalStage.Convicted, modalEnd);
            Find(found, clause, Acquittal(), LegalStage.Acquitted, modalEnd);
        }
        return found.Count > 0 ? found.MaxBy(f => f.End).Stage : null;
    }

    private static void Find(List<(int, LegalStage)> found, string clause, Regex pattern, LegalStage stage, int modalEnd)
    {
        foreach (Match m in pattern.Matches(clause))
            if (modalEnd > m.Index && !Negated(clause, m.Index)) found.Add((m.Index + m.Length, stage));
    }

    // Where the first modal of the clause ends (past the end when there is none). A modal anywhere before the keyword makes
    // it no stage (最重可判處, 檢方請求法院依法從重判處), but a word that only holds the modal's characters is not one.
    // ponytail: a name holding 可 or 求 (陳可欣遭起訴) still reads as a modal, so no stage: the safe side; exclude the matched
    // names when that costs real hits.
    internal static int FirstModalEnd(string clause)
    {
        var first = int.MaxValue;
        foreach (var (modal, notBefore, notAfter) in Modals)
            for (var at = clause.IndexOf(modal, StringComparison.Ordinal); at >= 0 && at + modal.Length < first;
                 at = clause.IndexOf(modal, at + 1, StringComparison.Ordinal))
            {
                var next = at + modal.Length;
                if (at > 0 && notBefore.Contains(clause[at - 1], StringComparison.Ordinal)) continue;
                if (next < clause.Length && notAfter.Contains(clause[next], StringComparison.Ordinal)) continue;
                first = next;
                break;
            }
        return first;
    }

    // 尚未起訴, 沒有被起訴, 是否遭到起訴: the words just before the keyword, past a passive marker.
    internal static bool Negated(string clause, int keyword)
    {
        var before = clause.AsSpan(0, keyword);
        foreach (var passive in Passives)
            if (before.EndsWith(passive, StringComparison.Ordinal)) { before = before[..^passive.Length]; break; }
        foreach (var negation in Negations)
            if (before.EndsWith(negation, StringComparison.Ordinal)) return true;
        return false;
    }

    private static readonly string[] Passives = ["遭到", "遭", "被", "受"];

    private static readonly string[] Negations = ["未", "沒有", "沒", "不會", "無", "是否"];

    private static readonly (string Modal, string NotBefore, string NotAfter)[] Modals =
    [
        ("可", "許認核", "疑是"), ("恐", "", "嚇怖慌懼龍"), ("求", "要訴需追", "償援助職"), ("最重", "", ""), ("推定", "", ""),
        ("擬", "模虛", ""), ("預計", "", ""), ("即將", "", ""), ("將會", "", ""), ("若", "", "干"), ("如果", "", ""), ("一旦", "", ""), ("據傳", "", ""),
    ];

    private static readonly string[] OtherParty = ["同案", "另一", "另名", "其他", "其餘", "共犯", "共同被告"];

    // ponytail: the commonest Taiwanese surnames, enough to tell 李四遭起訴 from 一審遭判處; a person with a rarer surname
    // still reads as the Profile person's clause.
    internal const string CommonSurnames =
        "陳林黃張李王吳劉蔡楊許鄭謝郭洪曾邱廖賴周徐蘇葉莊呂江何蕭羅高潘簡朱鍾鐘游彭詹胡施沈余盧梁趙顏柯翁魏孫戴范方宋鄧杜傅侯曹薛丁卓阮馬董溫唐藍蔣石古紀姚連馮歐程湯田康姜白汪鄒尤巫黎涂龔嚴韓袁金童陸夏柳邵錢伍倪于譚駱熊任甘秦顧毛章史官萬俞雷粘饒";

    [GeneratedRegex(@"^\s*(?:而|但|則|另外|另|隨後|其後|後來|至於)?(?<surname>\p{IsCJKUnifiedIdeographs})(?:\p{IsCJKUnifiedIdeographs}{1,2}|姓(?:男子|女子|男|女|嫌犯|被告))(?:也|亦|則|已|均)?(?:遭到|遭|被|獲)")]
    private static partial Regex OtherPersonOpening();

    // Non-backtracking: a backtracking engine rescans to the end from every unclosed opening quote (1M 「 is quadratic).
    [GeneratedRegex("「[^」]*」|『[^』]*』|“[^”]*”", RegexOptions.NonBacktracking)]
    internal static partial Regex Quoted();

    [GeneratedRegex("不起訴|緩起訴|撤回起訴|不予起訴|免予起訴")]
    internal static partial Regex Dismissal();

    [GeneratedRegex("(?<!不|緩|撤回|予)起訴")]
    internal static partial Regex Indictment();

    [GeneratedRegex("判處|判刑|判決有罪|有罪確定|(?<!無罪)定讞")]
    internal static partial Regex Conviction();

    [GeneratedRegex("(判決|改判|宣判)[^，,]{0,10}?無罪(?!推定)")]
    internal static partial Regex Acquittal();
}
