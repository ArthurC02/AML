using Aml.Core.Identity;
using Aml.Core.Shared;
using Aml.Core.Subjects;
using static Aml.Core.Shared.ComparisonStatus;

namespace Aml.Tests.Unit.Identity;

/// <summary>Boundaries, vocabularies and several-value inputs of the evidence rules, each pinned so a changed rule fails.</summary>
public class IdentityBoundaryTests
{
    private static ScreenedSubject Subject(PartialDate? dob = null) =>
        new(SubjectRole.Customer, null, ["陳志明"], Gender.Male, dob ?? new PartialDate(1980, 5, 20), ["TW"],
            null, null, null, null, [], []);

    private static SourceProfile Profile() =>
        new("DJ:1", Gender.Male, ["陳志明"], [], ["TW"], [], [new SourceCategory("Special Interest Person (SIP)", "Fraud")],
            [], [], [], null, [], []);

    private static PartialDate D(int y, int? m = null, int? d = null) => new(y, m, d);

    [Theory]
    [InlineData(1989, null, null, 1990, null, null, true)]
    [InlineData(1991, null, null, 1990, null, null, false)]
    [InlineData(1990, 4, null, 1990, 5, null, true)]
    [InlineData(1990, 6, null, 1990, 5, null, false)]
    [InlineData(1990, null, null, 1990, 6, null, false)]  // the year alone cannot order dates in the same year
    [InlineData(1990, 5, 1, 1990, 5, 2, true)]
    [InlineData(1990, 5, 2, 1990, 5, 1, false)]
    [InlineData(1990, 5, 1, 1990, 5, 1, false)]
    [InlineData(1990, 5, null, 1990, 5, 3, false)]
    [InlineData(1990, 5, 1, 1990, 5, null, false)]
    public void Before_holds_only_when_every_date_a_can_be_precedes_every_date_b_can(int ay, int? am, int? ad, int by, int? bm, int? bd, bool before) =>
        Assert.Equal(before, HardConflictGate.Before(D(ay, am, ad), D(by, bm, bd)));

    // Born 2000-05-20.
    [Theory]
    [InlineData(2014, null, null, 14)]
    [InlineData(2014, 4, null, 13)]
    [InlineData(2014, 6, null, 14)]
    [InlineData(2014, 6, 1, 14)]
    [InlineData(2014, 5, 21, 14)]
    [InlineData(2014, 5, null, 14)]
    [InlineData(2014, 5, 19, 13)]
    [InlineData(2014, 5, 20, 14)]
    public void Max_age_at_counts_the_birthday_only_when_it_can_have_passed(int y, int? m, int? d, int age) =>
        Assert.Equal(age, HardConflictGate.MaxAgeAt(D(2000, 5, 20), D(y, m, d)));

    [Theory]
    [InlineData(19, true)]
    [InlineData(20, false)]  // exactly the minimum role age by the Profile's own DOB
    public void A_role_the_profiles_own_dob_makes_younger_than_14_is_a_source_quality_concern(int day, bool concern) =>
        Assert.Equal(concern, HardConflictGate.SourceQualityConcern(Profile() with
        {
            DatesOfBirth = [D(2000, 5, 20)],
            Roles = [new ProfileRole("經理", "宏遠投資股份有限公司", D(2014, 5, day))],
        }));

    [Theory]
    [InlineData("WIFE", "SPOUSE")]
    [InlineData("husband", "SPOUSE")]
    [InlineData("SPOUSE", "SPOUSE")]
    [InlineData("配偶", "SPOUSE")]
    [InlineData("妻", "SPOUSE")]
    [InlineData("夫", "SPOUSE")]
    [InlineData("FATHER", "PARENT")]
    [InlineData("MOTHER", "PARENT")]
    [InlineData("PARENT", "PARENT")]
    [InlineData("父", "PARENT")]
    [InlineData("母", "PARENT")]
    [InlineData("父親", "PARENT")]
    [InlineData("母親", "PARENT")]
    [InlineData("SON", "CHILD")]
    [InlineData("DAUGHTER", "CHILD")]
    [InlineData("CHILD", "CHILD")]
    [InlineData("子", "CHILD")]
    [InlineData("女", "CHILD")]
    [InlineData("兒子", "CHILD")]
    [InlineData("女兒", "CHILD")]
    [InlineData("BROTHER", "SIBLING")]
    [InlineData("SISTER", "SIBLING")]
    [InlineData("SIBLING", "SIBLING")]
    [InlineData("兄弟", "SIBLING")]
    [InlineData("姊妹", "SIBLING")]
    public void Every_relation_label_names_its_relation(string label, string relation) =>
        Assert.Equal($"SAME_NAMED_RELATIVE:{relation}", EvidenceBuilder.RelativeEvidence(EvidenceGroup.Relationship,
            [new Relative("許雅婷", label)], [new ProfileRelationship("2", "許雅婷", relation)], Strength.Medium)?.Detail);

