using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using Aml.Core.Normalization;
using Aml.Core.Pages;
using Aml.Core.Shared;

namespace Aml.Infra.Web;

/// <summary>
/// The publication date a page states (external-sources.md#common-path), where pages reliably state it: structured metadata
/// (JSON-LD datePublished, meta article:published_time / og:published_time / itemprop datePublished, a &lt;time datetime&gt;
/// outside a sentence), a publication label with its date in a short element of the page, read from the DOM before
/// boilerplate is removed (發布日期：…; not in a list, sidebar, boilerplate-named container or another article's card), and
/// a date line, a dateline or a labelled line among the first lines of the main text. Every date found must agree (at its precision), else
/// the date is MISSING; none found is MISSING too, and so is a value present but unparseable. Only when the page states no
/// publication date at all does its update date stand in (更新日期：…, article:modified_time; DC.Date is not read), read the same way and
/// marked as an update date. Dates are read by <see cref="PartialDate.Parse"/>, as written (an ISO time's own local date).
/// Lines and values are cut short and every match is time-bounded (untrusted input).
/// </summary>
public static partial class PublishedDates
{
    private const int FirstLines = 3;
    // A label may follow a letterhead of up to four lines (a PDF's office name, its English name split in two).
    private const int FirstLabelLines = 5;
    private const int FirstGregorianYear = 1000;
    private const string RocEra = "民國";

    // The page is untrusted: a real date line, dateline or metadata value is short, so each is cut to this length before
    // any match, and every match has a timeout whose expiry makes the date MISSING, never a failed page.
    private const int MaxLine = 200;
    private const int MatchTimeoutMs = 200;
    // ponytail: a JSON-LD block is searched in its first 64K characters only; a datePublished past that is not read.
    private const int MaxJsonLd = 65536;
    // A label's element is its text node's parent, or that one's parent or grandparent (the label in its own cell); at most
    // this many labels are looked at.
    private const int LabelLevels = 3;
    private const int MaxLabels = 20;

    private static readonly HashSet<string> MetaNames = new(
        ["article:published_time", "og:published_time", "og:article:published_time", "datePublished"], StringComparer.OrdinalIgnoreCase);

    // Dublin Core's DC.Date is not read: on the government sites measured it is the update date on one template and, on
    // another, a date years before the publication date.
    private static readonly HashSet<string> UpdateMetaNames = new(
        ["article:modified_time", "og:updated_time", "dateModified"], StringComparer.OrdinalIgnoreCase);

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
            values.AddRange(MetaValues(doc, MetaNames));
            foreach (var e in doc.QuerySelectorAll("[itemprop='datePublished']:not(meta)"))
                values.Add(e.GetAttribute("datetime") ?? e.GetAttribute("content") ?? e.TextContent);
            // A <time> written inside a sentence dates what the sentence tells, one in a list, sidebar, menu or footer
            // another article's: neither is the page's.
            foreach (var t in doc.QuerySelectorAll("time[datetime]:not([itemprop])"))
                if (t.Closest("p,li,aside,nav,footer") is null) values.Add(t.GetAttribute("datetime"));
            return Parsed(values);
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    /// <summary>The update dates the document's metadata states (article:modified_time, og:updated_time, dateModified); null as for <see cref="Metadata"/>.</summary>
    public static List<PartialDate>? Updated(IHtmlDocument doc) => Parsed(MetaValues(doc, UpdateMetaNames));

    private static IEnumerable<string?> MetaValues(IHtmlDocument doc, HashSet<string> names) =>
        doc.QuerySelectorAll("meta[content]")
            .Where(m => names.Contains(m.GetAttribute("property") ?? m.GetAttribute("name") ?? m.GetAttribute("itemprop") ?? ""))
            .Select(m => m.GetAttribute("content"));

