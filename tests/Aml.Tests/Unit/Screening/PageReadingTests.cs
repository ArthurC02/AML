using Aml.Core.Explanation;
using Aml.Core.Pages;
using Aml.Core.Ports;
using Aml.Core.Screening;
using Aml.Core.Shared;

namespace Aml.Tests.Unit.Screening;

/// <summary>A received page's persons (external-sources.md#common-path): the news digest's rules, each value with its place in the text.</summary>
public class PageReadingTests
{
    private const string Text = "被告王大明（男，45歲）涉嫌詐欺，遭臺北地檢署提起公訴。\n證人李小華昨日到庭作證。";

    [Fact]
    public void Every_person_the_page_names_is_read_with_the_places_of_the_name_and_of_what_is_stated_next_to_it()
    {
        var reading = PageReadings.Read("0123456789abcdef", Text, null, []);
        Assert.Equal(["WEB:0123456789abcdef-1", "WEB:0123456789abcdef-2"], reading.Persons.Select(p => p.ProfileKey));
        var (wang, li) = (reading.Persons[0], reading.Persons[1]);

        Assert.Equal(("王大明", "王大明", "被告", NewsParty.Suspect), (wang.Name, string.Join(",", wang.Forms), string.Join(",", wang.Roles), wang.Party));
        Assert.Equal([new PageMention(2, 3)], wang.Mentions);
        Assert.Equal((45, Gender.Male), (wang.StatedAge, wang.StatedGender));
        Assert.Empty(wang.StatedBirthDates);
        Assert.Equal([new PageIdentifier(NewsIdentifierKind.Gender, "男", null, 6), new PageIdentifier(NewsIdentifierKind.Age, "45歲", null, 8)], wang.Identifiers);
        Assert.Equal((LegalStage.Indicted, "STAGE_KEYWORD", (IReadOnlyList<StageReaderResult>?)null), (wang.Stage, wang.StageBasis, wang.StageReaders));
        Assert.Equal([(NewsEventType.Indictment, (int?)0)], wang.Events.Select(e => (e.Event.Type, e.At)));

        Assert.Equal(("李小華", "證人", NewsParty.Witness), (li.Name, string.Join(",", li.Roles), li.Party));
        Assert.Equal([new PageMention(Text.IndexOf("李小華", StringComparison.Ordinal), 3)], li.Mentions);
        Assert.Equal((null, null), (li.StatedAge, li.StatedGender));
        Assert.Empty(li.Events);
        Assert.Empty(reading.OtherEvents);
    }

    [Fact]
    public void An_anonymised_form_linked_to_one_full_name_is_a_form_of_that_person()
    {
        var reading = PageReadings.Read("p", "被告王大明涉嫌詐欺。王姓男子否認犯行。", null, []);
        Assert.Equal(["王大明", "王姓男子"], Assert.Single(reading.Persons).Forms);
    }

    [Fact]
    public void A_person_only_the_news_models_find_is_a_model_read_person_and_one_the_rules_find_stays_rule_read()
    {
        const string text = "被告王大明涉嫌詐欺，警方約談到案後依詐欺罪嫌移送地檢署偵辦。\n同案的陳小華亦遭約談。";
        var model = new FakeNewsModel(new Dictionary<string, string> { ["王大明"] = "PER-Suspect", ["陳小華"] = "PER-Suspect", ["檢方"] = "ORG-Other" });
        var reading = PageReadings.Read("p", text, model, []);
        Assert.Equal([("WEB:p-1", "王大明", PersonReadBy.RuleRead), ("WEB:p-2", "陳小華", PersonReadBy.ModelRead)],
            reading.Persons.Select(p => (p.ProfileKey, p.Name, p.ReadBy)));
        var chen = reading.Persons[1];
        Assert.Equal((NewsParty.Suspect, 0, LegalStage.Alleged), (chen.Party, chen.Roles.Count, chen.Stage));
        Assert.Equal([new PageMention(text.IndexOf("陳小華", StringComparison.Ordinal), 3)], chen.Mentions);

        // Without the models the rules alone read the page: the second person is not invented.
        Assert.Equal(["王大明"], PageReadings.Read("p", text, null, []).Persons.Select(p => p.Name));
    }

