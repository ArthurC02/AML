using System.Security.Cryptography;
using Aml.Core.Pages;

namespace Aml.Infra.Web;

/// <summary>A received URL's page as text (external-sources.md#common-path): fetched politely through the guard, HTML by <see cref="MainText"/>, PDF by <see cref="PdfText"/> then unwrapped.</summary>
public static class WebPages
{
    public static async Task<PageFetch> ReadAsync(PoliteFetcher fetcher, Uri url, CancellationToken ct)
    {
        var got = await fetcher.GetAsync(url, ct).ConfigureAwait(false);
        if (got.Page is not { } page) return new(null, null, null, null, null, got.Problem, got.Transient);
        PageText read;
        try
        {
            // A PDF has no metadata read here: its publication date is the date line or dateline at the head of its text.
            if (page.MediaType == "application/pdf")
            {
                var pdf = PdfText.Unwrap(PdfText.FromPdf(page.Body));
                var (on, source) = PublishedDates.Agreed([], PublishedDates.FromText(pdf));
                read = new PageText(null, pdf, on, source);
            }
            else read = MainText.FromHtml(page.Body);
        }
        catch (FormatException)
        {
            return Refused(page, page.MediaType == "application/pdf" ? PageProblems.PdfRefused : PageProblems.NoMainText);
        }
        // ponytail: one page at a time, so a site's letterhead (RepeatedLines) is not stripped; it needs the site's other documents.
        return string.IsNullOrWhiteSpace(read.Text) ? Refused(page, PageProblems.NoMainText)
            : new(page.Url.AbsoluteUri, page.MediaType, Hash(page.Body), read.Title, read.Text, PublishedOn: read.PublishedOn, PublishedOnSource: read.PublishedOnSource);
    }

    private static PageFetch Refused(Fetched page, string problem) => new(page.Url.AbsoluteUri, page.MediaType, Hash(page.Body), null, null, problem);

    private static string Hash(byte[] body) => Convert.ToHexStringLower(SHA256.HashData(body));
}