    // Born 1980-05-20: 19 on 1999-06-01, when adulthood was 20.
    [Theory]
    [InlineData("董事", ReasonCodes.ImpossibleRoleAtReportedEvent)]
    [InlineData("董事長", ReasonCodes.ImpossibleRoleAtReportedEvent)]
    [InlineData("副董事長", ReasonCodes.ImpossibleRoleAtReportedEvent)]
    [InlineData("常務董事", ReasonCodes.ImpossibleRoleAtReportedEvent)]
    [InlineData("獨立董事", ReasonCodes.ImpossibleRoleAtReportedEvent)]
    [InlineData("監察人", ReasonCodes.ImpossibleRoleAtReportedEvent)]
    [InlineData(" director ", ReasonCodes.ImpossibleRoleAtReportedEvent)]
    [InlineData("Supervisor", ReasonCodes.ImpossibleRoleAtReportedEvent)]
    [InlineData("Director of Marketing", null)]  // the whole title must be listed
    [InlineData("經理", null)]
    public void Only_a_listed_adult_title_makes_a_minor_role_impossible(string title, string? code) =>
        Assert.Equal(code, HardConflictGate.Check(Subject(), Profile() with { Roles = [new ProfileRole(title, "宏遠投資股份有限公司", D(1999, 6, 1))] }));

    // Born 2004-05-20: 18 on 2022-06-01, when adulthood was 20; 19 on 2023-06-01, when it is 18.
    [Theory]
    [InlineData(2022, ReasonCodes.ImpossibleRoleAtReportedEvent)]
    [InlineData(2023, null)]
    public void Adulthood_is_20_before_2023_and_18_since(int year, string? code) =>
        Assert.Equal(code, HardConflictGate.Check(Subject(D(2004, 5, 20)),
            Profile() with { Roles = [new ProfileRole("董事", "宏遠投資股份有限公司", D(year, 6, 1))] }));

    [Fact]
    public void Documents_are_comparable_for_the_same_type_and_a_known_equal_country_and_imo_whatever_the_flag()
    {
        Assert.True(EvidenceBuilder.Comparable(new("PASSPORT", "1", "TW"), new("PASSPORT", "2", "TW")));
        Assert.False(EvidenceBuilder.Comparable(new("PASSPORT", "1", "TW"), new("PASSPORT", "1", "JP")));
        Assert.False(EvidenceBuilder.Comparable(new("PASSPORT", "1"), new("PASSPORT", "1")));
        Assert.False(EvidenceBuilder.Comparable(new("PASSPORT", "1", "TW"), new("PASSPORT", "1")));
        Assert.False(EvidenceBuilder.Comparable(new("PASSPORT", "1", "TW"), new("TW_NATIONAL_ID", "1", "TW")));
        Assert.True(EvidenceBuilder.Comparable(new("IMO_NUMBER", "1", "PA"), new("IMO_NUMBER", "1")));
    }

