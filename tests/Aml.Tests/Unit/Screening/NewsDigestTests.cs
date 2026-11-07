using Aml.Core.Explanation;
using Aml.Core.Subjects;

namespace Aml.Tests.Unit.Screening;

/// <summary>The news digest on made-up news: people, roles, events with their slots, and the fixed-pattern summary.</summary>
public class NewsDigestTests
{
    private static NewsDigest Digest(string text, params string[] names) =>
        NewsDigests.Build(new NewsItem("1", "d1", "測試新聞", "某報", null, text), names.Length > 0 ? names : ["王大明"], "S2")!;

    [Fact]
    public void No_news_has_no_digest() => Assert.Null(NewsDigests.Build(null, ["王大明"], "S1"));

    [Fact]
    public void Reads_the_profile_person_events_and_slots_into_a_cited_summary()
    {
        var d = Digest("臺北地檢署偵辦王大明涉嫌詐欺、洗錢案，王大明於2026年5月6日遭起訴，涉案金額新台幣300萬元。");

        var person = Assert.Single(d.People);
        Assert.Equal(("王大明", NewsMatch.ProfileName, 2), (person.Name, person.Match, person.Mentions));
        Assert.Equal([NewsEventType.Investigation, NewsEventType.Indictment], d.Events.Select(e => e.Type));
        var indictment = d.Events[1];
        Assert.Equal((NewsCertainty.Stated, "王大明", NewsMatch.ProfileName, "2026年5月6日", "臺北地檢署", "新台幣300萬元"),
            (indictment.Certainty, indictment.Actor, indictment.ActorMatch, indictment.Date, indictment.Agency, indictment.Amount));
        Assert.Equal(["詐欺", "洗錢"], indictment.Offences);
        Assert.Null(d.Events[0].Date); // the indictment's date is not the investigation's
        Assert.Equal(
        [
            "某報報導「測試新聞」[S2]。",
            "報導人物：王大明（名單人物）。",
            "王大明：臺北地檢署偵辦或調查（詐欺、洗錢；新台幣300萬元）；2026年5月6日 臺北地檢署起訴（詐欺、洗錢；新台幣300萬元）[S2]。",
        ], d.Summary);
    }

    [Fact]
    public void An_event_of_another_named_person_is_theirs()
    {
        var d = Digest("王大明與李四涉詐欺，李四遭起訴。");
        Assert.Equal([("王大明", NewsMatch.ProfileName), ("李四", NewsMatch.OtherPerson)], d.People.Select(p => (p.Name, p.Match)));
        Assert.Equal(("李四", NewsMatch.OtherPerson), (Assert.Single(d.Events).Actor, d.Events[0].ActorMatch));
    }

    [Fact]
    public void A_role_before_a_name_is_kept_and_a_two_character_name_is_not_run_on()
    {
        var d = Digest("被告王明涉嫌背信，檢察官陳志強起訴王明，前立委林大同出庭作證。");
        Assert.Equal([("王明", "被告"), ("陳志強", "檢察官"), ("林大同", "前立委")],
            d.People.Select(p => (p.Name, Assert.Single(p.Roles))));
        Assert.All(d.People, p => Assert.Equal(NewsMatch.OtherPerson, p.Match)); // 王明 is not 王大明
        Assert.Equal("王明", Assert.Single(d.Events, e => e.Type == NewsEventType.Indictment).Actor);
    }

    [Fact]
    public void Anonymised_forms_are_one_person_and_a_shared_surname_is_marked()
    {
        var d = Digest("一名林姓男子遭羈押，林男坦承犯行。陳姓女子則獲不起訴。", "林志明");
        Assert.Equal([("林姓男子", NewsMatch.ProfileSurname, 2), ("陳姓女子", NewsMatch.OtherPerson, 1)],
            d.People.Select(p => (p.Name, p.Match, p.Mentions)));
        Assert.Equal([(NewsEventType.Detention, "林姓男子"), (NewsEventType.NonIndictment, "陳姓女子")], d.Events.Select(e => (e.Type, e.Actor)));
    }

    [Theory]
    [InlineData("王大明尚未被起訴。", NewsEventType.Indictment, NewsCertainty.Negated)]
    [InlineData("王大明恐遭起訴。", NewsEventType.Indictment, NewsCertainty.Hedged)]
    [InlineData("檢方向法院聲請羈押王大明。", NewsEventType.Detention, NewsCertainty.Hedged)]
    [InlineData("檢方聲請羈押王大明獲准。", NewsEventType.Detention, NewsCertainty.Stated)]
    [InlineData("王大明遭拘提到案。", NewsEventType.Arrest, NewsCertainty.Stated)]
    public void Negation_hedging_and_requests_are_kept_apart(string news, NewsEventType type, NewsCertainty certainty)
    {
        var e = Assert.Single(Digest(news).Events, e => e.Type == type);
        Assert.Equal((certainty, "王大明"), (e.Certainty, e.Actor));
    }

