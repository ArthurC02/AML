using Aml.Core.Cases;
using Aml.Core.Explanation;
using Aml.Core.Identity;
using Aml.Core.Pages;
using Aml.Core.Scoring;
using Aml.Core.Screening;
using Aml.Core.Shared;
using Aml.Core.Subjects;

namespace Aml.Tests.Unit.Identity;

/// <summary>
/// Implausible age at a dated document, advisory (identity-adjudication.md#implausible-age-at-a-dated-document-advisory,
/// user 2026-11-27): the customer under 14 at most on the header date of an attached item in which a suspect or officer word
/// stands directly before the Profile person's whole full name gives the hint AGE_IMPLAUSIBLE_AT_DOCUMENT on stage 3 row 1
/// (REVIEW); it never concludes. Invented text; the Profile 陳志明 has no birth date of its own; the item is dated 2017-05-09.
/// </summary>
public class DocumentAgeTests
{
    private static readonly PartialDate On = new(2017, 5, 9);
    private static readonly PartialDate Child = new(2006, 7, 15); // 10 on 2017-05-09

    private const string Director = "範例地方法院今天判決範例公司負責人林大華、董事陳志明等4人各有期徒刑四月。";
    private const string Release = "臺灣高等法院有關105年度上訴字第1234號被告陳志明等貪污案件之新聞稿。\n本院審理被告陳志明等貪污案件，上訴駁回，於今(9日)宣判。";

    private static ScreenedSubject Customer(PartialDate dob, Document? document = null) =>
        new(SubjectRole.Customer, null, ["陳志明"], Gender.Male, dob, ["TW"], document, null, null, null, [], []);

    private static SourceProfile Profile(params string[] names) =>
        new("DJ:1", Gender.Male, names.Length == 0 ? ["陳志明"] : names, [], ["TW"], [], [new SourceCategory("Special Interest Person (SIP)", "Fraud")], [], [], [], null, [], []);

    private static NewsItem Item(string text, PartialDate? on = null) => new("1", null, "標題", "某報", on ?? On, text);

    private static IdentityResult Decide(PartialDate dob, params NewsItem[] news) => IdentityDecisionPolicy.Decide(Customer(dob), Profile(), news);

    private static bool Hints(IdentityResult r) => r.Evidence.GetValueOrDefault(EvidenceGroup.TemporalRole)?.Detail == ReasonCodes.AgeImplausibleAtDocument;

    [Fact]
    public void A_sentenced_director_in_a_list_and_a_defendant_in_a_court_release_are_hinted_and_left_to_review()
    {
        foreach (var text in new[] { Director, Release })
        {
            var r = Decide(Child, Item(text));
            Assert.Equal((IdentityDecision.Review, (string?)null, 1, ReasonCodes.AgeImplausibleAtDocument), (r.Decision, r.HardConflict, r.Stage3Row, r.ReasonCodes[0]));
            Assert.DoesNotContain(ReasonCodes.AgeImplausibleAtDocument, HardConflictGate.ValidatedReasonCodes);
            var t = r.Evidence[EvidenceGroup.TemporalRole];
            Assert.Equal((Direction.Conflict, ReasonCodes.AgeImplausibleAtDocument), (t.Direction, t.Detail));
            Assert.Equal($"第 1 則附加新聞（報導日期 2017-05-09）稱名單人物為「{(text == Director ? "董事" : "被告")}」，當日客戶最多 10 歲；未自動判定，請覆核", t.Note);
            // The score band: no item and no veto (0 points), so the pair stays REVIEW there too.
            var score = ScoringRule.Identity(r, entityRecord: false);
            Assert.Equal(IdentityBand.Review, score.Band);
            Assert.DoesNotContain(score.Items, i => i.Code == ReasonCodes.AgeImplausibleAtDocument);
        }
    }

