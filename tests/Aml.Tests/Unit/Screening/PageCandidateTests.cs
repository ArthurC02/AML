using Aml.Core.Cases;
using Aml.Core.Explanation;
using Aml.Core.Identity;
using Aml.Core.Pages;
using Aml.Core.Retrieval;
using Aml.Core.Screening;
using Aml.Core.Screening.Stage;
using Aml.Core.Shared;
using Aml.Core.Subjects;
using Aml.Infra.Txt;
using Aml.Tests.GoldenCases;
using Aml.Tests.Synthetic;

namespace Aml.Tests.Unit.Screening;

/// <summary>
/// The persons of a received page matched against a Case (external-sources.md#common-path): a candidate by the pipeline's
/// own name comparison (EXACT only), a source record that carries names alone, the page as its attached text, the
/// condition from the stage the page reading gave, and the pair through the existing matrix. The customer is 王大明, a man
/// born 1980-05-20 (民國69年), Taiwanese.
/// </summary>
public class PageCandidateTests
{
    private const string PageId = "0123456789abcdef";
    private const string Url = "https://news.example.test/a/1";

    private static ScreenedSubject Subject(string name = "王大明") =>
        new(SubjectRole.Customer, null, [name], Gender.Male, new PartialDate(1980, 5, 20), ["TW"], null, null, null, null, [], []);

    private static PagePerson Person(string name, string[] forms, NewsParty? party = null, LegalStage stage = LegalStage.Alleged,
        PersonReadBy readBy = PersonReadBy.RuleRead, string[]? stageCodes = null) =>
        new($"WEB:{PageId}-1", name, forms, [], party, [], [], null, [], null, stage, stage == LegalStage.Unknown ? null : StageBasis.Keyword, null,
            stageCodes ?? [], [], readBy);

    private static PageRecord Page(string text) =>
        new(PageId, Url, PageStatus.Read, new DateTimeOffset(2026, 11, 20, 0, 0, 0, TimeSpan.Zero), "j", "b", null, Url, "text/html", "詐欺案起訴",
            new DateTimeOffset(2026, 11, 20, 8, 0, 0, TimeSpan.Zero), "C0FFEE", text, PageReadings.Read(PageId, text, null, []).Persons, []);

    private static CaseInput Input(PageRecord page, ScreenedSubject? subject = null) =>
        new(subject ?? Subject(), [], new Dictionary<string, SourceProfile>(), new Dictionary<string, NewsItem>(), [], Pages: [page]);

    [Theory]
    [InlineData("王大明", "王大明", "EXACT", null, true)]
    [InlineData("王大明", "王大明|王男", "EXACT", null, true)]
    [InlineData("鍾大明", "鐘大明", "EXACT", "VARIANT_CHARACTERS", true)]
    [InlineData("王大明", "王大名", "PARTIAL", "NOT_IDENTICAL_AFTER_NORMALIZATION（Jaro-Winkler 0.82，Levenshtein 1，DISSIMILAR）", false)]
    [InlineData("王大明", "林小華", "PARTIAL", "NOT_IDENTICAL_AFTER_NORMALIZATION（Jaro-Winkler 0.00，Levenshtein 3，DISSIMILAR）", false)]
    [InlineData("王明", "王○明", "PROFILE_MISSING", "REDACTED_NAME", false)]
    public void A_page_person_is_a_candidate_only_when_a_written_name_is_exact_and_every_person_is_recorded(
        string subject, string forms, string status, string? detail, bool candidate)
    {
        var written = forms.Split('|');
        var page = Page("x") with { Persons = [Person(written[0], written)] };

        var person = Assert.Single(PageCandidates.Screen(Subject(subject), page).Persons);

        Assert.Equal(($"WEB:{PageId}-1", written[0], status, detail, RetrievalMode.SourcePage, candidate),
            (person.ProfileKey, person.Name, CaseJson.Code(person.NameStatus), person.NameDetail, person.Mode, person.Candidate));
    }

    [Fact]
    public void A_page_person_as_a_source_record_carries_its_unmasked_names_only_and_the_page_as_its_attached_text()
    {
        var page = Page("被告王大明涉嫌詐欺。");
        var person = Person("王大明", ["王男", "王大明", "王○明"], readBy: PersonReadBy.ModelRead);

        var profile = PageCandidates.Profile(person);
        Assert.Equal(($"WEB:{PageId}-1", "王大明,王男", "Person", PageCandidates.ModelRead),
            (profile.ProfileKey, string.Join(",", profile.Names), profile.RecordType, string.Join(",", profile.Findings)));
        Assert.True(profile.Gender is null && profile.DatesOfBirth.Count == 0 && profile.Citizenship.Count == 0 && profile.Categories.Count == 0 && profile.Sources.Count == 0);

        var news = PageCandidates.News(page, person);
        Assert.Equal(($"{PageId}-1", PageId, "詐欺案起訴", "news.example.test", (PartialDate?)null, page.Text, Url, "2026-11-20"),
            (news.ProfileId, news.DocumentId, news.Title, news.Publisher, news.PublishedOn, news.Text, news.Url, news.RetrievedOn));
        // The finding is the person's own readBy code, so it reads the same in the catalog.
        Assert.Equal(PageCandidates.ModelRead, CaseJson.Code(PersonReadBy.ModelRead));
    }

