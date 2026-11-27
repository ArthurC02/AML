using System.Globalization;
using Aml.Core.Identity;
using Aml.Core.Scoring;
using Aml.Core.Screening;
using Aml.Core.Shared;
using static Aml.Core.Scoring.IdentityBand;
using static Aml.Core.Shared.ComparisonStatus;

namespace Aml.Tests.Unit.Scoring;

/// <summary>The PROPOSED table of docs/feature/scoring-rule.md, cell by cell; expected values are the spec's.</summary>
public class ScoringRuleTests
{
    private static readonly Dictionary<string, Evidence> Named = new()
    {
        ["DocAgree"] = new(EvidenceGroup.Document, Direction.Support, Exact, Strength.Unique),
        ["DocConflict"] = new(EvidenceGroup.Document, Direction.Conflict, Conflict, Strength.None, ReasonCodes.DocumentIdentityConflict),
        ["DocNotComparable"] = new(EvidenceGroup.Document, Direction.Neutral, Uncertain, Strength.None, EvidenceDetails.NotComparable),
        ["DobDay"] = new(EvidenceGroup.DateOfBirth, Direction.Support, Exact, Strength.High),
        ["DobMonth"] = new(EvidenceGroup.DateOfBirth, Direction.Support, Partial, Strength.Medium),
        ["DobYear"] = new(EvidenceGroup.DateOfBirth, Direction.Support, Partial, Strength.Low, ReasonCodes.BirthYearMatchFullDateNotComparable),
        ["DobTolerance"] = new(EvidenceGroup.DateOfBirth, Direction.Support, Partial, Strength.Low, ReasonCodes.DobAgreesWithinTolerance),
        ["DobFullConflict"] = new(EvidenceGroup.DateOfBirth, Direction.Conflict, Conflict, Strength.None, ReasonCodes.FullDobConflict),
        ["DobConflict"] = new(EvidenceGroup.DateOfBirth, Direction.Conflict, Conflict, Strength.None, ReasonCodes.DobConflict),
        ["AddrHouse"] = new(EvidenceGroup.Address, Direction.Support, Exact, Strength.Medium),
        ["AddrDistrict"] = new(EvidenceGroup.Address, Direction.Support, Partial, Strength.Low),
        ["AddrSoft"] = new(EvidenceGroup.Address, Direction.Neutral, Uncertain, Strength.None, EvidenceDetails.SoftDifference),
        ["Employer"] = new(EvidenceGroup.Employment, Direction.Support, Exact, Strength.Medium, EvidenceDetails.SameOrganizationSamePeriod),
        ["EmployerUnsettled"] = new(EvidenceGroup.Employment, Direction.Neutral, Uncertain, Strength.None, EvidenceDetails.DifferentOrganization),
        ["Company"] = new(EvidenceGroup.CompanyAssociation, Direction.Support, Exact, Strength.Low),
        ["Relative"] = new(EvidenceGroup.Relationship, Direction.Support, Exact, Strength.Medium),
        ["OtherId"] = new(EvidenceGroup.OtherIdentifier, Direction.Support, Exact, Strength.Low, EvidenceDetails.SameIdentifierNatureUnconfirmed),
        ["EntityId"] = new(EvidenceGroup.EntityIdentifier, Direction.Support, Exact, Strength.Unique),
        ["AgeDiff"] = new(EvidenceGroup.StatedAge, Direction.Conflict, Conflict, Strength.None, ReasonCodes.AgeDifferenceAtReport),
        ["NewsDobDiff"] = new(EvidenceGroup.StatedBirthDate, Direction.Conflict, Conflict, Strength.None, ReasonCodes.NewsBirthDateDifference),
        ["NewsDobFull"] = new(EvidenceGroup.StatedBirthDate, Direction.Support, Exact, Strength.Medium, ReasonCodes.NewsBirthDateAgrees),
        ["NewsDobYear"] = new(EvidenceGroup.StatedBirthDate, Direction.Support, Partial, Strength.Low, ReasonCodes.NewsBirthDateAgrees),
        ["NewsGenderDiff"] = new(EvidenceGroup.StatedGender, Direction.Conflict, Conflict, Strength.None, ReasonCodes.NewsGenderDifference),
        ["GenderMissing"] = new(EvidenceGroup.Gender, Direction.Prerequisite, SubjectMissing),
        ["GenderConflict"] = new(EvidenceGroup.Gender, Direction.Prerequisite, Conflict, Strength.None, ReasonCodes.GenderConflict),
        ["NameNative"] = new(EvidenceGroup.Name, Direction.Prerequisite, Exact, Strength.None, ReasonCodes.NativeNameConflict),
        ["NameNativeOrder"] = new(EvidenceGroup.Name, Direction.Prerequisite, Exact, Strength.None, "NAME_ORDER_VARIANT；" + ReasonCodes.NativeNameConflict),
    };

