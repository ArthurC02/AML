using Aml.Core.Identity;
using Aml.Core.Normalization;
using Aml.Core.Shared;
using Aml.Core.Subjects;
using static Aml.Core.Shared.ComparisonStatus;

namespace Aml.Tests.Core;

/// <summary>Rules without a dedicated Golden Case yet, tested on minimal synthetic data.</summary>
public class IdentityRuleTests
{
    private static ScreenedSubject Subject(string name, PartialDate? dob = null) =>
        new(SubjectRole.Customer, null, [name], Gender.Male, dob ?? new PartialDate(1980, 5, 20), ["TW"],
            null, null, null, null, [], []);

    private static SourceProfile Profile(params string[] names) =>
        new("DJ:1", Gender.Male, names, [], ["TW"], [], [new SourceCategory("Special Interest Person (SIP)", "Fraud")],
            [], [], [], null, [], []);

    private static NewsItem News(string text, PartialDate? on = null) =>
        new("1", "DOC1", "標題", "某報", on ?? new PartialDate(2026, 3, 1), text);

    [Theory]
    [InlineData("鍾仁聲", "鐘仁聲")]
    [InlineData("张家豪", "張家豪")]
    [InlineData("ＷＡＮＧ ＭＩＮＧ", "Wang Ming")]   // full-width Latin, NFKC
    public void Controlled_variants_and_NFKC_make_names_exact(string subject, string profile)
    {
        var (status, _) = Normalize.CompareNames([subject], [profile]);
        Assert.Equal(Exact, status);
    }

    [Fact]
    public void Variant_match_is_disclosed_and_distinct_surnames_never_fold()
    {
        Assert.Equal((Exact, Findings.VariantCharacters), Normalize.CompareNames(["鍾仁聲"], ["鐘仁聲"]));
        Assert.Equal((Exact, (string?)null), Normalize.CompareNames(["鐘仁聲"], ["鐘仁聲"]));
        Assert.Equal(Partial, Normalize.CompareNames(["涂志明"], ["塗志明"]).Status);
        Assert.Equal(Partial, Normalize.CompareNames(["鍾仁聲"], ["鐘任聲"]).Status); // 仁/任 differ in reading and are not folded
    }

    [Theory]
    [InlineData("CHUNG JEN-SHENG", "Zhong Ren Sheng")]   // Taiwanese passport vs Hanyu Pinyin
    [InlineData("JHONG JENN SHENG", "Ren Sheng Zhong")]  // Tongyong-style spelling, given-first order
    [InlineData("Hsieh Chih-Ming", "XIE ZHI MING")]       // Wade-Giles
    public void Romanization_variants_are_partial_and_disclosed(string subject, string profile) =>
        Assert.Equal((Partial, Findings.RomanizationVariant), Normalize.CompareNames([subject], [profile]));

    [Fact]
    public void Romanization_needs_every_syllable_and_never_reaches_chinese()
    {
        Assert.NotEqual(Findings.RomanizationVariant, Normalize.CompareNames(["CHUNG JEN"], ["Zhong Ren Sheng"]).Detail);
        Assert.NotEqual(Findings.RomanizationVariant, Normalize.CompareNames(["CHUNG JEN SHENG"], ["Zhong Lin Sheng"]).Detail);
        Assert.NotEqual(Findings.RomanizationVariant, Normalize.CompareNames(["CHUNG JEN SHENG"], ["鍾仁聲"]).Detail);
    }

    [Fact]
    public void Name_similarity_is_disclosed_on_partial_only()
    {
        var (status, detail) = Normalize.CompareNames(["陳志明"], ["陳志銘"]);
        Assert.Equal(Partial, status);
        Assert.Equal("NOT_IDENTICAL_AFTER_NORMALIZATION（Jaro-Winkler 0.82，Levenshtein 1）", detail);
        Assert.Equal(3, Similarity.Levenshtein("KITTEN", "SITTING"));
        Assert.Equal(0.961, Similarity.JaroWinkler("MARTHA", "MARHTA"), 3);
    }