    private static List<PartialDate>? Parsed(IEnumerable<string?> values)
    {
        var dates = values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => PartialDate.Parse(Cut(v!.Trim(), MaxLine))).ToList();
        return dates.Contains(null) ? null : [.. dates.OfType<PartialDate>()];
    }

    /// <summary>
    /// Full dates on a line of their own (2026-11-20, 2026/11/20 10:30, 2026年11月20日, 民國115年11月20日, 115年11月20日,
    /// 發布日期：…) or inside a dateline's bracket (〔2026-11-20 記者／臺北報導〕) among the first three lines of the text, and a
    /// publication label ending one of the first five lines with its date (…Press Release發稿日期：115年9月29日,
    /// 發布日期：115-11-20, 新聞稿（115.9.10）); with <paramref name="update"/>, only an update label ending one of the first
    /// five lines (更新日期：115-11-20). A date in a sentence is the sentence's, not the page's, and is not read. A two-digit
    /// year is read only after 民國. Null (MISSING) when a match timed out, when a date matched is not a valid day (115-02-30,
    /// 115-00-10), or when one line holds both a publication and an update label (which date is which is not guessed).
    /// </summary>
    public static List<PartialDate>? FromText(string text, bool update = false)
    {
        try
        {
            var found = new List<PartialDate>();
            var lines = text.Split('\n', FirstLabelLines + 1).Take(FirstLabelLines).Select(l => l.Trim()).ToList();
            for (var i = 0; i < lines.Count; i++)
            {
                var line = CjkText.HalfWidth(Cut(lines[i], MaxLine));
                if (PublishedLabel().IsMatch(line) && UpdateLabel().IsMatch(line)) return null;
                // A label line is short: one cut from a longer paragraph is not one, as it could end on a date of the sentence.
                var labelled = lines[i].Length > MaxLine ? Match.Empty : update ? UpdateLine().Match(line) : LabelledLine().Match(line);
                var bare = i < FirstLines && !update ? new[] { DateLine().Match(line), Dateline().Match(line) } : [];
                foreach (var m in bare.Append(labelled).Where(m => m.Success))
                    if (Parse(m) is { } d) found.Add(d);
                    else return null;
            }
            return found;
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    /// <summary>
    /// The dates a page's template states apart from its main text, under a publication label (發布日期：, 發佈時間：,
    /// 發稿日期：, 上版日期：, 刊登日期：) or, with <paramref name="update"/>, an update label (更新日期：, 最後更新：), in one
    /// short element that holds the label and its date and nothing else (a table row 發布日期：| 2026/9/8 下午 02:15); read
    /// before boilerplate is removed. A label in a list, sidebar, menu, footer, template or noscript, or under an element whose
    /// class or id names boilerplate (related, comment, footer …), inside a link, or whose nearest block holding a heading holds
    /// only link headings (another article's card, whatever it is named), dates another article and is not read. Null (MISSING)
    /// when a match timed out, a date matched is not a valid day, or one element holds both a publication and an update label.
    /// </summary>
    public static List<PartialDate>? Labelled(IHtmlDocument doc, bool update = false)
    {
        try
        {
            var found = new List<PartialDate>();
            if (doc.Body is not { } body) return found;
            var label = update ? UpdateLabel() : PublishedLabel();
            var line = update ? UpdateLine() : LabelledLine();
            var cards = HeadingHolders(body);
            // The cheap test first, then the places that date another article; at most MaxLabels labels left are looked at.
            var labels = body.GetDescendants().OfType<IText>()
                .Where(t => label.IsMatch(Cut(t.Data, MaxLine)) && t.ParentElement is { } p
                    && p.Closest("a,li,aside,nav,footer,script,style,template,noscript") is null && !InBoilerplate(p) && !InCard(p, cards))
                .Take(MaxLabels).ToList();
            foreach (var t in labels)
                for (var (e, up) = (t.ParentElement, 0); e is not null && up < LabelLevels; (e, up) = (e.ParentElement, up + 1))
                {
                    if (ShortText(e) is not { } s) break;
                    var text = CjkText.HalfWidth(s);
                    if (PublishedLabel().IsMatch(text) && UpdateLabel().IsMatch(text)) return null;
                    var m = line.Match(text);
                    if (m.Success && m.Index == 0)
                    {
                        if (Parse(m) is not { } d) return null;
                        found.Add(d);
                        break;
                    }
                }
            return found;
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    private const int PlainHeading = 1, LinkHeading = 2;

    // Every element holding a heading, with the kinds it holds: a plain heading, or one that is a link (<h3><a href>…</a></h3>,
    // <a href><h3>…</h3></a>), another article's. Each element is marked at most once per kind, so the work is linear.
    private static Dictionary<IElement, int> HeadingHolders(IElement body)
    {
        var holders = new Dictionary<IElement, int>();
        foreach (var h in body.QuerySelectorAll("h1,h2,h3,h4,h5,h6"))
        {
            var kind = h.Closest("a[href]") is not null || h.QuerySelector("a[href]") is not null ? LinkHeading : PlainHeading;
            for (var at = h.ParentElement; at is not null; at = at.ParentElement)
            {
                var known = holders.GetValueOrDefault(at);
                if ((known & kind) != 0) break;
                holders[at] = known | kind;
            }
        }
        return holders;
    }

    // A label whose nearest block holding a heading holds only link headings is on another article's card (最新消息,
    // 延伸閱讀), whatever the card is named; a block holding the page's own heading (no link) is the page's.
    private static bool InCard(IElement e, Dictionary<IElement, int> holders)
    {
        for (var at = e; at is not null; at = at.ParentElement)
            if (holders.TryGetValue(at, out var kinds)) return kinds == LinkHeading;
        return false;
    }

    private static bool InBoilerplate(IElement e)
    {
        for (var at = e; at is not null; at = at.ParentElement)
            if (MainText.NamedBoilerplate(at)) return true;
        return false;
    }

    // An element's text when it is short, whitespace runs as one space; null past MaxLine, read no further than that.
    private static string? ShortText(IElement e)
    {
        var sb = new StringBuilder();
        foreach (var t in e.GetDescendants().OfType<IText>())
            if (sb.Append(t.Data).Length > MaxLine * 2) return null;
        var s = WhiteSpace().Replace(sb.ToString(), " ").Trim();
        return s.Length > MaxLine ? null : s;
    }

    /// <summary>
    /// The date when every publication date found agrees, the most precise kept, with where it came from. When the page
    /// states no publication date at all (both readers found none), the update dates stand in the same way, as
    /// <see cref="PublicationSource.UpdateDate"/>. Else MISSING: dates that disagree (an update date never papers over that),
    /// none, or a reader that gave up.
    /// </summary>
    public static (PartialDate? On, PublicationSource Source) Agreed(IReadOnlyList<PartialDate>? metadata, IReadOnlyList<PartialDate>? text,
        IReadOnlyList<PartialDate>? updated = null)
    {
        if (metadata is null || text is null) return (null, PublicationSource.Missing);
        if (metadata.Count + text.Count == 0)
            return updated is { Count: > 0 } && Agree(updated) is { } u ? (u, PublicationSource.UpdateDate) : (null, PublicationSource.Missing);
        return Agree([.. metadata, .. text]) is { } kept ? (kept, metadata.Contains(kept) ? PublicationSource.Metadata : PublicationSource.Text)
            : (null, PublicationSource.Missing);
    }

    private static PartialDate? Agree(IReadOnlyList<PartialDate> all) =>
        all.Any(a => all.Any(b => !a.AgreesWith(b))) ? null : all.MaxBy(d => d.Precision);

    private static string Cut(string s, int max) => s.Length > max ? s[..max] : s;

    // A year after 民國, or of three digits, is a ROC year (115年); four digits are Western. A bare two-digit year (26年) is not read.
    // Every form read has a day: one that parses coarser (day or month 0) or not at all (month 13, 30 Feb) is no date.
    private static PartialDate? Parse(Match m)
    {
        var (y, mo, d) = (m.Groups["y"].Value, m.Groups["m"].Value, m.Groups["d"].Value);
        var date = m.Groups["roc"].Success || int.Parse(y, CultureInfo.InvariantCulture) < FirstGregorianYear
            ? PartialDate.Parse($"{RocEra}{y}年{mo}月{d}日") : PartialDate.Parse($"{y}/{mo}/{d}");
        return date is { Precision: DatePrecision.Day } ? date : null;
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

    // A label and its date ending the line. The label starts the line or follows a character that is not CJK (a PDF letterhead's
    // English name runs into it: …Press Release發稿日期), so 預定發布日期, 原發布日期 or 資料更新日期 are other labels, not read.
    // After a label a three-digit year may also be written with dashes, slashes or dots (115-11-20, 115.9.10), and a 12-hour
    // time (下午 02:15) may follow.
    private const string NotAfterCjk = @"(?<!\p{IsCJKUnifiedIdeographs})";
    private const string PublishedWord = @"(?:發布|發佈|發稿|上版|刊登|出刊)(?:日期|時間)";
    private const string UpdateWord = @"(?:最後更新|更新)(?:日期|時間)?";
    private const string LabelledDate = @"(?:" + Date + @"|(?<y>[0-9]{3})[/.\-](?<m>[0-9]{1,2})[/.\-](?<d>[0-9]{1,2}))"
        + @"(?:\s?(?:上午|下午)?\s?[0-9]{1,2}:[0-9]{2}(?::[0-9]{2})?)?";

    [GeneratedRegex(NotAfterCjk + @"(?:" + PublishedWord + @"\s?[:：]\s?(?:" + LabelledDate + @")|新\s?聞\s?稿\s?[（(](?:" + LabelledDate + @")[）)])$",
        RegexOptions.None, MatchTimeoutMs)]
    private static partial Regex LabelledLine();

    [GeneratedRegex(NotAfterCjk + UpdateWord + @"\s?[:：]\s?(?:" + LabelledDate + @")$", RegexOptions.None, MatchTimeoutMs)]
    private static partial Regex UpdateLine();

    [GeneratedRegex(PublishedWord, RegexOptions.None, MatchTimeoutMs)]
    private static partial Regex PublishedLabel();

    [GeneratedRegex(NotAfterCjk + UpdateWord + @"\s?[:：]", RegexOptions.None, MatchTimeoutMs)]
    private static partial Regex UpdateLabel();

    [GeneratedRegex(@"\s+", RegexOptions.None, MatchTimeoutMs)]
    private static partial Regex WhiteSpace();
}