    /// <summary>Name, gender and nationality EXACT (0 points), then the named evidence, a later one replacing its group.</summary>
    private static IdentityResult Pair(string[] evidence, string? hard = null, IdentityDecision decision = IdentityDecision.Review, params string[] reasons)
    {
        var e = new[] { EvidenceGroup.Name, EvidenceGroup.Gender, EvidenceGroup.Nationality }
            .ToDictionary(g => g, g => new Evidence(g, Direction.Prerequisite, Exact));
        foreach (var name in evidence) e[Named[name].Group] = Named[name];
        return new IdentityResult(e, hard, null, decision, reasons);
    }

    private static decimal D(string s) => decimal.Parse(s, CultureInfo.InvariantCulture);

    [Theory]
    // A list record's own fields count at the list's 1.0; a record read from a page or judgment at that source's factor.
    [InlineData("DobYear", SourceKind.OfficialList, SourceKind.AllowlistedNews, "8")]
    [InlineData("DobYear", SourceKind.AllowlistedNews, SourceKind.AllowlistedNews, "4.8")]
    [InlineData("OtherId", SourceKind.CourtJudgment, SourceKind.CourtJudgment, "5")]
    // Stated values count at their text's factor: news 0.6, a press release 0.8, a court text 1.0, a model's reading 0.
    [InlineData("NewsDobFull", SourceKind.OfficialList, SourceKind.AllowlistedNews, "9.0")]
    [InlineData("NewsDobFull", SourceKind.OfficialList, SourceKind.PressRelease, "12.0")]
    [InlineData("NewsDobFull", SourceKind.OfficialList, SourceKind.CourtJudgment, "15")]
    [InlineData("NewsDobDiff", SourceKind.AllowlistedNews, SourceKind.ModelRead, "0")]
    public void Each_item_counts_at_the_factor_of_the_source_its_value_comes_from(string evidence, SourceKind record, SourceKind text, string points)
    {
        var item = Assert.Single(ScoringRule.Identity(Pair([evidence]), entityRecord: false, record: record, text: text).Items);
        Assert.Equal((D(points), Named[evidence].Group is EvidenceGroup.StatedBirthDate ? text : record), (item.Points, item.Source));
    }

    [Theory]
    [InlineData("DocAgree", "UNIQUE_IDENTIFIER_AGREEMENT", "40")]
    [InlineData("DobDay", "SCORE_DOB_DAY", "25")]
    [InlineData("DobMonth", "SCORE_DOB_MONTH", "15")]
    [InlineData("DobYear", "SCORE_DOB_YEAR", "8")]
    [InlineData("AddrHouse", "SCORE_ADDRESS", "15")]
    [InlineData("AddrDistrict", "SCORE_ADDRESS", "5")]
    [InlineData("Employer", "SCORE_ASSOCIATION", "15")]
    [InlineData("Company", "SCORE_ASSOCIATION", "15")]
    [InlineData("Relative", "SCORE_ASSOCIATION", "15")]
    [InlineData("OtherId", "SCORE_OTHER_IDENTIFIER", "5")]
    [InlineData("EntityId", "ENTITY_IDENTIFIER_AGREEMENT", "40")]
    [InlineData("DobTolerance", "DOB_AGREES_WITHIN_TOLERANCE", "8")]       // as a birth year agreeing
    [InlineData("DobFullConflict", null, "0")]                            // the validated veto, outside the sum
    [InlineData("DobConflict", null, "0")]
    [InlineData("DocConflict", "DOCUMENT_IDENTITY_CONFLICT", "-40")]
    [InlineData("AgeDiff", "AGE_DIFFERENCE_AT_REPORT", "-4.8")]          // −8 × news 0.6
    [InlineData("NewsDobDiff", "NEWS_BIRTH_DATE_DIFFERENCE", "-4.8")]
    [InlineData("NewsDobFull", "NEWS_BIRTH_DATE_AGREES", "9.0")]          // MEDIUM 15 × 0.6
    [InlineData("NewsDobYear", "NEWS_BIRTH_DATE_AGREES", "4.8")]          // LOW 8 × 0.6
    [InlineData("AddrSoft", null, "0")]                                   // a difference is never an item
    [InlineData("EmployerUnsettled", null, "0")]
    [InlineData("GenderMissing", null, "0")]                              // prerequisites score 0
    [InlineData("NameNative", "NATIVE_NAME_CONFLICT", "-15")]             // romanised names agree, Chinese-character names differ
    [InlineData("NameNativeOrder", "NATIVE_NAME_CONFLICT", "-15")]        // beside a relaxation finding
    public void Each_evidence_cell_gives_its_item(string evidence, string? code, string points)
    {
        var s = ScoringRule.Identity(Pair([evidence]), entityRecord: false);
        Assert.Equal(code is null ? [] : new[] { code }, s.Items.Select(i => i.Code));
        Assert.Equal(D(points), s.Total);
        Assert.Equal("score-proposal-0.3", s.PolicyVersion);
    }

