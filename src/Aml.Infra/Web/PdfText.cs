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

    [GeneratedRegex(@"^(?:[-–—]\s*\d+\s*[-–—]|\d{1,3}|第\s*\d+\s*頁(?:\s*[，,/]?\s*共\s*\d+\s*頁)?|\d+\s*/\s*\d+|Page\s*\d+(?:\s*of\s*\d+)?)$", RegexOptions.IgnoreCase)]
    private static partial Regex PageNumber();
}