    [Fact]
    public void Quoted_speech_holds_no_event_but_names_still_count()
    {
        var d = Digest("王大明說：「我從來沒有被起訴，也沒有被羈押」。");
        Assert.Empty(d.Events);
        Assert.Equal("王大明", Assert.Single(d.People).Name);
        Assert.Equal("報導中未讀到偵查、起訴、判決等事件。", d.Summary[^1]);
    }

    [Fact]
    public void A_conviction_carries_its_penalty_and_a_pronoun_its_person()
    {
        var d = Digest("王大明涉嫌洗錢。他一審被判處有期徒刑3年6月。");
        var conviction = Assert.Single(d.Events, e => e.Type == NewsEventType.Conviction);
        Assert.Equal(("王大明", "有期徒刑3年6月"), (conviction.Actor, conviction.Penalty));
        Assert.Empty(conviction.Offences); // offences are read from the event's own sentence
    }

    [Fact]
    public void An_event_with_nobody_named_says_so()
    {
        var d = Digest("警方昨（6）日搜索公司，帶回帳冊。");
        var search = Assert.Single(d.Events);
        Assert.Equal((NewsEventType.Search, null, "昨（6）日"), (search.Type, search.Actor, search.Date));
        Assert.Equal("未指明對象：昨（6）日 搜索[S2]。", d.Summary[^1]);
    }

    [Fact]
    public void A_name_that_only_starts_like_the_profile_name_is_another_person()
    {
        var d = Digest("被告王明涉嫌詐欺，遭起訴。王明華另案審理。", "王明華");
        Assert.Equal([("王明", NewsMatch.OtherPerson), ("王明華", NewsMatch.ProfileName)], d.People.Select(p => (p.Name, p.Match)).OrderBy(p => p.Item1));
        Assert.Equal(1, d.People.Single(p => p.Name == "王明").Mentions); // not also inside 王明華
        Assert.Equal(("王明", NewsMatch.OtherPerson), (Assert.Single(d.Events).Actor, d.Events[0].ActorMatch));
    }

    [Theory]
    [InlineData("高層被約談。")]
    [InlineData("林務局被調查。")]
    [InlineData("許多人被搜索。")]
    public void Common_words_and_organisations_are_not_people(string news) =>
        Assert.Null(Assert.Single(Digest(news).Events).Actor);

    [Fact]
    public void A_bystander_is_the_actor_only_when_nobody_else_is_named()
    {
        var d = Digest("檢察官陳志強偵辦此案。");
        Assert.Equal("陳志強", Assert.Single(d.Events).Actor);
    }

    [Fact]
    public void An_actor_past_the_people_cap_keeps_their_summary_line()
    {
        // 60 anonymised people (one per surname); only 50 are listed.
        var d = Digest(string.Concat("陳林黃張李王吳劉蔡楊許鄭謝郭洪曾邱廖賴周徐蘇葉莊呂江何蕭羅高潘簡朱鍾鐘游彭詹胡施沈余盧梁趙顏柯翁魏孫戴范方宋鄧杜傅侯曹薛丁卓阮".Distinct().Take(60).Select(s => $"{s}姓男子遭起訴。")));
        Assert.Equal(50, d.People.Count);
        Assert.Equal(60, d.Events.Count);
        Assert.Equal(60, d.Summary.Count(l => l.Contains("起訴[S2]", StringComparison.Ordinal)));
    }

    [Fact]
    public void A_quote_is_cut_at_200_characters_without_splitting_a_surrogate_pair()
    {
        var d = Digest(new string('甲', 199) + "𠀀" + "遭起訴。");
        Assert.Equal(new string('甲', 199) + "…", Assert.Single(d.Events).Quote);
    }

    [Theory]
    [InlineData("digits")]
    [InlineData("clauses")]
    [InlineData("anonymised")]
    [InlineData("triggers")]
    public void Hostile_text_is_read_in_bounded_time(string shape)
    {
        var news = shape switch
        {
            "digits" => "搜索" + new string('1', 2_000_000),
            "clauses" => string.Concat(Enumerable.Repeat("，搜索" + new string('洗', 20_000), 100)),
            "anonymised" => string.Concat(Enumerable.Repeat("王男、", 1_000_000)),
            _ => string.Concat(Enumerable.Repeat("王大明遭起訴，", 300_000)),
        };
        var started = System.Diagnostics.Stopwatch.StartNew();
        Digest(news);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(5), $"{shape} took {started.Elapsed}");
    }

    [Fact]
    public void A_long_text_is_read_in_linear_time_with_capped_lists()
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        var d = Digest(string.Concat(Enumerable.Repeat("王大明遭起訴，李四遭羈押。被告張三被判處有期徒刑1年。", 20_000)) + new string('「', 100_000));
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(5), $"took {started.Elapsed}");
        Assert.Equal(100, d.Events.Count);
        Assert.True(d.People.Count <= 50);
    }
}