    [Fact]
    public void The_explanation_leads_with_the_hint_and_names_it_first_among_the_conflicts()
    {
        var input = new CaseInput(Customer(Child), [new CandidateRow("陳志明", "1", null, false)], new Dictionary<string, SourceProfile> { ["1"] = Profile() },
            new Dictionary<string, IReadOnlyList<NewsItem>> { ["1"] = [Item(Director)] }, []);
        var a = CaseAnalyzer.Analyze(input, asOf: new DateOnly(2026, 11, 27));
        var h = Assert.Single(ExplanationBuilder.Build(input, a).Hits);
        Assert.StartsWith("第 1 則附加新聞（報導日期 2017-05-09）稱名單人物為「董事」，當日客戶最多 10 歲；未自動判定，請覆核。相符：", h.Summary.AgreementsAndDifferences, StringComparison.Ordinal);
        Assert.Contains("衝突：附加新聞日期時的年齡", h.Summary.AgreementsAndDifferences, StringComparison.Ordinal);
        // Row 1 REVIEW, as a stated difference: priority LOW (no raised Sanction here).
        Assert.Equal(ReviewPriority.Low, Assert.Single(a.Hits).ReviewPriority);
    }

    [Fact]
    public void A_pair_a_hard_conflict_decides_carries_no_hint()
    {
        var item = Item("被告陳志明（45歲）涉嫌詐欺。"); // the stated age concludes STATED_DOB_CONFLICT for a customer of ten
        var input = new CaseInput(Customer(Child), [new CandidateRow("陳志明", "1", null, false)], new Dictionary<string, SourceProfile> { ["1"] = Profile() },
            new Dictionary<string, IReadOnlyList<NewsItem>> { ["1"] = [item] }, []);
        var a = CaseAnalyzer.Analyze(input, asOf: new DateOnly(2026, 11, 27));
        var hit = Assert.Single(a.Hits);
        Assert.Equal((IdentityDecision.NoMatch, ReasonCodes.StatedDobConflict), (hit.Identity.Decision, hit.Identity.HardConflict));
        Assert.False(Hints(hit.Identity));
        Assert.DoesNotContain(ReasonCodes.AgeImplausibleAtDocument, hit.Identity.ReasonCodes);
        Assert.DoesNotContain("請覆核", Assert.Single(ExplanationBuilder.Build(input, a).Hits).Summary.AgreementsAndDifferences, StringComparison.Ordinal);
        // The same item without the age: the hint, on row 1.
        Assert.True(Hints(Decide(Child, Item("被告陳志明涉嫌詐欺。"))));
    }

    [Theory]
    [InlineData("範例公司副董事長陳志明遭判刑。", "副董事長")]
    [InlineData("範例公司獨立董事陳志明遭判刑。", "獨立董事")]
    [InlineData("範例公司副總經理陳志明遭判刑。", "副總經理")]
    [InlineData("本院審理第1234號被告陳志明詐欺案件。", "被告")] // a case number before 被告 is not a word that holds it
    public void Officer_compounds_listed_explicitly_still_qualify(string text, string role)
    {
        Assert.Equal(role, HardConflictGate.RoleAChildCannotHold(text, ["陳志明"]));
    }

