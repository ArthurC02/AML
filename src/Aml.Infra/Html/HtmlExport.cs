using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;

namespace Aml.Infra.Html;

/// <summary>
/// Turns a supplied HTML page into the .txt export layout of the same file, so one field mapping serves both formats.
/// Tables and form controls are interpreted before any text is produced: a checked box reads 是 and an unchecked one 否,
/// a select reads its selected option, an input its value; colspan and rowspan repeat the cell.
/// ponytail: assumes the pages use the export's own labels (no HTML sample yet); add a label map per template version
/// when the samples arrive (open question: stable HTML schemas).
/// </summary>
public static class HtmlExport
{
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

    public static string ToExportText(string html, Kind kind)
    {
        var doc = new HtmlParser().ParseDocument(html);
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
                case IHtmlTableElement table:
                    Table(table, kind, lines, table.Caption?.TextContent.Trim() is { Length: > 0 } c ? c : heading);
                    heading = null;
                    break;
                case IElement e when e.LocalName is "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                    heading = Clean(e.TextContent);
                    if (kind != Kind.Profile) lines.Add(heading);
                    break;
                case IElement e when e.LocalName is "script" or "style" or "head":
                    break;
                case IElement e when e.QuerySelector("table, h1, h2, h3, h4, h5, h6") is not null:
                    Walk(e, kind, lines, heading);
                    break;
                case IElement e when e.LocalName == "dl":
                    foreach (var dt in e.Children.Where(x => x.LocalName == "dt"))
                        Pair(Value(dt), dt.NextElementSibling is { LocalName: "dd" } dd ? Value(dd) : "", kind, lines);
                    break;
                case IElement e:
                    lines.AddRange(e.TextContent.Split('\n').Select(Clean).Where(l => l.Length > 0));
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
            default: lines.Add(label); lines.Add(value.Length > 0 ? value : "-"); break;
        }
    }

    /// <summary>Rows of cell values with colspan repeated across and rowspan carried down.</summary>
    private static List<List<string>> Grid(IHtmlTableElement table)
    {
        var grid = new List<List<string>>();
        var carried = new Dictionary<int, (string Value, int Rows)>();
        foreach (var row in table.Rows)
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

    /// <summary>The value a reader sees in a cell, form controls included.</summary>
    private static string Value(IElement cell)
    {
        var controls = cell.QuerySelectorAll("input, select, textarea").ToList();
        if (controls.Count == 0) return Clean(cell.TextContent);
        var parts = new List<string>();
        foreach (var c in controls)
            switch (c)
            {
                case IHtmlInputElement { Type: "checkbox" } box: parts.Add(box.IsChecked ? "是" : "否"); break;
                case IHtmlInputElement { Type: "radio" } radio when radio.IsChecked:
                    parts.Add(radio.Labels.FirstOrDefault()?.TextContent.Trim() is { Length: > 0 } l ? l : radio.Value); break;
                case IHtmlInputElement { Type: "radio" }: break;
                case IHtmlInputElement input: parts.Add(input.Value); break;
                case IHtmlSelectElement select:
                    parts.Add(string.Join(", ", select.Options.Where(o => o.IsSelected).Select(o => Clean(o.Text)))); break;
                case IHtmlTextAreaElement area: parts.Add(area.Value); break;
            }
        return Clean(string.Join(" ", parts.Where(p => p.Length > 0)));
    }

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
