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
    //   無罪 is an acquittal only as a judgment's word (see Acquittal; 堅稱無罪 is a claim).
    // - The last stage in the text wins; 定讞 alone settles what came before, and 通緝 is WANTED only while no judicial
    //   stage is told. 偵辦 or 調查 give ALLEGED only when there is no stage.
    // The full rules, with examples, are in docs/feature/watchlist-and-adverse-information.md (Stage from attached news).
    private static LegalStage LegalStageFromNews(string? news, IReadOnlyList<string> names)
    {
        if (news is null) return LegalStage.Unknown;
        var stage = LegalStage.Unknown;
        var surnames = names.Where(n => n.Length > 1 && CjkText.IsCjk(n[0])).Select(n => n[0]).ToHashSet();
        // Quoted speech is blanked out, keeping every position: stages are looked for in the blanked text, names in the
        // original (綽號「阿明」的王大明 still names the person).
        var blanked = Quoted().Replace(news, m => new string('　', m.Length));
        // An aside in brackets is about whoever stands right before it: 李小四（另案起訴）、陳志明 says nothing of 陳志明.
        // The brackets are paired in the text as written (a quote blanked out may have held one of a pair).
        var kept = blanked.ToCharArray();
        foreach (Match m in Aside().Matches(news))
            if (!GroupAside().IsMatch(m.Value) && AfterAnotherName(news, m.Index, names, surnames)) kept.AsSpan(m.Index, m.Length).Fill('　');
        // 辯稱遭人陷害云云, 稱其帳戶已遭通緝云云: from the verb of saying to 云云 is what someone said.
        foreach (Match m in Reported().Matches(news)) kept.AsSpan(m.Index, m.Length).Fill('　');
        blanked = new string(kept);
        var named = WithoutRelations(news, names);
        var (judgment, ordered, defendant, sole) = JudgmentReading(news, names);
        var suspected = false;
        // A judgment names many people besides its defendants (witnesses, account holders, accomplices tried elsewhere):
        // for them its subjectless 起訴 is the defendants', and 起訴書, 起訴範圍 and 起訴意旨 are the case's papers.
        var bystander = judgment && !defendant;
        foreach (var (start, length) in Spans(blanked, 0, blanked.Length, SentenceEnds))
        {
            var sentence = blanked.Substring(start, length);
            var others = OtherParty.Any(m => sentence.Contains(m, StringComparison.Ordinal));
            // A judgment or ruling with one defendant goes on about 被告 without the name: that is the person.
            var own = MentionsAnyName(named.Substring(start, length), names) || (sole && !others && sentence.Contains("被告", StringComparison.Ordinal) && !NotThisCase().IsMatch(sentence));
            var relation = named.AsSpan(start, length).Contains(RelationMark);
            var attributable = own || (!bystander && !relation && !others);
            // 李小四等3人潛逃出境，發布通緝: outside a judgment a wanted notice said of a group is everyone's.
            var collective = attributable && !judgment && Group().IsMatch(sentence);
            suspected |= own && (Suspicion().IsMatch(sentence) || Group().IsMatch(sentence)); // a list of names ending 等9名 is the suspects'
            // 佯稱被害人涉案，已遭通緝: what a fraudster told the victim, to the end of that clause and through the next one.
            // No further: the sentence may go on to the fraudster's own arrest and indictment.
            var claimed = false;
            foreach (var (at, size) in Spans(blanked, start, length, ClauseEnds))
            {
                var clause = blanked.Substring(at, size);
                // …unless the next clause opens with the passive: 陳志明佯稱可獲利，遭起訴 is the fraudster's own indictment.
                if (claimed) { claimed = false; if (!OpensPassive().IsMatch(clause)) continue; }
                if (Pretence().Match(clause) is { Success: true } pretence) (claimed, clause) = (true, clause[..pretence.Index]);
                if (bystander) clause = IndictmentPaper().Replace(clause, m => new string('　', m.Length));
                var mine = MentionsAnyName(named.Substring(at, size), names);
                if (!mine && (named.AsSpan(at, size).Contains(RelationMark) || NamesAnotherPerson(clause, surnames) || IndictsAnotherPerson(clause, surnames, news))) continue;
                if (LastStageIn(clause, attributable, own) is { } found) stage = found;
                else if (sole && own && Says(clause, Sentenced())) stage = LegalStage.Convicted;
                // 定讞 alone settles what came before: an indictment (or nothing said) ends in a conviction, while
                // 不起訴處分，全案定讞 and 判決無罪，全案定讞 stay what they are.
                // 共犯李小四日前定讞 or 另案定讞 settles someone or something else.
                else if (own && stage is LegalStage.Unknown or LegalStage.Indicted && Says(clause, Final())
                    && !OtherParty.Any(m => clause.Contains(m, StringComparison.Ordinal)) && !clause.Contains("另案", StringComparison.Ordinal)) stage = LegalStage.Convicted;
                // A wanted notice is a stage only until a judicial one is told, before or after it (起訴後棄保遭通緝 stays INDICTED).
                else if ((own || collective) && stage == LegalStage.Unknown && Says(clause, WantedNotice())) stage = LegalStage.Wanted;
            }
        }
        if (ordered is { } outcome) return outcome; // a judgment's order is its outcome
        if (defendant && stage == LegalStage.Unknown) return LegalStage.Indicted; // a listed defendant stands indicted, whatever the order goes on to say
        // 偵辦 or 調查 somewhere in the text is the person's when a sentence naming them says they are under suspicion or
        // names a step (涉嫌, 約談, 移送 …); someone the text names only as a counterpart in the facts is not ALLEGED by it.
        // A text that never writes the Profile's names is about them as a whole.
        var underInvestigation = !bystander && (suspected || !MentionsAnyName(named, names))
            && (blanked.Contains("偵辦", StringComparison.Ordinal) || blanked.Contains("調查", StringComparison.Ordinal));
        return stage == LegalStage.Unknown && underInvestigation ? LegalStage.Alleged : stage;
    }

    [GeneratedRegex("無罪部分(?=[^。]{0,40}撤銷)")]
    private static partial Regex RevokedAcquittal();

    // 李小四、陳志明（均遭起訴）: an aside that opens with a word for all of them is the whole list's.
    [GeneratedRegex("^[（(](?:均|皆|同|等|[兩二三四五]人)")]
    private static partial Regex GroupAside();

    [GeneratedRegex(@"^\s*(?:遭|被|經)")]
    private static partial Regex OpensPassive();

    [GeneratedRegex("佯稱|謊稱|誆稱|訛稱|詐稱")]
    private static partial Regex Pretence();

    [GeneratedRegex("[（(][^（）()。\n]{1,200}[）)]", RegexOptions.NonBacktracking)]
    private static partial Regex Aside();

    // The two or three characters before the bracket read as another person's name: a common surname other than the
    // Profile person's own, then ideographs or ○ (李小四, 李○○, 李四). After one of the Profile's names it is theirs.
    // ponytail: a noun opening with a surname character (高層（已起訴）) is taken for a name too.
    private static bool AfterAnotherName(string news, int bracket, IReadOnlyList<string> names, HashSet<char> ownSurnames)
    {
        var before = news.AsSpan(0, bracket);
        if (names.Take(100).Any(n => n.Length > 1 && news.AsSpan(0, bracket).EndsWith(n, StringComparison.OrdinalIgnoreCase))) return false;
        foreach (var length in new[] { 3, 2 })
            if (before.Length >= length && before[^length..] is var name && CommonSurnames.Contains(name[0], StringComparison.Ordinal)
                && !ownSurnames.Contains(name[0]) && name[1..].IndexOfAnyExcept(NameCharacters) < 0) return true;
        return false;
    }

    private static readonly System.Buffers.SearchValues<char> NameCharacters = System.Buffers.SearchValues.Create(
        "○" + string.Concat(Enumerable.Range(0x4E00, 0x9FFF - 0x4E00 + 1).Select(c => (char)c)));

    // 王大明的哥哥遭起訴, 王大明之妻李四遭判刑: the one spoken of is the relative, aide or lawyer, so the Profile name right
    // before 的 / 之 and such a word is marked out, keeping every position. A sentence that names the person only this way
    // is someone else's, and so is such a clause in a sentence that also names the person outright.
    // ponytail: a fixed word list, and the relation must follow 的 or 之 (王大明胞弟 still reads as the person).
    private static string WithoutRelations(string news, IReadOnlyList<string> names)
    {
        var chars = news.ToCharArray();
        foreach (Match m in Relation().Matches(news))
            if (names.Take(100).FirstOrDefault(n => n.Length > 1 && news.AsSpan(0, m.Index).EndsWith(n, StringComparison.OrdinalIgnoreCase)) is { } name)
                chars.AsSpan(m.Index - name.Length, name.Length).Fill(RelationMark);
        // 被害人王大明報案, 證人王大明指出: named in that role, the sentence's steps are someone else's.
        foreach (Match m in Bystander().Matches(news))
            if (names.Take(100).FirstOrDefault(n => n.Length > 1 && news.AsSpan(m.Index + m.Length).StartsWith(n, StringComparison.OrdinalIgnoreCase)) is { } name)
                chars.AsSpan(m.Index + m.Length, name.Length).Fill(RelationMark);
        return new string(chars);
    }

    private const char RelationMark = '￼';

    [GeneratedRegex("被害人|受害人|證人|告訴人|告發人|檢舉人")]
    private static partial Regex Bystander();

    // A sole defendant's 被告 sentence about another case, an earlier one, or the law in general is not this case's step.
    [GeneratedRegex("另案|該案|他案|前案|前因|前科|規定|條第")]
    private static partial Regex NotThisCase();

    [GeneratedRegex("(?:辯稱|佯稱|謊稱|誆稱|訛稱|詐稱|聲稱|宣稱|供稱|陳稱|堅稱|主張|(?<![下簡名俗職])稱)[^。！？；\n]{0,200}?云云")]
    private static partial Regex Reported();

    [GeneratedRegex("[的之](?:[妻夫女父母兄弟姊姐妹哥岳媳婿姪甥胞兒孫叔伯舅姑嬸]|子(?!公司)|丈夫|先生|太太|老公|老婆|配偶|前妻|前夫|前女友|前男友|男友|友人|助理|秘書|司機|特助|部屬|下屬|員工|同事|合夥人|律師)")]
    internal static partial Regex Relation();

    /// <summary>Every place the sentence writes one of the forms goes on with 的 / 之 and a relation word: it speaks of that relative, not of the person.</summary>
    internal static bool OnlyAsRelation(string sentence, IEnumerable<string> forms)
    {
        var any = false;
        foreach (var f in forms.Where(f => f.Length > 1))
            for (var at = sentence.IndexOf(f, StringComparison.Ordinal); at >= 0; at = sentence.IndexOf(f, at + 1, StringComparison.Ordinal))
            {
                if (Relation().Match(sentence, at + f.Length) is not { Success: true } m || m.Index != at + f.Length) return false;
                any = true;
            }
        return any;
    }

    // A judgment's order (the lines from 主文 to the 事實 / 理由 heading) is written to a fixed form: who is sentenced
    // (處有期徒刑, 拘役, 罰金, 無期徒刑, 死刑, 免刑) and who is 無罪. Only what the order says of the Profile person counts:
    // where they are the subject of a clause (the name opens it, alone or in a list of names: 林美玲、王大明均無罪; never
    // 王大明對林美玲犯詐欺罪 or 原判決關於林美玲部分撤銷), up to a clause that opens with another person's name (林美玲處
    // 有期徒刑一年，王大明無罪). A sentence in any part of the order makes CONVICTED, else an acquittal ACQUITTED; an order
    // that says neither of them (免訴, 公訴不受理) gives nothing, save 上訴駁回 on the person's own appeal (below). Whether the person is a listed defendant (a
    // 被告 line before 主文) is told as well: such a person stands indicted even when the order is not read.
    // ponytail: pending a judgment Golden Case. Not read: an order sentence that goes on about the person without naming
    // them again (又犯…罪，處…), a co-defendant of the same surname, and anything after the order that overturns it (the
    // order wins over the rest of the text). An order with no 事實 / 理由 heading is read for 40 lines at most.
    private static (bool Judgment, LegalStage? Stage, bool Defendant, bool Sole) JudgmentReading(string news, IReadOnlyList<string> names)
    {
        // Full-width letters and digits are read as half-width ones: a name may be written either way (Ａ０５, A05).
        static string HalfWidth(string s) => string.Concat(s.Select(c => c is (>= 'Ａ' and <= 'Ｚ') or (>= 'ａ' and <= 'ｚ') or (>= '０' and <= '９') ? (char)(c - 0xFEE0) : c));
        var lines = HalfWidth(news).Split('\n');
        var start = Array.FindIndex(lines, l => Compact(l) == "主文");
        if (start < 0) return (false, null, false, false);
        var own = names.Take(100).Where(n => n.Length > 1).Select(HalfWidth).ToList();
        var surnames = own.Where(n => CjkText.IsCjk(n[0])).Select(n => n[0]).ToHashSet();
        // The header lists defendants on a 被告 line and on the lines after it, up to the next party's line (辯護人, 上列 …).
        var listed = new List<string>();
        var defendantLines = 0;
        string? head = null;
        foreach (var l in lines.Take(start).Select(Compact))
        {
            if (DefendantLine().IsMatch(l)) head = l;
            else if (OtherPartyLine().IsMatch(l)) head = null;
            if (head is not null && l.Length > 0) defendantLines++;
            if (head is not null && own.Exists(n => l.Contains(n, StringComparison.OrdinalIgnoreCase))) listed.Add(head);
        }
        // The only defendant: one line in all the 被告 blocks, and it is theirs. The text's bare 被告 is then this person.
        var sole = listed.Count == 1 && defendantLines == 1;
        if (sole) own.Add("被告"); // the order of a one-defendant judgment may say 被告無罪 without the name
        var defendant = listed.Count > 0;
        var end = Array.FindIndex(lines, start + 1, l => OrderEnd().IsMatch(Compact(l)));
        var order = string.Join('\n', lines[(start + 1)..Math.Min(end < 0 ? lines.Length : end, start + 1 + MaxOrderLines)]);
        LegalStage? stage = null;
        foreach (var (at, length) in Spans(order, 0, order.Length, SentenceEnds))
        {
            var theirs = new System.Text.StringBuilder();
            foreach (var (from, size) in Spans(order, at, length, ClauseEnds))
            {
                var clause = order.Substring(from, size);
                var named = own.Exists(n => clause.IndexOf(n, StringComparison.OrdinalIgnoreCase) is >= 0 and var i && OrderSubject().IsMatch(clause[..i]));
                if (theirs.Length == 0 && !named) continue;
                if (!named && OrderOpensWithName().Match(clause) is { Success: true } m
                    && CommonSurnames.Contains(m.Value.TrimStart()[0], StringComparison.Ordinal) && !surnames.Contains(m.Value.TrimStart()[0])) break;
                theirs.Append(clause).Append('，');
            }
            var said = theirs.ToString();
            if (OrderSentence().IsMatch(said)) return (true, LegalStage.Convicted, defendant, sole);
            // 原判決關於X沒收部分、Y無罪部分均撤銷 names a part of the judgment below, not an outcome.
            // 陳志明無罪部分，檢察官上訴駁回 upholds the acquittal and stays one.
            if (RevokedAcquittal().Replace(said, "").Contains("無罪", StringComparison.Ordinal)) stage = LegalStage.Acquitted;
        }
        // A defendant appeals a conviction only: 上訴駁回 on the appeal of an 上訴人即被告 leaves that conviction standing.
        if (stage is null && order.Contains("上訴駁回", StringComparison.Ordinal) && listed.Exists(l => l.StartsWith("上訴人即被告", StringComparison.Ordinal) || l.StartsWith("即被告", StringComparison.Ordinal))) // 即被告 opens the line under 上訴人
            stage = LegalStage.Convicted;
        return (true, stage, defendant, sole);
    }

    private const int MaxOrderLines = 40;

    private static string Compact(string s) => string.Concat(s.Where(c => !char.IsWhiteSpace(c)));

    [GeneratedRegex("處(?:有期徒刑|拘役|罰金|無期徒刑|死刑|如(?:本院)?附[表件])|免刑")]
    private static partial Regex OrderSentence();

    [GeneratedRegex("^(?:[一二三四五壹貳參]、)?(?:犯罪)?(?:事實|理由).{0,12}$")]
    private static partial Regex OrderEnd();

    [GeneratedRegex("^(?:上訴人即|上訴人|聲請人即|抗告人即|即)*被告")]
    private static partial Regex DefendantLine();

    [GeneratedRegex("^(?:(?:選任|指定|公設)?辯護人|上列|輔佐人|(?:訴訟|法定)?代理人|公訴人|上訴人|告訴人|參與人|被害人|具保人|聲請人|受刑人|抗告人|檢察官)")]
    private static partial Regex OtherPartyLine();

    // What may stand before the person's name when they are the clause's subject: an item number (一、), 被告, and other
    // names of a list (a published judgment may write them 甲○○ or A05).
    [GeneratedRegex(@"^\s*(?:[一二三四五六七八九十]+、)?(?:(?:上訴人即)?被告)?(?:[\p{IsCJKUnifiedIdeographs}○A-Za-z0-9]{2,4}[、及與])*$")]
    private static partial Regex OrderSubject();

    // Another defendant's part of an order sentence: a name of two to four characters right before what the order says of them.
    [GeneratedRegex(@"^\s*\p{IsCJKUnifiedIdeographs}{2,4}?(?=共同|幫助|犯|無罪|處|均|應|部分|被訴)")]
    private static partial Regex OrderOpensWithName();

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

    // The clause's indictment or dismissal has another person for its object: 檢方已起訴李小四, 對李小四為不起訴處分.
    // Losing the Profile person's own indictment is the worse error, so the object must be a three-character name of a
    // common surname that the text writes at least twice (起訴高層, 起訴官員, 起訴金額 are no names), and the clause must
    // hold no character of the person's own surname (起訴李小四、王男 may include 王大明). 起訴李小四等七人 stays collective.
    // ponytail: a two-character name (李四) or one of a rare surname is not told apart; widen when a real case needs it.
    private static bool IndictsAnotherPerson(string clause, HashSet<char> ownSurnames, string news) =>
        !clause.Any(ownSurnames.Contains) && (OtherPersonObject().Matches(clause).Any(m =>
            CommonSurnames.Contains(m.Groups["name"].Value[0], StringComparison.Ordinal)
            && news.IndexOf(m.Groups["name"].Value, StringComparison.Ordinal) != news.LastIndexOf(m.Groups["name"].Value, StringComparison.Ordinal))
            || OtherPersonJudged().Matches(clause).Any(m => CommonSurnames.Contains(m.Groups["name"].Value[0], StringComparison.Ordinal)));

    // The same for a judgment or a wanted notice, where what follows the name tells it is one, written once or not:
    // 法院另判處李小四有期徒刑一年, 判李小四無罪, 警方通緝的李小四落網.
    // A two-character name counts here too (另判李四無罪); a title does not (判處董事長有期徒刑 may be the person).
    [GeneratedRegex(@"判[決處]?(?<name>\p{IsCJKUnifiedIdeographs}{1,2}[^\P{IsCJKUnifiedIdeographs}長員官人層嫌犯])(?=有期徒刑|無期徒刑|死刑|拘役|罰金|無罪|[0-9０-９一二三四五六七八九十兩]+(?:年|個月|月))|通緝的?(?<name>\p{IsCJKUnifiedIdeographs}{3})(?=落網|到案|歸案|$)")]
    private static partial Regex OtherPersonJudged();

    [GeneratedRegex("(?<![平同相對])等共?[0-9０-９一二兩三四五六七八九十百餘多數]*(?:人|名|嫌|被告)")]
    private static partial Regex Group();

    [GeneratedRegex("涉|嫌|偵辦|調查|逮捕|拘提|約談|羈押|收押|移送|函送|交保|搜索|查獲|到案|被告|共犯|首腦|主謀|成員|車手|起訴|通緝")]
    private static partial Regex Suspicion();

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
            // 提起公訴 is a judgment's word for the indictment, and a judgment names many people: only with the person's name.
            // Tried and left out: reading 對李小四等4人提起公訴 in a report for everyone the text names. On the second dev
            // set it gave the indictment to five people the group did not hold and to three it did.
            Find(found, clause, PublicProsecution(), LegalStage.Indicted, modalEnd);
            Find(found, clause, Conviction(), LegalStage.Convicted, modalEnd);
            Find(found, clause, Acquittal(), LegalStage.Acquitted, modalEnd);
        }
        return found.Count > 0 ? found.MaxBy(f => f.End).Stage : null;
    }

    private static bool Says(string clause, Regex pattern)
    {
        var found = new List<(int, LegalStage)>();
        Find(found, clause, pattern, LegalStage.Unknown, FirstModalEnd(clause));
        return found.Count > 0;
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

    // The name ends the clause or goes on in a list (、及與和); 等 after it makes a group, which is not matched.
    [GeneratedRegex(@"起訴(?<name>\p{IsCJKUnifiedIdeographs}{3})(?=$|[、及與和\s])|對(?<name>\p{IsCJKUnifiedIdeographs}{3})(?:為|作成|予以)?(?:(?:不|緩)起訴|提起公訴)")]
    private static partial Regex OtherPersonObject();

    // Non-backtracking: a backtracking engine rescans to the end from every unclosed opening quote (1M 「 is quadratic).
    [GeneratedRegex("「[^」]*」|『[^』]*』|“[^”]*”", RegexOptions.NonBacktracking)]
    internal static partial Regex Quoted();

    [GeneratedRegex("不起訴|緩起訴|撤回起訴|不予起訴|免予起訴")]
    internal static partial Regex Dismissal();

    [GeneratedRegex("(?<!不|不予|未|未經)提起公訴")]
    internal static partial Regex PublicProsecution();

    // 聲請簡易判決處刑 is the summary form of an indictment.
    [GeneratedRegex("(?<!不|緩|撤回|予)起訴(?!罪名|法條)|聲請簡易判決處刑")]
    internal static partial Regex Indictment();

    [GeneratedRegex("起訴(?:書|範圍|意旨)")]
    private static partial Regex IndictmentPaper();

    [GeneratedRegex("(?<!(?:撤銷|解除|撤回)[^，,]{0,8})通緝")]
    internal static partial Regex WantedNotice();

    // A report also words a sentence as 判 with the penalty itself: 判陳志明有期徒刑三年, 遭判無期徒刑, 重判十五年, 判1年6月.
    // Between 判 and the penalty only 決 / 處 and a name (他, 她, or two to four characters) fit; 審判 (but 一審判他五年,
    // 原審判…), 裁判, 談判, 誤判, 判斷, 判決前, 宣判時間 … are other words, and 判賠 names no penalty.
    [GeneratedRegex(@"判處|判刑|判決有罪|有罪確定|(?<!(?<![一二三更初原])審|[裁談批評誤研預])判(?>[決處]?)(?![前後時斷期])(?:[他她]|\p{IsCJKUnifiedIdeographs}{2,4}?)??(?:有期徒刑|無期徒刑|死刑|拘役|罰金|[0-9０-９一二三四五六七八九十兩]+(?:年|個月|月))")]
    internal static partial Regex Conviction();

    // 認被告犯…罪，處有期徒刑二年: a judgment's own wording, with no 判. Whose sentence it is the clause does not say, so it
    // is read only where the text has one defendant.
    [GeneratedRegex(@"^\s*(?:各|均)?處(?:有期徒刑|拘役|無期徒刑|死刑)")]
    private static partial Regex Sentenced();

    [GeneratedRegex("(?<!無罪)定讞(?!前)")]
    internal static partial Regex Final();

    // 判決無罪, 獲判無罪, 判陳志明無罪, 二審逆轉無罪, 無罪定讞; 堅稱無罪 is a claim and 無罪推定 a presumption.
    // After 判決, 改判, 宣判 or 獲判 anything short may stand before 無罪 (宣判陳志明等三人均無罪); after a bare 判 only a
    // name (判陳志明無罪; never 判陳志明罰金、李四無罪, 難以判斷是否無罪 or 應判無罪).
    [GeneratedRegex(@"(?:判決|改判|宣判|獲判)[^，,、]{0,10}?無罪(?!推定|判決)|(?<!(?<![一二三更初原])審|[裁談批評誤研預應])判(?![斷決])\p{IsCJKUnifiedIdeographs}{0,4}?無罪(?!推定|判決)|(?:[一二三]審|更[一二三]?審|逆轉)[^，,、判]{0,6}?無罪(?!推定|判決)|無罪(?:定讞|確定)")]
    internal static partial Regex Acquittal();
}
