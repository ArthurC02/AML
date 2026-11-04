using Aml.Core.Identity;
using Aml.Core.Shared;
using Aml.Core.Subjects;

namespace Aml.Core.Screening;

public enum Recommendation { OnList, NotOnList }

/// <summary>
/// Advisory Article 22 recommendation (review-and-disposition.md, ADR-0017, ADR-0020). Null when no rule resolves the
/// pair (③); the pending checks are listed instead. It never changes the identity decision or the source conditions.
/// </summary>
public sealed record DispositionRecommendation(Recommendation? Recommendation, IReadOnlyList<string> ReasonCodes, IReadOnlyList<string> PendingChecks);

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

    private enum Nationality { Taiwan, Foreign, Unknown }

    public static DispositionRecommendation Recommend(ScreenedSubject s, SourceProfile p, IReadOnlyList<SourceCondition> conditions,
        IdentityResult identity, bool hasNews)
    {
        var pending = PendingChecks(p, conditions, hasNews);
        var reasons = new List<string>();

        // ②: only the unrelated ADVERSE_MEDIA condition is set aside; other conditions still count.
        var remaining = conditions.Where(c => !(c.Type == ListType.AdverseMedia && c.AmlRelevance == AmlRelevance.Unrelated)).ToList();
        if (remaining.Count < conditions.Count) reasons.Add(AdverseUnrelated);

        if (identity.Decision == IdentityDecision.NoMatch)
            return new(Recommendation.NotOnList, [.. reasons, IdentityNoMatch], pending);

        // ①: foreign PEP presumption, on nationality-labelled fields only.
        if (remaining.Any(c => c.Status != ConditionStatus.NotHit && c.Type is ListType.Pep or ListType.Unknown))
        {
            var (subject, profile) = (Classify(s.Nationalities), Classify(p.Citizenship));
            if (subject == Nationality.Foreign && profile == Nationality.Foreign)
                return new(Recommendation.OnList, [.. reasons, ForeignPepPresumption], pending);
            if (subject != Nationality.Taiwan && profile != Nationality.Taiwan)
                pending = [ConfirmNationality, .. pending];
        }

        if (reasons.Count > 0 && remaining.All(c => c.Status == ConditionStatus.NotHit))
            return new(Recommendation.NotOnList, reasons, pending);

        // The 2026-10-14 ruling (no bilateral direct identifier and likelihood below 65% → not on list) is flagged, not
        // applied: there is no likelihood, and GC-12 (row 6, no direct identifier) keeps no recommendation (open question).
        if (identity.Stage3Row == 6 && identity.ReasonCodes.Contains(ReasonCodes.NoBilateralDirectIdentifier)) reasons.Add(Ruling20261014MayApply);

        // ③: nothing resolves the pair.
        return new(null, [.. reasons, NotDetermined], pending);
    }

    // A value that is not a two-letter code did not map unambiguously to a country (Normalize.Country).
    private static Nationality Classify(IReadOnlyList<string> codes) =>
        codes.Count == 0 || codes.Any(c => c.Length != 2) ? Nationality.Unknown
        : codes.Contains("TW") ? Nationality.Taiwan
        : Nationality.Foreign;

    /// <summary>Checks the verification-record triggers start; an unmapped list type leaves the relevance checks open.</summary>
    private static List<string> PendingChecks(SourceProfile p, IReadOnlyList<SourceCondition> conditions, bool hasNews)
    {
        bool Open(ListType t) => conditions.Any(c => c.Status != ConditionStatus.NotHit && (c.Type == t || c.Type == ListType.Unknown));
        var checks = new List<string>();
        if (Open(ListType.Pep)) checks.Add(EmploymentRelevance);
        if (Open(ListType.AdverseMedia)) checks.Add(RegionRelevance);
        if (conditions.Any(c => c.Type == ListType.Pep && c.Status == ConditionStatus.Hit)
            && conditions.All(c => c.Type != ListType.Rca) && p.DatesOfBirth.Count == 0)
            checks.Add(IdentityCheck);
        if (p.RecordType.Equals("Person", StringComparison.OrdinalIgnoreCase) && (hasNews || Open(ListType.AdverseMedia)))
            checks.Add(JudgmentPartyIdentity);
        return checks;
    }
}