    [Fact]
    public void A_subject_document_against_a_profile_without_documents_is_profile_missing_and_no_direct_identifier()
    {
        var r = IdentityDecisionPolicy.Decide(Subject() with { Document = new("TW_NATIONAL_ID", "A123456789") }, Profile());
        Assert.Equal(ProfileMissing, r.Evidence[EvidenceGroup.Document].Status);
        Assert.Contains(ReasonCodes.NoBilateralDirectIdentifier, r.ReasonCodes);

        // A comparable document on both sides is a direct identifier, whatever its number; another type is not.
        var conflict = IdentityDecisionPolicy.Decide(Subject() with { Document = new("TW_NATIONAL_ID", "A123456789") },
            Profile() with { Documents = [new("TW_NATIONAL_ID", "A223456788")] });
        Assert.Equal((1, ReasonCodes.DocumentIdentityConflict), (conflict.Stage3Row, conflict.ReasonCodes[0]));
        Assert.DoesNotContain(ReasonCodes.NoBilateralDirectIdentifier, conflict.ReasonCodes);
        var passport = IdentityDecisionPolicy.Decide(Subject() with { Document = new("TW_NATIONAL_ID", "A123456789") },
            Profile() with { Documents = [new("PASSPORT", "A123456789", "TW")] });
        Assert.Contains(ReasonCodes.NoBilateralDirectIdentifier, passport.ReasonCodes);
    }

    [Fact]
    public void Entity_identifiers_agree_when_any_comparable_pair_has_the_same_number()
    {
        var vessel = Subject() with { PartyType = "Company", Document = new("IMO_NUMBER", "IMO9123456", "PA") };
        var profile = Profile() with { RecordType = "Vessel" };
        var agree = IdentityDecisionPolicy.Decide(vessel, profile with { Documents = [new("IMO_NUMBER", "IMO 9999999"), new("IMO_NUMBER", "IMO 9123456")] });
        Assert.Equal((IdentityDecision.ProbableMatch, Exact), (agree.Decision, agree.Evidence[EvidenceGroup.EntityIdentifier].Status));

        var other = IdentityDecisionPolicy.Decide(vessel, profile with { Documents = [new("REGISTRATION_NUMBER", "12345678", "TW")] });
        Assert.Equal(Uncertain, other.Evidence[EvidenceGroup.EntityIdentifier].Status);
        Assert.Equal(ProfileMissing, IdentityDecisionPolicy.Decide(vessel, profile).Evidence[EvidenceGroup.EntityIdentifier].Status);
        Assert.Equal(SubjectMissing, IdentityDecisionPolicy.Decide(vessel with { Document = null }, profile with { Documents = [new("IMO_NUMBER", "IMO 9123456")] })
            .Evidence[EvidenceGroup.EntityIdentifier].Status);

        // A registration number without a jurisdiction on either side is not comparable, even with the same number.
        var company = Subject() with { PartyType = "Company", Document = new("REGISTRATION_NUMBER", "12345678") };
        var unknown = IdentityDecisionPolicy.Decide(company, Profile() with { RecordType = "Entity", Documents = [new("REGISTRATION_NUMBER", "12345678")] });
        Assert.Equal(Uncertain, unknown.Evidence[EvidenceGroup.EntityIdentifier].Status);
        Assert.Equal([ReasonCodes.NonPersonSubjectReview], unknown.ReasonCodes);
    }

