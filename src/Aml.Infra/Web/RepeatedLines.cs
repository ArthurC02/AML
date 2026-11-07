using System.Text.RegularExpressions;

namespace Aml.Infra.Web;

/// <summary>
/// Drops lines that repeat across the parts of a whole: a PDF's letterhead and footer across its pages, a site's template
/// (heading, spokesperson and telephone block, fixed notices) across its documents (web-text.md). Digits are ignored when
/// lines are compared, so 第 2 頁 and a changing date still match. Linear in the text.
/// </summary>
public static partial class RepeatedLines
{
    /// <summary>Each part without the lines found in at least <paramref name="minParts"/> of the parts.</summary>
    public static List<List<string>> Strip(IReadOnlyList<string> parts, int minParts)
    {
        var lines = parts.Select(p => p.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList()).ToList();
        var inParts = lines.SelectMany(part => part.Select(Shape).Distinct(StringComparer.Ordinal))
            .GroupBy(s => s, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        return lines.Select(part => part.Where(l => inParts[Shape(l)] < minParts).ToList()).ToList();
    }

    /// <summary>A site's documents without its template: lines found in at least 3 documents and a fifth of them.</summary>
    public static List<string> WithoutSiteTemplate(IReadOnlyList<string> documents) =>
        Strip(documents, Math.Max(3, (documents.Count + 4) / 5)).Select(d => string.Join('\n', d)).ToList();

    private static string Shape(string line) => Digits().Replace(line, "#");

    [GeneratedRegex(@"\d+")]
    private static partial Regex Digits();
}
