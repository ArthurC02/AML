using Aml.Core.Pages;
using Aml.Core.Shared;
using Aml.Infra.Web;

namespace Aml.Tests.Unit.Extraction;

/// <summary>
/// The publication date a made-up page states (external-sources.md#common-path): metadata first, then a date line or dateline
/// at the head of the main text; every date found must agree, else MISSING; none is MISSING; never the fetch time.
/// </summary>
public class PublishedDateTests
{
    private const string Article =
        "臺北地檢署偵結一起詐欺案，被告王姓男子涉嫌以投資名義吸收資金，金額達新臺幣三千萬元，依詐欺及洗錢罪嫌提起公訴。"
        + "檢察官調查發現，被告陸續向數十名投資人招攬，並將款項轉入人頭帳戶，再層轉至境外。";

    private static PageText Read(string head, string lead = "") =>
        MainText.FromHtml($"<html><head><title>詐欺案</title>{head}</head><body><article><h1>詐欺案起訴</h1>{lead}<p>{Article}</p></article></body></html>");

    [Theory]
    // Metadata, each form alone.
    [InlineData("<script type='application/ld+json'>{\"@type\":\"NewsArticle\",\"datePublished\":\"2026-11-20T08:30:00+08:00\"}</script>", "", "2026-11-20", "METADATA")]
    [InlineData("<meta property='article:published_time' content='2026-11-20T08:30:00+08:00'>", "", "2026-11-20", "METADATA")]
    [InlineData("<meta property='og:published_time' content='2026-11-20'>", "", "2026-11-20", "METADATA")]
    [InlineData("<meta itemprop='datePublished' content='2026-11-20'>", "", "2026-11-20", "METADATA")]
    [InlineData("", "<div class='byline'><time itemprop='datePublished' datetime='2026-11-20T09:00'>11月20日</time></div>", "2026-11-20", "METADATA")]
    [InlineData("", "<header><time datetime='2026-11-20T09:00'>2026/11/20 09:00</time></header>", "2026-11-20", "METADATA")]
    [InlineData("<meta property='article:published_time' content='2026-11'>", "", "2026-11", "METADATA")]
    // The text: a date on a line of its own or in a dateline's bracket, Western and 民國.
    [InlineData("", "<p>2026-11-20 10:30</p>", "2026-11-20", "TEXT")]
    [InlineData("", "<p>2026／11／20</p>", "2026-11-20", "TEXT")]
    [InlineData("", "<p>2026年11月20日</p>", "2026-11-20", "TEXT")]
    [InlineData("", "<p>民國115年11月20日</p>", "2026-11-20", "TEXT")]
    [InlineData("", "<p>發布日期：115年11月20日</p>", "2026-11-20", "TEXT")]
    [InlineData("", "<p>〔2026/11/20 本報記者／臺北報導〕王大明涉嫌詐欺，遭臺北地檢署提起公訴。</p>", "2026-11-20", "TEXT")]
    // Agreeing sources: the metadata's, the most precise kept.
    [InlineData("<meta property='article:published_time' content='2026-11-20'>", "<p>2026年11月20日</p>", "2026-11-20", "METADATA")]
    [InlineData("<meta property='article:published_time' content='2026-11'>", "<p>2026年11月20日</p>", "2026-11-20", "TEXT")]
    // Disagreeing sources: MISSING.
    [InlineData("<meta property='article:published_time' content='2026-11-20'><script type='application/ld+json'>{\"datePublished\":\"2026-11-19\"}</script>", "", null, "MISSING")]
    [InlineData("<meta property='article:published_time' content='2026-11-20'>", "<p>2026年11月18日</p>", null, "MISSING")]
    [InlineData("", "<p>2026-11-20</p><p>2026-11-21</p>", null, "MISSING")]
    // None, or none reliable: a date in a sentence, a <time> in a sentence.
    [InlineData("", "", null, "MISSING")]
    [InlineData("", "<p>〔本報記者／臺北報導〕王大明於2025年1月5日涉嫌詐欺，遭臺北地檢署提起公訴。</p>", null, "MISSING")]
    [InlineData("", "<p>王大明預定於<time datetime='2026-12-01'>12月1日</time>出庭，檢方指其涉嫌詐欺。</p>", null, "MISSING")]
    // A modified time stands in only when no publication date is stated, as an update date; Dublin Core's DC.Date is not read.
    [InlineData("<meta property='article:modified_time' content='2026-11-20'>", "", "2026-11-20", "UPDATE_DATE")]
    [InlineData("<meta property='article:modified_time' content='2026-11-25'>", "<p>2026年11月20日</p>", "2026-11-20", "TEXT")]
    [InlineData("<meta name='DC.Date' content='2026-11-20'>", "", null, "MISSING")]
    // A label date that differs from an unlabelled first-line date: MISSING.
    [InlineData("", "<p>2026-11-19</p><p>發布日期：115-11-20</p>", null, "MISSING")]
    // A <time> in a list, sidebar, menu or footer dates another article.
    [InlineData("", "<ul><li><time datetime='2026-11-18'>11/18</time>相關新聞標題</li></ul>", null, "MISSING")]
    [InlineData("", "<aside><time datetime='2026-11-18'>11/18</time></aside>", null, "MISSING")]
    [InlineData("", "<footer><time datetime='2026-11-18'>11/18</time></footer>", null, "MISSING")]
    // A bare two-digit year is not read as 民國; with 民國 it is, and MISSING follows from the 1990 bound (below).
    [InlineData("", "<p>26年11月20日</p>", null, "MISSING")]
    [InlineData("", "<p>民國26年11月20日</p>", "1937-11-20", "TEXT")]
    // A publication label and its date: a 民國 year with dashes or slashes, a 12-hour time; an update label alone gives an
    // update date; both labels on one line, MISSING.
    [InlineData("", "<p>發布日期： 115-11-20</p>", "2026-11-20", "TEXT")]
    [InlineData("", "<p>發佈時間：115/11/20 下午 02:15</p>", "2026-11-20", "TEXT")]
    [InlineData("", "<p>更新日期：115-11-20</p>", "2026-11-20", "UPDATE_DATE")]
    [InlineData("", "<p>發布日期：115-11-20 更新日期：115-11-21</p>", null, "MISSING")]
    // A label preceded by a CJK character is another label (預定發布日期, 原發布日期, 附件發布日期, 資料更新日期): not read.
    [InlineData("", "<p>預定發布日期：115-11-20</p>", null, "MISSING")]
    [InlineData("", "<p>原發布日期：115-11-20</p>", null, "MISSING")]
    [InlineData("", "<p>附件發布日期：115-11-20</p>", null, "MISSING")]
    [InlineData("", "<p>資料更新日期：115-11-20</p>", null, "MISSING")]
    // A day or month that is no day after a label (0, 13, 30 Feb, 45-67): MISSING, never a coarser date, and no update
    // date stands in for a publication date stated but unreadable.
    [InlineData("", "<p>發布日期：123-45-67</p>", null, "MISSING")]
    [InlineData("", "<p>發布日期：100/13/40</p>", null, "MISSING")]
    [InlineData("", "<p>發布日期：115-13-01</p>", null, "MISSING")]
    [InlineData("", "<p>發布日期：115-02-30</p>", null, "MISSING")]
    [InlineData("", "<p>發布日期：115-11-00</p>", null, "MISSING")]
    [InlineData("", "<p>發布日期：115-00-00</p><p>更新日期：115-11-20</p>", null, "MISSING")]
    [InlineData("", "<p>檢方於115-11-20發布新聞稿說明王大明涉嫌詐欺。</p>", null, "MISSING")]
    [InlineData("", "<p>發布日期：115-11-20</p><p>發布日期：115-11-19</p>", null, "MISSING")]
    // A metadata value present but unparseable (another format, a year after this one) is MISSING, the text not asked instead.
    [InlineData("<meta property='article:published_time' content='Nov 20, 2026'>", "<p>2026年11月20日</p>", null, "MISSING")]
    [InlineData("<meta property='article:published_time' content='2999-01-01'>", "<p>2026年11月20日</p>", null, "MISSING")]
    public void A_page_states_its_publication_date_in_metadata_or_at_the_head_of_its_text_and_disagreeing_or_none_is_missing(
        string head, string lead, string? on, string source)
    {
        var page = Read(head, lead);
        Assert.Equal((on, source), (page.PublishedOn?.ToString(), Aml.Core.Cases.CaseJson.Code(page.PublishedOnSource)));
    }

