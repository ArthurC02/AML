using System.Security.Cryptography;
using System.Text;

namespace Aml.Core.Pages;

/// <summary>
/// A received URL as a page key (external-sources.md#received-url-source-2): the rules a URL must meet before anything is
/// fetched, and its canonical form, whose hash is the page id. Whether the host may be reached (the address it resolves
/// to) is the fetcher's URL guard, checked before every request and redirect hop.
/// </summary>
public static class PageUrl
{
    public const int MaxLength = 2048;

    /// <summary>The canonical URL, or the rule the text breaks.</summary>
    public static (string? Canonical, string? Broken) Read(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (null, "no URL");
        if (text.Length > MaxLength) return (null, $"over {MaxLength} characters");
        if (!Uri.TryCreate(text, UriKind.Absolute, out var url)) return (null, "not an absolute URL");
        return Broken(url) is { } broken ? (null, broken) : (Canonical(url), null);
    }

    /// <summary>The rule an absolute URL breaks, or null: http or https, no user name or password, port 80 or 443, at most 2,048 characters.</summary>
    public static string? Broken(Uri url) =>
        !url.IsAbsoluteUri ? "not an absolute URL"
        : url.Scheme is not ("http" or "https") ? "not http or https"
        : url.UserInfo.Length > 0 ? "a user name or password in the URL"
        : url.Port is not (80 or 443) ? "a port other than 80 or 443"
        : url.AbsoluteUri.Length > MaxLength ? $"over {MaxLength} characters"
        : null;

    /// <summary>Lower-case scheme and host (an international name as punycode), no default port, no fragment, no utm_* parameter.</summary>
    public static string Canonical(Uri url)
    {
        var query = string.Join('&', url.Query.TrimStart('?').Split('&')
            .Where(p => p.Length > 0 && !p.StartsWith("utm_", StringComparison.OrdinalIgnoreCase)));
        var host = url.HostNameType == UriHostNameType.IPv6 ? url.Host : url.IdnHost;
        return $"{url.Scheme}://{host}{(url.IsDefaultPort ? "" : ":" + url.Port)}{url.AbsolutePath}{(query.Length > 0 ? "?" + query : "")}";
    }

    /// <summary>The first 16 hex digits of the canonical URL's SHA-256: the page id, and its persons' WEB: key.</summary>
    public static string PageId(string canonical) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..16];
}

/// <summary>Why a page was not read (the page's <c>problem</c>; kind PAGE_PROBLEM in /api/codes).</summary>
public static class PageProblems
{
    public const string UrlNotAllowed = "URL_NOT_ALLOWED";
    public const string RobotsDisallowed = "ROBOTS_DISALLOWED";
    public const string RobotsUnavailable = "ROBOTS_UNAVAILABLE";
    public const string UnsupportedMediaType = "UNSUPPORTED_MEDIA_TYPE";
    public const string TooLarge = "TOO_LARGE";
    public const string TooManyRedirects = "TOO_MANY_REDIRECTS";
    public const string NoMainText = "NO_MAIN_TEXT";
    public const string PdfRefused = "PDF_REFUSED";
    public const string Unreachable = "UNREACHABLE";

    public static string HttpStatus(int status) => $"HTTP_STATUS:{status}";
}