    [Fact]
    public void A_short_model_name_written_inside_another_model_entity_s_longer_name_does_not_drop_that_person()
    {
        const string text = "被告王大明涉嫌詐欺，警方約談到案後依詐欺罪嫌移送地檢署偵辦。\n同案的陳明亦遭約談，另案的陳明華也到案說明。";
        var model = new FakeNewsModel(new Dictionary<string, string> { ["王大明"] = "PER-Suspect", ["陳明"] = "PER-Suspect", ["陳明華"] = "PER-Suspect" });
        var reading = PageReadings.Read("p", text, model, []);
        Assert.Equal([("王大明", PersonReadBy.RuleRead), ("陳明", PersonReadBy.ModelRead), ("陳明華", PersonReadBy.ModelRead)],
            reading.Persons.Select(p => (p.Name, p.ReadBy)));
        Assert.Equal([new PageMention(text.IndexOf("陳明亦", StringComparison.Ordinal), 2)], reading.Persons[1].Mentions);
        Assert.Equal([new PageMention(text.IndexOf("陳明華", StringComparison.Ordinal), 3)], reading.Persons[2].Mentions);
    }

    [Fact]
    public void With_two_stage_models_only_a_unanimous_stage_stands_and_a_missing_answer_is_marked()
    {
        var agree = PageReadings.Read("p", Text, null, [new Fixed("classifier", LegalStage.Indicted), new Fixed("llm", LegalStage.Indicted)]).Persons[0];
        Assert.Equal((LegalStage.Indicted, "STAGE_VOTE_UNANIMOUS"), (agree.Stage, agree.StageBasis));
        Assert.Equal(["rules", "classifier", "llm"], agree.StageReaders!.Select(r => r.Reader));
        Assert.Empty(agree.StageReasonCodes);

        var missing = PageReadings.Read("p", Text, null, [new Fixed("classifier", LegalStage.Indicted), new Fixed("llm", null)]);
        Assert.Equal((LegalStage.Unknown, (string?)null), (missing.Persons[0].Stage, missing.Persons[0].StageBasis));
        Assert.Equal(["STAGE_READER_NO_ANSWER"], missing.Persons[0].StageReasonCodes);
        Assert.True(PageReadings.StageAnswerMissing(missing));
        Assert.False(PageReadings.StageAnswerMissing(PageReadings.Read("p", Text, null, [])));
    }

    [Fact]
    public void The_summary_view_keeps_who_is_named_and_their_stage_and_leaves_out_text_identifiers_stated_values_and_events()
    {
        var reading = PageReadings.Read("p", Text, null, [new Fixed("classifier", LegalStage.Indicted)]);
        var page = new PageRecord("p", "https://news.example.test/a", PageStatus.Read, DateTimeOffset.UnixEpoch, "j", "b", Text: Text,
            Persons: reading.Persons, OtherEvents: reading.OtherEvents, Model: reading.Model);
        var summary = PageReadings.Summary(page);
        Assert.Null(summary.Text);
        var wang = summary.Persons![0];
        Assert.Equal(("王大明", LegalStage.Indicted, 1), (wang.Name, wang.Stage, wang.Mentions.Count));
        Assert.Equal((0, (int?)null, 0, (Gender?)null, (IReadOnlyList<StageReaderResult>?)null, 0),
            (wang.Identifiers.Count, wang.StatedAge, wang.StatedBirthDates.Count, wang.StatedGender, wang.StageReaders, wang.Events.Count));
        Assert.Empty(summary.OtherEvents!);
    }

    /// <summary>A model's quote lives only in StageReaders; the summary's stageBasis is a catalog code, so no quote reaches the summary.</summary>
    [Fact]
    public void A_model_quote_never_reaches_the_summary_view()
    {
        const string quote = "遭臺北地檢署提起公訴";
        var reading = PageReadings.Read("p", Text, null, [new Fixed("classifier", LegalStage.Indicted, quote), new Fixed("llm", LegalStage.Indicted, quote)]);
        Assert.Contains(reading.Persons[0].StageReaders!, r => r.Quote == quote);
        var summary = PageReadings.Summary(new PageRecord("p", "https://news.example.test/a", PageStatus.Read, DateTimeOffset.UnixEpoch, "j", "b",
            Persons: reading.Persons, OtherEvents: reading.OtherEvents, Model: reading.Model));
        Assert.Equal("STAGE_VOTE_UNANIMOUS", summary.Persons![0].StageBasis);
        Assert.DoesNotContain(quote, System.Text.Json.JsonSerializer.Serialize(summary, Aml.Core.Cases.CaseJson.Options), StringComparison.Ordinal);
    }

    private sealed class Fixed(string name, LegalStage? stage, string? quote = null) : IStageReader
    {
        public string Name => name;

        public StageAnswer? Read(string text, IReadOnlyList<string> names) => stage is { } s ? new StageAnswer(s, quote) : null;
    }
}
