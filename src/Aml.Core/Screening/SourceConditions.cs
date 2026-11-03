using Aml.Core.Subjects;

namespace Aml.Core.Screening;

/// <summary>Bank list types (ADR-0016). UNKNOWN: an unmapped source category, left for the reviewer.</summary>
public enum ListType { Sanction, Pep, Rca, Sie, OfficialWantedList, OtherOfficialList, AdverseMedia, BankInternalList, Unknown }

public enum ConditionStatus { Hit, NotHit, Unknown }

public enum ConditionBasis { Flag, CategoryMapping, Unmapped }

public enum LegalStage { Unknown, Alleged, Indicted, Wanted }

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
public static class SourceConditions
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

    // ponytail: flags (PEP/黑名單/負面新聞) are not parsed yet (column order pending), so only category mapping runs.
    public static IReadOnlyList<SourceCondition> Determine(SourceProfile profile, bool isRca, string? news)
    {
        var types = new List<ListType>();
        var risks = new List<string>();
        foreach (var c in profile.Categories)
        {
            var type = MapCategory(c);
            if (!types.Contains(type)) types.Add(type);
            if (type == ListType.AdverseMedia && c.Subcategory is { } sub && SipRiskCategories.TryGetValue(sub, out var r) && !risks.Contains(r))
                risks.Add(r);
        }
        if (types.Contains(ListType.AdverseMedia))
            foreach (var k in profile.Keywords)
                if (KeywordRiskCategories.TryGetValue(k, out var r) && !risks.Contains(r)) risks.Add(r);
        if (isRca && !types.Contains(ListType.Rca)) types.Add(ListType.Rca);

        return types.Select(t => t switch
        {
            ListType.AdverseMedia => Hit(t) with { RiskCategories = risks, LegalStage = LegalStageFromNews(news), AmlRelevance = AmlRelevance.Uncertain },
            ListType.OtherOfficialList when IsActive(profile.OfficialList, "Wanted", "通緝") =>
                Hit(ListType.OfficialWantedList) with { LegalStage = LegalStage.Wanted },
            ListType.Sanction => Sanction(profile),
            ListType.Pep => Hit(t) with { PepType = PepType(profile) },
            ListType.Rca => Hit(t) with { PepType = "RCA_UNCONFIRMED" },
            ListType.Unknown => new SourceCondition(t, ConditionStatus.Unknown, ConditionBasis.Unmapped, [], LegalStage.Unknown, null, PolicyVersion, [UnmappedCategory]),
            _ => Hit(t),
        }).ToList();
    }

    private static SourceCondition Hit(ListType t) =>
        new(t, ConditionStatus.Hit, ConditionBasis.CategoryMapping, [], LegalStage.Unknown, null, PolicyVersion, []);

    /// <summary>A generic Active Profile is not an active sanction measure: HIT needs an active, named measure.</summary>
    private static SourceCondition Sanction(SourceProfile p) =>
        p.OfficialList is { } e && e.Status.Equals("Active", StringComparison.OrdinalIgnoreCase)
            ? Hit(ListType.Sanction) with { Program = e.List, Jurisdiction = e.Authority }
            : Hit(ListType.Sanction) with { Status = ConditionStatus.Unknown, ReasonCodes = [SanctionMeasureNotConfirmed] };

    // ponytail: only FORMER_PEP is read from the subcategory; PEP_FAMILY / PEP_CLOSE_ASSOCIATE need approved source fields (GC-04).
    private static string? PepType(SourceProfile p) =>
        p.Categories.Any(c => c.Category.StartsWith("Politically Exposed", StringComparison.OrdinalIgnoreCase)
                              && c.Subcategory?.Contains("Former", StringComparison.OrdinalIgnoreCase) == true)
            ? "FORMER_PEP" : null;

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
    private static LegalStage LegalStageFromNews(string? news) => news switch
    {
        null => LegalStage.Unknown,
        _ when news.Contains("起訴", StringComparison.Ordinal) => LegalStage.Indicted,
        _ when news.Contains("偵辦", StringComparison.Ordinal) || news.Contains("調查", StringComparison.Ordinal) => LegalStage.Alleged,
        _ => LegalStage.Unknown,
    };
}