    [Fact]
    public void Several_values_count_when_any_one_agrees()
    {
        var document = EvidenceBuilder.DocumentEvidence(new("PASSPORT", "P1", "TW"), [new("PASSPORT", "P9", "TW"), new("PASSPORT", "P1", "TW")]);
        Assert.Equal((Exact, Strength.Unique), (document.Status, document.Strength));

        var relative = EvidenceBuilder.RelativeEvidence(EvidenceGroup.Relationship, [new Relative("許雅婷", "妻")],
            [new ProfileRelationship("3", "王大同", "Father"), new ProfileRelationship("2", "許雅婷", "Wife")], Strength.Medium);
        Assert.Equal(Exact, relative?.Status);

        var other = EvidenceBuilder.OtherIdentifierEvidence("X123", [new OtherIdentifier("Ref", "Y999"), new OtherIdentifier("Ref", "X123")]);
        Assert.Equal(Exact, other?.Status);

        var employment = EvidenceBuilder.EmploymentEvidence(Subject() with
        {
            Employments = [new Employment("鍾記投資股份有限公司", null, D(2021), null), new Employment("鍾記投資股份有限公司", null, D(2016), null)],
        }, Profile() with { Roles = [new ProfileRole("總經理", "鐘記投資股份有限公司", D(2015), D(2020))] });
        Assert.Equal("SAME_ORGANIZATION_SAME_PERIOD", employment.Detail); // variant characters fold for organizations, on both sides
        var folded = EvidenceBuilder.EmploymentEvidence(Subject() with { Employments = [new Employment("鐘記投資股份有限公司", null, D(2016), null)] },
            Profile() with { Roles = [new ProfileRole("總經理", "鍾記投資股份有限公司", D(2015), D(2020))] });
        Assert.Equal("SAME_ORGANIZATION_SAME_PERIOD", folded.Detail);

        var subject = Subject() with
        {
            Addresses = [new Address("通訊地址", "東京都港区", null, "JP"), new Address("永久地址", "x", "新北市新莊區中華路35巷8號3樓", "TW")],
        };
        var same = EvidenceBuilder.AddressEvidence(subject, Profile() with
        { AddressCountries = ["TW"], AddressLines = ["臺北市大安區信義路四段1號", "台北縣新莊市中華路３５巷８號３樓"] });
        Assert.Equal("SAME_ADDRESS_NO_PERIOD", same.Detail); // the normalized text, not the raw one, is compared

        var nearest = EvidenceBuilder.AddressEvidence(subject with { Addresses = [subject.Addresses[1]] }, Profile() with
        { AddressCountries = ["TW"], AddressLines = ["臺北市大安區信義路四段1號", "新北市新莊區中華路35巷10號3樓"] });
        Assert.Equal("SOFT_DIFFERENCE（Jaccard 0.72，SIMILAR）", nearest.Detail); // the closest Profile line
        Assert.Equal("SOFT_DIFFERENCE", EvidenceBuilder.AddressEvidence(subject, Profile() with { AddressCountries = ["TW"] }).Detail);
        var atThreshold = EvidenceBuilder.AddressEvidence(Subject() with { Addresses = [new Address("通訊地址", "Alpha Beta", null, "US")] },
            Profile() with { AddressCountries = ["US"], AddressLines = ["Alpha"] });
        Assert.Equal("SOFT_DIFFERENCE（Jaccard 0.50，SIMILAR）", atThreshold.Detail); // the threshold itself is SIMILAR
    }

    [Fact]
    public void Missing_sides_and_kinds_are_kept_apart()
    {
        Assert.Equal(SubjectMissing, EvidenceBuilder.OtherIdentifierEvidence(null, [new OtherIdentifier("Ref", "X123")])?.Status);
        Assert.Null(EvidenceBuilder.RelativeEvidence(EvidenceGroup.Relationship, [], [], Strength.Medium));
        Assert.Null(EvidenceBuilder.Build(Subject(), Profile() with { Gender = Gender.Male })[EvidenceGroup.Gender].Detail);
        var fullDob = EvidenceBuilder.Build(Subject(), Profile() with { DatesOfBirth = [D(1980, 5, 20)] })[EvidenceGroup.DateOfBirth];
        Assert.Equal((Strength.High, (string?)null), (fullDob.Strength, fullDob.Detail));

        // A related person on the Profile is not a related company.
        var company = EvidenceBuilder.Build(Subject() with { Relatives = [new Relative("宏遠投資", "Shareholder", IsEntity: true)] },
            Profile() with { Relationships = [new ProfileRelationship("2", "宏遠投資", "Shareholder")] });
        Assert.Equal(ProfileMissing, company[EvidenceGroup.CompanyAssociation].Status);
    }

    [Fact]
    public void An_event_before_the_minimum_role_age_is_uncertain_with_the_age()
    {
        var e = EvidenceBuilder.TemporalEvidence(Subject(D(2015, 1, 1)), Profile(), new NewsItem("1", "DOC1", "標題", "某報", D(2026, 3, 1), "x"));
        Assert.Equal((Uncertain, (string?)null, "最早事件時最多 11 歲"), (e?.Status, e?.Detail, e?.Note));
        var adult = EvidenceBuilder.TemporalEvidence(Subject(D(2000, 1, 1)), Profile(), new NewsItem("1", "DOC1", "標題", "某報", D(2026, 3, 1), "x"));
        Assert.Equal((Exact, ReasonCodes.TemporallyPlausible, "最早事件時最多 26 歲"), (adult?.Status, adult?.Detail, adult?.Note));
    }

