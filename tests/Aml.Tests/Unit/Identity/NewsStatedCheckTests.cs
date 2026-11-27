using Aml.Core.Identity;
using Aml.Core.Scoring;
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
    // Two dates next to one written name, more than two years apart: agreeing with one is no support (none agreeing, both
    // far, is the hard conflict: A_list_profile_news_value_more_than_two_years_off_concludes_no_match).
    [InlineData("陳志明（男，民國69年5月20日生）涉嫌詐欺。另一名陳志明（男，民國50年3月3日生）遭通緝。", 0, Direction.Neutral, Exact, Strength.None, ReasonCodes.NewsBirthDateAmbiguous, 6)]
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

    // A person of a received page (user, 2026-11-27): every comparable value the page states more than two years from the
    // customer's birth date (1980-05-20) is the validated hard conflict; within two years it is neutral, never support.
    // Text, publication date (yyyymmdd, 0 = MISSING) → hard conflict, decision, stage 3 row, score band (news factor).
    [Theory]
    // Birth dates: exactly two years is within (neutral, row 6); one day more is the conflict.
    [InlineData("陳志明（男，民國71年5月20日生）涉嫌詐欺。", 0, null, IdentityDecision.Review, 6, IdentityBand.Review)]
    [InlineData("陳志明（男，民國71年5月21日生）涉嫌詐欺。", 0, ReasonCodes.StatedDobConflict, IdentityDecision.NoMatch, null, IdentityBand.NoMatch)]
    [InlineData("陳志明（男，民國67年5月19日生）涉嫌詐欺。", 0, ReasonCodes.StatedDobConflict, IdentityDecision.NoMatch, null, IdentityBand.NoMatch)]
    // A year-only value is compared at its precision: 1978 may be two years from 1980, 1977 cannot.
    [InlineData("陳志明（男，67年次）涉嫌詐欺。", 0, null, IdentityDecision.Review, 6, IdentityBand.Review)]
    [InlineData("陳志明（男，66年次）涉嫌詐欺。", 0, ReasonCodes.StatedDobConflict, IdentityDecision.NoMatch, null, IdentityBand.NoMatch)]
    // Ages at the publication date 2026-05-20 (the customer is 46): 48 is born 1977-05-21 to 1978-05-20, exactly two years
    // at the latest, so within; 49 more than two years; 44 born 1981-05-21 to 1982-05-20, within; 43 beyond.
    [InlineData("陳志明（48歲）涉嫌詐欺。", 20260520, null, IdentityDecision.Review, 6, IdentityBand.Review)]
    [InlineData("陳志明（49歲）涉嫌詐欺。", 20260520, ReasonCodes.StatedDobConflict, IdentityDecision.NoMatch, null, IdentityBand.NoMatch)]
    [InlineData("陳志明（44歲）涉嫌詐欺。", 20260520, null, IdentityDecision.Review, 6, IdentityBand.Review)]
    [InlineData("陳志明（43歲）涉嫌詐欺。", 20260520, ReasonCodes.StatedDobConflict, IdentityDecision.NoMatch, null, IdentityBand.NoMatch)]
    // No publication date, or an age at the event: not comparable, nothing concludes (row 6).
    [InlineData("陳志明（60歲）涉嫌詐欺。", 0, null, IdentityDecision.Review, 6, IdentityBand.Review)]
    [InlineData("陳志明時年60歲涉嫌詐欺。", 20260520, null, IdentityDecision.Review, 6, IdentityBand.Review)]
    // 時年 before the age that precedes the name marks it the same way; the same age unmarked before the name is compared.
    [InlineData("時年60歲的陳志明涉嫌詐欺。", 20260520, null, IdentityDecision.Review, 6, IdentityBand.Review)]
    [InlineData("時年 60 歲陳志明涉嫌詐欺。", 20260520, null, IdentityDecision.Review, 6, IdentityBand.Review)]
    [InlineData("60歲的陳志明涉嫌詐欺。", 20260520, ReasonCodes.StatedDobConflict, IdentityDecision.NoMatch, null, IdentityBand.NoMatch)]
    // Every comparable value must be off: an age within ±2 beside a far birth date keeps the soft conflict (row 1).
    [InlineData("陳志明（男，民國60年1月1日生）涉嫌詐欺。陳志明（46歲）否認。", 20260520, null, IdentityDecision.Review, 1, IdentityBand.Review)]
    // An age not comparable beside a far birth date: the birth date decides.
    [InlineData("陳志明（男，民國60年1月1日生）涉嫌詐欺。陳志明（46歲）否認。", 0, ReasonCodes.StatedDobConflict, IdentityDecision.NoMatch, null, IdentityBand.NoMatch)]
    public void A_page_stated_value_more_than_two_years_off_concludes_no_match(string text, int published, string? hard, IdentityDecision decision, int? row, IdentityBand band)
    {
        var news = News(text) with { PublishedOn = published == 0 ? null : new PartialDate(published / 10000, published / 100 % 100, published % 100) };

        var r = IdentityDecisionPolicy.Decide(Subject(Born), PageProfile, news);

        Assert.Equal((hard, decision, row), (r.HardConflict, r.Decision, r.Stage3Row));
        Assert.DoesNotContain(r.Evidence.Values, e => e.IsSupport && e.Group is EvidenceGroup.StatedAge or EvidenceGroup.StatedBirthDate);
        var score = ScoringRule.Identity(r, entityRecord: false, record: SourceKind.AllowlistedNews, text: SourceKind.AllowlistedNews);
        Assert.Equal(band, score.Band);
    }

    // A customer born 2000-02-29, whose date plus two years is 2002-02-28 (PartialDate.IsMoreThanYearsApart): exactly two
    // years is not a conflict, one day more is, by the birth-date path and the age path alike. The age N at the publication
    // date allows the births from the day after the date N + 1 years before to the date N years before.
    [Theory]
    [InlineData("陳志明（男，民國91年2月28日生）涉嫌詐欺。", 0, null)] // 2002-02-28: exactly two years
    [InlineData("陳志明（男，民國91年3月1日生）涉嫌詐欺。", 0, ReasonCodes.StatedDobConflict)]
    [InlineData("陳志明（男，民國87年3月1日生）涉嫌詐欺。", 0, null)] // 1998-03-01: under two years before
    [InlineData("陳志明（男，民國87年2月28日生）涉嫌詐欺。", 0, ReasonCodes.StatedDobConflict)]
    [InlineData("陳志明（23歲）涉嫌詐欺。", 20260227, null)] // born 2002-02-28 to 2003-02-27: exactly two years at the earliest
    [InlineData("陳志明（23歲）涉嫌詐欺。", 20260228, ReasonCodes.StatedDobConflict)] // born 2002-03-01 at the earliest
    [InlineData("陳志明（28歲）涉嫌詐欺。", 20260301, null)] // born 1997-03-02 to 1998-03-01
    [InlineData("陳志明（28歲）涉嫌詐欺。", 20260228, ReasonCodes.StatedDobConflict)] // born 1998-02-28 at the latest
    public void A_29_february_customer_is_two_years_from_28_february_by_birth_date_and_by_age(string text, int published, string? hard)
    {
        var news = News(text) with { PublishedOn = published == 0 ? null : new PartialDate(published / 10000, published / 100 % 100, published % 100) };

        var r = IdentityDecisionPolicy.Decide(Subject(new PartialDate(2000, 2, 29)), PageProfile, news);

        Assert.Equal((hard, hard is null ? IdentityDecision.Review : IdentityDecision.NoMatch), (r.HardConflict, r.Decision));
    }

    // Known limitation, documented (identity-adjudication.md#two-year-conflict-on-stated-values): an age a page states as of a
    // past event without 時年 is compared at the publication date. The customer (46 then) was 36 at the event ten years before,
    // as stated, and the rule closes the same person today. A fix must change this row deliberately.
    [Fact]
    public void Known_limitation_an_age_stated_for_a_past_event_without_shi_nian_is_compared_at_publication_and_closes_the_same_person()
    {
        var news = News("陳志明（36歲）於2016年間涉嫌詐欺，十年後案件仍未審結。") with { PublishedOn = new PartialDate(2026, 5, 20) };

        var r = IdentityDecisionPolicy.Decide(Subject(Born), PageProfile, news);

        Assert.Equal((ReasonCodes.StatedDobConflict, IdentityDecision.NoMatch), (r.HardConflict, r.Decision));
    }

    [Fact]
    public void A_page_birth_date_within_two_years_is_neutral_and_scores_nothing()
    {
        var r = IdentityDecisionPolicy.Decide(Subject(Born), PageProfile, News("陳志明（男，民國70年1月1日生）涉嫌詐欺。"));

        var e = r.Evidence[EvidenceGroup.StatedBirthDate];
        Assert.Equal((Direction.Neutral, Partial, Strength.None, ReasonCodes.PageBirthDateWithinTolerance), (e.Direction, e.Status, e.Strength, e.Detail));
        Assert.Equal((6, ReasonCodes.NoNonPrerequisiteSupport), (r.Stage3Row, r.ReasonCodes[0]));
        Assert.Empty(ScoringRule.Identity(r, entityRecord: false, record: SourceKind.AllowlistedNews, text: SourceKind.AllowlistedNews).Items);
        // The same text attached to a list Profile keeps its difference (row 1).
        Assert.Equal(1, IdentityDecisionPolicy.Decide(Subject(Born), Profile(), News("陳志明（男，民國70年1月1日生）涉嫌詐欺。")).Stage3Row);
    }

    [Fact]
    public void Without_a_customer_birth_date_a_page_value_concludes_nothing()
    {
        var r = IdentityDecisionPolicy.Decide(Subject(null), PageProfile, News("陳志明（男，民國50年1月1日生）涉嫌詐欺。") with { PublishedOn = new(2026, 5, 20) });

        Assert.Equal(((string?)null, IdentityDecision.Review), (r.HardConflict, r.Decision));
    }

    // A list Profile's attached news (user, 2026-11-27): the same conflict as a page's, on the NO_MATCH side only. The
    // customer is born 1980-05-20; the news is dated 2026-05-20 (0 = undated); the Profile's own DOB yyyymmdd (0 = none).
    // Text, report date, Profile DOB → hard conflict, stage 3 row, decision.
    [Theory]
    // More than two years off, no Profile DOB: concluded.
    [InlineData("陳志明（49歲）涉嫌詐欺。", 20260520, 0, ReasonCodes.StatedDobConflict, null, IdentityDecision.NoMatch)]
    [InlineData("陳志明（男，民國67年5月19日生）涉嫌詐欺。", 0, 0, ReasonCodes.StatedDobConflict, null, IdentityDecision.NoMatch)]
    [InlineData("陳志明（男，民國60年5月20日生）涉嫌詐欺。另一名陳志明（男，民國50年3月3日生）遭通緝。", 0, 0, ReasonCodes.StatedDobConflict, null, IdentityDecision.NoMatch)]
    // Known limitation, as on a page: an age as of a past event without 時年 is compared at the report date and closes the
    // same person (36 in 2016, as stated).
    [InlineData("陳志明（36歲）於2016年間涉嫌詐欺，十年後案件仍未審結。", 20260520, 0, ReasonCodes.StatedDobConflict, null, IdentityDecision.NoMatch)]
    // Within two years, exactly two included: unchanged (an age compatible, row 6; a news birth date that differs, row 1).
    [InlineData("陳志明（48歲）涉嫌詐欺。", 20260520, 0, null, 6, IdentityDecision.Review)]
    [InlineData("陳志明（男，民國71年5月20日生）涉嫌詐欺。", 0, 0, null, 1, IdentityDecision.Review)]
    [InlineData("陳志明（男，民國70年1月1日生）涉嫌詐欺。", 0, 0, null, 1, IdentityDecision.Review)]
    // The Profile's own DOB agrees, exactly or within two years: the news may be about someone else; row 1 as before.
    [InlineData("陳志明（49歲）涉嫌詐欺。", 20260520, 19800520, null, 1, IdentityDecision.Review)]
    [InlineData("陳志明（49歲）涉嫌詐欺。", 20260520, 19810101, null, 1, IdentityDecision.Review)]
    // Not comparable (undated age): unchanged.
    [InlineData("陳志明（60歲）涉嫌詐欺。", 0, 0, null, 6, IdentityDecision.Review)]
    public void A_list_profile_news_value_more_than_two_years_off_concludes_no_match(string text, int reported, int profileDob, string? hard, int? row, IdentityDecision decision)
    {
        var profile = profileDob == 0 ? Profile() : Profile(Gender.Male, new PartialDate(profileDob / 10000, profileDob / 100 % 100, profileDob % 100));
        var news = News(text) with { PublishedOn = reported == 0 ? null : new PartialDate(reported / 10000, reported / 100 % 100, reported % 100) };

        var r = IdentityDecisionPolicy.Decide(Subject(Born), profile, news);

        Assert.Equal((hard, row, decision), (r.HardConflict, r.Stage3Row, r.Decision));
    }

    // Only a value read next to a full name form concludes: a CJK form of three characters or more, a Latin one as a whole
    // word. A shorter alias may sit inside another person's name (志明 in 林志明); what it reads stays evidence (row 1) and
    // never concludes. The customer is born 1980-05-20; the news is dated 2026-05-20, so 30 is far.
    // Profile names (| between), text → hard conflict, row.
    [Theory]
    [InlineData("陳大華|志明", "林志明（30歲）涉嫌詐欺。", null, 1)]
    [InlineData("陳大華|志明", "林志明（男，民國50年1月1日生）涉嫌詐欺。", null, 1)]
    [InlineData("陳大華|志明", "陳大華（30歲）涉嫌詐欺。", ReasonCodes.StatedDobConflict, null)]
    [InlineData("陳大華|志明", "陳大華（男，民國50年1月1日生）涉嫌詐欺。", ReasonCodes.StatedDobConflict, null)]
    [InlineData("陳大華|Ming", "Chen Chih-Ming, aged 30, was charged.", null, 1)]
    [InlineData("陳大華|Chih-Ming Chen", "Chih-Ming Chen, aged 30, was charged.", ReasonCodes.StatedDobConflict, null)]
    // The short alias reads a compatible age, the full name a far birth date: the near value still blocks.
    [InlineData("陳大華|志明", "林志明（46歲）涉嫌詐欺。陳大華（男，民國50年1月1日生）遭通緝。", null, 1)]
    // Known limitation, pinned: a three-character name inside another person's four-character name (歐陽大華) still
    // concludes. A fix must change this row deliberately.
    [InlineData("陽大華", "歐陽大華（30歲）涉嫌詐欺。", ReasonCodes.StatedDobConflict, null)]
    // An anonymised or masked form fits anyone of that surname: what it reads stays evidence, as a short alias's does.
    [InlineData("陳大華|林姓男子", "林姓男子（30歲）涉嫌詐欺。", null, 1)]
    [InlineData("陳大華|歐陽○明", "歐陽○明（30歲）涉嫌詐欺。", null, 1)]
    // A four-character name of a compound surname is a full form.
    [InlineData("歐陽志明", "歐陽志明（30歲）涉嫌詐欺。", ReasonCodes.StatedDobConflict, null)]
    public void Only_a_value_read_next_to_a_full_name_form_concludes(string names, string text, string? hard, int? row)
    {
        var profile = Profile() with { Names = names.Split('|') };
        var news = News(text) with { PublishedOn = new PartialDate(2026, 5, 20) };

        var r = IdentityDecisionPolicy.Decide(Subject(Born), profile, news);

        Assert.Equal((hard, row), (r.HardConflict, r.Stage3Row));
    }

    // A full form names somebody. Anonymised forms (surname + 姓 + any noun, or surname + 男, 女, 嫌 …) and masked ones
    // (○, Ｏ, O, ＊, *, Ｘ, X, 某) are not; 姓 counts only right after the surname, so a 姓 elsewhere stays a name character.
    [Theory]
    [InlineData("陳志明", true)]
    [InlineData("歐陽志明", true)]
    [InlineData("志明", false)]
    [InlineData("林姓男子", false)]
    [InlineData("王姓女子", false)]
    [InlineData("陳姓嫌犯", false)]
    [InlineData("張姓負責人", false)]
    [InlineData("林姓主嫌", false)]
    [InlineData("歐陽姓男子", false)]
    [InlineData("歐陽男", false)]
    [InlineData("歐陽○明", false)]
    [InlineData("歐陽Ｏ明", false)]
    [InlineData("歐陽O明", false)]
    [InlineData("歐陽＊明", false)]
    [InlineData("歐陽*明", false)]
    [InlineData("歐陽X明", false)]
    [InlineData("陳某某", false)]
    [InlineData("陳志姓", true)]
    [InlineData("林俊男", true)] // a real name ending in 男 is not an anonymised form
    [InlineData("王勝男", true)]
    [InlineData("Xavier Ono", true)]
    public void A_full_form_names_somebody(string name, bool full) => Assert.Equal(full, StatedAgeCheck.IsFullForm(name));

    // List Profiles, the customer born 1980-05-20, the news dated 2026-05-20: what keeps a far value from concluding.
    [Fact]
    public void A_near_and_a_far_news_birth_date_none_agreeing_stay_on_row_1()
    {
        var r = IdentityDecisionPolicy.Decide(Subject(Born), Profile(), News("陳志明（男，民國70年1月1日生）涉嫌詐欺。另一名陳志明（男，民國50年3月3日生）遭通緝。"));

        Assert.Equal(((string?)null, 1, ReasonCodes.NewsBirthDateDifference), (r.HardConflict, r.Stage3Row, r.ReasonCodes[0]));
    }

    [Fact]
    public void An_age_within_two_years_beside_a_far_news_birth_date_does_not_conclude()
    {
        var r = IdentityDecisionPolicy.Decide(Subject(Born), Profile(), News("陳志明（男，民國60年1月1日生）涉嫌詐欺。陳志明（46歲）否認。") with { PublishedOn = new(2026, 5, 20) });

        Assert.Equal(((string?)null, 1, IdentityDecision.Review), (r.HardConflict, r.Stage3Row, r.Decision));
    }

    [Fact]
    public void An_unparseable_profile_value_downgrades_the_stated_conflict_to_review()
    {
        var profile = Profile() with { Findings = [Findings.Unparseable + ":出生日期"] };

        var r = IdentityDecisionPolicy.Decide(Subject(Born), profile, News("陳志明（49歲）涉嫌詐欺。") with { PublishedOn = new(2026, 5, 20) });

        Assert.Equal((ReasonCodes.StatedDobConflict, (int?)null, IdentityDecision.Review), (r.HardConflict, r.Stage3Row, r.Decision));
        Assert.Equal([ReasonCodes.StatedDobConflict, ReasonCodes.SourceQualityDowngrade], r.ReasonCodes);
    }

    [Fact]
    public void Scattered_profile_dobs_keep_a_far_news_age_from_concluding()
    {
        // Three Profile DOBs, one within two years: PROFILE_MULTIPLE_DOB, neutral; the Profile has a DOB of its own.
        var profile = Profile(Gender.Male, new PartialDate(1979, 1, 1), new PartialDate(1960, 1, 1), new PartialDate(1990, 1, 1));

        var r = IdentityDecisionPolicy.Decide(Subject(Born), profile, News("陳志明（49歲）涉嫌詐欺。") with { PublishedOn = new(2026, 5, 20) });

        Assert.Equal(Findings.ProfileMultipleDob, r.Evidence[EvidenceGroup.DateOfBirth].Detail);
        Assert.Equal(((string?)null, 1, IdentityDecision.Review), (r.HardConflict, r.Stage3Row, r.Decision));
    }

    [Fact]
    public void A_medium_support_does_not_keep_a_far_news_age_from_concluding()
    {
        // Today's behaviour, pinned: only HIGH or UNIQUE support (a full DOB, a document) blocks; an employer agreeing over the
        // same period is MEDIUM and the far age still concludes.
        var subject = Subject(Born) with { Employments = [new Employment("宏遠投資股份有限公司", null, new PartialDate(2010), null)] };
        var profile = Profile() with { Roles = [new ProfileRole("經理", "宏遠投資股份有限公司", new PartialDate(2012, 1, 1))] };

        var r = IdentityDecisionPolicy.Decide(subject, profile, News("陳志明（49歲）涉嫌詐欺。") with { PublishedOn = new(2026, 5, 20) });

        Assert.Equal((Direction.Support, Strength.Medium), (r.Evidence[EvidenceGroup.Employment].Direction, r.Evidence[EvidenceGroup.Employment].Strength));
        Assert.Equal((ReasonCodes.StatedDobConflict, IdentityDecision.NoMatch), (r.HardConflict, r.Decision));
    }

    // A person read from a received page, as PageCandidates.Profile maps it: names only.
    private static readonly SourceProfile PageProfile =
        new("WEB:P1-1", null, ["陳志明"], [], [], [], [], [], [], [], null, [], []);

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
    // Without the document: the birth date within two years stays row 1; the age 14 years off is the hard conflict.
    [InlineData("陳志明（男，民國70年1月1日生）涉嫌詐欺。", null, ReasonCodes.NewsBirthDateDifference, null)]
    [InlineData("陳志明（60歲）涉嫌詐欺。", 2026, ReasonCodes.AgeDifferenceAtReport, ReasonCodes.StatedDobConflict)]
    public void An_agreeing_document_outranks_a_news_conflict(string text, int? reportYear, string alongside, string? hardWithout)
    {
        var document = new Document("PASSPORT", "X1234567", "TW");
        var subject = Subject(Born) with { Document = document };
        var news = News(text) with { PublishedOn = reportYear is { } y ? new PartialDate(y, 3, 1) : null };

        var r = IdentityDecisionPolicy.Decide(subject, Profile() with { Documents = [document] }, news);

        Assert.Equal((2, IdentityDecision.ProbableMatch), (r.Stage3Row, r.Decision));
        Assert.Equal([ReasonCodes.UniqueIdentifierAgreement, alongside], r.ReasonCodes);
        var without = IdentityDecisionPolicy.Decide(Subject(Born), Profile(), news);
        Assert.Equal((hardWithout, hardWithout is null ? 1 : (int?)null), (without.HardConflict, without.Stage3Row));
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