    [Theory]
    [InlineData(PersonReadBy.RuleRead, NewsParty.Suspect, LegalStage.Alleged, "", "HIT", "")]
    [InlineData(PersonReadBy.RuleRead, null, LegalStage.Indicted, "", "HIT", "")]
    [InlineData(PersonReadBy.RuleRead, NewsParty.Victim, LegalStage.Alleged, "", "NOT_HIT", "PAGE_PERSON_NOT_SUSPECT")]
    [InlineData(PersonReadBy.RuleRead, NewsParty.Witness, LegalStage.Unknown, "", "NOT_HIT", "PAGE_PERSON_NOT_SUSPECT")]
    [InlineData(PersonReadBy.RuleRead, NewsParty.Official, LegalStage.Convicted, "", "NOT_HIT", "PAGE_PERSON_NOT_SUSPECT")]
    [InlineData(PersonReadBy.ModelRead, NewsParty.Victim, LegalStage.Alleged, "", "HIT", "")]
    [InlineData(PersonReadBy.ModelRead, NewsParty.Witness, LegalStage.Unknown, "", "UNKNOWN", "PAGE_STAGE_NOT_READ")]
    [InlineData(PersonReadBy.RuleRead, null, LegalStage.Unknown, "", "UNKNOWN", "PAGE_STAGE_NOT_READ")]
    [InlineData(PersonReadBy.RuleRead, null, LegalStage.Unknown, "STAGE_READERS_DISAGREE", "UNKNOWN", "STAGE_READERS_DISAGREE")]
    [InlineData(PersonReadBy.RuleRead, null, LegalStage.Unknown, "STAGE_READER_NO_ANSWER", "UNKNOWN", "STAGE_READER_NO_ANSWER")]
    public void A_page_person_s_condition_is_the_stage_the_page_reading_gave_and_a_role_word_the_rules_read_that_is_no_suspect_s_raises_none(
        PersonReadBy readBy, NewsParty? party, LegalStage stage, string stageCodes, string status, string codes)
    {
        var person = Person("王大明", ["王大明"], party, stage, readBy, stageCodes.Length == 0 ? [] : [stageCodes]);

        var c = Assert.Single(SourceConditions.FromPage(person, PageCandidates.Profile(person), "王大明涉嫌詐欺。"));

        Assert.Equal((ListType.AdverseMedia, status, ConditionBasis.PageReading, stage, person.StageBasis, codes),
            (c.Type, CaseJson.Code(c.Status), c.Basis, c.LegalStage, c.LegalStageBasis, string.Join(",", c.ReasonCodes)));
    }

