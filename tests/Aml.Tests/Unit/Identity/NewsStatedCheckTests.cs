using Aml.Core.Identity;
using Aml.Core.Shared;
using Aml.Core.Subjects;
using static Aml.Core.Shared.ComparisonStatus;

namespace Aml.Tests.Unit.Identity;

/// <summary>
/// Birth date and gender that made-up attached news writes next to the Profile person's name, set against the customer
/// (identity-adjudication.md#birth-date-and-gender-stated-in-a-source). The customer is a man born 1980-05-20 (民國69年).
/// </summary>
public class NewsStatedCheckTests
{
    private static readonly PartialDate Born = new(1980, 5, 20);

    private static ScreenedSubject Subject(PartialDate? dob, Gender? gender = Gender.Male, string? reference = null) =>
        new(SubjectRole.Customer, null, ["陳志明"], gender, dob, ["TW"], null, reference, null, null, [], []);

    private static SourceProfile Profile(Gender? gender = Gender.Male, params PartialDate[] dobs) =>
        new("DJ:1", gender, ["陳志明"], dobs, ["TW"], [], [new SourceCategory("Special Interest Person (SIP)", "Fraud")], [], [], [], null, [], []);

    private static NewsItem News(string text) => new("1", "DOC1", "標題", "某報", null, text);

    [Theory]
    [InlineData("民國69年5月20日生", 1980, 5, 20)]
    [InlineData("69年5月生", 1980, 5, null)]
    [InlineData("69年次", 1980, null, null)]
    [InlineData("1980年5月20日出生", 1980, 5, 20)]
    [InlineData("民國６９年５月２０日生", 1980, 5, 20)]
    public void Reads_a_birth_date_as_news_writes_it(string text, int year, int? month, int? day) =>
        Assert.Equal(new PartialDate(year, month, day), NewsStatedCheck.Parse(text));

    [Theory]
    [InlineData("69年13月生")]
    [InlineData("69年2月30日生")]
    [InlineData("5年生")]
    [InlineData("٦٩年次")] // digits of another script
    [InlineData("民國69年5月20日")]
    public void A_text_that_is_no_birth_date_reads_as_nothing(string text) => Assert.Null(NewsStatedCheck.Parse(text));

    // News text, the Profile's own birth year-month-day (0 = none) → the news birth date evidence and the stage 3 row.
    [Theory]
    // The Profile has no birth date: the news value is the only one. A full date is MEDIUM, a coarser one LOW; row 5.
    [InlineData("陳志明（男，民國69年5月20日生）涉嫌詐欺。", 0, Direction.Support, Exact, Strength.Medium, ReasonCodes.NewsBirthDateAgrees, 5)]
    [InlineData("陳志明（男，民國69年5月生）涉嫌詐欺。", 0, Direction.Support, Partial, Strength.Low, ReasonCodes.NewsBirthDateAgrees, 5)]
    [InlineData("陳志明（男，69年次）涉嫌詐欺。", 0, Direction.Support, Partial, Strength.Low, ReasonCodes.NewsBirthDateAgrees, 5)]
    // A different date: soft conflict, row 1.
    [InlineData("陳志明（男，民國70年1月1日生）涉嫌詐欺。", 0, Direction.Conflict, Conflict, Strength.None, ReasonCodes.NewsBirthDateDifference, 1)]
    // The Profile's own full date agrees (row 4): the news adds no second support (that would be row 3), and a
    // different news date is two sources disagreeing, not a conflict with the customer.
    [InlineData("陳志明（男，民國69年5月20日生）涉嫌詐欺。", 19800520, Direction.Neutral, Exact, Strength.None, ReasonCodes.NewsBirthDateAgrees, 4)]
    [InlineData("陳志明（男，民國70年1月1日生）涉嫌詐欺。", 19800520, Direction.Neutral, Uncertain, Strength.None, ReasonCodes.NewsBirthDateDifference, 4)]
    // The Profile's own date is within two years (agreement within tolerance, support): the agreeing news date is the same
    // fact again, neutral; the pair stays on row 4.
    [InlineData("陳志明（男，民國69年5月20日生）涉嫌詐欺。", 19790101, Direction.Neutral, Exact, Strength.None, ReasonCodes.NewsBirthDateAgrees, 4)]
    // Two dates next to one written name, more than two years apart: agreeing with one is no support; none agreeing is a conflict.
    [InlineData("陳志明（男，民國69年5月20日生）涉嫌詐欺。另一名陳志明（男，民國50年3月3日生）遭通緝。", 0, Direction.Neutral, Exact, Strength.None, ReasonCodes.NewsBirthDateAmbiguous, 6)]
    [InlineData("陳志明（男，民國60年5月20日生）涉嫌詐欺。另一名陳志明（男，民國50年3月3日生）遭通緝。", 0, Direction.Conflict, Conflict, Strength.None, ReasonCodes.NewsBirthDateDifference, 1)]
    // Three dates within two years are scattered too.
    [InlineData("陳志明（民國69年5月20日生）、陳志明（民國69年6月1日生）、陳志明（民國70年1月1日生）均遭起訴。", 0, Direction.Neutral, Exact, Strength.None, ReasonCodes.NewsBirthDateAmbiguous, 6)]
    // A coarser value the full one covers is one date, not two.
    [InlineData("陳志明（男，民國69年5月20日生）涉嫌詐欺。陳志明（69年次）否認。", 0, Direction.Support, Exact, Strength.Medium, ReasonCodes.NewsBirthDateAgrees, 5)]
    public void News_birth_date_against_the_customer(string text, int profileDob, Direction direction, ComparisonStatus status, Strength strength, string detail, int row)
    {
        var profile = profileDob == 0 ? Profile() : Profile(Gender.Male, new PartialDate(profileDob / 10000, profileDob / 100 % 100, profileDob % 100));

        var r = IdentityDecisionPolicy.Decide(Subject(Born), profile, News(text));

        var e = r.Evidence[EvidenceGroup.StatedBirthDate];
        Assert.Equal((direction, status, strength, detail), (e.Direction, e.Status, e.Strength, e.Detail));
        Assert.Equal(row, r.Stage3Row);
        Assert.Null(r.HardConflict);
        Assert.NotEqual(IdentityDecision.NoMatch, r.Decision);
    }