    [Fact]
    public void Full_dob_conflict_is_row_1_review_never_no_match()
    {
        var r = IdentityDecisionPolicy.Decide(Subject("陳志明"), Profile("陳志明") with { DatesOfBirth = [new PartialDate(1981, 5, 20)] });
        Assert.Equal(ComparisonStatus.Conflict, r.Evidence[EvidenceGroup.DateOfBirth].Status);
        Assert.Null(r.HardConflict);
        Assert.Equal((1, IdentityDecision.Review), (r.Stage3Row, r.Decision));
        Assert.Equal(ReasonCodes.FullDobConflict, r.ReasonCodes[0]);

        var year = IdentityDecisionPolicy.Decide(Subject("陳志明"), Profile("陳志明") with { DatesOfBirth = [new PartialDate(1975)] });
        Assert.Equal(ReasonCodes.DobConflict, year.ReasonCodes[0]);
    }

    [Fact]
    public void One_agreeing_dob_among_several_is_support_not_conflict()
    {
        var r = IdentityDecisionPolicy.Decide(Subject("陳志明"),
            Profile("陳志明") with { DatesOfBirth = [new PartialDate(1975, 1, 1), new PartialDate(1980, 5, 20)] });
        Assert.Equal(Exact, r.Evidence[EvidenceGroup.DateOfBirth].Status);
        Assert.Equal(4, r.Stage3Row);
    }

    [Fact]
    public void Support_without_agreeing_prerequisites_is_row_5()
    {
        var r = IdentityDecisionPolicy.Decide(Subject("陳志明"), Profile("陳志銘") with { DatesOfBirth = [new PartialDate(1980)] });
        Assert.Equal((5, IdentityDecision.Review), (r.Stage3Row, r.Decision));
        Assert.Equal(ReasonCodes.PartialSupportOnly, r.ReasonCodes[0]);
    }

    private static readonly Document NationalId = new("TW_NATIONAL_ID", "A123456789");

    [Fact]
    public void Same_document_key_is_unique_support_row_2()
    {
        var r = IdentityDecisionPolicy.Decide(Subject("陳志明") with { Document = NationalId },
            Profile("陳志明") with { Documents = [new("TW_NATIONAL_ID", "a123456789", "TW")] });
        Assert.Equal(Strength.Unique, r.Evidence[EvidenceGroup.Document].Strength);
        Assert.Equal((2, IdentityDecision.ProbableMatch), (r.Stage3Row, r.Decision));
        Assert.Equal([ReasonCodes.UniqueIdentifierAgreement], r.ReasonCodes); // a bilateral document is a direct identifier
    }

    [Fact]
    public void Different_number_for_the_same_key_is_a_conflict_and_other_documents_are_not_comparable()
    {
        var conflict = IdentityDecisionPolicy.Decide(Subject("陳志明") with { Document = NationalId },
            Profile("陳志明") with { Documents = [new("TW_NATIONAL_ID", "A223456788", "TW")] });
        Assert.Equal((1, IdentityDecision.Review), (conflict.Stage3Row, conflict.Decision));
        Assert.Equal(ReasonCodes.DocumentIdentityConflict, conflict.ReasonCodes[0]);

        var passport = IdentityDecisionPolicy.Decide(Subject("陳志明") with { Document = NationalId },
            Profile("陳志明") with { Documents = [new("PASSPORT", "A123456789", "TW")] });
        Assert.Equal((Uncertain, "NOT_COMPARABLE"), (passport.Evidence[EvidenceGroup.Document].Status, passport.Evidence[EvidenceGroup.Document].Detail));
    }