    [Theory]
    // A site template's row outside the main text: the publication label read, the update row beside it not.
    [InlineData("<table><tr><td>發布日期：</td><td>2026/11/20 下午 02:15:54</td></tr><tr><td>更新日期：</td><td>2026/11/21 上午 09:00:00</td></tr></table>", "2026-11-20", "TEXT")]
    [InlineData("<div><span>上版日期</span><span>：115-11-20</span></div>", "2026-11-20", "TEXT")]
    // Only an update date: it stands in, marked UPDATE_DATE (DC.Date beside it not read); two that disagree are MISSING.
    [InlineData("<div>更新日期：115-11-20</div><meta name='DC.Date' content='2023-01-01'>", "2026-11-20", "UPDATE_DATE")]
    [InlineData("<div><span>最後更新：</span><span>2026/11/20 上午 09:00</span></div>", "2026-11-20", "UPDATE_DATE")]
    [InlineData("<div>更新日期：115-11-20</div><div>更新日期：115-11-21</div>", null, "MISSING")]
    // Publication dates that disagree stay MISSING: the update date does not paper over it.
    [InlineData("<div>發布日期：115-11-20</div><div>發布日期：115-11-19</div><div>更新日期：115-11-21</div>", null, "MISSING")]
    // Both labels in one element: which is which is not read, MISSING.
    [InlineData("<div>發布日期：2026/11/20 更新日期：2026/11/21</div>", null, "MISSING")]
    // A label in a list, in a container named as boilerplate (related, comment), in a template or noscript dates another
    // article: not read, one or several sharing a date, publication or update.
    [InlineData("<ul><li><span>發布日期：</span><span>115-11-18</span></li></ul>", null, "MISSING")]
    [InlineData("<div class='related-news'><div>發布日期：115-11-18</div></div>", null, "MISSING")]
    [InlineData("<div id='comments'><div>發布日期：115-11-18</div><div>發布日期：115-11-18</div></div>", null, "MISSING")]
    [InlineData("<div class='related'><div>更新日期：115-11-18</div></div>", null, "MISSING")]
    [InlineData("<noscript><div>發布日期：115-11-18</div></noscript><template><div>發布日期：115-11-18</div></template>", null, "MISSING")]
    // A publication label skipped is no publication date stated: the article's update date stands in.
    [InlineData("<div class='sidebar'><div>發布日期：115-11-18</div></div><div>更新日期：115-11-20</div>", "2026-11-20", "UPDATE_DATE")]
    // Another article's card, however named: a label near a heading that is a link, or inside a link, is not read, so it
    // never becomes the page's publication date (a report date); the article's own update date stands in.
    [InlineData("<div><h3><a href='/b'>另一則新聞</a></h3><div>發布日期：115-11-18</div></div><div>更新日期：115-11-20</div>", "2026-11-20", "UPDATE_DATE")]
    [InlineData("<a href='/b'><h3>另一則新聞</h3><span>發布日期：115-11-18</span></a>", null, "MISSING")]
    // The page's own heading is no link: its template row is read, and a card beside it does not count.
    [InlineData("<div><h2>本署新聞</h2><table><tr><td>發布日期：</td><td>2026/11/20</td></tr></table></div>", "2026-11-20", "TEXT")]
    [InlineData("<div><h1>標題</h1><div>發布日期：115-11-20</div></div><div><h3><a href='/b'>另一則</a></h3><div>發布日期：115-11-18</div></div>", "2026-11-20", "TEXT")]
    // Another label (預定發布日期) or a date that is no day in the template: MISSING, the update row not read instead.
    [InlineData("<div>預定發布日期：115-11-20</div>", null, "MISSING")]
    [InlineData("<div>發布日期：115-02-30</div><div>更新日期：115-11-20</div>", null, "MISSING")]
    // Two template labels that disagree are MISSING.
    [InlineData("<div>發布日期：115-11-20</div><div>發布日期：115-11-19</div>", null, "MISSING")]
    public void A_publication_label_in_the_page_template_outside_the_main_text_is_read_and_an_update_label_only_stands_in(string outside, string? on, string source)
    {
        var page = MainText.FromHtml($"<html><head><title>詐欺案</title></head><body>{outside}<article><h1>詐欺案起訴</h1><p>{Article}</p></article></body></html>");

        Assert.Equal((on, source), (page.PublishedOn?.ToString(), Aml.Core.Cases.CaseJson.Code(page.PublishedOnSource)));
    }

