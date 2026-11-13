using Aml.Core.Explanation;
using Aml.Core.Subjects;

namespace Aml.Tests.Unit.Screening;

/// <summary>
/// The news models' readout on made-up news (watchlist-and-adverse-information.md#news-models): the fixed corrections of the
/// tagger's spans, and the events per entity and sentence.
/// </summary>
public class NewsModelReadingTests
{
    private static NewsModelReadout Read(string text, Dictionary<string, string> roles, string[]? profile = null,
        params (string, string, string, float)[] events) =>
        NewsModelReading.Read(text, profile ?? ["王大明"], new FakeNewsModel(roles, events));

    [Fact]
    public void One_character_fragments_and_accounts_are_dropped()
    {
        var r = Read("王大明以人頭戶收款，林也到場。", new() { ["王大明"] = "PER-Suspect", ["人頭戶"] = "ORG-Other", ["林"] = "PER-Other" });

        Assert.Equal(["王大明"], r.Entities.Select(e => e.Name));
    }

    [Fact]
    public void A_fragment_or_an_over_long_span_holding_one_well_formed_name_is_that_name()
    {
        var r = Read("王大明遭起訴。大明否認，王大明等三人交保。", new() { ["王大明等"] = "PER-Suspect", ["王大明"] = "PER-Suspect", ["大明"] = "PER-Other" });

        var e = Assert.Single(r.Entities);
        Assert.Equal(("王大明", 3), (e.Name, e.Mentions));
        Assert.Equal(["大明", "王大明等"], e.OtherForms.Order(StringComparer.Ordinal));
        Assert.Equal(Core.Explanation.NewsParty.Suspect, e.Party); // two suspect mentions outvote one other
    }

    [Fact]
    public void A_fragment_inside_two_names_is_dropped_and_one_with_no_name_is_kept()
    {
        var r = Read("王大明與陳大明見面，大明先離開；約翰到場。",
            new() { ["王大明"] = "PER-Suspect", ["陳大明"] = "PER-Victim", ["大明"] = "PER-Other", ["約翰"] = "PER-Other" });

        Assert.Equal(["王大明", "陳大明", "約翰"], r.Entities.Select(e => e.Name));
        Assert.Null(r.Entities[2].Party);
    }

    [Fact]
    public void Quote_marks_and_a_role_word_taken_in_by_the_tagger_are_not_the_name()
    {
        var r = Read("「王大明」遭起訴，被告王大明否認。", new() { ["「王大明」"] = "PER-Suspect", ["被告王大明"] = "PER-Suspect" });

        var e = Assert.Single(r.Entities);
        Assert.Equal(("王大明", 2, 0), (e.Name, e.Mentions, e.OtherForms.Count));
    }

    [Fact]
    public void A_longer_role_word_is_taken_off_whole()
    {
        var r = Read("被告人王大明遭起訴，上訴人王大明否認。", new() { ["被告人王大明"] = "PER-Suspect", ["上訴人王大明"] = "PER-Suspect" });

        Assert.Equal(("王大明", 2), (Assert.Single(r.Entities).Name, r.Entities[0].Mentions));
    }

