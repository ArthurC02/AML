using Aml.Core.Cases;
using Aml.Core.Identity;
using Aml.Core.Shared;
using Aml.Core.Subjects;
using Aml.Infra.Txt;
using static Aml.Core.Shared.ComparisonStatus;

namespace Aml.Tests.Unit.Identity;

/// <summary>Date parsing, the source-quality downgrade, Profile DOB handling, employment and document comparability, gender conflict and stated ages.</summary>
public class IdentityEvidenceTests
{
    private static ScreenedSubject Subject(PartialDate? dob = null, Gender gender = Gender.Male) =>
        new(SubjectRole.Customer, null, ["陳志明"], gender, dob ?? new PartialDate(1980, 5, 20), ["TW"],
            null, null, null, null, [], []);

    private static SourceProfile Profile(params PartialDate[] dobs) =>
        new("DJ:1", Gender.Male, ["陳志明"], dobs, ["TW"], [], [new SourceCategory("Special Interest Person (SIP)", "Fraud")],
            [], [], [], null, [], []);

    private static NewsItem News(string text) => new("1", "DOC1", "標題", "某報", new PartialDate(2026, 3, 1), text);

    // Date parsing.

    [Theory]
    [InlineData("1990/13/45")]
    [InlineData("2023/2/29")]
    [InlineData("62/6/12")]       // a ROC year without 民國 is never guessed
    [InlineData("1799")]
    [InlineData("9999/1/1")]      // after the current year
    [InlineData("1970/00/15")]
    [InlineData("1970/4/31")]
    [InlineData("abc")]
    public void Invalid_dates_are_null(string raw) => Assert.Null(PartialDate.Parse(raw));

    [Theory]
    [InlineData("1970/00/00", "1970", DatePrecision.Year)]
    [InlineData("1970/5/00", "1970-05", DatePrecision.Month)]
    [InlineData("1973/6/12 00:00:00", "1973-06-12", DatePrecision.Day)]
    [InlineData("民國62年6月12日", "1973-06-12", DatePrecision.Day)]
    [InlineData("民國62年6月", "1973-06", DatePrecision.Month)]
    [InlineData("2024/2/29", "2024-02-29", DatePrecision.Day)]
    [InlineData("1800", "1800", DatePrecision.Year)]
    [InlineData("１９７３／６／１２", "1973-06-12", DatePrecision.Day)]
    [InlineData("１９７０－０５", "1970-05", DatePrecision.Month)]
    public void Valid_dates_keep_their_precision(string raw, string expected, DatePrecision precision)
    {
        var d = PartialDate.Parse(raw)!.Value;
        Assert.Equal((expected, precision), (d.ToString(), d.Precision));
    }

    // Source-quality downgrade.

    private static readonly ProfileRole DirectorFrom2010 = new("董事", "宏遠投資股份有限公司", new PartialDate(2010, 1));

    [Fact]
    public void Role_impossible_for_the_profiles_own_dob_downgrades_to_review()
    {
        var born = new PartialDate(2000, 3, 1);
        var r = IdentityDecisionPolicy.Decide(Subject(born), Profile(born) with { Roles = [DirectorFrom2010] });
        Assert.Equal((ReasonCodes.ImpossibleAgeAtReportedEvent, IdentityDecision.Review), (r.HardConflict, r.Decision));
        Assert.Equal([ReasonCodes.ImpossibleAgeAtReportedEvent, ReasonCodes.SourceQualityDowngrade], r.ReasonCodes);

        // Year-only Profile DOB: only LOW support, but the role is still impossible for the Profile itself.
        var year = IdentityDecisionPolicy.Decide(Subject(born), Profile(new PartialDate(2000)) with { Roles = [DirectorFrom2010] });
        Assert.Equal(IdentityDecision.Review, year.Decision);
        Assert.Contains(ReasonCodes.SourceQualityDowngrade, year.ReasonCodes);
    }

