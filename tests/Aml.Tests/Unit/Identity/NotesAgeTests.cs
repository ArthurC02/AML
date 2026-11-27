using Aml.Core.Cases;
using Aml.Core.Explanation;
using Aml.Core.Identity;
using Aml.Core.Scoring;
using Aml.Core.Shared;
using Aml.Core.Subjects;

namespace Aml.Tests.Unit.Identity;

/// <summary>
/// Implausible age at a dated event of the Profile notes, advisory (identity-adjudication.md#implausible-age-at-a-dated-document-advisory,
/// extraction-and-normalization.md#profile-notes): the customer under 14 at most, or not yet born, at the earliest event the
/// English notes date for the Profile person gives AGE_IMPLAUSIBLE_AT_DOCUMENT on stage 3 row 1 (REVIEW); never a
/// conclusion. Invented text; the customer is born 2002-06-15, the Profile 張文華 / Chang Wen-hua has no birth date.
/// </summary>
public class NotesAgeTests
{
    private static readonly PartialDate Child = new(2002, 6, 15);

    private static ScreenedSubject Customer(Document? document = null, PartialDate? dob = null) =>
        new(SubjectRole.Customer, null, ["張文華"], Gender.Male, dob ?? Child, ["TW"], document, null, null, null, [], []);

    private static SourceProfile Profile(string text, string? label = "Profile", string latin = "Chang Wen-hua", string? other = null) =>
        new("DJ:1", Gender.Male, ["張文華", latin], [], ["TW"], [], [new SourceCategory("Special Interest Person (SIP)", "Fraud")], [], [], [], null, [], [],
            Notes: new ProfileNotes($"Last Reviewed Date: 14-Sep-2026 {(other is null ? "" : $"Keywords: {other} ")}Profile: {text}", "名單資料_1.txt:20",
                [new ProfileNotePart("Last Reviewed Date", "14-Sep-2026"), .. other is null ? Array.Empty<ProfileNotePart>() : [new ProfileNotePart("Keywords", other)], new ProfileNotePart(label, text)]));

    private static int? MaxAge(string text, PartialDate? dob = null, string latin = "Chang Wen-hua", string? other = null) =>
        HardConflictGate.ImplausibleAgeInNotes(Customer(dob: dob), Profile(text, latin: latin, other: other))?.MaxAge;

    [Theory]
    // A number is a year only in a date context.
    [InlineData("Chang Wen-hua was fined USD 2000 for fraud.")]
    [InlineData("Chang Wen-hua was fined NT$2000.")]
    [InlineData("Chang Wen-hua was fined 2000 dollars.")]
    [InlineData("Chang Wen-hua was charged under Docket 2005/123.")]
    [InlineData("Chang Wen-hua was charged in Case 2005-123.")]
    [InlineData("Chang Wen-hua was charged in 2005-123.")] // a context year a number follows
    [InlineData("Chang Wen-hua was fined in 2000 shares.")]
    [InlineData("Chang Wen-hua was charged under Article 2005 of the code.")]
    [InlineData("Chang Wen-hua was charged with 2005 others.")]
    [InlineData("Chang Wen-hua was fined 2,005,000 in total.")]
    // Another party as the subject, a company, a two-letter token alone.
    [InlineData("Chang's company Sample Ltd was fined in 2003.")]
    [InlineData("A victim said he was defrauded in 2004.")]
    [InlineData("Sample Inc was sanctioned in 2004 and Chang Wen-hua resigned.")]
    public void Not_an_event_of_the_profile_person(string text)
    {
        Assert.Null(MaxAge(text));
    }