    [Fact]
    public void A_huge_item_of_failing_mentions_is_read_in_linear_time_and_gives_no_hint()
    {
        var text = string.Concat(Enumerable.Repeat("被告陳志明否認", 1_000_000 / 7));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Assert.Null(HardConflictGate.ImplausibleAgeAtDocument(Customer(Child), Profile(), [Item(text)]));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"took {clock.Elapsed}");
    }

    [Fact]
    public void A_negated_mention_in_one_sentence_leaves_a_qualifying_one_in_another()
    {
        Assert.True(Hints(Decide(Child, Item("他否認為董事陳志明。" + Director))));
    }

    [Theory]
    [InlineData(2003, 5, 9, false)]  // exactly 14 on 2017-05-09
    [InlineData(2003, 5, 10, true)]  // 13, one day short
    [InlineData(1999, 1, 1, false)]
    [InlineData(1980, 5, 20, false)]
    [InlineData(2018, 1, 1, true)]   // born after the item: a negative age is hinted as well
    public void Under_fourteen_at_most_on_the_header_date(int year, int month, int day, bool hinted)
    {
        var r = Decide(new PartialDate(year, month, day), Item(Director));
        Assert.Equal(hinted, Hints(r));
        Assert.NotEqual(IdentityDecision.NoMatch, r.Decision);
    }

    [Theory]
    [InlineData("被害人陳志明報案後，警方依詐欺罪嫌將林大華移送地檢署。")]
    [InlineData("證人陳志明到庭作證，被告林大華坦承犯行。")]
    [InlineData("範例地方法院今天判決少年陳志明有期徒刑四月。")]
    [InlineData("法官陳志明今天宣判。")]
    [InlineData("陳志明出席公司活動。")]
    [InlineData("範例公司董事陳志明（10歲）出席活動。")] // a compatible stated age: the item may be the customer's
    // Misattributions: only a word directly before the whole full name counts.
    [InlineData("檢察官陳志明今天起訴一名詐欺嫌犯。")] // the prosecutor, not the accused
    [InlineData("鄰居陳志明說，該公司老闆昨遭起訴。")] // another person's indictment later in the sentence
    [InlineData("被害人陳志明遭恐嚇，陳嫌被警方逮捕。")] // the alias 陳嫌 lends no role to the victim
    [InlineData("被告陳志明宏涉嫌詐欺。")] // a four-character name holding the full name
    [InlineData("陳志明國小校長遭起訴。")] // a school's name
    [InlineData("臺北地方法院今天依業務侵占罪判處陳志明有期徒刑一年。")] // no word before the name: no event path
    [InlineData("法官陳志明另案審理；被告陳志明詐欺案今日宣判。")] // any disqualifying mention blocks the item
    // A minor or child context anywhere in the item: a juvenile defendant may be the young customer.
    [InlineData("少年被告陳志明涉嫌詐欺。")]
    [InlineData("少年嫌犯陳志明被捕。")]
    [InlineData("未成年被告陳志明涉嫌詐欺。")]
    [InlineData("國中生嫌犯陳志明被捕。")]
    [InlineData("12歲嫌犯陳志明被捕。")]
    [InlineData("被告陳志明（少年）涉嫌詐欺。")]
    [InlineData("被告陳志明，少年法庭審理。")]
    [InlineData("被告陳志明，國小學生。")]
    // Negation, mistaken identity, misuse of a name or harm in the mention's sentence; a nominal officer anywhere.
    [InlineData("否認為董事陳志明。")]
    [InlineData("誤傳被告陳志明。")]
    [InlineData("冒用董事陳志明，登記公司。")]
    [InlineData("遭冒用的董事陳志明，報案。")]
    [InlineData("非董事陳志明。")]
    [InlineData("被告陳志明並非本案陳志明。")]
    [InlineData("同名被告陳志明並非本案陳志明。")]
    [InlineData("董事陳志明被害。")]
    [InlineData(Director + "另案國中生遭霸凌。")] // a child cue far from the name silences the item
    // A guardian, a civil or inheritance matter, schooling or young age, a kinship word, a role word inside another word.
    [InlineData("董事陳志明（由法定代理人代理）出席。")]
    [InlineData("被告陳志明（法定代理人陳大同）涉詐欺。")]
    [InlineData("法院審理分割遺產，被告陳志明等繼承人到庭。")]
    [InlineData("民事庭審理損害賠償，被告陳志明等人應連帶給付。")]
    [InlineData("董事陳志明，由父母代為出席。")]
    [InlineData("董事陳志明已故，其子女繼承股份。")]
    [InlineData("被告陳志明涉詐欺，剛念高中。")]
    [InlineData("小六生董事陳志明。")]
    [InlineData("年幼的董事陳志明。")]
    [InlineData("年僅12的被告陳志明。")]
    [InlineData("原被告陳志明涉詐欺。")]
    [InlineData("準被告陳志明涉詐欺。")]
    [InlineData("代位被告陳志明涉詐欺。")]
    [InlineData("他的兒子董事陳志明出席。")]
    public void A_role_a_child_can_hold_or_none_read_gives_no_hint(string text)
    {
        var r = Decide(Child, Item(text));
        Assert.False(Hints(r));
        Assert.Null(r.HardConflict);
    }

    [Theory]
    [InlineData("陳○明", "範例地方法院今天判決董事陳○明有期徒刑四月。")]
    [InlineData("陳姓男子", "被告陳姓男子遭判刑。")]
    [InlineData("志明", Director)] // a two-character alias may stand inside another person's name
    public void A_masked_anonymised_or_short_form_gives_no_hint(string name, string text)
    {
        Assert.Null(HardConflictGate.ImplausibleAgeAtDocument(Customer(Child), Profile(name), [Item(text)]));
    }

    [Fact]
    public void A_profile_birth_date_silences_the_hint_and_an_agreeing_document_leaves_it_alongside()
    {
        Assert.Null(HardConflictGate.ImplausibleAgeAtDocument(Customer(Child), Profile() with { DatesOfBirth = [Child] }, [Item(Director)]));

        var id = new Document("TW_NATIONAL_ID", "A123456789", "TW");
        var r = IdentityDecisionPolicy.Decide(Customer(Child, id), Profile() with { Documents = [id] }, [Item(Director)]);
        Assert.Equal((IdentityDecision.ProbableMatch, 2, (string?)null), (r.Decision, r.Stage3Row, r.HardConflict));
        Assert.Contains(ReasonCodes.AgeImplausibleAtDocument, r.ReasonCodes);
    }

    [Fact]
    public void Only_a_header_date_to_the_day_and_a_customer_birth_date_to_the_day_count()
    {
        Assert.False(Hints(Decide(Child, Item(Director, new PartialDate(2017)))));
        Assert.False(Hints(Decide(Child, Item(Director) with { PublishedOn = null })));
        Assert.False(Hints(Decide(new PartialDate(2006), Item(Director))));
    }

    [Fact]
    public void One_qualifying_item_is_enough_whatever_another_says()
    {
        var later = Item("市長陳志明今天出席活動。", new PartialDate(2026, 1, 1)); // an adult official years later
        var r = Decide(Child, later, Item(Director));
        Assert.Equal((IdentityDecision.Review, ReasonCodes.AgeImplausibleAtDocument), (r.Decision, r.ReasonCodes[0]));
        Assert.StartsWith("第 2 則附加新聞", r.Evidence[EvidenceGroup.TemporalRole].Note, StringComparison.Ordinal);
    }

    // ---- A received page: its publication date counts, an update date never ----

    private const string PageId = "0123456789abcdef";

    private static PageRecord Page(PartialDate on, PublicationSource source)
    {
        const string text = "〔本報記者／臺北報導〕被告陳志明涉嫌詐欺，遭臺北地檢署提起公訴。";
        return new PageRecord(PageId, "https://news.example.test/a/1", PageStatus.Read, new DateTimeOffset(2026, 11, 20, 0, 0, 0, TimeSpan.Zero), "j", "b", null,
            "https://news.example.test/a/1", "text/html", "詐欺案起訴", new DateTimeOffset(2026, 11, 20, 8, 0, 0, TimeSpan.Zero), "C0FFEE", text,
            PageReadings.Read(PageId, text, null, []).Persons, [])
        { PublishedOn = on, PublishedOnSource = source };
    }

    [Theory]
    [InlineData(PublicationSource.Metadata, true)]
    [InlineData(PublicationSource.UpdateDate, false)]
    public void A_page_counts_by_its_publication_date_only(PublicationSource source, bool hinted)
    {
        var input = new CaseInput(Customer(Child), [], new Dictionary<string, SourceProfile>(), new Dictionary<string, IReadOnlyList<NewsItem>>(), [],
            Pages: [Page(On, source)]);
        var hit = Assert.Single(CaseAnalyzer.Analyze(input, asOf: new DateOnly(2026, 11, 27)).Hits);
        Assert.Equal((IdentityDecision.Review, hinted), (hit.Identity.Decision, Hints(hit.Identity)));
    }
}
