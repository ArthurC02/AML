using System.Net;

namespace Aml.Infra.Web;

/// <summary>A fetched page or file, as received.</summary>
public sealed record Fetched(Uri Url, string MediaType, byte[] Body);

/// <summary>
/// Fetches pages the way a well-behaved crawler does (web-text.md): it follows each site's robots.txt, waits between
/// requests to one site (the site's Crawl-delay if longer), follows redirects itself so every hop is checked against its
/// own site's robots.txt and delay, takes only HTML and PDF, and stops reading past MaxBytes. Requests run one at a time.
/// A robots.txt that fails (5xx, 429, unreachable, timeout) means the site is not crawled now; it is asked again next time.
/// </summary>
public sealed class PoliteFetcher : IDisposable
{
    public const int MaxBytes = 10 * 1024 * 1024, MaxRedirects = 5;

    private readonly HttpClient _http;
    private readonly string _agent;
    private readonly TimeSpan _delay;
    private readonly Dictionary<string, Robots> _robots = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _next = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _one = new(1, 1);

    /// <param name="handler">For tests; by default a handler that does not follow redirects on its own.</param>
    public PoliteFetcher(string agent, TimeSpan delay, HttpMessageHandler? handler = null)
    {
        _http = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
        (_agent, _delay) = (agent, delay);
    }

    public void Dispose()
    {
        _http.Dispose();
        _one.Dispose();
    }

    /// <summary>Null when robots.txt disallows the URL or a hop of its redirects, the reply is not a success, or it is not HTML or PDF.</summary>
    public async Task<Fetched?> GetAsync(Uri url, CancellationToken ct = default)
    {
        await _one.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            for (var hop = 0; hop <= MaxRedirects; hop++)
            {
                if (url.Scheme is not ("http" or "https")) return null;
                var robots = await RobotsOf(url, ct).ConfigureAwait(false);
                if (robots is null || !robots.Allows(url.PathAndQuery)) return null;
                using var reply = await SendAsync(url, robots, ct).ConfigureAwait(false);
                if ((int)reply.StatusCode is >= 300 and < 400 && reply.Headers.Location is { } location)
                {
                    url = new Uri(url, location);
                    continue;
                }
                if (!reply.IsSuccessStatusCode) return null;
                var type = (reply.Content.Headers.ContentType?.MediaType ?? "").ToLowerInvariant();
                if (type == "application/x-pdf") type = "application/pdf";
                if (type is not ("text/html" or "application/xhtml+xml" or "application/pdf")) return null;
                if (reply.Content.Headers.ContentLength > MaxBytes) return null;
                var body = await ReadAtMost(reply, MaxBytes, ct).ConfigureAwait(false);
                return body is null ? null : new Fetched(url, type, body);
            }
            return null;
        }
        finally
        {
            _one.Release();
        }
    }

    // Null while the site's robots.txt cannot be read; that is not remembered, so the next request asks again.
    private async Task<Robots?> RobotsOf(Uri url, CancellationToken ct)
    {
        if (_robots.TryGetValue(url.Authority, out var known)) return known;
        Robots? robots = null;
        try
        {
            // RFC 9309 §2.3.1.2: up to five redirects are followed; a redirect left over counts as no robots.txt.
            var at = new Uri(url, "/robots.txt");
            for (var hop = 0; hop <= MaxRedirects && robots is null; hop++)
            {
                using var reply = await SendAsync(at, Robots.AllowAll, ct).ConfigureAwait(false);
                var status = (int)reply.StatusCode;
                if (status is >= 300 and < 400 && reply.Headers.Location is { } location && hop < MaxRedirects) { at = new Uri(at, location); continue; }
                if (reply.IsSuccessStatusCode)
                {
                    var text = await ReadAtMost(reply, Robots.MaxBytes, ct, truncate: true).ConfigureAwait(false);
                    robots = Robots.Parse(System.Text.Encoding.UTF8.GetString(text!), _agent);
                }
                else if (status is >= 300 and < 500 and not 429) robots = Robots.AllowAll;
                else return null;
            }
            robots ??= Robots.AllowAll;
        }
        catch (Exception e) when (e is HttpRequestException or IOException || e is TaskCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
        // The site's Crawl-delay holds from the robots.txt request on.
        if (robots.CrawlDelay is { } d && d > _delay) _next[url.Authority] = DateTimeOffset.UtcNow + d;
        return _robots[url.Authority] = robots;
    }

    private async Task<HttpResponseMessage> SendAsync(Uri url, Robots robots, CancellationToken ct)
    {
        var wait = _next.GetValueOrDefault(url.Authority) - DateTimeOffset.UtcNow;
        if (wait > TimeSpan.Zero) await Task.Delay(wait, ct).ConfigureAwait(false);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url) { Version = HttpVersion.Version11 };
            request.Headers.UserAgent.ParseAdd(_agent);
            return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        finally
        {
            var gap = robots.CrawlDelay is { } d && d > _delay ? d : _delay;
            _next[url.Authority] = DateTimeOffset.UtcNow + gap;
        }
    }

    private static async Task<byte[]?> ReadAtMost(HttpResponseMessage reply, int max, CancellationToken ct, bool truncate = false)
    {
        await using var stream = await reply.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var copy = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (copy.Length + read > max)
            {
                if (!truncate) return null;
                copy.Write(buffer, 0, max - (int)copy.Length);
                break;
            }
            copy.Write(buffer, 0, read);
        }
        return copy.ToArray();
    }
}