    [Fact]
    public void Same_employer_in_a_common_period_is_support_and_with_full_dob_reaches_row_3()
    {
        var profile = Profile("陳志明") with
        {
            DatesOfBirth = [new PartialDate(1980, 5, 20)],
            Roles = [new ProfileRole("總經理", "宏遠投資股份有限公司", new PartialDate(2015), new PartialDate(2020))],
        };
        var r = IdentityDecisionPolicy.Decide(Subject("陳志明") with
            { Employments = [new Employment("宏遠投資股份有限公司", "經理", new PartialDate(2018, 3), null)] }, profile);
        Assert.Equal((Exact, Strength.Medium), (r.Evidence[EvidenceGroup.Employment].Status, r.Evidence[EvidenceGroup.Employment].Strength));
        Assert.Equal((3, IdentityDecision.ProbableMatch), (r.Stage3Row, r.Decision));

        var undated = IdentityDecisionPolicy.Decide(Subject("陳志明") with
            { Employments = [new Employment("宏遠投資股份有限公司", null, null, null)] }, profile);
        Assert.Equal("SAME_ORGANIZATION_NO_COMMON_PERIOD", undated.Evidence[EvidenceGroup.Employment].Detail);
        Assert.False(undated.Evidence[EvidenceGroup.Employment].IsSupport);
    }

    [Fact]
    public void Relative_and_related_company_named_on_both_sides_are_support()
    {
        var profile = Profile("陳志明") with
        {
            Relationships = [new ProfileRelationship("2", "許雅婷", "Wife"), new ProfileRelationship("3", "宏遠投資股份有限公司", "Shareholder", IsEntity: true)],
        };
        var r = IdentityDecisionPolicy.Decide(Subject("陳志明") with
            { Relatives = [new Relative("許雅婷", "配偶"), new Relative("宏遠投資股份有限公司", "Shareholder", IsEntity: true)] }, profile);
        Assert.Equal(Strength.Medium, r.Evidence[EvidenceGroup.Relationship].Strength);
        Assert.Equal(Strength.Low, r.Evidence[EvidenceGroup.CompanyAssociation].Strength);
        Assert.Equal(5, r.Stage3Row); // no full DOB on the Profile: support without the strong combination

        var otherRelation = IdentityDecisionPolicy.Decide(Subject("陳志明") with { Relatives = [new Relative("許雅婷", "母親")] }, profile);
        Assert.Equal(Uncertain, otherRelation.Evidence[EvidenceGroup.Relationship].Status);
    }

    [Fact]
    public void Structured_taiwan_address_matches_across_old_names_and_full_width_digits()
    {
        Assert.Equal(AddressCompare.TaiwanKey("242台北縣新莊市中平里１２鄰中華路３５巷８號３樓"), AddressCompare.TaiwanKey("新北市新莊區中華路35巷8號3樓"));
        Assert.NotEqual(AddressCompare.TaiwanKey("新北市新莊區中華路35巷8號"), AddressCompare.TaiwanKey("新北市新莊區中華路35巷10號"));
        Assert.Null(AddressCompare.TaiwanKey("Thôn Example, Xã An Hòa"));
    }

    [Fact]
    public void Same_address_without_periods_is_never_support_and_a_different_one_is_a_soft_difference()
    {
        var subject = Subject("陳志明") with { Addresses = [new Address("永久地址", "新北市新莊區中華路35巷8號3樓", null, "TW")] };
        var same = IdentityDecisionPolicy.Decide(subject, Profile("陳志明") with
            { AddressCountries = ["TW"], AddressLines = ["台北縣新莊市中華路３５巷８號３樓"] });
        Assert.Equal((Uncertain, "SAME_ADDRESS_NO_PERIOD"), (same.Evidence[EvidenceGroup.Address].Status, same.Evidence[EvidenceGroup.Address].Detail));
        Assert.Equal(6, same.Stage3Row);

        var other = IdentityDecisionPolicy.Decide(subject, Profile("陳志明") with
            { AddressCountries = ["TW"], AddressLines = ["臺北市大安區信義路四段1號"] });
        Assert.StartsWith("SOFT_DIFFERENCE（Jaccard 0.", other.Evidence[EvidenceGroup.Address].Detail, StringComparison.Ordinal);
        Assert.NotEqual(ComparisonStatus.Conflict, other.Evidence[EvidenceGroup.Address].Status);
    }