    [Fact]
    public void A_bare_role_word_is_the_one_person_of_that_party_else_no_one()
    {
        var one = Read("被告王大明涉嫌詐欺。被告否認。", new() { ["王大明"] = "PER-Suspect", ["被告"] = "PER-Suspect" });
        var two = Read("被告王大明、陳小華涉嫌詐欺。被告否認。", new() { ["王大明"] = "PER-Suspect", ["陳小華"] = "PER-Suspect", ["被告"] = "PER-Suspect" });

        var e = Assert.Single(one.Entities);
        Assert.Equal("王大明", e.Name);
        Assert.Equal(["被告"], e.OtherForms);
        Assert.Equal(["王大明", "陳小華"], two.Entities.Select(x => x.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_fragment_inside_one_longer_name_of_another_shape_is_that_name()
    {
        var r = Read("阿莫伊斯達遭起訴，莫伊否認。", new() { ["阿莫伊斯達"] = "PER-Suspect", ["莫伊"] = "PER-Other" });

        var e = Assert.Single(r.Entities);
        Assert.Equal(("阿莫伊斯達", 2), (e.Name, e.Mentions));
        Assert.Equal(["莫伊"], e.OtherForms);
    }

    [Fact]
    public void A_short_organisation_name_inside_one_longer_one_is_its_abbreviation()
    {
        var r = Read("大明公司負責人王大明遭搜索，大明公司與大明營造均遭調查，大明另有子公司。",
            new() { ["大明公司"] = "ORG-Suspect", ["大明營造"] = "ORG-Other", ["大明"] = "ORG-Other", ["王大明"] = "PER-Suspect" });

        Assert.Equal([("王大明", false), ("大明公司", true), ("大明營造", true), ("大明", true)], r.Entities.Select(e => (e.Name, e.Organisation)));
        Assert.Equal(Core.Explanation.NewsParty.Suspect, r.Entities[1].Party);
    }

    [Fact]
    public void The_profile_person_comes_first_then_by_mentions()
    {
        var r = Read("陳小華指稱陳小華與林志強受騙，林志強報案，林志強作證，王大明否認。",
            new() { ["陳小華"] = "PER-Victim", ["林志強"] = "PER-Victim", ["王大明"] = "PER-Suspect" });

        Assert.Equal([("王大明", true), ("林志強", false), ("陳小華", false)], r.Entities.Select(e => (e.Name, e.ProfileName)));
        Assert.Equal("ner:test ev:test", r.Version);
    }

    [Fact]
    public void An_event_is_listed_when_at_least_one_sentence_likely_says_it_with_the_most_likely_sentence()
    {
        var r = Read("王大明遭聲押。法院裁定王大明無羈押必要！王大明有詐欺前科",
            new() { ["王大明"] = "PER-Suspect" }, null,
            ("王大明", "聲押", "Detention-Requested", 0.62f),
            ("王大明", "無羈押", "Detention-Negated", 0.914f),
            ("王大明", "無羈押", "Detention-Requested", 0.40f),
            ("王大明", "聲押", "Bail", 0.2f),
            ("王大明", "前科", "Bail", 0.2f),
            ("王大明", "前科", "prior", 0.3f),
            ("王大明", "前科", "Conviction", 0.29f),
            ("王大明", "前科", "NotALabel", 0.9f));

        var e = Assert.Single(r.Entities);
        Assert.Equal(
        [
            (NewsEventType.Detention, NewsCertainty.Negated, 0.91, "法院裁定王大明無羈押必要！"),
            (NewsEventType.Detention, NewsCertainty.Requested, 0.77, "王大明遭聲押。"), // 1 − 0.38 × 0.60
            (NewsEventType.Bail, NewsCertainty.Stated, 0.36, "王大明遭聲押。"),          // 1 − 0.8 × 0.8: two weak sentences
            (NewsEventType.PriorRecord, NewsCertainty.Stated, 0.3, "王大明有詐欺前科"),
        ], e.Events.Select(v => (v.Type, v.Certainty, v.Probability, v.Quote)));
    }

    [Fact]
    public void A_quote_is_cut_at_200_units_but_never_inside_a_surrogate_pair()
    {
        var sentence = "王大明" + new string('甲', 196) + "\U00020000" + "遭起訴";
        var r = Read(sentence, new() { ["王大明"] = "PER-Suspect" }, null, ("王大明", "起訴", "Indictment", 0.9f));

        Assert.Equal(sentence[..199], Assert.Single(Assert.Single(r.Entities).Events).Quote);
    }

    [Fact]
    public void Each_sentence_is_classified_with_the_form_written_in_it()
    {
        var model = new FakeNewsModel(new Dictionary<string, string> { ["王大明"] = "PER-Suspect", ["大明"] = "PER-Suspect" },
            ("大明", "交保", "Bail", 0.8f), ("王大明", "交保", "Bail", 0.1f));

        var r = NewsModelReading.Read("王大明遭起訴；另一句無關。大明交保", ["王大明"], model);

        Assert.Equal(3, model.Classifications); // the sentence naming nobody is carried over, with the form named before it
        Assert.Equal([(NewsEventType.Bail, "大明交保")], Assert.Single(r.Entities).Events.Select(v => (v.Type, v.Quote)));
    }

    [Fact]
    public void One_sentence_naming_no_person_is_read_for_the_person_named_last()
    {
        // 判處 is said in the sentence after 王大明, which names only a court; 罰金 two sentences on; 起訴 after the witness
        // 陳小華 is named, so it is read for no one.
        var r = Read("王大明遭起訴。地方法院判處有期徒刑3年。併科罰金。陳小華作證。檢方起訴。",
            new() { ["王大明"] = "PER-Suspect", ["陳小華"] = "PER-Other", ["地方法院"] = "ORG-Other" }, null,
            ("王大明", "判處", "Conviction", 0.9f), ("王大明", "罰金", "Fine", 0.9f), ("陳小華", "檢方起訴", "Indictment", 0.9f),
            ("王大明", "檢方起訴", "Indictment", 0.9f));

        Assert.Equal([NewsEventType.Conviction], r.Entities.Single(e => e.Name == "王大明").Events.Select(v => v.Type));
        Assert.Empty(r.Entities.Single(e => e.Name == "陳小華").Events);
    }

    [Fact]
    public void The_person_named_last_is_last_in_the_text_not_first_in_rank()
    {
        // 王大明 is the Profile person, so ranked first; 陳小華 is named before him in the sentence.
        var r = Read("陳小華指控王大明。法院判處有期徒刑。", new() { ["王大明"] = "PER-Suspect", ["陳小華"] = "PER-Victim" }, null,
            ("王大明", "判處", "Conviction", 0.9f), ("陳小華", "判處", "Conviction", 0.9f));

        Assert.Equal([NewsEventType.Conviction], r.Entities.Single(e => e.Name == "王大明").Events.Select(v => v.Type));
        Assert.Empty(r.Entities.Single(e => e.Name == "陳小華").Events);
    }

    [Fact]
    public void Only_a_suspect_is_carried_over_to_the_next_sentence()
    {
        // The sentence after the lawyer 陳小華 names no one and is not read for him; after the suspect 王大明 it is.
        var r = Read("陳小華到場辯護。法院判處有期徒刑。王大明遭起訴。併科罰金。",
            new() { ["王大明"] = "PER-Suspect", ["陳小華"] = "PER-Other" }, null,
            ("陳小華", "判處", "Conviction", 0.9f), ("王大明", "罰金", "Fine", 0.9f));

        Assert.Empty(r.Entities.Single(e => e.Name == "陳小華").Events);
        Assert.Equal([NewsEventType.Fine], r.Entities.Single(e => e.Name == "王大明").Events.Select(v => v.Type));
    }

    [Fact]
    public void An_organisation_is_not_arrested_or_detained()
    {
        var r = Read("大明公司遭搜索並遭羈押。", new() { ["大明公司"] = "ORG-Suspect" }, ["大明公司"],
            ("大明公司", "搜索", "Search", 0.9f), ("大明公司", "羈押", "Detention", 0.9f), ("大明公司", "羈押", "Arrest", 0.9f));

        Assert.Equal([NewsEventType.Search], Assert.Single(r.Entities).Events.Select(v => v.Type));
    }

    [Fact]
    public void The_digest_carries_the_readout_only_when_a_model_is_given()
    {
        var news = new NewsItem("1", "d1", "測試新聞", "某報", null, "王大明遭起訴。");
        var model = new FakeNewsModel(new Dictionary<string, string> { ["王大明"] = "PER-Suspect" });

        Assert.Null(NewsDigests.Build(news, ["王大明"], "S2")!.Model);
        Assert.Equal(["王大明"], NewsDigests.Build(news, ["王大明"], "S2", model)!.Model!.Entities.Select(e => e.Name));
    }
}
