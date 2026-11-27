using Aml.Core.Identity;
using Aml.Core.Screening;
using Aml.Core.Shared;
using static Aml.Core.Shared.EvidenceGroup;

namespace Aml.Core.Scoring;

/// <summary>Identity bands of the scoring rule; MATCH is automatic once the score is the decision (scoring-rule.md).</summary>
public enum IdentityBand { NoMatch, Review, ProbableMatch, Match }

/// <summary>Kind of source a scored value comes from; its reliability factor is in <see cref="ScoringPolicy"/>.</summary>
public enum SourceKind { OfficialList, CourtJudgment, PressRelease, AllowlistedNews, ModelRead }

/// <summary>One rule item: the group or condition it scores, its catalog code, its points (already times reliability) and its source.</summary>
public sealed record ScoreItem(string Of, string Code, decimal Points, SourceKind Source);

/// <summary>Applied: vetoes, caps and holds, each by code. Threshold: the band threshold the total crossed, or the veto code.</summary>
public sealed record IdentityScore(IReadOnlyList<ScoreItem> Items, IReadOnlyList<string> Applied, decimal Total, IdentityBand Band, string? Threshold, string PolicyVersion);

public sealed record ConditionScore(IReadOnlyList<ScoreItem> Items, IReadOnlyList<string> Applied, decimal Total, ConditionStatus Band, string? Threshold, string PolicyVersion);

/// <summary>The PROPOSED numbers of scoring-rule.md#proposal-table, not approved by the user: for trial calculation only.</summary>
public sealed record ScoringPolicy
{
    public static readonly ScoringPolicy Proposed = new();

    public string Version { get; init; } = "score-proposal-0.3";
    public decimal Document { get; init; } = 40;
    public decimal DobDay { get; init; } = 25;
    public decimal DobMonth { get; init; } = 15;
    public decimal DobYear { get; init; } = 8;
    public decimal DobWithinTolerance { get; init; } = 8;
    public decimal AddressHouseNumber { get; init; } = 15;
    public decimal AddressDistrict { get; init; } = 5;
    public decimal Association { get; init; } = 15;
    public decimal OtherIdentifier { get; init; } = 5;
    public decimal EntityIdentifier { get; init; } = 40;
    public decimal DocumentConflict { get; init; } = -40;
    public decimal StatedDifference { get; init; } = -8;
    public decimal NativeNameConflict { get; init; } = -15;
    public decimal Match { get; init; } = 60;
    public decimal Probable { get; init; } = 30;
    public decimal ProbableExact { get; init; } = 8;
    public decimal NoMatch { get; init; } = -25;
    public decimal ConditionBasis { get; init; } = 100;
    public decimal UnmappedCategory { get; init; } = 40;
    public decimal Hit { get; init; } = 60;
    public decimal Unknown { get; init; } = 30;

    public IReadOnlyDictionary<SourceKind, decimal> Reliability { get; init; } = new Dictionary<SourceKind, decimal>
    {
        [SourceKind.OfficialList] = 1m,
        [SourceKind.CourtJudgment] = 1m,
        [SourceKind.PressRelease] = 0.8m,
        [SourceKind.AllowlistedNews] = 0.6m,
        [SourceKind.ModelRead] = 0m,
    };

    public IReadOnlyDictionary<LegalStage, decimal> Stage { get; init; } = new Dictionary<LegalStage, decimal>
    {
        [LegalStage.Convicted] = 100,
        [LegalStage.Wanted] = 90,
        [LegalStage.Indicted] = 80,
        [LegalStage.Alleged] = 50,
        [LegalStage.Acquitted] = 10,
        [LegalStage.Dismissed] = 10,
    };
}

/// <summary>
/// The scoring rule (scoring-rule.md) as a deterministic sum of rule items over what the identity pipeline and the source
/// conditions already produced. Measurement only: the stage 3 table decides, and nothing here reaches a Case.
/// </summary>
public static class ScoringRule
{
    public const string DobDay = "SCORE_DOB_DAY";
    public const string DobMonth = "SCORE_DOB_MONTH";
    public const string DobYear = "SCORE_DOB_YEAR";
    public const string Address = "SCORE_ADDRESS";
    public const string Association = "SCORE_ASSOCIATION";
    public const string OtherIdentifierAgreement = "SCORE_OTHER_IDENTIFIER";
    public const string BandMatch = "SCORE_BAND_MATCH";
    public const string BandProbableMatch = "SCORE_BAND_PROBABLE_MATCH";
    public const string BandNoMatch = "SCORE_BAND_NO_MATCH";
    public const string HeldPendingValidation = "SCORE_HELD_PENDING_VALIDATION";
    public const string CappedProfileConflict = "SCORE_CAPPED_PROFILE_CONFLICT";
    public const string CappedGenderConflict = "SCORE_CAPPED_GENDER_CONFLICT";
    public const string CappedNativeNameConflict = "SCORE_CAPPED_NATIVE_NAME_CONFLICT";
    public const string CappedEntityRecord = "SCORE_CAPPED_ENTITY_RECORD";
    public const string ConditionBasis = "SCORE_CONDITION_BASIS";
    public const string ConditionStage = "SCORE_CONDITION_STAGE";
    public const string ConditionStageUnknown = "SCORE_CONDITION_STAGE_UNKNOWN";
    public const string ConditionBand = "SCORE_CONDITION_BAND";