    [Theory]
    // The birth date written next to the name is a value stated in a source (ADR-0012): equal agrees (MEDIUM, row 5), any
    // other date is the soft conflict of row 1, within two years or beyond; it never concludes NO_MATCH.
    [InlineData("（男，民國69年5月20日生）", "REVIEW", 5, "PARTIAL_SUPPORT_ONLY", "NEWS_BIRTH_DATE_AGREES")]
    [InlineData("（男，民國70年5月20日生）", "REVIEW", 1, "NEWS_BIRTH_DATE_DIFFERENCE", "NEWS_BIRTH_DATE_DIFFERENCE")]
    [InlineData("（男，民國73年5月20日生）", "REVIEW", 1, "NEWS_BIRTH_DATE_DIFFERENCE", "NEWS_BIRTH_DATE_DIFFERENCE")]
    [InlineData("（女）", "REVIEW", 6, "NO_NON_PREREQUISITE_SUPPORT", "NEWS_GENDER_DIFFERENCE")]
    [InlineData("", "REVIEW", 6, "NO_NON_PREREQUISITE_SUPPORT", null)]
    public void A_page_person_named_as_the_customer_is_compared_as_attached_news_is(string stated, string decision, int row, string reason, string? statedCode)
    {
        var page = Page($"〔本報記者／臺北報導〕被告王大明{stated}涉嫌詐欺，遭臺北地檢署提起公訴。\n證人李小華到庭。");

        var a = CaseAnalyzer.Analyze(Input(page), asOf: new DateOnly(2026, 11, 27));

        var hit = Assert.Single(a.Hits);
        Assert.Equal(($"WEB:{PageId}-1", 0, decision, row, reason), (hit.Profile.ProfileKey, hit.CandidateRows, CaseJson.Code(hit.Identity.Decision), hit.Identity.Stage3Row, hit.Identity.ReasonCodes[0]));
        Assert.Equal(("EXACT", "PROFILE_MISSING", "PROFILE_MISSING"), (CaseJson.Code(hit.Identity.Evidence[EvidenceGroup.Name].Status),
            CaseJson.Code(hit.Identity.Evidence[EvidenceGroup.Gender].Status), CaseJson.Code(hit.Identity.Evidence[EvidenceGroup.Nationality].Status)));
        Assert.Equal(statedCode, hit.Identity.Evidence.Values.FirstOrDefault(e => e.Group is EvidenceGroup.StatedBirthDate or EvidenceGroup.StatedGender)?.Detail);
        var c = Assert.Single(hit.Conditions);
        Assert.Equal((ListType.AdverseMedia, ConditionStatus.Hit, LegalStage.Indicted, "REVIEW"), (c.Type, c.Status, c.LegalStage, CaseJson.Code(hit.PairResult)));
        // Every person of the page is recorded, the one whose name does not correspond too.
        Assert.Equal([("王大明", true), ("李小華", false)], Assert.Single(a.Pages!).Persons.Select(p => (p.Name, p.Candidate)));
        CodeCatalogTests.AssertKnown(Input(page), a);
    }

    [Theory]
    // The customer, born 1980-05-20, is 46 on 2026-11-20. With the page's publication date the stated age is compared as an
    // attached news's at its report date (±2 years compatible, beyond a soft conflict, never NO_MATCH); without it, it is
    // recorded and not compared.
    [InlineData("46", "2026-11-20", "AGE_COMPATIBLE_AT_REPORT")]
    [InlineData("44", "2026-11-20", "AGE_COMPATIBLE_AT_REPORT")]
    [InlineData("43", "2026-11-20", "AGE_DIFFERENCE_AT_REPORT")]
    [InlineData("60", "2026-11-20", "AGE_DIFFERENCE_AT_REPORT")]
    [InlineData("60", null, "AGE_NOT_COMPARABLE")]
    public void A_stated_age_on_a_page_is_compared_at_its_publication_date_exactly_as_attached_news_at_its_report_date(string age, string? publishedOn, string code)
    {
        var text = $"〔本報記者／臺北報導〕被告王大明（{age}歲）涉嫌詐欺，遭臺北地檢署提起公訴。";
        var on = PartialDate.Parse(publishedOn);
        var page = Page(text) with { PublishedOn = on, PublishedOnSource = on is null ? PublicationSource.Missing : PublicationSource.Metadata };

        var hit = Assert.Single(CaseAnalyzer.Analyze(Input(page), asOf: new DateOnly(2026, 11, 27)).Hits);

        Assert.Equal(int.Parse(age, System.Globalization.CultureInfo.InvariantCulture), page.Persons![0].StatedAge);
        var evidence = hit.Identity.Evidence[EvidenceGroup.StatedAge];
        Assert.Equal(code, evidence.Detail);
        // The same evidence and decision as attached news of a list Profile with that report date.
        var attached = new NewsItem("1", null, "詐欺案起訴", "news.example.test", on, text);
        Assert.Equal(StatedAgeCheck.Check(Subject(), hit.Profile, attached), evidence);
        var asNews = IdentityDecisionPolicy.Decide(Subject(), hit.Profile, attached);
        Assert.Equal((asNews.Decision, asNews.Stage3Row, asNews.ReasonCodes[0]), (hit.Identity.Decision, hit.Identity.Stage3Row, hit.Identity.ReasonCodes[0]));
        Assert.Equal(IdentityDecision.Review, hit.Identity.Decision);
    }

    [Fact]
    public void The_customer_named_on_a_page_as_the_victim_raises_no_condition_and_a_page_naming_only_others_adds_no_hit()
    {
        var victim = Page("〔本報記者／臺北報導〕林小華涉嫌以投資名義詐取款項，被害人王大明報案後，警方依詐欺罪嫌移送地檢署偵辦。");
        var hit = Assert.Single(CaseAnalyzer.Analyze(Input(victim), asOf: new DateOnly(2026, 11, 27)).Hits);
        Assert.Equal((ConditionStatus.NotHit, ScreeningAssessment.NotApplicable, (ReviewPriority?)null), (hit.Conditions[0].Status, hit.PairResult, hit.ReviewPriority));
        Assert.Contains(SourceConditions.PagePersonNotSuspect, hit.Conditions[0].ReasonCodes);

        var others = Page("〔本報記者／臺北報導〕林小華涉嫌詐欺，遭臺北地檢署提起公訴。");
        var a = CaseAnalyzer.Analyze(Input(others), asOf: new DateOnly(2026, 11, 27));
        Assert.Empty(a.Hits);
        Assert.Equal(ScreeningAssessment.NoHit, CaseLifecycle.Aggregate(a));
        Assert.Equal([("林小華", false)], Assert.Single(a.Pages!).Persons.Select(p => (p.Name, p.Candidate)));
    }