    [Fact]
    public void Unique_support_downgrades_a_validated_hard_conflict_to_review()
    {
        var id = new Document("TW_NATIONAL_ID", "A123456789", "TW");
        var r = IdentityDecisionPolicy.Decide(Subject(new PartialDate(2000, 3, 1)) with { Document = id },
            Profile() with { Roles = [DirectorFrom2010], Documents = [id] });
        Assert.Equal(IdentityDecision.Review, r.Decision);
        Assert.Equal([ReasonCodes.ImpossibleAgeAtReportedEvent, ReasonCodes.SourceQualityDowngrade], r.ReasonCodes);

        // Without the document and the Profile DOB, nothing questions the source: the validated rule still closes the pair.
        var plain = IdentityDecisionPolicy.Decide(Subject(new PartialDate(2000, 3, 1)), Profile() with { Roles = [DirectorFrom2010] });
        Assert.Equal(IdentityDecision.NoMatch, plain.Decision);
        Assert.Equal([ReasonCodes.ImpossibleAgeAtReportedEvent], plain.ReasonCodes);
    }

    // Born 2000-03-01, director from 2010: the age rule fires. Each row adds exactly one concern to a Profile without a DOB.
    [Theory]
    [InlineData("宏遠投資股份有限公司", 1, null, false)]
    [InlineData("", 1, null, true)]                                  // dated role without its organization
    [InlineData(" ", 1, null, true)]
    [InlineData("宏遠投資股份有限公司", null, null, true)]             // role dated to the year only
    [InlineData("宏遠投資股份有限公司", 1, "UNPARSEABLE:職位", true)]  // a Profile value the reader could not parse
    public void Each_source_quality_concern_alone_downgrades_a_validated_hard_conflict(string organization, int? month, string? finding, bool downgraded)
    {
        var profile = Profile() with
        {
            Roles = [new ProfileRole("董事", organization, new PartialDate(2010, month))],
            Findings = finding is null ? [] : [finding],
        };
        var r = IdentityDecisionPolicy.Decide(Subject(new PartialDate(2000, 3, 1)), profile);
        Assert.Equal(ReasonCodes.ImpossibleAgeAtReportedEvent, r.HardConflict);
        Assert.Equal(downgraded ? IdentityDecision.Review : IdentityDecision.NoMatch, r.Decision);
        Assert.Equal(downgraded
            ? [ReasonCodes.ImpossibleAgeAtReportedEvent, ReasonCodes.SourceQualityDowngrade]
            : [ReasonCodes.ImpossibleAgeAtReportedEvent], r.ReasonCodes);
    }

    // In a full decision HIGH support only comes from a full DOB, and that Profile DOB already makes the role impossible
    // for the Profile itself, so the support condition is checked on its own.
    [Theory]
    [InlineData(Strength.Low, false)]
    [InlineData(Strength.Medium, false)]
    [InlineData(Strength.High, true)]
    [InlineData(Strength.Unique, true)]
    public void High_or_unique_support_is_a_source_quality_concern(Strength strength, bool concern)
    {
        var evidence = new Dictionary<EvidenceGroup, Evidence>
        {
            [EvidenceGroup.DateOfBirth] = new(EvidenceGroup.DateOfBirth, Direction.Support, Exact, strength),
        };
        Assert.Equal(concern, HardConflictGate.SourceQualityConcern(Profile(), evidence));
    }

    [Fact]
    public void Month_precision_subject_dob_never_fires_the_age_rule()
    {
        var r = IdentityDecisionPolicy.Decide(Subject(new PartialDate(2000, 3)), Profile() with { Roles = [DirectorFrom2010] });
        Assert.Null(r.HardConflict);
        Assert.Equal((6, IdentityDecision.Review), (r.Stage3Row, r.Decision));
    }