    [Theory]
    // A PDF's head: the label after a letterhead of up to four lines, or run into the letterhead's last line; 新聞稿（date）.
    [InlineData("某地方檢察署新聞稿\nSome District Prosecutors Office\nPress Release\n發稿日期：民國115年11月20日\n本文", "2026-11-20")]
    [InlineData("某地方檢察署新聞稿\nSome District Prosecutors Office Press Release發稿日期：115 年 11 月 20 日\n本文", "2026-11-20")]
    [InlineData("新聞聯絡：0200000000\n某署\nSome Office\n新 聞 稿（115.9.10）\n本文", "2026-09-10")]
    // Past the fifth line, a date with no label after the third, a label cut from a long line: none.
    [InlineData("一\n二\n三\n四\n五\n發稿日期：115年11月20日", null)]
    [InlineData("一\n二\n三\n2026年11月20日", null)]
    // Another label: a CJK character before it (預定, 原, 附件); a label run into Latin text is still read (above).
    [InlineData("某署新聞稿\n預定發布日期：115年11月20日", null)]
    [InlineData("某署新聞稿\n原發布日期：115年11月20日", null)]
    [InlineData("某署新聞稿\n附件發布日期：115年11月20日", null)]
    // A day that is none after a label.
    [InlineData("某署新聞稿\n發稿日期：115年2月30日", null)]
    [InlineData("某署新聞稿\n發稿日期：115年11月0日", null)]
    public void A_publication_label_at_the_head_of_a_text_is_read_within_five_lines(string text, string? on)
    {
        var (date, _) = PublishedDates.Agreed([], PublishedDates.FromText(text));

        Assert.Equal(on, date?.ToString());
    }