    [Fact]
    public void A_page_hit_is_explained_with_the_page_as_its_cited_source()
    {
        var input = Input(Page("〔本報記者／臺北報導〕被告王大明涉嫌詐欺，遭臺北地檢署提起公訴。"));
        var e = Assert.Single(ExplanationBuilder.Build(input, CaseAnalyzer.Analyze(input, asOf: new DateOnly(2026, 11, 27))).Hits);

        var source = Assert.Single(e.Sources);
        Assert.Equal(("S1", PageId, "news.example.test：詐欺案起訴", Url, "2026-11-20", SourceUse.LegalStage), (source.Ref, source.Id, source.Name, source.Url, source.RetrievedOn, source.UsedFor));
        Assert.Equal(($"{PageId}-1", $"WEB:{PageId}-1", true), (e.ProfileId, e.ProfileKey, e.AttachedNews));
        Assert.StartsWith($"收到網頁的人物 WEB:{PageId}-1[S1]判定為 ", e.Summary.ExternalData, StringComparison.Ordinal);
        Assert.Equal("王大明", Assert.Single(e.NewsDigest!.People).Name);
    }

    [Fact]
    public void A_page_snapshot_in_the_case_files_is_read_as_the_page_with_its_hash_and_never_as_an_attachment()
    {
        var files = new Dictionary<string, string>(SyntheticCaseGenerator.Generate()[0].Files)
        {
            [PageSnapshots.FileName(PageId)] = PageSnapshots.Write(Page("被告王大明涉嫌詐欺。")),
        };

        var input = TxtCaseReader.ReadFiles(files);

        Assert.Equal("被告王大明涉嫌詐欺。", Assert.Single(input.Pages!).Text);
        Assert.Matches("^[0-9A-F]{64}$", input.SourceHashes![PageSnapshots.FileName(PageId)]);
        Assert.DoesNotContain(input.Subject.Findings, f => f.StartsWith(TxtCaseReader.UnsupportedAttachment, StringComparison.Ordinal));
        Assert.Equal(PageSnapshots.TextHash("被告王大明涉嫌詐欺。"), AnalysisLineage.Of(input, [], new DateOnly(2026, 11, 27)).Pages!.Single().TextHash);
        Assert.Throws<CaseInputException>(() => TxtCaseReader.ReadFiles(new Dictionary<string, string>(files) { [PageSnapshots.FileName(PageId)] = "{}" }));
    }

    [Theory]
    [InlineData("DJ:123", "123", "DJ:123")]
    [InlineData("dj:123", "123", "DJ:123")]
    [InlineData("123", "123", "DJ:123")]
    [InlineData("WEB:0123456789abcdef-2", "0123456789abcdef-2", "WEB:0123456789abcdef-2")]
    [InlineData("web:0123456789abcdef-2", "0123456789abcdef-2", "WEB:0123456789abcdef-2")]
    public void A_web_key_keeps_its_namespace_where_a_list_key_takes_DJ(string value, string id, string key) =>
        Assert.Equal((id, key), (ProfileKeys.IdOf(value), ProfileKeys.KeyOf(value)));

    [Fact]
    public void A_missing_stage_answer_on_a_page_person_is_no_reason_to_retry_the_case_but_one_on_a_list_profile_is()
    {
        var person = Person("王大明", ["王大明"], stage: LegalStage.Unknown, stageCodes: [StageVote.ReaderNoAnswer]);
        var page = Page("王大明涉嫌詐欺。") with { Persons = [person] };
        var analysis = CaseAnalyzer.Analyze(Input(page), asOf: new DateOnly(2026, 11, 27));
        Assert.Equal(ConditionStatus.Unknown, Assert.Single(analysis.Hits).Conditions[0].Status);
        Assert.False(CaseLifecycle.StageAnswerMissing(analysis));

        var listed = analysis.Hits[0] with { Conditions = [analysis.Hits[0].Conditions[0] with { Basis = ConditionBasis.CategoryMapping }] };
        Assert.True(CaseLifecycle.StageAnswerMissing(analysis with { Hits = [listed] }));
    }
}