    [Fact]
    public void A_profile_dob_more_than_two_years_off_closes_the_pair_whatever_the_news_states()
    {
        // The news date agrees and is still support, but the Profile's own field decides: the validated DOB conflict.
        var r = IdentityDecisionPolicy.Decide(Subject(Born), Profile(Gender.Male, new PartialDate(1970, 1, 1)), News("陳志明（男，民國69年5月20日生）涉嫌詐欺。"));
        var e = r.Evidence[EvidenceGroup.StatedBirthDate];
        Assert.Equal((Direction.Support, Exact, Strength.Medium, ReasonCodes.NewsBirthDateAgrees), (e.Direction, e.Status, e.Strength, e.Detail));
        Assert.Equal((ReasonCodes.FullDobConflict, (int?)null, IdentityDecision.NoMatch), (r.HardConflict, r.Stage3Row, r.Decision));
    }

    [Fact]
    public void A_news_birth_date_conflict_is_the_reason_of_row_1_and_stays_in_review()
    {
        var r = IdentityDecisionPolicy.Decide(Subject(Born), Profile(), News("陳志明（男，民國70年1月1日生）涉嫌詐欺。"));

        Assert.Equal((IdentityDecision.Review, ReasonCodes.NewsBirthDateDifference), (r.Decision, r.ReasonCodes[0]));
        Assert.Equal("新聞所載出生日期 1981-01-01；客戶出生日期 1980-05-20", r.Evidence[EvidenceGroup.StatedBirthDate].Note);
    }

    // The same document on both sides: row 2 whatever the news says, with the news conflict's code alongside.
    // Born 1980-05-20, the news is undated, so only the report date in the second row makes the age comparable.
    [Theory]
    [InlineData("陳志明（男，民國70年1月1日生）涉嫌詐欺。", null, ReasonCodes.NewsBirthDateDifference)]
    [InlineData("陳志明（60歲）涉嫌詐欺。", 2026, ReasonCodes.AgeDifferenceAtReport)]
    public void An_agreeing_document_outranks_a_news_conflict(string text, int? reportYear, string alongside)
    {
        var document = new Document("PASSPORT", "X1234567", "TW");
        var subject = Subject(Born) with { Document = document };
        var news = News(text) with { PublishedOn = reportYear is { } y ? new PartialDate(y, 3, 1) : null };

        var r = IdentityDecisionPolicy.Decide(subject, Profile() with { Documents = [document] }, news);

        Assert.Equal((2, IdentityDecision.ProbableMatch), (r.Stage3Row, r.Decision));
        Assert.Equal([ReasonCodes.UniqueIdentifierAgreement, alongside], r.ReasonCodes);
        Assert.Equal(1, IdentityDecisionPolicy.Decide(Subject(Born), Profile(), news).Stage3Row); // without the document: row 1
    }