    /// <summary>
    /// Identity score of one pair. A validated hard conflict is the veto (the pipeline's NO_MATCH); a pending or downgraded
    /// one holds the pair at REVIEW whatever the sum. <paramref name="entityRecord"/>: the Profile is not a Person record.
    /// </summary>
    /// <param name="record">The kind of source the record's own fields come from: a list (1.0), or the page or judgment a
    /// person was read from, whose fields count at that source's factor.</param>
    /// <param name="text">The kind of the text the stated values (age, birth date next to the name) are read from: attached
    /// news by default, a press release or a court text at its own factor, a page at the page's.</param>
    public static IdentityScore Identity(IdentityResult r, bool entityRecord, ScoringPolicy? policy = null,
        SourceKind record = SourceKind.OfficialList, SourceKind text = SourceKind.AllowlistedNews)
    {
        var p = policy ?? ScoringPolicy.Proposed;
        var e = r.Evidence;
        var items = e.Values.OrderBy(v => v.Group).Select(v => Item(v, p, record, text)).OfType<ScoreItem>().ToList();
        var total = items.Sum(i => i.Points);
        IdentityScore Result(IdentityBand band, string? threshold, IReadOnlyList<string> applied) => new(items, applied, total, band, threshold, p.Version);

        if (r.HardConflict is { } hard)
            return r.Decision == IdentityDecision.NoMatch
                ? Result(IdentityBand.NoMatch, hard, [hard])
                : Result(IdentityBand.Review, null, [hard, .. r.ReasonCodes.Where(c => c == ReasonCodes.SourceQualityDowngrade), HeldPendingValidation]);

        bool Is(EvidenceGroup g, ComparisonStatus s) => e.GetValueOrDefault(g)?.Status == s;
        var prerequisitesExact = Is(Name, ComparisonStatus.Exact) && Is(EvidenceGroup.Gender, ComparisonStatus.Exact) && Is(Nationality, ComparisonStatus.Exact)
            && !new[] { Document, Employment }.Any(g => IdentityDecisionPolicy.IsUnsettled(e.GetValueOrDefault(g)));
        var (band, threshold) = total >= p.Match ? (IdentityBand.Match, BandMatch)
            : total >= p.Probable || (prerequisitesExact && total >= p.ProbableExact) ? (IdentityBand.ProbableMatch, BandProbableMatch)
            : total <= p.NoMatch ? (IdentityBand.NoMatch, BandNoMatch)
            : (IdentityBand.Review, (string?)null);

        var applied = new List<string>();
        if (band == IdentityBand.NoMatch && items.Any(i => i.Points < 0 && !HardConflictGate.ValidatedReasonCodes.Contains(i.Code)))
        {
            (band, threshold) = (IdentityBand.Review, null);
            applied.Add(HeldPendingValidation);
        }

        var caps = new List<(IdentityBand Max, string Code)>();
        if (entityRecord) caps.Add((e.GetValueOrDefault(EntityIdentifier)?.IsSupport == true ? IdentityBand.ProbableMatch : IdentityBand.Review, CappedEntityRecord));
        // A DOB conflict is the validated veto above; the document conflict is still pending.
        if (Is(Document, ComparisonStatus.Conflict)) caps.Add((IdentityBand.Review, CappedProfileConflict));
        if (Is(EvidenceGroup.Gender, ComparisonStatus.Conflict) || Is(StatedGender, ComparisonStatus.Conflict)) caps.Add((IdentityBand.Review, CappedGenderConflict));
        // As stage 3: an agreeing document keeps its band (row 2), the conflict then only listed as its item.
        if (NativeNames.In(e.GetValueOrDefault(Name)) && e.GetValueOrDefault(Document)?.IsSupport != true) caps.Add((IdentityBand.Review, CappedNativeNameConflict));
        var binding = caps.Where(c => c.Max < band).ToList();
        if (binding.Count > 0) band = binding.Min(c => c.Max);
        applied.AddRange(binding.Select(c => c.Code));
        return Result(band, threshold, applied);
    }