    [Fact]
    public void Pending_hard_conflicts_fire_but_keep_the_pair_in_review()
    {
        // Born 1980-05-20: 16 when named director in 1997 (adulthood was 20 then), so the role is impossible but the age is not.
        var role = IdentityDecisionPolicy.Decide(Subject("陳志明"), Profile("陳志明") with
            { Roles = [new ProfileRole("董事", "宏遠投資股份有限公司", new PartialDate(1997, 1))] });
        Assert.Equal((ReasonCodes.ImpossibleRoleAtReportedEvent, IdentityDecision.Review), (role.HardConflict, role.Decision));

        var death = IdentityDecisionPolicy.Decide(Subject("陳志明"), Profile("陳志明") with { DateOfDeath = new PartialDate(1979) });
        Assert.Equal((ReasonCodes.DeathBeforeCustomerBirth, IdentityDecision.Review), (death.HardConflict, death.Decision));

        var sameYear = IdentityDecisionPolicy.Decide(Subject("陳志明"), Profile("陳志明") with { DateOfDeath = new PartialDate(1980) });
        Assert.Null(sameYear.HardConflict); // precision too coarse to say death came first
        Assert.True(HardConflictGate.Before(new PartialDate(1980, 4), new PartialDate(1980, 5, 20)));
    }

    [Fact]
    public void Non_person_profile_stays_in_review()
    {
        var r = IdentityDecisionPolicy.Decide(Subject("陳志明"), Profile("陳志明") with { RecordType = "Entity" });
        Assert.Equal((IdentityDecision.Review, (int?)null), (r.Decision, r.Stage3Row));
        Assert.Equal([ReasonCodes.NonPersonSubjectReview], r.ReasonCodes);
    }

    [Theory]
    [InlineData("陳志明（45歲）涉嫌詐欺", 45)]
    [InlineData("負責人陳志明今年46歲。", 46)]
    [InlineData("47歲的陳志明遭起訴", 47)]
    public void Stated_age_is_read_next_to_the_name(string text, int expected) =>
        Assert.Equal(expected, StatedAgeCheck.FindStatedAge(text, ["陳志明"]));

    [Fact]
    public void Stated_age_of_someone_else_or_in_another_sentence_is_ignored()
    {
        Assert.Null(StatedAgeCheck.FindStatedAge("王姓男子（52歲）與陳志明合作", ["陳志明"]));
        Assert.Null(StatedAgeCheck.FindStatedAge("陳志明遭起訴。另一名被告45歲。", ["陳志明"]));
    }

    [Fact]
    public void Stated_age_within_two_years_is_compatible_and_never_support()
    {
        // Born 1980-05-20; on 2026-03-01 the subject is 45. Stated 47 → difference 2, compatible.
        var r = IdentityDecisionPolicy.Decide(Subject("陳志明"), Profile("陳志明"), News("陳志明（47歲）涉嫌詐欺。"));
        var age = r.Evidence[EvidenceGroup.StatedAge];
        Assert.Equal(ReasonCodes.AgeCompatibleAtReport, age.Detail);
        Assert.False(age.IsSupport);
        Assert.Equal(6, r.Stage3Row); // nothing else agrees: compatibility adds no support
        Assert.Contains("推算 45 歲", age.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Stated_age_beyond_two_years_is_a_soft_conflict_row_1_never_no_match()
    {
        var r = IdentityDecisionPolicy.Decide(Subject("陳志明"), Profile("陳志明"), News("陳志明（48歲）涉嫌詐欺。"));
        Assert.Equal(ComparisonStatus.Conflict, r.Evidence[EvidenceGroup.StatedAge].Status);
        Assert.Null(r.HardConflict);
        Assert.Equal(1, r.Stage3Row);
        Assert.Equal(IdentityDecision.Review, r.Decision);
        Assert.Equal(ReasonCodes.AgeDifferenceAtReport, r.ReasonCodes[0]);
    }

    [Fact]
    public void Stated_age_without_full_dob_is_not_comparable()
    {
        var r = IdentityDecisionPolicy.Decide(Subject("陳志明", new PartialDate(1980)), Profile("陳志明"), News("陳志明（60歲）涉嫌詐欺。"));
        Assert.Equal(Uncertain, r.Evidence[EvidenceGroup.StatedAge].Status);
        Assert.Equal(6, r.Stage3Row);
    }
}
