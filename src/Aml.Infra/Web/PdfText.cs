using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace Aml.Infra.Web;

/// <summary>
/// The text of a PDF (press releases of the prosecutors' offices are published only as PDF), without the lines repeated
/// on every page (letterhead, footer) and without page numbers (web-text.md).
/// ponytail: PdfPig has no time or decompression budget; the fetcher's 10 MB cap is the only bound. Run it in a worker
/// with a timeout if PDFs from untrusted sites are read in the request path.
/// </summary>
public static partial class PdfText
{
    public const int MaxPages = 200, MaxCharacters = 2_000_000;

    /// <exception cref="FormatException">Unreadable, encrypted, too many pages or too much text.</exception>
    public static string FromPdf(byte[] pdf)
    {
        try
        {
            using var doc = PdfDocument.Open(pdf);
            if (doc.NumberOfPages > MaxPages) throw new FormatException($"The PDF has more than {MaxPages} pages.");
            var pages = new List<string>();
            var total = 0;
            foreach (var page in doc.GetPages())
            {
                var text = ContentOrderTextExtractor.GetText(page);
                if ((total += text.Length) > MaxCharacters) throw new FormatException($"The PDF holds more than {MaxCharacters} characters.");
                pages.Add(text);
            }
            return string.Join('\n', WithoutRunningLines(pages));
        }
        catch (Exception e) when (e is not (FormatException or OutOfMemoryException or OperationCanceledException))
        {
            throw new FormatException("The PDF could not be read.", e);
        }
    }

    /// <summary>
    /// The pages' lines, less any line (digits aside) found on at least half the pages, and at least two, of a document
    /// of two or more pages, and less a page number standing as a page's first or last line.
    /// </summary>
    public static IEnumerable<string> WithoutRunningLines(IReadOnlyList<string> pages) =>
        RepeatedLines.Strip(pages, pages.Count >= 2 ? Math.Max(2, (pages.Count + 1) / 2) : int.MaxValue)
            .SelectMany(page => page.Where((line, i) => !((i == 0 || i == page.Count - 1) && PageNumber().IsMatch(line))));

    /// <summary>
    /// Joins the lines a PDF wraps inside a paragraph (web-text.md#pdf-text). A line runs on into the next when it is full,
    /// at least 80% of the text's usual line width, does not end a sentence and the next line does not open a list item.
    /// Width counts a Chinese, full-width or CJK-font symbol (○) as two and others as one, since digits and Latin letters
    /// are set half-width. Text whose lines mostly end sentences already is not wrapped and is left as it is. Run it after
    /// site templates are removed, which compare whole lines.
    /// </summary>
    public static string Unwrap(string text)
    {
        var lines = text.Split('\n');
        var measured = lines.Where(l => Width(l) >= 10).ToList();
        if (measured.Count < 3 || measured.Count(l => SentenceEnd().IsMatch(l.TrimEnd())) >= measured.Count * 0.7) return text;
        var widths = measured.Select(Width).Order().ToList();
        // The usual full line is the 80th percentile, so a few overlong lines do not set it.
        var full = widths[(int)(widths.Count * 0.8)] * 0.8;
        var sb = new System.Text.StringBuilder(lines[0]);
        for (var i = 1; i < lines.Length; i++)
        {
            string prev = lines[i - 1].TrimEnd(), next = lines[i].TrimStart();
            if (Width(prev) < full || next.Length == 0 || SentenceEnd().IsMatch(prev) || ListItem().IsMatch(next))
            {
                sb.Append('\n').Append(lines[i]);
                continue;
            }
            sb.Length -= lines[i - 1].Length - prev.Length;
            // Latin words keep a space between them; Chinese, and a number split across lines (新臺幣1 / 00萬元), join without.
            char a = prev[^1], b = next[0];
            var space = char.IsAsciiLetterOrDigit(a) && char.IsAsciiLetterOrDigit(b) && !(char.IsAsciiDigit(a) && char.IsAsciiDigit(b));
            sb.Append(space ? " " : "").Append(next);
        }
        return sb.ToString();
    }

    // Chinese, full-width forms and the symbols a Chinese font sets full-width (○ for a masked name, ※, —, “”) count two;
    // ASCII, Latin and Greek letters and the half-width forms count one. A character outside the BMP (a rare name character) is one rune.
    private static int Width(string line) =>
        line.Trim().EnumerateRunes().Sum(r => r.Value is (>= 0x2010 and <= 0x2BFF) or (>= 0x2E80 and not (>= 0xFF61 and <= 0xFFEE)) ? 2 : 1);

    // A sentence mark, then any closing quotes or brackets: 。 ！ ？ ； ： . ! ? : … ” 」 ）
    [GeneratedRegex(@"(?:[。！？；：.!?:…][」』”’）)》]*|[」』])$")]
    private static partial Regex SentenceEnd();

    [GeneratedRegex(@"^(?:[一二三四五六七八九十]+、|[（(][一二三四五六七八九十\d]+[）)]|\d{1,2}[、.．]|[①-⑳])")]
    private static partial Regex ListItem();

    [GeneratedRegex(@"^(?:[-–—]\s*\d+\s*[-–—]|\d{1,3}|第\s*\d+\s*頁(?:\s*[，,/]?\s*共\s*\d+\s*頁)?|\d+\s*/\s*\d+|Page\s*\d+(?:\s*of\s*\d+)?)$", RegexOptions.IgnoreCase)]
    private static partial Regex PageNumber();
}