    [Theory]
    // A text (a PDF) with an update label only: it stands in, marked; with a publication label beside it, that one wins.
    [InlineData("某署新聞稿\n更新日期：115年11月20日\n本文", "2026-11-20", "UPDATE_DATE")]
    [InlineData("某署新聞稿\n發稿日期：115年11月18日\n更新日期：115年11月20日\n本文", "2026-11-18", "TEXT")]
    [InlineData("某署新聞稿\n資料更新日期：115年11月20日\n本文", null, "MISSING")]
    public void An_update_label_at_the_head_of_a_text_stands_in_only_without_a_publication_date(string text, string? on, string source)
    {
        var (date, from) = PublishedDates.Agreed([], PublishedDates.FromText(text), PublishedDates.FromText(text, update: true));

        Assert.Equal((on, source), (date?.ToString(), Aml.Core.Cases.CaseJson.Code(from)));
    }

    [Fact]
    public void A_long_paragraph_ending_on_a_labelled_date_is_no_label_line()
    {
        var line = string.Concat(Enumerable.Repeat("王大明涉嫌詐欺遭起訴，", 20)) + "發布日期：115-11-20";

        Assert.Empty(PublishedDates.FromText(line)!);
    }

    [Theory]
    // A label inside 200 nested elements is read; a page nesting deeper than HtmlExport.MaxDepth is refused before any date
    // is looked for, quickly, labels and all.
    [InlineData(200, "2026-11-20")]
    [InlineData(5_000, null)]
    public void A_deeply_nested_page_is_depth_checked_before_its_labels_are_read(int depth, string? on)
    {
        var html = "<html><body>" + string.Concat(Enumerable.Repeat("<div>", depth)) + "<span>發布日期：115-11-20</span>"
            + string.Concat(Enumerable.Repeat("<span>發布日期：115-11-20</span>", 1_000)) + $"<p>{Article}</p></body></html>";
        var clock = System.Diagnostics.Stopwatch.StartNew();

        if (on is null) Assert.Throws<FormatException>(() => MainText.FromHtml(html));
        else Assert.Equal(on, MainText.FromHtml(html).PublishedOn?.ToString());
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"took {clock.Elapsed}");
    }

    [Fact]
    public void Labels_skipped_in_boilerplate_do_not_use_up_the_labels_looked_at()
    {
        var related = "<div class='related'>" + string.Concat(Enumerable.Repeat("<div>發布日期：115-11-18</div>", 25)) + "</div>";
        var page = MainText.FromHtml($"<html><body>{related}<div>發布日期：115-11-20</div><article><p>{Article}</p></article></body></html>");

        Assert.Equal(("2026-11-20", PublicationSource.Text), (page.PublishedOn?.ToString(), page.PublishedOnSource));
    }

    [Fact]
    public void A_paragraph_right_under_the_body_is_read_as_the_main_text()
    {
        // The grandparent of a paragraph under <body> is <html>, which has no counts: MainText.Best once threw here.
        var page = MainText.FromHtml($"<html><body><p>{Article}</p></body></html>");

        Assert.Equal(Article, page.Text);
    }

    [Theory]
    // A label's date after the fetch or before 1990 is MISSING at the page (PageReadings.Published), and no update date stands in.
    [InlineData("<div>發布日期：2026-11-30</div><div>更新日期：115-11-20</div>")]
    [InlineData("<div>發布日期：民國70年1月1日</div><div>更新日期：115-11-20</div>")]
    [InlineData("<div>更新日期：2026-11-30</div>")]
    [InlineData("<div>更新日期：1989/12/31</div>")]
    public void A_label_date_after_the_fetch_or_before_1990_is_missing(string outside)
    {
        var page = MainText.FromHtml($"<html><body>{outside}<article><p>{Article}</p></article></body></html>");
        var fetched = new PageFetch("https://news.example.test/a", "text/html", null, "t", page.Text, PublishedOn: page.PublishedOn, PublishedOnSource: page.PublishedOnSource);

        Assert.Equal(((PartialDate?)null, PublicationSource.Missing), PageReadings.Published(fetched, new DateTimeOffset(2026, 11, 26, 10, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void A_long_line_opening_a_bracket_it_never_closes_is_read_quickly_and_states_no_date()
    {
        // 1 MB of dates after an opening bracket: unbounded, the dateline pattern backtracked for seconds.
        var line = "[" + string.Concat(Enumerable.Repeat("2026-1-1 ", 1_000_000 / 9));
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var text = PublishedDates.FromText(line + "\n" + line + "\n" + line);
        var meta = MainText.FromHtml($"<html><head><script type='application/ld+json'>{line}</script></head><body><article><p>{Article}</p></article></body></html>");

        Assert.Equal((0, (PartialDate?)null, PublicationSource.Missing), (text!.Count, meta.PublishedOn, meta.PublishedOnSource));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"took {clock.Elapsed}");
    }

    [Theory]
    [InlineData("2026-11-26", "METADATA", "2026-11-26", "METADATA")]
    [InlineData("2026-11-27", "TEXT", "2026-11-27", "TEXT")] // the next day is already the date somewhere (UTC+14)
    [InlineData("2026-11-28", "METADATA", null, "MISSING")]   // after the fetch: invalid
    [InlineData("2026-11", "TEXT", "2026-11", "TEXT")]
    [InlineData("1990-01-01", "TEXT", "1990-01-01", "TEXT")]
    [InlineData("1989-12-31", "METADATA", null, "MISSING")]  // before 1990: taken as misread
    [InlineData("1937-11-20", "TEXT", null, "MISSING")]
    [InlineData(null, "MISSING", null, "MISSING")]
    public void A_publication_date_after_the_fetch_is_missing_and_the_fetch_time_is_never_the_date(string? read, string from, string? on, string source)
    {
        var fetched = new PageFetch("https://news.example.test/a", "text/html", null, "t", "x", PublishedOn: PartialDate.Parse(read),
            PublishedOnSource: Aml.Core.Cases.CaseJson.ParseCode<PublicationSource>(from));

        var (date, kind) = PageReadings.Published(fetched, new DateTimeOffset(2026, 11, 26, 10, 0, 0, TimeSpan.Zero));

        Assert.Equal((on, source), (date?.ToString(), Aml.Core.Cases.CaseJson.Code(kind)));
    }
}