    [Theory]
    [InlineData("Chang Wen-hua was charged in 2005.", 3)]
    [InlineData("Chang Wen-hua was charged since 2005.", 3)]
    [InlineData("Chang Wen-hua was arrested by 2005.", 3)]
    [InlineData("Chang Wen-hua was charged in early 2005.", 3)]
    [InlineData("Chang Wen-hua was charged during the year 2005.", 3)]
    [InlineData("Chang Wen-hua defrauded investors from 2004 to 2006.", 2)]
    [InlineData("Chang Wen-hua was charged on March 12, 2005.", 2)]
    [InlineData("Chang Wen-hua was charged on 12-Mar-2005.", 2)]
    [InlineData("Chang Wen-hua was fined in 2005, and he paid.", 3)] // a comma and a space are no number
    public void The_valid_date_forms_are_still_read(string text, int maxAge)
    {
        Assert.Equal(maxAge, MaxAge(text));
    }

    [Fact]
    public void A_two_letter_token_alone_never_identifies_the_person_the_full_form_does()
    {
        Assert.Null(MaxAge("An was charged in 2005.", latin: "An Li-hua"));
        Assert.Null(MaxAge("Li was charged in 2005.", latin: "An Li"));
        Assert.Equal(3, MaxAge("An Li was charged in 2005.", latin: "An Li"));
        Assert.Equal(3, MaxAge("Li-hua was charged in 2005.", latin: "An Li-hua")); // a token of three letters or more, capitalised
        Assert.Null(MaxAge("The chen was charged in 2005.", latin: "Chen Wu")); // a token not written capitalised
        Assert.Equal(3, MaxAge("Chang Wen-hua was charged in mid-2005."));
    }

    [Theory]
    [InlineData("DOB: 2002", null)]               // a birth year within two years, in another part: silent
    [InlineData("Date of Birth: 12 June 2001", null)]
    [InlineData("Born 2003.", null)]
    [InlineData("Age: 45", null)]                 // any stated age: silent
    [InlineData("a 45-year-old trader", null)]
    [InlineData("aged 45", null)]
    [InlineData("DOB: 1960", 3)]                  // far from the customer's: concludes nothing, the hint stays
    public void Birth_and_age_guards_read_the_whole_notes(string other, int? maxAge)
    {
        Assert.Equal(maxAge, MaxAge("Chang Wen-hua was charged in 2005.", other: other));
    }

    [Theory]
    [InlineData(2002, 6, 15, "on 15 June 2016", null)] // the 14th birthday: 14
    [InlineData(2002, 6, 15, "on 14 June 2016", 13)]   // the day before
    [InlineData(2004, 2, 29, "on 28 February 2018", 13)] // a 29 February birthday: not yet 14 on 28 February
    [InlineData(2004, 2, 29, "on 1 March 2018", null)]
    public void The_fourteenth_birthday_is_the_boundary(int y, int m, int d, string on, int? maxAge)
    {
        Assert.Equal(maxAge, MaxAge($"Chang Wen-hua was charged {on}.", new PartialDate(y, m, d)));
    }

    [Theory]
    [InlineData(100, 3)]
    [InlineData(101, null)]
    public void Over_a_hundred_dates_the_notes_are_silent(int count, int? maxAge)
    {
        Assert.Equal(maxAge, MaxAge(string.Concat(Enumerable.Repeat("Chang Wen-hua was charged in 2005. ", count))));
    }

    private static bool Hints(IdentityResult r) => r.Evidence.GetValueOrDefault(EvidenceGroup.TemporalRole)?.Detail == ReasonCodes.AgeImplausibleAtDocument;