    [Theory]
    [InlineData(new string[0], "0", Review, null, new string[0])]
    [InlineData(new[] { "DobYear" }, "8", ProbableMatch, "SCORE_BAND_PROBABLE_MATCH", new string[0])]              // T_probable_exact reached on the tie
    [InlineData(new[] { "GenderMissing", "DobYear" }, "8", Review, null, new string[0])]                        // prerequisites not all EXACT
    [InlineData(new[] { "DobYear", "EmployerUnsettled" }, "8", Review, null, new string[0])]                    // employment unsettled
    [InlineData(new[] { "DobYear", "DocNotComparable" }, "8", ProbableMatch, "SCORE_BAND_PROBABLE_MATCH", new string[0])] // NOT_COMPARABLE is no block
    [InlineData(new[] { "GenderMissing", "DobMonth", "Employer" }, "30", ProbableMatch, "SCORE_BAND_PROBABLE_MATCH", new string[0])] // T_probable tie
    [InlineData(new[] { "GenderMissing", "DobMonth", "OtherId" }, "20", Review, null, new string[0])]
    [InlineData(new[] { "DocAgree", "DobMonth", "OtherId" }, "60", Match, "SCORE_BAND_MATCH", new string[0])]  // T_match tie
    [InlineData(new[] { "DobTolerance" }, "8", ProbableMatch, "SCORE_BAND_PROBABLE_MATCH", new string[0])]         // within tolerance, as row 4
    [InlineData(new[] { "GenderMissing", "DobTolerance" }, "8", Review, null, new string[0])]
    [InlineData(new[] { "GenderMissing", "DocConflict" }, "-40", Review, null, new[] { "SCORE_HELD_PENDING_VALIDATION" })]
    [InlineData(new[] { "AgeDiff", "DobYear" }, "3.2", Review, null, new string[0])]
    [InlineData(new[] { "DobDay", "Employer", "Relative", "Company", "AddrHouse", "DocConflict" }, "45", Review, "SCORE_BAND_PROBABLE_MATCH",
        new[] { "SCORE_CAPPED_PROFILE_CONFLICT" })] // 85 − 40: the pending document conflict keeps row 1
    [InlineData(new[] { "GenderConflict", "DocAgree", "DobDay" }, "65", Review, "SCORE_BAND_MATCH", new[] { "SCORE_CAPPED_GENDER_CONFLICT" })]
    [InlineData(new[] { "NewsGenderDiff", "DocAgree" }, "40", Review, "SCORE_BAND_PROBABLE_MATCH", new[] { "SCORE_CAPPED_GENDER_CONFLICT" })]
    [InlineData(new[] { "GenderMissing", "DobMonth", "AddrHouse" }, "30", ProbableMatch, "SCORE_BAND_PROBABLE_MATCH", new string[0])] // listed difference: two MEDIUM
    [InlineData(new[] { "AddrDistrict", "OtherId" }, "10", ProbableMatch, "SCORE_BAND_PROBABLE_MATCH", new string[0])]  // listed difference: two LOW
    [InlineData(new[] { "Employer" }, "15", ProbableMatch, "SCORE_BAND_PROBABLE_MATCH", new string[0])]                 // listed difference: one employer
    [InlineData(new[] { "NameNative", "DobYear" }, "-7", Review, null, new string[0])]                                  // row 4's pair falls below T_probable_exact
    [InlineData(new[] { "NameNative", "DobDay", "Employer" }, "25", Review, "SCORE_BAND_PROBABLE_MATCH", new[] { "SCORE_CAPPED_NATIVE_NAME_CONFLICT" })] // ≥ 8 with EXACT prerequisites
    [InlineData(new[] { "NameNativeOrder", "DobDay", "Employer", "Relative", "AddrHouse" }, "55", Review, "SCORE_BAND_PROBABLE_MATCH", new[] { "SCORE_CAPPED_NATIVE_NAME_CONFLICT" })]
    [InlineData(new[] { "NameNative", "DocAgree", "DobDay" }, "50", ProbableMatch, "SCORE_BAND_PROBABLE_MATCH", new string[0])] // an agreeing document: no cap, as row 2
    [InlineData(new[] { "NameNativeOrder", "DocAgree", "DobDay", "Employer" }, "65", Match, "SCORE_BAND_MATCH", new string[0])]
    [InlineData(new[] { "NameNative", "DocConflict" }, "-55", Review, null, new[] { "SCORE_HELD_PENDING_VALIDATION" })] // never NO_MATCH: not validated
    public void Bands_thresholds_and_caps(string[] evidence, string total, IdentityBand band, string? threshold, string[] applied)
    {
        var s = ScoringRule.Identity(Pair(evidence), entityRecord: false);
        Assert.Equal((D(total), band, threshold), (s.Total, s.Band, s.Threshold));
        Assert.Equal(applied, s.Applied);
    }