    // Born 2000-03-15: 13 the day before the 14th birthday, 14 on it.
    [Theory]
    [InlineData(14, true)]
    [InlineData(15, false)]
    [InlineData(16, false)]
    public void Role_starting_before_the_14th_birthday_is_an_impossible_age(int day, bool impossible)
    {
        var start = new PartialDate(2014, 3, day);
        var r = IdentityDecisionPolicy.Decide(Subject(new PartialDate(2000, 3, 15)),
            Profile() with { Roles = [new ProfileRole("總經理", "宏遠投資股份有限公司", start)] });
        Assert.Equal(impossible ? 13 : 14, HardConflictGate.MaxAgeAt(new PartialDate(2000, 3, 15), start));
        if (impossible)
        {
            Assert.Equal((ReasonCodes.ImpossibleAgeAtReportedEvent, IdentityDecision.NoMatch), (r.HardConflict, r.Decision));
            Assert.Equal([ReasonCodes.ImpossibleAgeAtReportedEvent], r.ReasonCodes);
        }
        else
        {
            Assert.Null(r.HardConflict);
            Assert.Equal((6, IdentityDecision.Review), (r.Stage3Row, r.Decision));
            Assert.Equal(ReasonCodes.TemporallyPlausible, r.Evidence[EvidenceGroup.TemporalRole].Detail);
        }
    }

    [Fact]
    public void GC_01_stays_no_match()
    {
        var a = CaseAnalyzer.Analyze(TxtCaseReader.Read(GoldenCases.GoldenCaseTests.Folder("GC-01")));
        Assert.All(a.Hits, h => Assert.Equal(IdentityDecision.NoMatch, h.Identity.Decision));
        Assert.All(a.Hits, h => Assert.DoesNotContain(ReasonCodes.SourceQualityDowngrade, h.Identity.ReasonCodes));
    }

    // Coarser Profile DOBs.

    [Fact]
    public void Coarser_profile_dob_covered_by_a_precise_one_cannot_mask_a_full_dob_conflict()
    {
        var r = IdentityDecisionPolicy.Decide(Subject(new PartialDate(1970, 6, 1)), Profile(new PartialDate(1970, 5, 1), new PartialDate(1970)));
        Assert.Equal((1, IdentityDecision.Review), (r.Stage3Row, r.Decision));
        Assert.Equal(ReasonCodes.FullDobConflict, r.ReasonCodes[0]);
    }

    // Many or scattered Profile DOBs.

    [Fact]
    public void Agreeing_with_one_of_many_scattered_dobs_is_not_support()
    {
        var decade = Enumerable.Range(1960, 10).Select(y => new PartialDate(y)).ToArray();
        var r = IdentityDecisionPolicy.Decide(Subject(new PartialDate(1965, 4, 1)), Profile(decade));
        var dob = r.Evidence[EvidenceGroup.DateOfBirth];
        Assert.Equal((Partial, Strength.None, false), (dob.Status, dob.Strength, dob.IsSupport));
        Assert.NotEqual(IdentityDecision.ProbableMatch, r.Decision);
        Assert.Equal(6, r.Stage3Row);

        var span = IdentityDecisionPolicy.Decide(Subject(new PartialDate(1965, 4, 1)), Profile(new PartialDate(1960), new PartialDate(1965)));
        Assert.Equal((6, IdentityDecision.Review), (span.Stage3Row, span.Decision)); // two values, but over two years apart
        Assert.DoesNotContain(FellegiSunter.Score(span.Evidence).Groups, g => g.Group == EvidenceGroup.DateOfBirth);
    }

    // Subject born 1980-05-20; name, gender and nationality agree, so a supporting birth year reaches row 4.
    [Theory]
    [InlineData(4, new[] { 1980, 1982 })]         // a span of exactly two years is not scattered
    [InlineData(6, new[] { 1980, 1983 })]
    [InlineData(6, new[] { 1979, 1980, 1981 })]   // three values are many even within two years
    public void Profile_dobs_are_scattered_from_three_values_or_a_span_over_two_years(int row, int[] years)
    {
        var r = IdentityDecisionPolicy.Decide(Subject(), Profile(years.Select(y => new PartialDate(y)).ToArray()));
        var dob = r.Evidence[EvidenceGroup.DateOfBirth];
        Assert.Equal(row, r.Stage3Row);
        Assert.Equal(row == 4
            ? (Partial, Strength.Low, true, ReasonCodes.BirthYearMatchFullDateNotComparable)
            : (Partial, Strength.None, false, Findings.ProfileMultipleDob),
            (dob.Status, dob.Strength, dob.IsSupport, dob.Detail));
    }