    [Theory]
    // Strangers the hint reaches: an act of the Profile person dated when the customer was a small child or not yet born.
    [InlineData("Chang Wen-hua was charged by the Sample District Prosecutors Office on 12 March 2005 with fraud.", 2)]
    [InlineData("Chang defrauded investors between 1996 and 1999 and was indicted in 2004.", -6)] // the earliest year of the span
    [InlineData("He was convicted in 2012 of breach of trust.", 10)]
    [InlineData("Chang Wen-hua allegedly embezzled funds in 2006.", 4)] // an alleged act still counts
    [InlineData("Chang Wen-hua was arrested in May 2016.", 13)]
    [InlineData("In 2004 Chang Wen-hua was jailed; his sister was charged in 2020.", 2)] // ; ends the sentence: the first one is his
    [InlineData("Chang Wen-hua was fined on 2005-03-12.", 2)]
    // Not hinted: 14 or older, or no event of the Profile person read.
    [InlineData("Chang Wen-hua was arrested in March 2017.", null)] // 14 at most: the June birthday is surely still to come
    [InlineData("Chang Wen-hua was convicted in 2018.", null)] // 16 at most
    [InlineData("Chang Wen-hua will be tried in 2099.", null)] // a future year is no event
    [InlineData("Chang Wen-hua's father was convicted of fraud in 2005.", null)]
    [InlineData("Chang Wen-hua's brother was jailed in 2004; Chang Wen-hua was charged in 2020.", null)] // only the later one is his: 18
    [InlineData("Sample Trading Co. was founded in 2005 by Chang Wen-hua.", null)]
    [InlineData("The record was last updated in 2006 after Chang Wen-hua was charged.", null)]
    [InlineData("Chang Wen-hua was born in 2003 and was sentenced in 2010.", null)] // a birth sentence, and a birth year near the customer's
    [InlineData("Chang Wen-hua was charged in 2008 under the 2001 Securities Act.", null)] // a law named by year: the sentence is skipped
    [InlineData("Chang Wen-hua was not charged in 2005.", null)]
    [InlineData("Chang Wen-hua denied being charged in 2005.", null)]
    [InlineData("Chang Wen-hua could be charged in 2005 if the evidence holds.", null)]
    [InlineData("Chang Wen-hua, aged 45, was charged in 2005.", null)] // a stated age silences the notes
    [InlineData("Lin Ta-hua was charged in 2005.", null)] // no reference to the Profile person
    [InlineData("Chang Wen-hua was charged with fraud by the Sample District Prosecutors Office.", null)] // no date
    [InlineData("Chang Wen-hua chaired a firm in 2005.", null)] // no act word
    public void The_earliest_event_of_the_profile_person_under_fourteen_is_hinted(string text, int? maxAge)
    {
        var r = IdentityDecisionPolicy.Decide(Customer(), Profile(text), []);
        Assert.Equal((maxAge is not null, maxAge), (Hints(r), HardConflictGate.ImplausibleAgeInNotes(Customer(), Profile(text))?.MaxAge));
        Assert.NotEqual(IdentityDecision.NoMatch, r.Decision);
        Assert.Null(r.HardConflict);
    }

    [Fact]
    public void A_hint_is_review_on_row_one_with_the_event_and_the_sentence_and_no_score_item()
    {
        var r = IdentityDecisionPolicy.Decide(Customer(), Profile("Chang Wen-hua was charged in 2005. He was convicted on 12 March 2007."), []);
        Assert.Equal((IdentityDecision.Review, 1, ReasonCodes.AgeImplausibleAtDocument), (r.Decision, r.Stage3Row, r.ReasonCodes[0]));
        Assert.Equal("名單附註（名單資料_1.txt:20 Profile 第 1 句）記載名單人物「charged」於 2005，當時客戶最多 3 歲；未自動判定，請覆核", r.Evidence[EvidenceGroup.TemporalRole].Note);
        var score = ScoringRule.Identity(r, entityRecord: false);
        Assert.Equal(IdentityBand.Review, score.Band);
        Assert.DoesNotContain(score.Items, i => i.Code == ReasonCodes.AgeImplausibleAtDocument);

        var before = IdentityDecisionPolicy.Decide(Customer(), Profile("Chang Wen-hua defrauded investors between 1996 and 1999."), []);
        Assert.Equal("名單附註（名單資料_1.txt:20 Profile 第 1 句）記載名單人物「defrauded」於 1996，當時客戶尚未出生；未自動判定，請覆核", before.Evidence[EvidenceGroup.TemporalRole].Note);
    }

