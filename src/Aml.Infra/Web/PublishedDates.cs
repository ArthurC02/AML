using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp.Html.Dom;
using Aml.Core.Normalization;
using Aml.Core.Pages;
using Aml.Core.Shared;

namespace Aml.Infra.Web;

/// <summary>
/// The publication date a page states (external-sources.md#common-path), where pages reliably state it: structured metadata
/// first (JSON-LD datePublished, meta article:published_time / og:published_time / itemprop datePublished, a &lt;time datetime&gt;
/// outside a sentence), then a date line or a dateline among the first lines of the main text. Every date found must agree
/// (at its precision), else the date is MISSING; none found is MISSING too, and so is a metadata value present but unparseable.
/// Dates are read by <see cref="PartialDate.Parse"/>, as written (an ISO time's own local date). A modified or updated time
/// is not a publication date and is not read. Lines and values are cut short and every match is time-bounded (untrusted input).
/// </summary>
public static partial class PublishedDates
{
    private const int FirstLines = 3;
    private const int FirstGregorianYear = 1000;
    private const string RocEra = "民國";

    // The page is untrusted: a real date line, dateline or metadata value is short, so each is cut to this length before
    // any match, and every match has a timeout whose expiry makes the date MISSING, never a failed page.
    private const int MaxLine = 200;
    private const int MatchTimeoutMs = 200;
    // ponytail: a JSON-LD block is searched in its first 64K characters only; a datePublished past that is not read.
    private const int MaxJsonLd = 65536;

    private static readonly HashSet<string> MetaNames = new(
        ["article:published_time", "og:published_time", "og:article:published_time", "datePublished"], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The dates the document's metadata states; read before boilerplate is removed (a &lt;time&gt; in the header). Null
    /// (MISSING) when a value is present but does not parse (a foreign format, a year after this one) or a match timed out:
    /// the text is then not asked instead.
    /// </summary>
    public static List<PartialDate>? Metadata(IHtmlDocument doc)
    {
        try
        {
            var values = new List<string?>();
            foreach (var s in doc.QuerySelectorAll("script[type='application/ld+json']"))
                values.AddRange(JsonLdPublished().Matches(Cut(s.TextContent, MaxJsonLd)).Select(m => m.Groups["d"].Value));
            foreach (var m in doc.QuerySelectorAll("meta[content]"))
                if (MetaNames.Contains(m.GetAttribute("property") ?? m.GetAttribute("name") ?? m.GetAttribute("itemprop") ?? "")) values.Add(m.GetAttribute("content"));
            foreach (var e in doc.QuerySelectorAll("[itemprop='datePublished']:not(meta)"))
                values.Add(e.GetAttribute("datetime") ?? e.GetAttribute("content") ?? e.TextContent);
            // A <time> written inside a sentence dates what the sentence tells, one in a list, sidebar, menu or footer
            // another article's: neither is the page's.
            foreach (var t in doc.QuerySelectorAll("time[datetime]:not([itemprop])"))
                if (t.Closest("p,li,aside,nav,footer") is null) values.Add(t.GetAttribute("datetime"));
            var present = values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => Cut(v!.Trim(), MaxLine)).ToList();
            var dates = present.Select(PartialDate.Parse).ToList();
            return dates.Contains(null) ? null : [.. dates.OfType<PartialDate>()];
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    /// <summary>
    /// Full dates on a line of their own (2026-11-20, 2026/11/20 10:30, 2026年11月20日, 民國115年11月20日, 115年11月20日,
    /// 發布日期：…) or inside a dateline's bracket (〔2026-11-20 記者／臺北報導〕) among the first lines of the text.
    /// A date in a sentence is the sentence's, not the page's, and is not read. A two-digit year is read only after 民國.
    /// Null (MISSING) when a match timed out.
    /// </summary>
    public static List<PartialDate>? FromText(string text)
    {
        try
        {
            var found = new List<PartialDate>();
            foreach (var line in text.Split('\n', FirstLines + 1).Take(FirstLines).Select(l => CjkText.HalfWidth(Cut(l.Trim(), MaxLine))))
                foreach (var m in new[] { DateLine().Match(line), Dateline().Match(line) }.Where(m => m.Success))
                    if (Parse(m) is { } d) found.Add(d);
            return found;
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    /// <summary>The date when every one found agrees, the most precise kept, with where it came from; else MISSING (also when either reader gave up).</summary>
    public static (PartialDate? On, PublicationSource Source) Agreed(IReadOnlyList<PartialDate>? metadata, IReadOnlyList<PartialDate>? text)
    {
        if (metadata is null || text is null) return (null, PublicationSource.Missing);
        var all = metadata.Concat(text).ToList();
        if (all.Count == 0 || all.Exists(a => all.Exists(b => !a.AgreesWith(b)))) return (null, PublicationSource.Missing);
        var kept = all.MaxBy(d => d.Precision);
        return (kept, metadata.Contains(kept) ? PublicationSource.Metadata : PublicationSource.Text);
    }

    private static string Cut(string s, int max) => s.Length > max ? s[..max] : s;

    // A year after 民國, or of three digits, is a ROC year (115年); four digits are Western. A bare two-digit year (26年) is not read.
    private static PartialDate? Parse(Match m)
    {
        var (y, mo, d) = (m.Groups["y"].Value, m.Groups["m"].Value, m.Groups["d"].Value);
        return m.Groups["roc"].Success || int.Parse(y, CultureInfo.InvariantCulture) < FirstGregorianYear
            ? PartialDate.Parse($"{RocEra}{y}年{mo}月{d}日") : PartialDate.Parse($"{y}/{mo}/{d}");
    }

    private const string Date = @"(?:(?<roc>民國)\s?(?<y>[0-9]{2,3})|(?<y>[0-9]{3,4}))\s?年\s?(?<m>[0-9]{1,2})\s?月\s?(?<d>[0-9]{1,2})\s?日"
        + @"|(?<y>[0-9]{4})[/.\-／－](?<m>[0-9]{1,2})[/.\-／－](?<d>[0-9]{1,2})";

    [GeneratedRegex(@"""datePublished""\s*:\s*""(?<d>[^""]{1,100})""", RegexOptions.None, MatchTimeoutMs)]
    private static partial Regex JsonLdPublished();

    // [0-9], not \d: \d also takes digits of other scripts, which int.Parse refuses.
    [GeneratedRegex(@"^(?:(?:發布|發佈|刊登|出刊)?(?:日期|時間)\s?[:：]\s?)?(?:" + Date + @")(?:\s*[0-9]{1,2}:[0-9]{2}(?::[0-9]{2})?)?$", RegexOptions.None, MatchTimeoutMs)]
    private static partial Regex DateLine();

    // Bounded on both sides of the date, so a long line without a closing bracket cannot backtrack quadratically.
    [GeneratedRegex(@"^[〔【\[][^〕】\]]{0,60}?(?:" + Date + @")[^〕】\]]{0,60}[〕】\]]", RegexOptions.None, MatchTimeoutMs)]
    private static partial Regex Dateline();
}
