using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;

namespace Aml.Infra.Html;

/// <summary>
/// Turns a supplied HTML page into the .txt export layout of the same file, so one field mapping serves both formats.
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

    /// <summary>A page already decoded (CaseExport.Decode): its meta charset is not followed.</summary>
    public static string ToExportText(string html, Kind kind) =>
        Export(Parse((parser, ct) => parser.ParseDocumentAsync(html, ct)), kind);

    /// <summary>
    /// Longest a page may take to parse. The HTML parser checks element scope against every open element, so markup
    /// nested deeply, by any of the many ways HTML nests, parses in time quadratic in its depth (40,000 levels took 6 s, a
    /// 5 MB page could take hours). The parse is cancelled, not the markup guessed at. A 5 MB table page parses in about
    /// 1.3 s on one core; the budget leaves room for a slower machine, so a page accepted once is not refused when read again.
    /// </summary>
    public static readonly TimeSpan ParseBudget = TimeSpan.FromSeconds(5);

    // At most one parse per core: parses run side by side (a list import reads its Profiles in parallel, uploads arrive
    // together) would otherwise share the cores and each overrun the budget. Waiting for a turn is not counted.
    private static readonly SemaphoreSlim Parsers = new(Environment.ProcessorCount, Environment.ProcessorCount);

    private static IHtmlDocument Parse(Func<HtmlParser, CancellationToken, Task<IHtmlDocument>> parse)
    {
        Parsers.Wait();
        try
        {
            using var budget = new CancellationTokenSource(ParseBudget);
            // The parse runs on this thread and checks the token between tokens; nothing here waits on I/O.
            return parse(new HtmlParser(), budget.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException e) when (e.CancellationToken.IsCancellationRequested)
        {
            throw new FormatException($"The page did not parse within {ParseBudget.TotalSeconds:0} s.");
        }
        finally
        {
            Parsers.Release();
        }
    }

    /// <summary>Deepest element nesting read. A real export page nests a few dozen levels; reading recurses per level, and a
    /// stack overflow cannot be caught, so a deeper page is refused before it is read.</summary>
    public const int MaxDepth = 256;

    /// <summary>Most cells, and characters in them, a page's tables may spread into once row and column spans are repeated.</summary>
    public const int MaxCells = 200_000, MaxCellCharacters = 20_000_000;

    private static string Export(IHtmlDocument doc, Kind kind)
    {
        var root = doc.Body ?? (INode)doc;
        CheckDepth(root);
        return new Reader(doc, kind).Read(root);
    }

    private static void CheckDepth(INode root)
    {
        var pending = new Stack<(INode Node, int Depth)>();
        pending.Push((root, 0));
        while (pending.TryPop(out var at))
        {
            if (at.Depth > MaxDepth) throw new FormatException($"The page nests elements deeper than {MaxDepth} levels.");
            foreach (var child in at.Node.ChildNodes) if (child is IElement) pending.Push((child, at.Depth + 1));
        }
    }

    /// <summary>
    /// Reads one parsed page. Everything that would look across the page from each element (does a table or heading lie
    /// below it, which label names this control) is worked out once, up front: looked up per element, a page of tens of
    /// thousands of labels or nested blocks took minutes.
    /// </summary>
    private sealed class Reader
    {
        private readonly Kind _kind;
        private readonly List<string> _lines = [];
        private readonly HashSet<IElement> _holdsTableOrHeading = [];
        private readonly Dictionary<IHtmlLabelElement, IElement?> _controlOf = [];
        private readonly Dictionary<IElement, IHtmlLabelElement> _labelOf = [];
        private int _cells;
        private long _characters;

        public Reader(IHtmlDocument doc, Kind kind)
        {
            _kind = kind;
            var byId = new Dictionary<string, IElement>(StringComparer.Ordinal);
            foreach (var e in doc.All) if (e.Id is { Length: > 0 } id) byId.TryAdd(id, e);
            foreach (var e in doc.All)
            {
                if (e is IHtmlTableElement || IsHeading(e))
                    for (var up = e.ParentElement; up is not null && _holdsTableOrHeading.Add(up); up = up.ParentElement) { }
                if (e is IHtmlLabelElement label)
                {
                    // for= names a labelable element by id, else the label holds its control (AngleSharp's Labels misses for=).
                    var control = (label.HtmlFor is { Length: > 0 } f && byId.GetValueOrDefault(f) is { } target && IsLabelable(target) ? target : null)
                        ?? label.QuerySelector("input");
                    _controlOf[label] = control;
                    if (control is not null) _labelOf.TryAdd(control, label); // the first label in page order, as before
                }
            }
        }

        public string Read(INode root)
        {
            Walk(root, null);
            return string.Join('\n', _lines) + '\n';
        }

        private static bool IsHeading(IElement e) => e.LocalName is "h1" or "h2" or "h3" or "h4" or "h5" or "h6";

        private static bool IsLabelable(IElement e) => e is IHtmlInputElement or IHtmlSelectElement or IHtmlTextAreaElement or IHtmlButtonElement
            || e.LocalName is "meter" or "output" or "progress";

        private void Walk(INode node, string? heading)
        {
            foreach (var child in node.ChildNodes)
            {
                switch (child)
                {
                    case IElement e when Hidden(e):
                        break;
                    case IHtmlTableElement table:
                        Table(table, table.Caption is { } caption && Value(caption) is { Length: > 0 } c ? c : heading);
                        heading = null;
                        break;
                    case IElement e when IsHeading(e):
                        heading = Value(e);
                        if (_kind != Kind.Profile) _lines.Add(heading);
                        break;
                    case IElement e when _holdsTableOrHeading.Contains(e):
                        Walk(e, heading);
                        break;
                    case IElement e when e.LocalName == "dl":
                        foreach (var dt in e.Children.Where(x => x.LocalName == "dt" && !Hidden(x)))
                            Pair(Value(dt), dt.NextElementSibling is { LocalName: "dd" } dd ? Value(dd) : "");
                        break;
                    case IElement e:
                        _lines.AddRange(VisibleText(e).Split('\n').Select(Clean).Where(l => l.Length > 0));
                        break;
                    case IText t when Clean(t.Text) is { Length: > 0 } text:
                        _lines.Add(text);
                        break;
                }
            }
        }

        private void Table(IHtmlTableElement table, string? heading)
        {
            var grid = Grid(table);
            var isProfileSection = heading is not null && _kind == Kind.Profile;
            var isLabelValueForm = !isProfileSection && grid.All(r => r.Count == 2);
            if (isLabelValueForm)
            {
                foreach (var r in grid) Pair(r[0], r[1]);
                return;
            }
            if (isProfileSection) _lines.Add(heading!.TrimEnd(':', '：') + ":");
            _lines.AddRange(grid.Select(r => string.Join('\t', r)));
        }

        private void Pair(string label, string value)
        {
            label = label.TrimEnd(':', '：', ' ');
            switch (_kind)
            {
                case Kind.Profile: _lines.Add($"{label}:\t{value}"); break;
                case Kind.RelatedParty: _lines.Add($"{label} ："); if (value.Length > 0) _lines.Add(value); break;
                default: _lines.Add(label); _lines.Add(value); break;
            }
        }

        // Spans repeat a cell's text; a few kilobytes of colspan and rowspan would otherwise spread into millions of cells.
        // Both counts run across the page's tables, so many tables under the limits cannot add up past them.
        private List<List<string>> Grid(IHtmlTableElement table)
        {
            var grid = new List<List<string>>();
            var carried = new Dictionary<int, (string Value, int Rows)>();
            foreach (var row in table.Rows.Where(r => !Hidden(r)))
            {
                var cells = new List<string>();
                var col = 0;
                void Add(string value)
                {
                    if (++_cells > MaxCells || (_characters += value.Length) > MaxCellCharacters)
                        throw new FormatException($"The page's tables spread into more than {MaxCells} cells or {MaxCellCharacters} characters.");
                    cells.Add(value);
                    col++;
                }
                void CarryRowSpansDown()
                {
                    while (carried.TryGetValue(col, out var c))
                    {
                        if (c.Rows <= 1) carried.Remove(col); else carried[col] = (c.Value, c.Rows - 1);
                        Add(c.Value);
                    }
                }
                foreach (var cell in row.Cells)
                {
                    CarryRowSpansDown();
                    var value = Value(cell);
                    for (var i = 0; i < Math.Max(1, cell.ColumnSpan); i++)
                    {
                        if (cell.RowSpan > 1) carried[col] = (value, cell.RowSpan - 1);
                        Add(value);
                    }
                }
                CarryRowSpansDown();
                grid.Add(cells);
            }
            return grid;
        }

        private string Value(IElement cell) => Clean(VisibleText(cell));

        private string VisibleText(IElement element)
        {
            var sb = new StringBuilder();
            Append(element, sb, false);
            return sb.ToString();
        }

        private void Append(INode node, StringBuilder sb, bool insideChoiceControlLabel)
        {
            switch (node)
            {
                case IText t:
                    if (!insideChoiceControlLabel) sb.Append(t.Text);
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
                    if (radio.IsChecked) sb.Append(' ').Append(_labelOf.GetValueOrDefault(radio)?.TextContent.Trim() is { Length: > 0 } l ? l : radio.Value).Append(' ');
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
                case IHtmlLabelElement label when _controlOf.GetValueOrDefault(label) is IHtmlInputElement { Type: "checkbox" or "radio" }:
                    foreach (var child in label.ChildNodes) Append(child, sb, true);
                    return;
                case IElement e:
                    var block = BlockElements.Contains(e.LocalName);
                    if (block) sb.Append('\n');
                    foreach (var child in e.ChildNodes) Append(child, sb, insideChoiceControlLabel);
                    if (block) sb.Append('\n');
                    return;
            }
        }
    }

    private static readonly HashSet<string> BlockElements =
    [
        "address", "article", "aside", "blockquote", "caption", "dd", "div", "dl", "dt", "fieldset", "figcaption", "figure",
        "footer", "form", "h1", "h2", "h3", "h4", "h5", "h6", "header", "hr", "li", "main", "nav", "ol", "p", "pre", "section",
        "table", "td", "th", "tr", "ul",
    ];

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