    [Fact]
    public void Fellegi_sunter_full_dob_agreement_weighs_m_over_one_in_22000()
    {
        var evidence = new Dictionary<EvidenceGroup, Evidence>
        {
            [EvidenceGroup.DateOfBirth] = new(EvidenceGroup.DateOfBirth, Direction.Support, Exact, Strength.High),
        };
        Assert.Equal(Math.Log2(FellegiSunter.PlaceholderM * 22_000), Assert.Single(FellegiSunter.Score(evidence).Groups).Weight, 6);
    }

    [Fact]
    public void Fellegi_sunter_dob_u_is_multiplied_by_the_number_of_profile_dobs()
    {
        var hit = CaseAnalyzer.Analyze(TxtCaseReader.Read(GoldenCases.GoldenCaseTests.Folder("GC-11"))).Hits[0];
        var dob = Assert.Single(FellegiSunter.Score(hit.Identity.Evidence, hit.Profile).Groups); // 1973 and 1972
        Assert.Equal(Math.Log2(FellegiSunter.PlaceholderM * 60 / 2), dob.Weight, 6);
    }

    // Employment and document comparability.

    [Fact]
    public void Occupation_without_employer_is_not_comparable_and_keeps_gc_11_at_row_4()
    {
        var input = TxtCaseReader.Read(GoldenCases.GoldenCaseTests.Folder("GC-11"));
        var hit = CaseAnalyzer.Analyze(input).Hits[0];
        Assert.NotNull(input.Subject.Occupation);
        var profile = hit.Profile with { Roles = [new ProfileRole("業務經理", "和平商業銀行", new PartialDate(2020))] };
        var r = IdentityDecisionPolicy.Decide(input.Subject, profile);
        Assert.Equal((SubjectMissing, EvidenceBuilder.NotComparable),
            (r.Evidence[EvidenceGroup.Employment].Status, r.Evidence[EvidenceGroup.Employment].Detail));
        Assert.Equal((4, IdentityDecision.ProbableMatch), (r.Stage3Row, r.Decision));
    }

    [Fact]
    public void Different_organization_stays_uncertain_and_blocks_row_4()
    {
        var profile = Profile(new PartialDate(1980)) with { Roles = [new ProfileRole("總經理", "宏遠投資股份有限公司", new PartialDate(2015))] };
        var r = IdentityDecisionPolicy.Decide(Subject() with { Employments = [new Employment("和平商業銀行", null, null, null)] }, profile);
        Assert.Equal((Uncertain, "DIFFERENT_ORGANIZATION"), (r.Evidence[EvidenceGroup.Employment].Status, r.Evidence[EvidenceGroup.Employment].Detail));
        Assert.Equal((5, IdentityDecision.Review), (r.Stage3Row, r.Decision)); // the birth year still supports
    }

    [Fact]
    public void Document_of_another_type_does_not_block_row_4()
    {
        var r = IdentityDecisionPolicy.Decide(Subject() with { Document = new Document("TW_NATIONAL_ID", "A123456789", "TW") },
            Profile(new PartialDate(1980)) with { Documents = [new Document("PASSPORT", "300000001", "TW")] });
        Assert.Equal((Uncertain, EvidenceBuilder.NotComparable), (r.Evidence[EvidenceGroup.Document].Status, r.Evidence[EvidenceGroup.Document].Detail));
        Assert.Equal((4, IdentityDecision.ProbableMatch), (r.Stage3Row, r.Decision));
    }

    // Gender conflict.