    [Theory]
    [InlineData(new[] { "GenderMissing", "EntityId", "Relative", "OtherId" }, "60", ProbableMatch, new[] { "SCORE_CAPPED_ENTITY_RECORD" })]
    [InlineData(new[] { "GenderMissing", "EntityId" }, "40", ProbableMatch, new string[0])]
    [InlineData(new[] { "GenderMissing", "Relative", "Employer" }, "30", Review, new[] { "SCORE_CAPPED_ENTITY_RECORD" })]
    public void Entity_records_cap_at_probable_match_and_at_review_without_an_entity_identifier(string[] evidence, string total, IdentityBand band, string[] applied)
    {
        var s = ScoringRule.Identity(Pair(evidence), entityRecord: true);
        Assert.Equal((D(total), band), (s.Total, s.Band));
        Assert.Equal(applied, s.Applied);
    }

    [Theory]
    [InlineData(ReasonCodes.ImpossibleAgeAtReportedEvent, IdentityDecision.NoMatch, new string[0], NoMatch, new[] { "IMPOSSIBLE_AGE_AT_REPORTED_EVENT" })]
    [InlineData(ReasonCodes.ImpossibleAgeAtReportedEvent, IdentityDecision.Review, new[] { "IMPOSSIBLE_AGE_AT_REPORTED_EVENT", "SOURCE_QUALITY_DOWNGRADE" }, Review,
        new[] { "IMPOSSIBLE_AGE_AT_REPORTED_EVENT", "SOURCE_QUALITY_DOWNGRADE", "SCORE_HELD_PENDING_VALIDATION" })]
    [InlineData(ReasonCodes.FullDobConflict, IdentityDecision.NoMatch, new[] { "FULL_DOB_CONFLICT" }, NoMatch, new[] { "FULL_DOB_CONFLICT" })]
    [InlineData(ReasonCodes.DobConflict, IdentityDecision.NoMatch, new[] { "DOB_CONFLICT" }, NoMatch, new[] { "DOB_CONFLICT" })]
    [InlineData(ReasonCodes.FullDobConflict, IdentityDecision.Review, new[] { "FULL_DOB_CONFLICT", "SOURCE_QUALITY_DOWNGRADE" }, Review,
        new[] { "FULL_DOB_CONFLICT", "SOURCE_QUALITY_DOWNGRADE", "SCORE_HELD_PENDING_VALIDATION" })]
    [InlineData(ReasonCodes.DeathBeforeCustomerBirth, IdentityDecision.Review, new[] { "DEATH_BEFORE_CUSTOMER_BIRTH" }, Review,
        new[] { "DEATH_BEFORE_CUSTOMER_BIRTH", "SCORE_HELD_PENDING_VALIDATION" })]
    public void Hard_conflicts_veto_when_validated_and_hold_at_review_otherwise(string hard, IdentityDecision decision, string[] reasons, IdentityBand band, string[] applied)
    {
        // A document agreement (+40) shows the veto and the hold stand whatever the points.
        var s = ScoringRule.Identity(Pair(["DocAgree"], hard, decision, reasons), entityRecord: false);
        Assert.Equal((40m, band), (s.Total, s.Band));
        Assert.Equal(applied, s.Applied);
    }

