using System.ComponentModel;
using Aml.Core.Identity;
using Aml.Core.Shared;
using Aml.Core.Subjects;

namespace Aml.Core.Screening;

public enum Recommendation { OnList, NotOnList }

/// <summary>
/// Advisory Article 22 recommendation (review-and-disposition.md, ADR-0017, ADR-0020). It never changes the identity
/// decision or the source conditions.
/// </summary>
public sealed record DispositionRecommendation(
    [property: Description("建議：ON_LIST 或 NOT_ON_LIST；無法判定時為 null")] Recommendation? Recommendation,
    [property: Description("第 22 條理由碼")] IReadOnlyList<string> ReasonCodes,
    [property: Description("尚未執行的查核事項")] IReadOnlyList<string> PendingChecks)
{
    [property: Description("永遠為 true：建議僅供參考")]
    public bool Advisory { get; init; } = true;
}

public static class DispositionPolicy
{
    public const string ForeignPepPresumption = "ART22_FOREIGN_PEP_PRESUMPTION";
    public const string AdverseUnrelated = "ART22_ADVERSE_UNRELATED";
    public const string IdentityNoMatch = "ART22_IDENTITY_NO_MATCH";
    public const string NotDetermined = "ART22_NOT_DETERMINED";
    public const string Ruling20261014MayApply = "ART22_RULING_20261014_MAY_APPLY";

    public const string ConfirmNationality = "CONFIRM_NATIONALITY";
    public const string EmploymentRelevance = "EMPLOYMENT_RELEVANCE";
    public const string RegionRelevance = "REGION_RELEVANCE";
    public const string IdentityCheck = "IDENTITY_CHECK";
    public const string JudgmentPartyIdentity = "JUDGMENT_PARTY_IDENTITY";
    public const string FormerPepInfluence = "FORMER_PEP_INFLUENCE";

    private const string TaiwanCode = "TW";

    private enum Nationality { Taiwan, Foreign, Unknown }

    public static DispositionRecommendation Recommend(ScreenedSubject s, SourceProfile p, IReadOnlyList<SourceCondition> conditions,
        IdentityResult identity, bool hasNews, DateOnly? asOf = null)
    {
        var pending = PendingChecks(p, conditions, hasNews, asOf ?? DateOnly.FromDateTime(DateTime.UtcNow));
        var reasons = new List<string>();

        // Article 22 ②
        var counted = conditions.Where(c => !SourceConditions.IsUnrelatedAdverseMedia(c)).ToList();
        if (counted.Count < conditions.Count) reasons.Add(AdverseUnrelated);

        if (identity.Decision == IdentityDecision.NoMatch)
            return new(Recommendation.NotOnList, [.. reasons, IdentityNoMatch], pending);

        // Article 22 ①, on nationality-labelled fields only.
        if (RaisesPossiblePep(conditions, counted))
        {
            var (subject, profile) = (Classify(s.Nationalities, s.Findings, "國籍"), Classify(p.Citizenship, p.Findings, "Citizenship"));
            if (subject == Nationality.Foreign && profile == Nationality.Foreign)
                return new(Recommendation.OnList, [.. reasons, ForeignPepPresumption], pending);
            if (subject != Nationality.Taiwan && profile != Nationality.Taiwan)
                pending = [ConfirmNationality, .. pending];
        }

        if (reasons.Count > 0 && counted.All(c => c.Status == ConditionStatus.NotHit))
            return new(Recommendation.NotOnList, reasons, pending);

        if (MayFallUnderRuling20261014(identity)) reasons.Add(Ruling20261014MayApply);

        // Article 22 ③
        return new(null, [.. reasons, NotDetermined], pending);
    }

    private static bool RaisesPossiblePep(IReadOnlyList<SourceCondition> all, IReadOnlyList<SourceCondition> counted)
    {
        var pepFlagSaysNo = all.Any(c => c.Type == ListType.Pep && c.Status == ConditionStatus.NotHit && c.Basis == ConditionBasis.Flag);
        bool IsUnmappedCategory(SourceCondition c) => c.Type == ListType.Unknown && c.ReasonCodes.Contains(SourceConditions.UnmappedCategory);
        return counted.Any(c => c.Status != ConditionStatus.NotHit && (c.Type == ListType.Pep || (IsUnmappedCategory(c) && !pepFlagSaysNo)));
    }

    // Flagged, not applied: the 2026-10-14 ruling also needs a likelihood below 65%, and there is none (GC-12, open question).
    private static bool MayFallUnderRuling20261014(IdentityResult identity) =>
        identity.Stage3Row == Stage3Rows.NoSupport && identity.ReasonCodes.Contains(ReasonCodes.NoBilateralDirectIdentifier);

    private static Nationality Classify(IReadOnlyList<string> codes, IReadOnlyList<string> findings, string field)
    {
        var unmappedToCountry = findings.Any(f => f.StartsWith($"{Findings.Ambiguous}:{field}", StringComparison.Ordinal));
        var handBuiltNonAlpha2 = codes.Any(c => c.Length != 2);
        return codes.Count == 0 || handBuiltNonAlpha2 || unmappedToCountry ? Nationality.Unknown
            : codes.Contains(TaiwanCode) ? Nationality.Taiwan
            : Nationality.Foreign;
    }

    private static List<string> PendingChecks(SourceProfile p, IReadOnlyList<SourceCondition> conditions, bool hasNews, DateOnly today)
    {
        bool RaisedOrUnmapped(ListType t) => conditions.Any(c => c.Status != ConditionStatus.NotHit && (c.Type == t || c.Type == ListType.Unknown));
        bool Raised(ListType t) => conditions.Any(c => c.Type == t && c.Status != ConditionStatus.NotHit);
        // 認定標準第 5、8 條: a former PEP's influence, also through family and associates, is assessed with no fixed period.
        var formerPepInfluence = (Raised(ListType.Pep) && SourceConditions.IsFormerPep(p, ownRoles: true, today))
            || (Raised(ListType.Rca) && SourceConditions.IsFormerPep(p, ownRoles: false, today));
        var pepHitWithoutBirthDate = conditions.Any(c => c.Type == ListType.Pep && c.Status == ConditionStatus.Hit)
            && conditions.All(c => c.Type != ListType.Rca) && p.DatesOfBirth.Count == 0;
        var isPerson = p.RecordType.Equals("Person", StringComparison.OrdinalIgnoreCase);

        var checks = new List<string>();
        if (RaisedOrUnmapped(ListType.Pep)) checks.Add(EmploymentRelevance);
        if (formerPepInfluence) checks.Add(FormerPepInfluence);
        if (RaisedOrUnmapped(ListType.AdverseMedia)) checks.Add(RegionRelevance);
        if (pepHitWithoutBirthDate) checks.Add(IdentityCheck);
        if (isPerson && (hasNews || RaisedOrUnmapped(ListType.AdverseMedia))) checks.Add(JudgmentPartyIdentity);
        return checks;
    }
}
