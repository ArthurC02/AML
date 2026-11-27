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
    // None, or none reliable: a date in a sentence, a <time> in a sentence, a modified time.
    [InlineData("", "", null, "MISSING")]
    [InlineData("", "<p>〔本報記者／臺北報導〕王大明於2025年1月5日涉嫌詐欺，遭臺北地檢署提起公訴。</p>", null, "MISSING")]
    [InlineData("", "<p>王大明預定於<time datetime='2026-12-01'>12月1日</time>出庭，檢方指其涉嫌詐欺。</p>", null, "MISSING")]
    [InlineData("<meta property='article:modified_time' content='2026-11-20'>", "", null, "MISSING")]
    // A <time> in a list, sidebar, menu or footer dates another article.
    [InlineData("", "<ul><li><time datetime='2026-11-18'>11/18</time>相關新聞標題</li></ul>", null, "MISSING")]
    [InlineData("", "<aside><time datetime='2026-11-18'>11/18</time></aside>", null, "MISSING")]
    [InlineData("", "<footer><time datetime='2026-11-18'>11/18</time></footer>", null, "MISSING")]
    // A bare two-digit year is not read as 民國; with 民國 it is, and MISSING follows from the 1990 bound (below).
    [InlineData("", "<p>26年11月20日</p>", null, "MISSING")]
    [InlineData("", "<p>民國26年11月20日</p>", "1937-11-20", "TEXT")]
    // A metadata value present but unparseable (another format, a year after this one) is MISSING, the text not asked instead.
    [InlineData("<meta property='article:published_time' content='Nov 20, 2026'>", "<p>2026年11月20日</p>", null, "MISSING")]
    [InlineData("<meta property='article:published_time' content='2999-01-01'>", "<p>2026年11月20日</p>", null, "MISSING")]
    public void A_page_states_its_publication_date_in_metadata_or_at_the_head_of_its_text_and_disagreeing_or_none_is_missing(
        string head, string lead, string? on, string source)
    {
        var page = Read(head, lead);
        Assert.Equal((on, source), (page.PublishedOn?.ToString(), Aml.Core.Cases.CaseJson.Code(page.PublishedOnSource)));
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
