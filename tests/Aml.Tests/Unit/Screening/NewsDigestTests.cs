using Aml.Core.Explanation;
using Aml.Core.Shared;
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
            "王大明：臺北地檢署偵辦或調查（詐欺、洗錢；新台幣300萬元）；2026-05-06 臺北地檢署起訴（詐欺、洗錢；新台幣300萬元）[S2]。",
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
    [InlineData("檢方向法院聲請羈押王大明。", NewsEventType.Detention, NewsCertainty.Requested)]
    [InlineData("檢方聲請羈押王大明獲准。", NewsEventType.Detention, NewsCertainty.Stated)]
    [InlineData("檢方聲請羈押王大明遭駁回。", NewsEventType.Detention, NewsCertainty.Negated)]
    [InlineData("王大明遭拘提到案。", NewsEventType.Arrest, NewsCertainty.Stated)]
    [InlineData("王大明是否遭起訴仍未定。", NewsEventType.Indictment, NewsCertainty.Hedged)]
    [InlineData("檢方不排除王大明遭羈押。", NewsEventType.Detention, NewsCertainty.Hedged)]
    [InlineData("一旦罪證確鑿，王大明即遭羈押。", NewsEventType.Detention, NewsCertainty.Hedged)]
    [InlineData("若無意外，但王大明已遭羈押。", NewsEventType.Detention, NewsCertainty.Stated)]
    [InlineData("檢方對王大明提起公訴。", NewsEventType.Indictment, NewsCertainty.Stated)]
    [InlineData("王大明遭金管會裁罰。", NewsEventType.RegulatoryPenalty, NewsCertainty.Stated)]
    [InlineData("王大明遭判處有期徒刑1年，得易科罰金。", NewsEventType.Conviction, NewsCertainty.Stated)]
    [InlineData("法院裁定王大明停止羈押。", NewsEventType.Bail, NewsCertainty.Stated)]
    [InlineData("王大明遭查扣名下帳戶。", NewsEventType.AssetRestraint, NewsCertainty.Stated)]
    [InlineData("王大明提起上訴。", NewsEventType.Appeal, NewsCertainty.Stated)]
    [InlineData("王大明從泰國遭遣返。", NewsEventType.Extradition, NewsCertainty.Stated)]
    [InlineData("假如王大明遭起訴，將面臨重刑。", NewsEventType.Indictment, NewsCertainty.Hedged)]
    [InlineData("法院裁定王大明不予羈押。", NewsEventType.Detention, NewsCertainty.Negated)]
    [InlineData("法院駁回檢方羈押王大明的聲請。", NewsEventType.Detention, NewsCertainty.Negated)]
    [InlineData("王大明全案不得上訴。", NewsEventType.Appeal, NewsCertainty.Negated)]
    [InlineData("王大明的帳戶遭凍結。", NewsEventType.AssetRestraint, NewsCertainty.Stated)]
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
    [InlineData("被告歐陽志明涉嫌詐欺遭起訴。", "歐陽志明")]
    [InlineData("被告約翰·史密斯遭起訴。", "約翰·史密斯")]
    [InlineData("小美（化名）遭起訴。", "小美")]
    [InlineData("少年A遭起訴。", "少年A")]
    public void Compound_dotted_pseudonymous_and_lettered_names_are_people(string news, string name)
    {
        var d = Digest(news);
        Assert.Contains(name, d.People.Select(p => p.Name));
        Assert.Equal(name, Assert.Single(d.Events, e => e.Type == NewsEventType.Indictment).Actor);
    }

    [Theory]
    [InlineData("王品集團遭裁罰。")]
    [InlineData("林口分局被搜索。")]
    [InlineData("金融帳戶遭凍結。")]
    [InlineData("田產遭查封。")]
    public void An_organisation_is_not_a_person(string news)
    {
        var d = Digest(news);
        Assert.Empty(d.People);
        Assert.Null(Assert.Single(d.Events).Actor);
    }

    [Fact]
    public void An_anonymised_form_is_the_one_full_name_of_its_surname()
    {
        var d = Digest("王大明涉嫌詐欺。王男昨遭羈押。");
        var person = Assert.Single(d.People);
        Assert.Equal(("王大明", NewsMatch.ProfileName, 2), (person.Name, person.Match, person.Mentions));
        Assert.Equal(("王大明", NewsMatch.ProfileName), (d.Events[0].Actor, d.Events[0].ActorMatch));
    }

    [Fact]
    public void An_anonymised_form_with_two_full_names_of_its_surname_stays_apart()
    {
        var d = Digest("被告王大明與被告王小華涉詐欺，王男遭起訴。");
        Assert.Equal(["王大明", "王男", "王小華"], d.People.Select(p => p.Name));
        Assert.Equal(NewsMatch.ProfileSurname, d.People[1].Match);
    }

    [Fact]
    public void A_role_word_says_the_party()
    {
        var d = Digest("被告王明涉嫌背信，檢察官陳志強起訴王明，證人林大同出庭，告訴人李小華到庭。");
        Assert.Equal([("王明", NewsParty.Suspect), ("陳志強", NewsParty.Official), ("林大同", NewsParty.Witness), ("李小華", NewsParty.Victim)],
            d.People.Select(p => (p.Name, p.Party!.Value)));
    }

    [Theory]
    [InlineData("據了解，王大明已遭約談。", NewsSource.UnnamedSource)]
    [InlineData("判決指出，王大明判處有期徒刑2年。", NewsSource.Court)]
    [InlineData("檢方調查，王大明遭約談。", NewsSource.Investigators)]
    [InlineData("王大明否認遭約談。", NewsSource.Defence)]
    [InlineData("據了解，案情複雜，但王大明已遭約談。", NewsSource.Media)]
    [InlineData("王大明遭約談。", NewsSource.Media)]
    [InlineData("王大明否認犯行，法院仍依詐欺罪判處有期徒刑2年。", NewsSource.Media)]
    public void Whose_claim_an_event_is_comes_from_the_cue_before_it(string news, NewsSource source) =>
        Assert.Equal(source, Digest(news).Events[^1].Source);

    [Theory]
    [InlineData("王大明昨（6）日遭起訴。", "2026-05-06")]
    [InlineData("王大明昨天遭起訴。", "2026-05-06")]
    [InlineData("王大明於3日遭起訴。", "2026-05-03")]
    [InlineData("王大明於12日遭起訴。", "2026-04-12")]
    [InlineData("王大明於12月3日遭起訴。", "2025-12-03")]
    [InlineData("王大明於113年5月6日遭起訴。", "2024-05-06")]
    [InlineData("王大明去年遭起訴。", "2025")]
    [InlineData("王大明日前遭起訴。", null)]
    [InlineData("王大明判處有期徒刑3年6月。", null)]
    [InlineData("王大明判處有期徒刑10年6月。", null)]
    [InlineData("王大明遭羈押2月。", null)]
    public void A_date_is_resolved_against_the_publication_date(string news, string? date)
    {
        var d = NewsDigests.Build(new NewsItem("1", "d1", "測試新聞", "某報", new PartialDate(2026, 5, 7), news), ["王大明"], "S2")!;
        Assert.Equal(date, Assert.Single(d.Events).DateValue?.ToString());
    }

    [Theory]
    [InlineData(2025, 3, 1, "王大明昨（28）日遭起訴。", "2025-02-28")]
    [InlineData(2025, 3, 1, "王大明於31日遭起訴。", "2025-01-31")]
    [InlineData(2025, 3, 2, "王大明於2月29日遭起訴。", "2024-02-29")]
    [InlineData(2026, 1, 1, "王大明昨天遭起訴。", "2025-12-31")]
    public void A_date_at_a_month_or_year_edge_is_the_latest_that_exists(int year, int month, int day, string news, string date)
    {
        var d = NewsDigests.Build(new NewsItem("1", "d1", "測試新聞", "某報", new PartialDate(year, month, day), news), ["王大明"], "S2")!;
        Assert.Equal(date, Assert.Single(d.Events).DateValue?.ToString());
    }

    [Fact]
    public void Dated_events_of_one_person_are_told_in_date_order()
    {
        var d = NewsDigests.Build(new NewsItem("1", "d1", "測試新聞", "某報", new PartialDate(2026, 5, 7),
            "王大明於5月6日遭起訴。王大明於3月2日遭羈押。"), ["王大明"], "S2")!;
        Assert.Equal("王大明：2026-03-02 羈押；2026-05-06 起訴[S2]。", d.Summary[^1]);
    }

    [Fact]
    public void Offences_carry_risk_categories_and_slots_their_request_and_finality()
    {
        var d = Digest("王大明涉嫌內線交易及洗錢遭起訴，檢方具體求刑8年。");
        var indictment = Assert.Single(d.Events);
        Assert.Equal(["MARKET_ABUSE", "MONEY_LAUNDERING"], indictment.OffenceCategories);
        Assert.Equal("8年", indictment.RequestedPenalty);
        Assert.All(indictment.OffenceCategories, c => Assert.NotNull(Aml.Core.Codes.CodeCatalog.Find(c)));

        var final = Assert.Single(Digest("王大明詐欺案判處有期徒刑3年，緩刑5年，全案定讞。").Events, e => e.Type == NewsEventType.Conviction);
        Assert.True(final.Final);
        Assert.Equal("有期徒刑3年，緩刑5年", final.Penalty);
        Assert.False(Assert.Single(Digest("王大明判處有期徒刑3年，可上訴。").Events, e => e.Type == NewsEventType.Conviction).Final);
        Assert.False(Assert.Single(Digest("王大明案尚未定讞。").Events).Final);
        Assert.Equal("有期徒刑8年", Assert.Single(Digest("檢方求處有期徒刑10年，法院判處王大明有期徒刑8年。").Events, e => e.Type == NewsEventType.Conviction).Penalty);
    }

    [Fact]
    public void Every_offence_category_is_in_the_catalog()
    {
        string[] words = ["違反證券交易法", "洗錢", "詐欺", "違反銀行法", "逃漏稅", "偽造文書", "偽鈔", "背信", "貪污", "收賄", "販毒", "走私", "人口販運",
            "組織犯罪", "資恐", "槍械", "殺人", "竊盜", "恐嚇", "妨害性自主", "盜採", "違反公司法"];
        var d = Digest(string.Concat(words.Select(w => $"王大明涉嫌{w}遭起訴。")));
        var categories = d.Events.SelectMany(e => e.OffenceCategories).ToList();
        Assert.Equal(words.Length, categories.Count);
        Assert.All(categories, c => Assert.NotNull(Aml.Core.Codes.CodeCatalog.Find(c)));
    }

    [Fact]
    public void A_request_says_so_in_the_summary()
    {
        var d = Digest("檢方向法院聲請羈押王大明。");
        Assert.Equal("王大明：聲請羈押（尚未裁定）[S2]。", d.Summary[^1]);
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