    [Fact]
    public void Gender_conflict_rules_out_rows_3_and_4()
    {
        var born = new PartialDate(1980, 5, 20);
        var r = IdentityDecisionPolicy.Decide(Subject(born) with { Relatives = [new Relative("許雅婷", "配偶")] },
            Profile(born) with { Gender = Gender.Female, Relationships = [new ProfileRelationship("2", "許雅婷", "Spouse")] });
        Assert.NotEqual(IdentityDecision.ProbableMatch, r.Decision);
        Assert.Equal(5, r.Stage3Row);
        Assert.Contains(ReasonCodes.GenderConflict, r.ReasonCodes);
        Assert.Equal((Conflict, ReasonCodes.GenderConflict), (r.Evidence[EvidenceGroup.Gender].Status, r.Evidence[EvidenceGroup.Gender].Detail));

        var row4 = IdentityDecisionPolicy.Decide(Subject(born, Gender.Female), Profile(new PartialDate(1980)));
        Assert.Equal(5, row4.Stage3Row); // birth year agrees, but opposite gender: row 5, not row 4
        Assert.Contains(ReasonCodes.GenderConflict, row4.ReasonCodes);
    }

    // Row 4 needs gender EXACT; a gender missing on either side is no conflict, but the pair stays at row 5.
    [Theory]
    [InlineData(null, Gender.Male)]
    [InlineData(Gender.Male, null)]
    public void Missing_gender_with_dob_support_is_row_5(Gender? subject, Gender? profile)
    {
        var r = IdentityDecisionPolicy.Decide(Subject() with { Gender = subject }, Profile(new PartialDate(1980)) with { Gender = profile });
        Assert.Equal((5, IdentityDecision.Review), (r.Stage3Row, r.Decision));
        Assert.Equal(ReasonCodes.PartialSupportOnly, r.ReasonCodes[0]);
        Assert.DoesNotContain(ReasonCodes.GenderConflict, r.ReasonCodes);
    }

    // Stated ages and adult-only titles.

    [Fact]
    public void Age_at_the_event_is_not_compared_with_the_report_date()
    {
        var r = IdentityDecisionPolicy.Decide(Subject(), Profile(), News("陳志明時年30歲時涉嫌詐欺。"));
        Assert.Equal((Uncertain, ReasonCodes.AgeNotComparable), (r.Evidence[EvidenceGroup.StatedAge].Status, r.Evidence[EvidenceGroup.StatedAge].Detail));
        Assert.DoesNotContain(ReasonCodes.AgeDifferenceAtReport, r.ReasonCodes);
        Assert.Equal(30, StatedAgeCheck.FindStatedAge("陳志明時年30歲時涉嫌詐欺。", ["陳志明"]));
    }

    [Fact]
    public void Adult_only_titles_match_the_whole_title()
    {
        // Born 1980-05-20: 16 in 1997, when adulthood was 20.
        var marketing = IdentityDecisionPolicy.Decide(Subject(), Profile() with
        { Roles = [new ProfileRole("Director of Marketing", "Example Ltd", new PartialDate(1997, 1))] });
        Assert.Null(marketing.HardConflict);

        var director = IdentityDecisionPolicy.Decide(Subject(), Profile() with
        { Roles = [new ProfileRole("director", "Example Ltd", new PartialDate(1997, 1))] });
        Assert.Equal(ReasonCodes.ImpossibleRoleAtReportedEvent, director.HardConflict);
    }

    // Adulthood was 20 before 2023-01-01 and 18 since.
    [Theory]
    [InlineData(2003, 2022, 12, 31, ReasonCodes.ImpossibleRoleAtReportedEvent)] // 19 on the last day adulthood was 20
    [InlineData(2004, 2023, 1, 1, null)]                                         // 18 on the first day adulthood was 18
    public void Adult_only_titles_follow_the_adulthood_age_in_force_at_the_role_start(int born, int year, int month, int day, string? expected)
    {
        var r = IdentityDecisionPolicy.Decide(Subject(new PartialDate(born, 6, 1)), Profile() with
        { Roles = [new ProfileRole("董事", "宏遠投資股份有限公司", new PartialDate(year, month, day))] });
        Assert.Equal(expected, r.HardConflict);
    }
}