    [Fact]
    public void The_explanation_leads_with_the_notes_hint_and_names_its_source_among_the_conflicts()
    {
        var p = Profile("Chang Wen-hua was charged in 2005.");
        var input = new CaseInput(Customer(), [new CandidateRow("張文華", "1", null, false)], new Dictionary<string, SourceProfile> { ["1"] = p },
            new Dictionary<string, IReadOnlyList<NewsItem>>(), []);
        var a = CaseAnalyzer.Analyze(input, asOf: new DateOnly(2026, 11, 27));
        var h = Assert.Single(ExplanationBuilder.Build(input, a).Hits);
        Assert.StartsWith("名單附註（名單資料_1.txt:20 Profile 第 1 句）記載名單人物「charged」於 2005，當時客戶最多 3 歲；未自動判定，請覆核。相符：", h.Summary.AgreementsAndDifferences, StringComparison.Ordinal);
        Assert.Contains("衝突：名單附註事件時的年齡", h.Summary.AgreementsAndDifferences, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_narrative_parts_are_read()
    {
        foreach (var label in new[] { "Keywords", "Companies mentioned", "People mentioned", "Last Reviewed Date" })
            Assert.Null(HardConflictGate.ImplausibleAgeInNotes(Customer(), Profile("Chang Wen-hua was charged in 2005.", label)));
        Assert.NotNull(HardConflictGate.ImplausibleAgeInNotes(Customer(), Profile("Chang Wen-hua was charged in 2005.", null)));
        Assert.NotNull(HardConflictGate.ImplausibleAgeInNotes(Customer(), Profile("Chang Wen-hua was charged in 2005.", "Comments")));
    }

    [Fact]
    public void Guards_a_profile_birth_date_a_far_birth_year_an_agreeing_document_and_an_imprecise_customer_birth_date()
    {
        const string text = "Chang Wen-hua was charged in 2005.";
        Assert.Null(HardConflictGate.ImplausibleAgeInNotes(Customer(), Profile(text) with { DatesOfBirth = [new PartialDate(1970)] }));
        Assert.Null(HardConflictGate.ImplausibleAgeInNotes(Customer() with { DateOfBirth = new PartialDate(2002) }, Profile(text)));
        // A birth year the notes state far from the customer's leaves the hint: it only makes the stranger plainer.
        Assert.NotNull(HardConflictGate.ImplausibleAgeInNotes(Customer(), Profile("Chang Wen-hua was born in 1960. " + text)));
        // An agreeing document: row 2, the hint recorded alongside, as for an attached item.
        var id = new Document("TW_NATIONAL_ID", "A123456789", "TW");
        var r = IdentityDecisionPolicy.Decide(Customer(id), Profile(text) with { Documents = [id] }, []);
        Assert.Equal((IdentityDecision.ProbableMatch, 2), (r.Decision, r.Stage3Row));
        Assert.Contains(ReasonCodes.AgeImplausibleAtDocument, r.ReasonCodes);
    }

    [Fact]
    public void An_attached_item_grounds_the_hint_before_the_notes()
    {
        var item = new NewsItem("1", null, "標題", "某報", new PartialDate(2010, 5, 9), "範例地方法院今天判決董事張文華有期徒刑四月。");
        var r = IdentityDecisionPolicy.Decide(Customer(), Profile("Chang Wen-hua was charged in 2005."), [item]);
        Assert.StartsWith("第 1 則附加新聞", r.Evidence[EvidenceGroup.TemporalRole].Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Notes_with_many_dates_are_silent_and_read_in_linear_time()
    {
        var text = string.Concat(Enumerable.Repeat("Chang Wen-hua was charged in 2005. ", 500));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Assert.Null(HardConflictGate.ImplausibleAgeInNotes(Customer(), Profile(text)));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"took {clock.Elapsed}");
    }
}