    [Fact]
    public void Without_a_customer_birth_date_the_news_date_is_shown_only()
    {
        var e = IdentityDecisionPolicy.Decide(Subject(null), Profile(), News("陳志明（男，民國69年5月20日生）涉嫌詐欺。")).Evidence[EvidenceGroup.StatedBirthDate];

        Assert.Equal((Direction.Neutral, SubjectMissing, (string?)null, "新聞所載出生日期 1980-05-20"), (e.Direction, e.Status, e.Detail, e.Note));
    }

    [Fact]
    public void News_without_a_birth_date_or_gender_next_to_the_name_adds_no_evidence()
    {
        var e = IdentityDecisionPolicy.Decide(Subject(Born), Profile(), News("陳志明涉嫌詐欺。王大明（女，民國70年1月1日生）另案偵辦。")).Evidence;

        Assert.False(e.ContainsKey(EvidenceGroup.StatedBirthDate));
        Assert.False(e.ContainsKey(EvidenceGroup.StatedGender));
        Assert.False(IdentityDecisionPolicy.Decide(Subject(Born), Profile()).Evidence.ContainsKey(EvidenceGroup.StatedGender));
    }

    // News text, the Profile's own gender → the news gender evidence.
    [Theory]
    [InlineData("陳志明（男）涉嫌詐欺。", null, Direction.Neutral, Exact, ReasonCodes.NewsGenderAgrees)]
    [InlineData("陳志明（女）涉嫌詐欺。", null, Direction.Conflict, Conflict, ReasonCodes.NewsGenderDifference)]
    [InlineData("陳志明（女）涉嫌詐欺。", Gender.Female, Direction.Conflict, Conflict, ReasonCodes.NewsGenderDifference)]
    [InlineData("陳志明（女）涉嫌詐欺。", Gender.Male, Direction.Neutral, Uncertain, ReasonCodes.NewsGenderDifference)] // the Profile agrees: two sources disagree
    public void News_gender_against_the_customer(string text, Gender? profileGender, Direction direction, ComparisonStatus status, string detail)
    {
        var e = IdentityDecisionPolicy.Decide(Subject(Born), Profile(profileGender), News(text)).Evidence[EvidenceGroup.StatedGender];

        Assert.Equal((direction, status, Strength.None, detail), (e.Direction, e.Status, e.Strength, e.Detail));
    }

    [Fact]
    public void Both_genders_next_to_one_written_name_or_no_customer_gender_is_no_reading()
    {
        Assert.Null(NewsStatedCheck.CheckGender(Subject(Born), Profile(null), News("陳志明（男）涉嫌詐欺。另一名陳志明（女）遭通緝。")));
        Assert.Null(NewsStatedCheck.CheckGender(Subject(Born, gender: null), Profile(null), News("陳志明（女）涉嫌詐欺。")));
    }

    // The Profile's full birth date agrees (HIGH) and the reference number is the same (LOW): row 3 without the news.
    [Theory]
    [InlineData("陳志明（男）涉嫌詐欺。", 3, IdentityDecision.ProbableMatch, false)]
    [InlineData("陳志明（女）涉嫌詐欺。", 5, IdentityDecision.Review, true)]
    public void A_news_gender_difference_rules_out_row_3(string text, int row, IdentityDecision decision, bool coded)
    {
        var profile = Profile(null, Born) with { OtherIdentifiers = [new OtherIdentifier("Ref", "X123")] };

        var r = IdentityDecisionPolicy.Decide(Subject(Born, reference: "X123"), profile, News(text));

        Assert.Equal((row, decision), (r.Stage3Row, r.Decision));
        Assert.Equal(coded, r.ReasonCodes.Contains(ReasonCodes.NewsGenderDifference));
    }
}
