using System.Net;
using System.Text;
using Aml.Infra.Web;

namespace Aml.Tests.Unit.Extraction;

/// <summary>Main text of made-up web pages, PDF running lines, robots.txt rules and the polite fetcher.</summary>
public class WebTextTests
{
    private const string Article =
        "臺北地檢署今日偵結一起詐欺案，被告王姓男子涉嫌以投資名義吸收資金，金額達新臺幣三千萬元，依詐欺及洗錢罪嫌提起公訴。"
        + "檢察官調查發現，被告自去年起陸續向數十名投資人招攬，並將款項轉入人頭帳戶，再層轉至境外。";

    [Fact]
    public void The_main_text_is_kept_and_menus_footers_and_link_lists_are_dropped()
    {
        var html = $$"""
            <html><head><title>新聞稿 - 測試網站</title></head><body>
            <div class="header"><a href="/">首頁</a><a href="/a">機關介紹</a><a href="/b">便民服務</a></div>
            <nav><ul><li><a href="/1">最新消息</a></li><li><a href="/2">新聞稿</a></li></ul></nav>
            <div class="wrapper">
              <div class="sidebar"><a href="/x">相關連結一</a><a href="/y">相關連結二</a></div>
              <div class="cp">
                <h1>偵結詐欺案新聞稿</h1>
                <p>{{Article}}</p>
                <p>本案經檢察官偵查終結，依法起訴，並請法院從重量刑，以儆效尤，保障投資人權益。</p>
                <ul><li><a href="/n1">上一則：另一則新聞</a></li><li><a href="/n2">下一則：又一則新聞</a></li></ul>
              </div>
            </div>
            <div id="footer">地址：臺北市 電話：02-0000-0000 <a href="/privacy">隱私權政策</a></div>
            <script>var x = "不是內文";</script>
            </body></html>
            """;
        var page = MainText.FromHtml(html);
        Assert.Equal("偵結詐欺案新聞稿", page.Title);
        Assert.Contains("提起公訴", page.Text, StringComparison.Ordinal);
        Assert.Contains("以儆效尤", page.Text, StringComparison.Ordinal);
        foreach (var noise in new[] { "首頁", "最新消息", "相關連結", "上一則", "隱私權", "不是內文", "電話" })
            Assert.DoesNotContain(noise, page.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Text_written_in_a_div_with_line_breaks_is_main_text_one_line_per_break()
    {
        var html = $"<html><body><div class='menu'><a href='/'>首頁</a></div><div><div>{Article}<br>第二段：被告否認犯行，辯稱為正當投資，檢方不採信。</div></div></body></html>";
        var lines = MainText.FromHtml(html).Text.Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("第二段", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void A_wrapper_holding_most_of_the_page_is_kept_whatever_its_name()
    {
        // ASP.NET WebForms wraps the whole page in a <form>; a theme names its wrapper "no-sidebar".
        var html = $"<html><body><form id='form1'><div id='page' class='site no-sidebar'><div class='entry'><p>{Article}</p><p>{Article}</p></div></div></form></body></html>";
        Assert.Contains("提起公訴", MainText.FromHtml(html).Text, StringComparison.Ordinal);
        // Whole class tokens: "shareholder" is not "share".
        Assert.Contains("股東名冊", MainText.FromHtml($"<html><body><div><p>{Article}</p><p class='shareholder-info'>股東名冊：本公司董事會決議通過，相關資料如下，請參閱。</p></div></body></html>").Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_long_link_list_is_not_the_main_text()
    {
        var links = string.Concat(Enumerable.Range(0, 1000).Select(i => $"<li><a href='/n{i}'>第{i}則新聞標題文字，標題文字標題文字標題文字</a></li>"));
        var html = $"<html><body><ul>{links}</ul><div><p>{Article}</p><p>{Article}</p></div></body></html>";
        var text = MainText.FromHtml(html).Text;
        Assert.Contains("提起公訴", text, StringComparison.Ordinal);
        Assert.DoesNotContain("新聞標題", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Source_line_breaks_inline_tags_and_table_cells_stay_in_their_line()
    {
        var html = $"<html><body><div><p>{Article}</p><p>被告否認\n犯行，罰金<del>10</del>20萬元。</p><table><tr><td>被告</td><td>王○○</td></tr></table></div></body></html>";
        var lines = MainText.FromHtml(html).Text.Split('\n');
        Assert.Contains("被告否認 犯行，罰金1020萬元。", lines);
        Assert.Contains("被告 王○○", lines);
    }

    [Fact]
    public void The_title_is_the_main_texts_heading_not_the_sites_logo()
    {
        var html = $"<html><head><title>新聞稿</title></head><body><header><h1><a href='/'>某某地檢署</a></h1></header><div><h1>偵結詐欺案</h1><p>{Article}</p><p>{Article}</p></div></body></html>";
        Assert.Equal("偵結詐欺案", MainText.FromHtml(html).Title);
    }

    [Fact]
    public void A_big5_page_is_decoded_by_its_meta_charset()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var html = $"<html><head><meta charset=\"big5\"></head><body><div><p>{Article}</p></div></body></html>";
        Assert.Contains("提起公訴", MainText.FromHtml(Encoding.GetEncoding(950).GetBytes(html)).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_page_nested_too_deep_is_refused_not_overflowed() =>
        Assert.Throws<FormatException>(() => MainText.FromHtml(string.Concat(Enumerable.Repeat("<div>", 5_000)) + Article));

    [Fact]
    public void Lines_on_every_page_and_page_numbers_are_dropped_from_a_pdf()
    {
        string[] pages =
        [
            "臺灣臺北地方檢察署新聞稿\n發布日期：115年11月3日\n第一頁內文\n- 1 -",
            "臺灣臺北地方檢察署新聞稿\n第二頁內文\n第 2 頁，共 3 頁",
            "臺灣臺北地方檢察署新聞稿\n第三頁內文\n3/3",
        ];
        Assert.Equal(["發布日期：115年11月3日", "第一頁內文", "第二頁內文", "第三頁內文"], PdfText.WithoutRunningLines(pages));
        Assert.Equal(["標題", "內文"], PdfText.WithoutRunningLines(["標題\n內文"]));
        // A number inside the text (a table cell, an amount) is not a page number.
        Assert.Equal(["金額", "30000000", "內文"], PdfText.WithoutRunningLines(["金額\n30000000\n內文\n1"]));
        Assert.Equal(["內文一", "內文二"], PdfText.WithoutRunningLines(["標題\n內文一", "標題\n內文二"]));
    }

    [Fact]
    public void A_sites_template_lines_are_dropped_and_its_documents_kept()
    {
        var docs = Enumerable.Range(1, 10).Select(i =>
            $"臺灣某某地方檢察署新聞稿\n發稿日期：115年11月{i}日\n聯絡電話：02-1234-5678\n這是第{"一二三四五六七八九十"[i - 1]}則新聞的內文。\n另一行內文{new string('甲', i)}").ToList();
        var stripped = RepeatedLines.WithoutSiteTemplate(docs);
        Assert.Equal("這是第三則新聞的內文。\n另一行內文甲甲甲", stripped[2]);
        // Two documents sharing a line are not a template.
        Assert.Equal(["共同一行\n甲", "共同一行\n乙"], RepeatedLines.WithoutSiteTemplate(["共同一行\n甲", "共同一行\n乙"]));
    }

    [Theory]
    [InlineData("/private/a", false)]
    [InlineData("/private/public/a", true)]
    [InlineData("/page.pdf", false)]
    [InlineData("/page.pdf?x=1", true)]
    [InlineData("/news/1", true)]
    public void Robots_rules_take_the_longest_match(string path, bool allowed)
    {
        var robots = Robots.Parse("""
            User-agent: Googlebot
            Disallow: /

            User-agent: *
            Disallow: /private
            Allow: /private/public
            Disallow: /*.pdf$
            Crawl-delay: 5
            """, "Mozilla/5.0 (compatible; AmlPoC-research/0.1)");
        Assert.Equal(allowed, robots.Allows(path));
        Assert.Equal(TimeSpan.FromSeconds(5), robots.CrawlDelay);
    }

    [Fact]
    public void A_crawler_named_in_robots_follows_its_own_group()
    {
        var robots = Robots.Parse("User-agent: amlpoc-research\nSitemap: /sitemap.xml\nUser-agent: other\nDisallow: /\n\nUser-agent: *\nAllow: /", "Mozilla/5.0 (compatible; AmlPoC-research/0.1)");
        Assert.False(robots.Allows("/news"));
        // "Mozilla" in the agent string is not the crawler's name.
        Assert.True(Robots.Parse("User-agent: Mozilla\nDisallow: /", "Mozilla/5.0 (compatible; AmlPoC-research/0.1)").Allows("/news"));
    }

    [Fact]
    public void A_rule_written_in_chinese_matches_the_encoded_path() =>
        Assert.False(Robots.Parse("User-agent: *\nDisallow: /政策/", "x").Allows(new Uri("https://x.tw/政策/a").PathAndQuery));

    [Fact]
    public async Task The_fetcher_follows_robots_skips_other_types_and_stops_at_the_size_cap()
    {
        var handler = new Stub(path => path switch
        {
            "/robots.txt" => (HttpStatusCode.OK, "text/plain", Encoding.UTF8.GetBytes("User-agent: *\nDisallow: /secret")),
            "/page" => (HttpStatusCode.OK, "text/html", Encoding.UTF8.GetBytes("<p>ok</p>")),
            "/image" => (HttpStatusCode.OK, "image/png", [1, 2, 3]),
            "/huge" => (HttpStatusCode.OK, "application/pdf", new byte[PoliteFetcher.MaxBytes + 1]),
            "/go-secret" => (HttpStatusCode.Found, "location:/secret/2", []),
            "/go-page" => (HttpStatusCode.MovedPermanently, "location:/page", []),
            _ => (HttpStatusCode.NotFound, "text/html", []),
        });
        using var fetcher = new PoliteFetcher("test-agent", TimeSpan.Zero, handler);
        var ct = TestContext.Current.CancellationToken;
        Assert.NotNull(await fetcher.GetAsync(new Uri("https://example.test/page"), ct));
        Assert.Null(await fetcher.GetAsync(new Uri("https://example.test/secret/1"), ct));
        Assert.Null(await fetcher.GetAsync(new Uri("https://example.test/image"), ct));
        Assert.Null(await fetcher.GetAsync(new Uri("https://example.test/huge"), ct));
        Assert.Null(await fetcher.GetAsync(new Uri("https://example.test/missing"), ct));
        // A redirect is a new request: its target is checked against robots.txt too.
        Assert.Null(await fetcher.GetAsync(new Uri("https://example.test/go-secret"), ct));
        Assert.Equal("https://example.test/page", (await fetcher.GetAsync(new Uri("https://example.test/go-page"), ct))!.Url.ToString());
        Assert.DoesNotContain(handler.Requested, p => p.StartsWith("/secret", StringComparison.Ordinal));
        Assert.Equal(1, handler.Requested.Count(p => p == "/robots.txt"));
    }

    [Fact]
    public async Task A_site_whose_robots_txt_fails_is_not_crawled_and_asked_again_later()
    {
        var handler = new Stub(path => path == "/robots.txt" ? (HttpStatusCode.ServiceUnavailable, "text/plain", []) : (HttpStatusCode.OK, "text/html", "<p>x</p>"u8.ToArray()));
        using var fetcher = new PoliteFetcher("test-agent", TimeSpan.Zero, handler);
        Assert.Null(await fetcher.GetAsync(new Uri("https://example.test/page"), TestContext.Current.CancellationToken));
        Assert.Null(await fetcher.GetAsync(new Uri("https://example.test/page"), TestContext.Current.CancellationToken));
        Assert.Equal(["/robots.txt", "/robots.txt"], handler.Requested);
    }

    [Fact]
    public async Task A_redirected_robots_txt_is_followed()
    {
        var handler = new Stub(path => path switch
        {
            "/robots.txt" => (HttpStatusCode.Found, "location:/robots", []),
            "/robots" => (HttpStatusCode.OK, "text/plain", "User-agent: *\nDisallow: /secret"u8.ToArray()),
            _ => (HttpStatusCode.OK, "text/html", "<p>x</p>"u8.ToArray()),
        });
        using var fetcher = new PoliteFetcher("test-agent", TimeSpan.Zero, handler);
        Assert.NotNull(await fetcher.GetAsync(new Uri("https://example.test/page"), TestContext.Current.CancellationToken));
        Assert.Null(await fetcher.GetAsync(new Uri("https://example.test/secret"), TestContext.Current.CancellationToken));
    }

    private sealed class Stub(Func<string, (HttpStatusCode Status, string Type, byte[] Body)> reply) : HttpMessageHandler
    {
        public List<string> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requested.Add(path);
            var (status, type, body) = reply(path);
            var response = new HttpResponseMessage(status) { RequestMessage = request, Content = new ByteArrayContent(body) };
            if (type.StartsWith("location:", StringComparison.Ordinal)) response.Headers.Location = new Uri(type["location:".Length..], UriKind.Relative);
            else response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(type);
            return Task.FromResult(response);
        }
    }
}