    [Theory]
    [InlineData(ConditionStatus.Hit, ConditionBasis.Flag, "100", "SCORE_CONDITION_BASIS", ConditionStatus.Hit)]
    [InlineData(ConditionStatus.Hit, ConditionBasis.CategoryMapping, "100", "SCORE_CONDITION_BASIS", ConditionStatus.Hit)]
    [InlineData(ConditionStatus.Unknown, ConditionBasis.Unmapped, "40", "UNMAPPED_CATEGORY", ConditionStatus.Unknown)]
    [InlineData(ConditionStatus.Unknown, ConditionBasis.Flag, "40", "SOURCE_CONDITION_UNKNOWN", ConditionStatus.Unknown)]
    [InlineData(ConditionStatus.NotHit, ConditionBasis.Flag, "0", "SCORE_CONDITION_BASIS", ConditionStatus.NotHit)]
    public void A_list_condition_comes_out_as_today(ConditionStatus status, ConditionBasis basis, string points, string code, ConditionStatus band)
    {
        var s = ScoringRule.Condition(new SourceCondition(ListType.Pep, status, basis, [], LegalStage.Unknown, null, "v", []));
        Assert.Equal((D(points), code, band), (s.Total, Assert.Single(s.Items).Code, s.Band));
    }

    [Theory]
    [InlineData(LegalStage.Indicted, true, SourceKind.AllowlistedNews, "48", ConditionStatus.Unknown)]   // spec examples
    [InlineData(LegalStage.Indicted, true, SourceKind.PressRelease, "64", ConditionStatus.Hit)]
    [InlineData(LegalStage.Convicted, true, SourceKind.AllowlistedNews, "60", ConditionStatus.Hit)]      // T_hit tie
    [InlineData(LegalStage.Alleged, true, SourceKind.AllowlistedNews, "30", ConditionStatus.Unknown)]    // T_unknown tie
    [InlineData(LegalStage.Convicted, true, SourceKind.CourtJudgment, "100", ConditionStatus.Hit)]
    [InlineData(LegalStage.Wanted, true, SourceKind.CourtJudgment, "90", ConditionStatus.Hit)]
    [InlineData(LegalStage.Acquitted, true, SourceKind.CourtJudgment, "10", ConditionStatus.NotHit)]
    [InlineData(LegalStage.Dismissed, true, SourceKind.CourtJudgment, "10", ConditionStatus.NotHit)]
    [InlineData(LegalStage.Convicted, false, SourceKind.CourtJudgment, "0", ConditionStatus.NotHit)]     // not a suspect
    public void A_source_person_scores_stage_times_reliability(LegalStage stage, bool suspect, SourceKind source, string points, ConditionStatus band)
    {
        var s = ScoringRule.SourcePerson(stage, suspect, source);
        Assert.Equal((D(points), band, "SCORE_CONDITION_STAGE"), (s.Total, s.Band, Assert.Single(s.Items).Code));
    }

    [Theory]
    [InlineData(LegalStage.Unknown, SourceKind.CourtJudgment)]
    [InlineData(null, SourceKind.AllowlistedNews)]   // a reader gave no answer
    public void An_unknown_stage_is_unknown_by_rule_whatever_the_source(LegalStage? stage, SourceKind source)
    {
        var s = ScoringRule.SourcePerson(stage, suspect: true, source);
        Assert.Equal((ConditionStatus.Unknown, 0), (s.Band, s.Items.Count));
        Assert.Equal(["SCORE_CONDITION_STAGE_UNKNOWN"], s.Applied);
    }
}
