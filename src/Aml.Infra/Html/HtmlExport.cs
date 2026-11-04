using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;

namespace Aml.Infra.Html;

/// <summary>
/// Turns a supplied HTML page into the .txt export layout of the same file, so one field mapping serves both formats.
/// Tables and form controls are interpreted before any text is produced: a checked box reads 是 and an unchecked one 否,
/// a select reads its selected option, an input its value; colspan and rowspan repeat the cell. A cell keeps its text and
/// its control values in document order; &lt;br&gt; and block elements break lines. Hidden elements (hidden attribute,
/// inline display:none or visibility:hidden), template, script, buttons and hidden inputs are not read. A blank field is
/// written blank, never as "-".
/// ponytail: assumes the pages use the export's own labels (no HTML sample yet); add a label map per template version
/// when the samples arrive (open question: stable HTML schemas). Hiding by stylesheet class is not seen.
/// </summary>
public static partial class HtmlExport
{
    static HtmlExport() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); // Big5 pages

    /// <summary>The export dialect of each file kind: how a two-cell label/value row is written.</summary>
    public enum Kind { CaseTable, Customer, RelatedParty, Profile, News }

    public static Kind KindOf(string stem) => stem switch
    {
        _ when stem.StartsWith("案件資訊", StringComparison.Ordinal) => Kind.CaseTable,
        _ when stem.StartsWith("名單資料_", StringComparison.Ordinal) => Kind.Profile,
        _ when stem.StartsWith("新聞資訊_", StringComparison.Ordinal) => Kind.News,
        "客戶-關聯人背景資料" => Kind.RelatedParty,
        _ => Kind.Customer,
    };

    public static string ToExportText(string html, Kind kind) => Export(new HtmlParser().ParseDocument(html), kind);

    /// <summary>The page as uploaded: AngleSharp decides the encoding (BOM, then meta charset).</summary>
    public static string ToExportText(byte[] html, Kind kind)
    {
        using var stream = new MemoryStream(html);
        return Export(new HtmlParser().ParseDocument(stream), kind);
    }

    private static string Export(IHtmlDocument doc, Kind kind)
    {
        var lines = new List<string>();
        Walk(doc.Body ?? (INode)doc, kind, lines, null);
        return string.Join('\n', lines) + '\n';
    }

    private static void Walk(INode node, Kind kind, List<string> lines, string? heading)
    {
        foreach (var child in node.ChildNodes)
        {
            switch (child)
            {
                case IElement e when Hidden(e):
                    break;
                case IHtmlTableElement table:
                    Table(table, kind, lines, table.Caption is { } caption && Value(caption) is { Length: > 0 } c ? c : heading);
                    heading = null;
                    break;
                case IElement e when e.LocalName is "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                    heading = Value(e);
                    if (kind != Kind.Profile) lines.Add(heading);
                    break;
                case IElement e when e.QuerySelector("table, h1, h2, h3, h4, h5, h6") is not null:
                    Walk(e, kind, lines, heading);
                    break;
                case IElement e when e.LocalName == "dl":
                    foreach (var dt in e.Children.Where(x => x.LocalName == "dt" && !Hidden(x)))
                        Pair(Value(dt), dt.NextElementSibling is { LocalName: "dd" } dd ? Value(dd) : "", kind, lines);
                    break;
                case IElement e:
                    lines.AddRange(Text(e).Split('\n').Select(Clean).Where(l => l.Length > 0));
                    break;
                case IText t when Clean(t.Text) is { Length: > 0 } text:
                    lines.Add(text);
                    break;
            }
        }
    }

    private static void Table(IHtmlTableElement table, Kind kind, List<string> lines, string? heading)
    {
        var grid = Grid(table);
        // A two-column table is a label/value form, except a headed Profile table (e.g. 國家資料), which is a section.
        if ((heading is null || kind != Kind.Profile) && grid.All(r => r.Count == 2))
        {
            foreach (var r in grid) Pair(r[0], r[1], kind, lines);
            return;
        }
        if (heading is not null && kind == Kind.Profile) lines.Add(heading.TrimEnd(':', '：') + ":");
        lines.AddRange(grid.Select(r => string.Join('\t', r)));
    }

    private static void Pair(string label, string value, Kind kind, List<string> lines)
    {
        label = label.TrimEnd(':', '：', ' ');
        switch (kind)
        {
            case Kind.Profile: lines.Add($"{label}:\t{value}"); break;
            case Kind.RelatedParty: lines.Add($"{label} ："); if (value.Length > 0) lines.Add(value); break;
            default: lines.Add(label); lines.Add(value); break;
        }
    }

    /// <summary>Rows of cell values with colspan repeated across and rowspan carried down.</summary>
    private static List<List<string>> Grid(IHtmlTableElement table)
    {
        var grid = new List<List<string>>();
        var carried = new Dictionary<int, (string Value, int Rows)>();
        foreach (var row in table.Rows.Where(r => !Hidden(r)))
        {
            var cells = new List<string>();
            var col = 0;
            void Carry()
            {
                while (carried.TryGetValue(col, out var c))
                {
                    cells.Add(c.Value);
                    if (c.Rows <= 1) carried.Remove(col); else carried[col] = (c.Value, c.Rows - 1);
                    col++;
                }
            }
            foreach (var cell in row.Cells)
            {
                Carry();
                var value = Value(cell);
                for (var i = 0; i < Math.Max(1, cell.ColumnSpan); i++)
                {
                    if (cell.RowSpan > 1) carried[col] = (value, cell.RowSpan - 1);
                    cells.Add(value);
                    col++;
                }
            }
            Carry();
            grid.Add(cells);
        }
        return grid;
    }

    /// <summary>The value a reader sees in a cell, form controls included, on one line.</summary>
    private static string Value(IElement cell) => Clean(Text(cell));

    /// <summary>The visible text of an element with form-control values in place; &lt;br&gt; and block boundaries are line breaks.</summary>
    private static string Text(IElement element)
    {
        var sb = new StringBuilder();
        Append(element, sb, false);
        return sb.ToString();
    }

    // choiceLabel: inside the label of a checkbox or radio, whose control already gives the value.
    private static void Append(INode node, StringBuilder sb, bool choiceLabel)
    {
        switch (node)
        {
            case IText t:
                if (!choiceLabel) sb.Append(t.Text);
                return;
            case IElement e when Hidden(e):
                return;
            case IElement { LocalName: "br" }:
                sb.Append('\n');
                return;
            case IHtmlInputElement { Type: "checkbox" } box:
                sb.Append(box.IsChecked ? " 是 " : " 否 ");
                return;
            case IHtmlInputElement { Type: "radio" } radio:
                if (radio.IsChecked) sb.Append(' ').Append(LabelOf(radio)?.TextContent.Trim() is { Length: > 0 } l ? l : radio.Value).Append(' ');
                return;
            case IHtmlInputElement input:
                sb.Append(' ').Append(input.Value).Append(' ');
                return;
            case IHtmlSelectElement select:
                sb.Append(' ').Append(string.Join(", ", select.Options.Where(o => o.IsSelected).Select(o => Clean(o.Text)))).Append(' ');
                return;
            case IHtmlTextAreaElement area:
                sb.Append(' ').Append(area.Value).Append(' ');
                return;
            case IHtmlLabelElement label when (label.Control ?? label.QuerySelector("input")) is IHtmlInputElement { Type: "checkbox" or "radio" }:
                foreach (var child in label.ChildNodes) Append(child, sb, true);
                return;
            case IElement e:
                var block = BlockElements.Contains(e.LocalName);
                if (block) sb.Append('\n');
                foreach (var child in e.ChildNodes) Append(child, sb, choiceLabel);
                if (block) sb.Append('\n');
                return;
        }
    }

    // The label of a control, by for= or by nesting (AngleSharp's Labels misses for=).
    private static IHtmlLabelElement? LabelOf(IHtmlInputElement input) =>
        input.Owner?.QuerySelectorAll("label").OfType<IHtmlLabelElement>().FirstOrDefault(l => (l.Control ?? l.QuerySelector("input")) == input);

    private static readonly HashSet<string> BlockElements =
    [
        "address", "article", "aside", "blockquote", "caption", "dd", "div", "dl", "dt", "fieldset", "figcaption", "figure",
        "footer", "form", "h1", "h2", "h3", "h4", "h5", "h6", "header", "hr", "li", "main", "nav", "ol", "p", "pre", "section",
        "table", "td", "th", "tr", "ul",
    ];

    /// <summary>Not shown to a reader: hidden attribute or inline style, non-content elements, buttons and hidden inputs.</summary>
    private static bool Hidden(IElement e) =>
        e.LocalName is "script" or "style" or "head" or "template" or "noscript" or "button"
        || e.HasAttribute("hidden")
        || e is IHtmlInputElement { Type: "hidden" or "button" or "submit" or "reset" or "image" }
        || (e.GetAttribute("style") is { } style && HiddenStyle().IsMatch(style));

    [GeneratedRegex(@"display\s*:\s*none|visibility\s*:\s*hidden", RegexOptions.IgnoreCase)]
    private static partial Regex HiddenStyle();

    private static string Clean(string s)
    {
        var sb = new StringBuilder(s.Length);
        var space = false;
        foreach (var ch in s.Trim())
        {
            if (char.IsWhiteSpace(ch)) { if (!space) sb.Append(' '); space = true; }
            else { sb.Append(ch); space = false; }
        }
        return sb.ToString();
    }
}