    // Profile fields count at the record's source factor, stated values at their text's. A DOB conflict is a veto (validated
    // hard conflict), outside the sum, so it gives no item. The advisory AGE_IMPLAUSIBLE_AT_DOCUMENT gives none either (0):
    // a weight for it would be a new unapproved weight, and it is a hint for the reviewer, not evidence to sum.
    private static ScoreItem? Item(Evidence v, ScoringPolicy p, SourceKind field, SourceKind news)
    {
        (decimal Points, string Code, SourceKind Source)? x = (v.Group, v.Direction, v.Strength) switch
        {
            (Document, Direction.Support, _) => (p.Document, ReasonCodes.UniqueIdentifierAgreement, field),
            (Document, Direction.Conflict, _) => (p.DocumentConflict, ReasonCodes.DocumentIdentityConflict, field),
            (DateOfBirth, Direction.Support, _) when v.Detail == ReasonCodes.DobAgreesWithinTolerance => (p.DobWithinTolerance, ReasonCodes.DobAgreesWithinTolerance, field),
            (DateOfBirth, Direction.Support, Strength.High) => (p.DobDay, DobDay, field),
            (DateOfBirth, Direction.Support, Strength.Medium) => (p.DobMonth, DobMonth, field),
            (DateOfBirth, Direction.Support, _) => (p.DobYear, DobYear, field),
            (EvidenceGroup.Address, Direction.Support, >= Strength.Medium) => (p.AddressHouseNumber, Address, field),
            (EvidenceGroup.Address, Direction.Support, _) => (p.AddressDistrict, Address, field),
            (Employment or CompanyAssociation or Relationship, Direction.Support, _) => (p.Association, Association, field),
            (OtherIdentifier, Direction.Support, _) => (p.OtherIdentifier, OtherIdentifierAgreement, field),
            (EntityIdentifier, Direction.Support, _) => (p.EntityIdentifier, ReasonCodes.EntityIdentifierAgreement, field),
            (StatedBirthDate, Direction.Support, >= Strength.Medium) => (p.DobMonth, ReasonCodes.NewsBirthDateAgrees, news),
            (StatedBirthDate, Direction.Support, _) => (p.DobYear, ReasonCodes.NewsBirthDateAgrees, news),
            (StatedAge or StatedBirthDate, Direction.Conflict, _) => (p.StatedDifference, v.Detail!, news),
            // Pending, never validated: the NO_MATCH band it could reach is held at REVIEW, and the cap holds it there anyway.
            (Name, _, _) when NativeNames.In(v) => (p.NativeNameConflict, ReasonCodes.NativeNameConflict, field),
            _ => null,
        };
        return x is var (points, code, source) ? new(v.Group.ToString(), code, points * p.Reliability[source], source) : null;
    }

    /// <summary>
    /// Condition score of a list Profile's condition: the basis as today's determination gives it (HIT 100, UNKNOWN 40,
    /// NOT_HIT 0) times the list's reliability.
    /// </summary>
    public static ConditionScore Condition(SourceCondition c, ScoringPolicy? policy = null)
    {
        var p = policy ?? ScoringPolicy.Proposed;
        var (points, code) = c.Status switch
        {
            ConditionStatus.Hit => (p.ConditionBasis, ConditionBasis),
            ConditionStatus.NotHit => (0m, ConditionBasis),
            _ => (p.UnmappedCategory, c.Basis == Screening.ConditionBasis.Unmapped ? SourceConditions.UnmappedCategory : AssessmentPolicy.SourceConditionUnknown),
        };
        return Banded(new(c.Type.ToString(), code, points * p.Reliability[SourceKind.OfficialList], SourceKind.OfficialList), p);
    }

    /// <summary>
    /// Condition score of a person read from a page or judgment: the stage the vote gives, times the source's reliability;
    /// not a suspect scores 0. A stage UNKNOWN (or no answer, null) is UNKNOWN by rule, outside the sum.
    /// </summary>
    public static ConditionScore SourcePerson(LegalStage? stage, bool suspect, SourceKind source, ScoringPolicy? policy = null)
    {
        var p = policy ?? ScoringPolicy.Proposed;
        if (stage is not { } s || !p.Stage.TryGetValue(s, out var points))
            return new([], [ConditionStageUnknown], 0, ConditionStatus.Unknown, null, p.Version);
        return Banded(new(s.ToString(), ConditionStage, (suspect ? points : 0) * p.Reliability[source], source), p);
    }

    private static ConditionScore Banded(ScoreItem item, ScoringPolicy p)
    {
        var band = item.Points >= p.Hit ? ConditionStatus.Hit : item.Points >= p.Unknown ? ConditionStatus.Unknown : ConditionStatus.NotHit;
        return new([item], [], item.Points, band, band == ConditionStatus.NotHit ? null : ConditionBand, p.Version);
    }
}