    [Fact]
    public void Stated_ages_pair_up_only_when_every_name_has_one()
    {
        Assert.Null(StatedAgeCheck.FindStatedAge("陳志明、林美玲分別為45歲", ["陳志明"]));
        Assert.Null(StatedAgeCheck.FindStatedAge("某人45歲", ["", "陳志明"])); // an empty name matches nothing
        Assert.Null(StatedAgeCheck.FindStatedAge("陳志明、林美玲分別為45歲、三四歲", ["陳志明"])); // one age unreadable

        var news = new NewsItem("1", "DOC1", "標題", "某報", D(2026, 3, 1), "陳志明、林美玲分別為45歲、40歲");
        var age = StatedAgeCheck.Check(Subject(), Profile(), news);
        Assert.Equal((ReasonCodes.AgeCompatibleAtReport, "報導年齡 45 歲；依報導日期 2026-03-01 推算 45 歲"), (age?.Detail, age?.Note));

        var coarse = StatedAgeCheck.Check(Subject(D(1980)), Profile(), news);
        Assert.Equal((ReasonCodes.AgeNotComparable, "報導年齡 45 歲；缺少完整出生日期或報導日期，依報導日期 2026-03-01 可能為 45～46 歲，不比較"), (coarse?.Detail, coarse?.Note));
        Assert.Equal("報導年齡 45 歲；缺少完整出生日期或報導日期，無法換算",
            StatedAgeCheck.Check(Subject(D(1980)), Profile(), news with { PublishedOn = null })?.Note);
    }

    // Every age a partial DOB allows at a partial time point; the youngest is the oldest less one unless the birthday has surely come.
    [Theory]
    [InlineData(1980, null, null, 2026, 3, 1, "45～46 歲")]
    [InlineData(1980, 5, null, 2026, 3, 1, "45 歲")]   // March is before May: the birthday has not come
    [InlineData(1980, 2, null, 2026, 3, 1, "46 歲")]   // February: it has
    [InlineData(1980, 3, null, 2026, 3, 1, "45～46 歲")] // same month, day unknown
    [InlineData(1980, 3, 1, 2026, 3, 1, "46 歲")]     // the birthday itself
    [InlineData(1980, 5, 20, 2026, null, null, "45～46 歲")]
    [InlineData(2026, null, null, 2026, 3, 1, "0 歲")] // never below zero
    public void The_possible_age_range_covers_every_date_both_values_can_stand_for(int y, int? m, int? d, int ay, int? am, int? ad, string expected) =>
        Assert.Equal(expected, StatedAgeCheck.AgeRange(new PartialDate(y, m, d), new PartialDate(ay, am, ad)));

    // Either side's period can end before the other's starts.
    [Theory]
    [InlineData(2010, 2012, 2015, null, "SAME_ORGANIZATION_NO_COMMON_PERIOD")]
    [InlineData(2015, null, 2010, 2012, "SAME_ORGANIZATION_NO_COMMON_PERIOD")]
    [InlineData(2010, 2015, 2015, null, "SAME_ORGANIZATION_SAME_PERIOD")]  // periods compare by year, so the end year itself overlaps
    public void An_ended_period_overlaps_only_through_its_end_year(int subjectFrom, int? subjectTo, int profileFrom, int? profileTo, string detail) =>
        Assert.Equal(detail, EvidenceBuilder.EmploymentEvidence(
            Subject() with { Employments = [new Employment("宏遠投資股份有限公司", null, D(subjectFrom), subjectTo is { } st ? D(st) : null)] },
            Profile() with { Roles = [new ProfileRole("經理", "宏遠投資股份有限公司", D(profileFrom), profileTo is { } pt ? D(pt) : null)] }).Detail);

    [Theory]
    [InlineData("UNPARSEABLE:第 3 列", true)]
    [InlineData("AMBIGUOUS:Citizenship", false)]
    public void Only_an_unparseable_finding_is_a_source_quality_concern(string finding, bool concern) =>
        Assert.Equal(concern, HardConflictGate.SourceQualityConcern(Profile() with { Findings = [finding] }));
}
