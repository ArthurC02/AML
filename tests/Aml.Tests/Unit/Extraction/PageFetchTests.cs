using System.Net;
using System.Text;
using Aml.Core.Pages;
using Aml.Infra.Web;

namespace Aml.Tests.Unit.Extraction;

/// <summary>
/// A received URL (external-sources.md#received-url-source-2): the URL guard on every request and redirect hop, the
/// canonical URL and page id, and the problem each refusal reports. Pages come from memory; hosts resolve from a table.
/// </summary>
public class PageFetchTests
{
    private const string Allowed = "https://news.example.test";

    // Hosts a request may resolve: a name of the bank's network, the machine itself, one public and one private address
    // together, a public IPv6 address, an IPv4-mapped private one, and a name with no address.
    private static readonly Dictionary<string, string[]> Hosts = new()
    {
        ["intranet.bank.test"] = ["10.0.0.5"],
        ["localhost"] = ["127.0.0.1", "::1"],
        ["mixed.example.test"] = ["203.0.113.10", "192.168.0.1"],
        ["v6.example.test"] = ["2606:4700::1111"],
        ["mapped.example.test"] = ["::ffff:10.0.0.1"],
        ["nowhere.example.test"] = [],
    };

    /// <summary>
    /// The guard, one row per rule; a redirect is a new request checked the same way. A refused URL is never requested:
    /// only the allowed start page (and its robots.txt) is.
    /// </summary>
    [Theory]
    [InlineData(Allowed + "/a", null)]
    [InlineData("http://news.example.test/a", null)]
    [InlineData("http://news.example.test:443/a", null)]                 // port 443 over http: an allowed port
    [InlineData("ftp://news.example.test/a", "URL_NOT_ALLOWED")]
    [InlineData("file:///etc/passwd", "URL_NOT_ALLOWED")]
    [InlineData("https://user:secret@news.example.test/a", "URL_NOT_ALLOWED")]
    [InlineData("https://user@news.example.test/a", "URL_NOT_ALLOWED")]
    [InlineData("https://news.example.test:8443/a", "URL_NOT_ALLOWED")]
    [InlineData("http://news.example.test:22/a", "URL_NOT_ALLOWED")]
    [InlineData("http://127.0.0.1/", "URL_NOT_ALLOWED")]                  // loopback
    [InlineData("http://127.255.255.254/", "URL_NOT_ALLOWED")]
    [InlineData("http://10.1.2.3/", "URL_NOT_ALLOWED")]                   // private
    [InlineData("http://172.16.0.1/", "URL_NOT_ALLOWED")]
    [InlineData("http://172.31.255.255/", "URL_NOT_ALLOWED")]
    [InlineData("http://172.32.0.1/", null)]                              // just outside 172.16.0.0/12
    [InlineData("http://192.168.1.1/", "URL_NOT_ALLOWED")]
    [InlineData("http://169.254.169.254/latest/meta-data/", "URL_NOT_ALLOWED")] // link-local: the cloud metadata service
    [InlineData("http://100.64.0.1/", "URL_NOT_ALLOWED")]                 // carrier-grade NAT
    [InlineData("http://0.0.0.0/", "URL_NOT_ALLOWED")]
    [InlineData("http://224.0.0.1/", "URL_NOT_ALLOWED")]                  // multicast
    [InlineData("http://255.255.255.255/", "URL_NOT_ALLOWED")]
    [InlineData("http://2130706433/", "URL_NOT_ALLOWED")]                 // 127.0.0.1 written as one decimal number
    [InlineData("http://0x7f000001/", "URL_NOT_ALLOWED")]                 // … as hex
    [InlineData("http://0x7f.0.0.1/", "URL_NOT_ALLOWED")]
    [InlineData("http://0177.0.0.1/", "URL_NOT_ALLOWED")]                 // … octal
    [InlineData("http://127.1/", "URL_NOT_ALLOWED")]                      // … short form
    [InlineData("http://[::1]/", "URL_NOT_ALLOWED")]                      // IPv6 loopback
    [InlineData("http://[::]/", "URL_NOT_ALLOWED")]                       // unspecified
    [InlineData("http://[fe80::1]/", "URL_NOT_ALLOWED")]                  // link-local
    [InlineData("http://[fec0::1]/", "URL_NOT_ALLOWED")]                  // site-local
    [InlineData("http://[fc00::1]/", "URL_NOT_ALLOWED")]                  // unique-local
    [InlineData("http://[fd12:3456::1]/", "URL_NOT_ALLOWED")]
    [InlineData("http://[ff02::1]/", "URL_NOT_ALLOWED")]                  // multicast
    [InlineData("http://[::ffff:127.0.0.1]/", "URL_NOT_ALLOWED")]         // IPv4-mapped loopback
    [InlineData("http://[::ffff:a00:1]/", "URL_NOT_ALLOWED")]             // IPv4-mapped 10.0.0.1, hex form
    [InlineData("http://[64:ff9b::a00:1]/", "URL_NOT_ALLOWED")]           // NAT64 of 10.0.0.1
    [InlineData("http://[2002:c0a8:101::1]/", "URL_NOT_ALLOWED")]         // 6to4 of 192.168.1.1
    [InlineData("http://[2001:0:4136:e378::1]/", "URL_NOT_ALLOWED")]      // Teredo
    [InlineData("http://[2606:4700::1111]/", null)]                       // public IPv6
    [InlineData("http://localhost/", "URL_NOT_ALLOWED")]                  // a name: its resolved addresses are checked
    [InlineData("http://intranet.bank.test/", "URL_NOT_ALLOWED")]
    [InlineData("http://mixed.example.test/", "URL_NOT_ALLOWED")]         // every address must be public, not one
    [InlineData("http://mapped.example.test/", "URL_NOT_ALLOWED")]
    [InlineData("http://v6.example.test/", null)]
    [InlineData("http://nowhere.example.test/", "UNREACHABLE")]
    [InlineData(Allowed + "/redirect?to=http://127.0.0.1/", "URL_NOT_ALLOWED")]      // a redirect hop to a private address
    [InlineData(Allowed + "/redirect?to=http://[::1]/admin", "URL_NOT_ALLOWED")]
    [InlineData(Allowed + "/redirect?to=http://intranet.bank.test/", "URL_NOT_ALLOWED")]
    [InlineData(Allowed + "/redirect?to=http://169.254.169.254/", "URL_NOT_ALLOWED")]
    [InlineData(Allowed + "/redirect?to=ftp://news.example.test/a", "URL_NOT_ALLOWED")]
    [InlineData(Allowed + "/redirect?to=https://u:p@news.example.test/a", "URL_NOT_ALLOWED")]
    [InlineData(Allowed + "/redirect?to=https://news.example.test:8443/a", "URL_NOT_ALLOWED")]
    [InlineData(Allowed + "/redirect?to=https://news.example.test/a", null)]
    public async Task The_url_guard_refuses_every_request_that_is_not_to_a_public_address_on_80_or_443(string url, string? problem)
    {
        var stub = new WebStub(u => u.AbsolutePath switch
        {
            "/robots.txt" => (HttpStatusCode.NotFound, "text/plain", []),
            "/redirect" => (HttpStatusCode.Found, "location:" + Uri.UnescapeDataString(u.Query["?to=".Length..]), []),
            _ => (HttpStatusCode.OK, "text/html", "<p>x</p>"u8.ToArray()),
        });
        using var fetcher = new PoliteFetcher("test-agent", TimeSpan.Zero, WebStub.Dns(Hosts), stub);
        var got = await fetcher.GetAsync(new Uri(url), TestContext.Current.CancellationToken);
        Assert.Equal(problem, got.Problem);
        Assert.Equal(problem is null, got.Page is not null);
        if (problem is not null)
            Assert.All(stub.RequestedUrls, u => Assert.Equal(Allowed, u.GetLeftPart(UriPartial.Authority)));
    }

    [Theory]
    [InlineData("HTTPS://News.Example.TEST/a/b?x=1#part", "https://news.example.test/a/b?x=1")]
    [InlineData("https://news.example.test:443/a", "https://news.example.test/a")]
    [InlineData("http://news.example.test:80/a", "http://news.example.test/a")]
    [InlineData("http://news.example.test:443/a", "http://news.example.test:443/a")]
    [InlineData("https://news.example.test/a?utm_source=x&id=7&UTM_Medium=y&utm_campaign=", "https://news.example.test/a?id=7")]
    [InlineData("https://news.example.test/a?utm_source=x", "https://news.example.test/a")]
    [InlineData("https://news.example.test/a?", "https://news.example.test/a")]
    [InlineData("https://news.example.test", "https://news.example.test/")]
    [InlineData("https://news.example.test/文章", "https://news.example.test/%E6%96%87%E7%AB%A0")]
    public void The_canonical_url_drops_the_fragment_tracking_parameters_and_default_port(string url, string canonical)
    {
        Assert.Equal((canonical, null), PageUrl.Read(url));
        var id = PageUrl.PageId(canonical);
        Assert.Matches("^[0-9a-f]{16}$", id);
        Assert.Equal(id, PageUrl.PageId(PageUrl.Read(canonical).Canonical!));
    }

    [Theory]
    [InlineData(null, "no URL")]
    [InlineData(" ", "no URL")]
    [InlineData("news.example.test/a", "not an absolute URL")]
    [InlineData("ftp://news.example.test/a", "not http or https")]
    [InlineData("https://u:p@news.example.test/a", "a user name or password in the URL")]
    [InlineData("https://news.example.test:8080/a", "a port other than 80 or 443")]
    public void A_url_that_cannot_be_fetched_says_which_rule_it_breaks(string? url, string broken) =>
        Assert.Equal((null, broken), PageUrl.Read(url));

    [Fact]
    public void A_url_over_2048_characters_is_refused() =>
        Assert.Equal((null, "over 2048 characters"), PageUrl.Read(Allowed + "/" + new string('a', 2048)));

    /// <summary>Each refusal reports its problem; a transient one (worth asking again later) says so.</summary>
    [Theory]
    [InlineData("/ok", null, false)]
    [InlineData("/secret/page", "ROBOTS_DISALLOWED", false)]
    [InlineData("/image", "UNSUPPORTED_MEDIA_TYPE", false)]
    [InlineData("/huge", "TOO_LARGE", false)]
    [InlineData("/loop/0", "TOO_MANY_REDIRECTS", false)]
    [InlineData("/missing", "HTTP_STATUS:404", false)]
    [InlineData("/gone", "HTTP_STATUS:410", false)]
    [InlineData("/busy", "HTTP_STATUS:503", true)]
    [InlineData("/slow-down", "HTTP_STATUS:429", true)]
    [InlineData("/empty", "NO_MAIN_TEXT", false)]
    [InlineData("/deep", "NO_MAIN_TEXT", false)]
    [InlineData("/broken.pdf", "PDF_REFUSED", false)]
    [InlineData("/down", "UNREACHABLE", true)]
    public async Task Every_refusal_is_reported_by_its_problem_code(string path, string? problem, bool transient)
    {
        var stub = new WebStub(u => u.AbsolutePath switch
        {
            "/robots.txt" => (HttpStatusCode.OK, "text/plain", "User-agent: *\nDisallow: /secret"u8.ToArray()),
            "/ok" => (HttpStatusCode.OK, "text/html", Encoding.UTF8.GetBytes("<html><body><article><p>被告王大明涉嫌詐欺，遭臺北地檢署提起公訴。</p></article></body></html>")),
            "/image" => (HttpStatusCode.OK, "image/png", [1, 2, 3]),
            "/huge" => (HttpStatusCode.OK, "text/html", new byte[PoliteFetcher.MaxBytes + 1]),
            var p when p.StartsWith("/loop/", StringComparison.Ordinal) => (HttpStatusCode.Found, $"location:/loop/{int.Parse(p[6..], System.Globalization.CultureInfo.InvariantCulture) + 1}", []),
            "/gone" => (HttpStatusCode.Gone, "text/html", []),
            "/busy" => (HttpStatusCode.ServiceUnavailable, "text/html", []),
            "/slow-down" => (HttpStatusCode.TooManyRequests, "text/html", []),
            "/empty" => (HttpStatusCode.OK, "text/html", "<html><head><title>空白</title></head><body> </body></html>"u8.ToArray()),
            "/deep" => (HttpStatusCode.OK, "text/html", Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("<div>", 5_000)) + "內文")),
            "/broken.pdf" => (HttpStatusCode.OK, "application/pdf", "%PDF-1.7 not really"u8.ToArray()),
            "/down" => throw new HttpRequestException("connection refused"),
            _ => (HttpStatusCode.NotFound, "text/html", []),
        });
        using var fetcher = new PoliteFetcher("test-agent", TimeSpan.Zero, WebStub.Dns(), stub);
        var page = await WebPages.ReadAsync(fetcher, new Uri(Allowed + path), TestContext.Current.CancellationToken);
        Assert.Equal((problem, transient), (page.Problem, page.Transient));
        Assert.Equal(problem is null, page.Text is not null);
        if (problem is null) Assert.Equal(("https://news.example.test/ok", "text/html", "被告王大明涉嫌詐欺，遭臺北地檢署提起公訴。"), (page.FinalUrl, page.MediaType, page.Text));
    }

    [Fact]
    public async Task A_robots_txt_that_cannot_be_read_is_transient_and_one_that_redirects_to_a_private_address_is_refused()
    {
        var stub = new WebStub(u => (u.Host, u.AbsolutePath) switch
        {
            ("down.example.test", "/robots.txt") => (HttpStatusCode.BadGateway, "text/plain", []),
            ("news.example.test", "/robots.txt") => (HttpStatusCode.MovedPermanently, "location:http://10.0.0.5/robots.txt", []),
            _ => (HttpStatusCode.OK, "text/html", "<p>x</p>"u8.ToArray()),
        });
        using var fetcher = new PoliteFetcher("test-agent", TimeSpan.Zero, WebStub.Dns(), stub);
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal(new FetchResult(null, "ROBOTS_UNAVAILABLE", true), await fetcher.GetAsync(new Uri("https://down.example.test/a"), ct));
        Assert.Equal(new FetchResult(null, "URL_NOT_ALLOWED", false), await fetcher.GetAsync(new Uri("https://news.example.test/a"), ct));
        Assert.DoesNotContain(stub.RequestedUrls, u => u.Host == "10.0.0.5");
    }

    /// <summary>
    /// The production handler checks the addresses again when it connects: a private answer there stops the request before
    /// any socket is opened. It keeps no cookies and follows no redirect by itself.
    /// </summary>
    [Fact]
    public async Task The_production_handler_refuses_to_connect_to_a_private_address()
    {
        var resolved = 0;
        using var handler = UrlGuard.Handler((_, _) => { resolved++; return Task.FromResult(new[] { IPAddress.Parse("10.0.0.1") }); });
        Assert.Equal((false, false, false), (handler.UseCookies, handler.UseProxy, handler.AllowAutoRedirect));
        using var http = new HttpClient(handler);
        var e = await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync("http://intranet.bank.test/", TestContext.Current.CancellationToken));
        Assert.IsType<AddressRefusedException>(e.InnerException);
        Assert.Equal(1, resolved);
    }

    /// <summary>
    /// DNS rebinding: the name answers a public address to both guard checks (the page, its robots.txt) and a private one
    /// when the production handler connects. The fetch is refused at the connect step, before any socket is opened.
    /// </summary>
    [Fact]
    public async Task A_name_that_turns_private_between_the_check_and_the_connect_is_refused()
    {
        var answers = 0;
        Task<IPAddress[]> Rebinding(string host, CancellationToken ct) =>
            Task.FromResult(new[] { IPAddress.Parse(++answers <= 2 ? "203.0.113.10" : "127.0.0.1") });
        using var fetcher = new PoliteFetcher("test-agent", TimeSpan.Zero, Rebinding);
        Assert.Equal(new FetchResult(null, "URL_NOT_ALLOWED"), await fetcher.GetAsync(new Uri("http://rebind.example.test/a"), TestContext.Current.CancellationToken));
        Assert.Equal(3, answers);
    }

    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("203.0.113.10", true)]
    [InlineData("100.63.255.255", true)]
    [InlineData("100.128.0.0", true)]
    [InlineData("192.0.0.8", false)]
    [InlineData("198.18.0.1", false)]
    [InlineData("198.20.0.1", true)]
    [InlineData("240.0.0.1", false)]
    [InlineData("2606:4700::1111", true)]
    [InlineData("2001:db8::1", false)]           // documentation
    [InlineData("3fff::1", false)]               // documentation
    [InlineData("::ffff:0:a00:1", false)]        // SIIT
    [InlineData("64:ff9b:1::808:808", false)]    // local-use NAT64
    [InlineData("100::1", false)]                // discard
    [InlineData("100:0:0:1::1", true)]           // just outside 100::/64
    [InlineData("::ffff:8.8.8.8", true)]
    [InlineData("64:ff9b::808:808", true)]
    [InlineData("::127.0.0.1", false)]
    public void Address_classes_beyond_the_guard_rows(string address, bool isPublic) =>
        Assert.Equal(isPublic, UrlGuard.IsPublic(IPAddress.Parse(address)));
}
