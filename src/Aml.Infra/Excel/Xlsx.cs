using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace Aml.Infra.Excel;

/// <summary>One worksheet: a header row and data rows. A cell is text, a number, a time or null (no cell).</summary>
public sealed record Sheet(string Name, IReadOnlyList<string> Header, IReadOnlyList<IReadOnlyList<object?>> Rows);

/// <summary>
/// Writes an .xlsx workbook (SpreadsheetML, ECMA-376) with the standard library: one plain table per sheet with a bold,
/// frozen and filterable header row, so it reads and filters as data (no merged cells, formulas, macros or colour codes).
/// <list type="bullet">
/// <item>Text is always a text cell formatted as Text (@), never a formula: a value such as <c>=HYPERLINK(…)</c> stays
/// text (formula injection, OWASP; CWE-1236), and identifiers keep their leading zeros.</item>
/// <item>Characters XML cannot carry are dropped, and text past Excel's 32,767 characters per cell is cut with a note.
/// Literal <c>_xHHHH_</c> text is escaped, so Excel does not read it as a character.</item>
/// <item>Text cells that look like numbers are not flagged as numbers stored as text.</item>
/// <item>Times are Excel dates in UTC (yyyy-mm-dd hh:mm:ss).</item>
/// <item>The same sheets and stamp give the same bytes: fixed part order and zip entry times.</item>
/// </list>
/// ponytail: inline strings and no table parts; shared strings or Excel tables when files grow or users ask for them.
/// </summary>
public static partial class Xlsx
{
    public const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    public const int MaxCellText = 32_767;
    public const string CutNote = "…（已截斷，全文見 API）";

    private const string Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string Relationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string PackageRelationships = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const int MaxRows = 1_048_576;
    private const int HeaderStyle = 1, TimeStyle = 2, TextStyle = 3;
    private static readonly System.Buffers.SearchValues<char> SheetNameForbidden = System.Buffers.SearchValues.Create(@"[]:*?/\");

    public static void Write(Stream output, IReadOnlyList<Sheet> sheets, DateTimeOffset stamp)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in sheets)
        {
            // Excel's rules: 1 to 31 characters, none of []:*?/\, no leading or trailing apostrophe, not "History", unique ignoring case.
            if (s.Name.Length is 0 or > 31 || s.Name.AsSpan().ContainsAny(SheetNameForbidden) || s.Name.StartsWith('\'') || s.Name.EndsWith('\'')
                || s.Name.Equals("History", StringComparison.OrdinalIgnoreCase) || !names.Add(s.Name))
                throw new ArgumentException($"Excel refuses the sheet name '{s.Name}'.", nameof(sheets));
            if (s.Rows.Count + 1 > MaxRows) throw new ArgumentException($"Sheet '{s.Name}' has more rows than Excel holds.", nameof(sheets));
        }
        // Zip entry times are DOS times (1980-2107), local to no zone; the stamp keeps the bytes reproducible.
        var entryTime = new DateTimeOffset(Math.Clamp(stamp.UtcDateTime.Ticks, new DateTime(1980, 1, 2).Ticks, new DateTime(2107, 12, 31).Ticks), TimeSpan.Zero);
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        void Part(string name, Action<XmlWriter> write)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            entry.LastWriteTime = entryTime;
            using var stream = entry.Open();
            using var xml = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), NewLineHandling = NewLineHandling.None });
            write(xml);
        }

        Part("[Content_Types].xml", x => ContentTypes(x, sheets.Count));
        Part("_rels/.rels", x => Rels(x, PackageRelationships, [("rId1", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument", "xl/workbook.xml")]));
        Part("xl/workbook.xml", x => Workbook(x, sheets));
        Part("xl/_rels/workbook.xml.rels", x => Rels(x, PackageRelationships,
            [.. sheets.Select((_, i) => ($"rId{i + 1}", $"{Relationships}/worksheet", $"worksheets/sheet{i + 1}.xml")),
             ($"rId{sheets.Count + 1}", $"{Relationships}/styles", "styles.xml")]));
        Part("xl/styles.xml", Styles);
        for (var i = 0; i < sheets.Count; i++)
        {
            var sheet = sheets[i];
            var first = i == 0;
            Part($"xl/worksheets/sheet{i + 1}.xml", x => Worksheet(x, sheet, first));
        }
    }

    private static void ContentTypes(XmlWriter x, int sheetCount)
    {
        const string ns = "http://schemas.openxmlformats.org/package/2006/content-types";
        const string ml = "application/vnd.openxmlformats-officedocument.spreadsheetml.";
        x.WriteStartElement("Types", ns);
        Default("rels", "application/vnd.openxmlformats-package.relationships+xml");
        Default("xml", "application/xml");
        Override("/xl/workbook.xml", ml + "sheet.main+xml");
        Override("/xl/styles.xml", ml + "styles+xml");
        for (var i = 1; i <= sheetCount; i++) Override($"/xl/worksheets/sheet{i}.xml", ml + "worksheet+xml");
        x.WriteEndElement();

        void Default(string extension, string type)
        {
            x.WriteStartElement("Default", ns);
            x.WriteAttributeString("Extension", extension);
            x.WriteAttributeString("ContentType", type);
            x.WriteEndElement();
        }

        void Override(string part, string type)
        {
            x.WriteStartElement("Override", ns);
            x.WriteAttributeString("PartName", part);
            x.WriteAttributeString("ContentType", type);
            x.WriteEndElement();
        }
    }

    private static void Rels(XmlWriter x, string ns, IEnumerable<(string Id, string Type, string Target)> rels)
    {
        x.WriteStartElement("Relationships", ns);
        foreach (var (id, type, target) in rels)
        {
            x.WriteStartElement("Relationship", ns);
            x.WriteAttributeString("Id", id);
            x.WriteAttributeString("Type", type);
            x.WriteAttributeString("Target", target);
            x.WriteEndElement();
        }
        x.WriteEndElement();
    }

    private static void Workbook(XmlWriter x, IReadOnlyList<Sheet> sheets)
    {
        x.WriteStartElement("workbook", Main);
        x.WriteAttributeString("xmlns", "r", null, Relationships);
        x.WriteStartElement("sheets", Main);
        for (var i = 0; i < sheets.Count; i++)
        {
            x.WriteStartElement("sheet", Main);
            x.WriteAttributeString("name", sheets[i].Name);
            x.WriteAttributeString("sheetId", (i + 1).ToString(CultureInfo.InvariantCulture));
            x.WriteAttributeString("id", Relationships, $"rId{i + 1}");
            x.WriteEndElement();
        }
        x.WriteEndElement();
        // The range Excel itself records for a sheet's filter.
        x.WriteStartElement("definedNames", Main);
        for (var i = 0; i < sheets.Count; i++)
        {
            x.WriteStartElement("definedName", Main);
            x.WriteAttributeString("name", "_xlnm._FilterDatabase");
            x.WriteAttributeString("localSheetId", i.ToString(CultureInfo.InvariantCulture));
            x.WriteAttributeString("hidden", "1");
            x.WriteString($"'{sheets[i].Name.Replace("'", "''", StringComparison.Ordinal)}'!{Absolute(Range(sheets[i]))}");
            x.WriteEndElement();
        }
        x.WriteEndElement();
        x.WriteEndElement();
    }

    // Style indexes: 0 default, 1 header (bold text), 2 time, 3 text (@).
    private static void Styles(XmlWriter x)
    {
        x.WriteStartElement("styleSheet", Main);
        Element("numFmts", ("count", "1"), () => Empty("numFmt", ("numFmtId", "164"), ("formatCode", "yyyy-mm-dd hh:mm:ss")));
        Element("fonts", ("count", "2"), () =>
        {
            Element("font", null, () => { Empty("sz", ("val", "11")); Empty("name", ("val", "Microsoft JhengHei")); });
            Element("font", null, () => { Empty("b"); Empty("sz", ("val", "11")); Empty("name", ("val", "Microsoft JhengHei")); });
        });
        Element("fills", ("count", "2"), () =>
        {
            Element("fill", null, () => Empty("patternFill", ("patternType", "none")));
            Element("fill", null, () => Empty("patternFill", ("patternType", "gray125")));
        });
        Element("borders", ("count", "1"), () => Element("border", null, () => { Empty("left"); Empty("right"); Empty("top"); Empty("bottom"); Empty("diagonal"); }));
        Element("cellStyleXfs", ("count", "1"), () => Empty("xf", ("numFmtId", "0"), ("fontId", "0"), ("fillId", "0"), ("borderId", "0")));
        Element("cellXfs", ("count", "4"), () =>
        {
            Empty("xf", ("numFmtId", "0"), ("fontId", "0"), ("fillId", "0"), ("borderId", "0"), ("xfId", "0"));
            Empty("xf", ("numFmtId", "49"), ("fontId", "1"), ("fillId", "0"), ("borderId", "0"), ("xfId", "0"), ("applyFont", "1"), ("applyNumberFormat", "1"));
            Empty("xf", ("numFmtId", "164"), ("fontId", "0"), ("fillId", "0"), ("borderId", "0"), ("xfId", "0"), ("applyNumberFormat", "1"));
            Empty("xf", ("numFmtId", "49"), ("fontId", "0"), ("fillId", "0"), ("borderId", "0"), ("xfId", "0"), ("applyNumberFormat", "1"));
        });
        Element("cellStyles", ("count", "1"), () => Empty("cellStyle", ("name", "Normal"), ("xfId", "0"), ("builtinId", "0")));
        x.WriteEndElement();

        void Element(string name, (string, string)? attribute, Action content)
        {
            x.WriteStartElement(name, Main);
            if (attribute is var (k, v)) x.WriteAttributeString(k, v);
            content();
            x.WriteEndElement();
        }

        void Empty(string name, params (string Name, string Value)[] attributes)
        {
            x.WriteStartElement(name, Main);
            foreach (var (k, v) in attributes) x.WriteAttributeString(k, v);
            x.WriteEndElement();
        }
    }

    private static void Worksheet(XmlWriter x, Sheet sheet, bool selected)
    {
        x.WriteStartElement("worksheet", Main);
        x.WriteStartElement("dimension", Main);
        x.WriteAttributeString("ref", Range(sheet));
        x.WriteEndElement();

        x.WriteStartElement("sheetViews", Main);
        x.WriteStartElement("sheetView", Main);
        if (selected) x.WriteAttributeString("tabSelected", "1");
        x.WriteAttributeString("workbookViewId", "0");
        x.WriteStartElement("pane", Main);
        x.WriteAttributeString("ySplit", "1");
        x.WriteAttributeString("topLeftCell", "A2");
        x.WriteAttributeString("activePane", "bottomLeft");
        x.WriteAttributeString("state", "frozen");
        x.WriteEndElement();
        x.WriteStartElement("selection", Main);
        x.WriteAttributeString("pane", "bottomLeft");
        x.WriteEndElement();
        x.WriteEndElement();
        x.WriteEndElement();

        x.WriteStartElement("cols", Main);
        for (var c = 0; c < sheet.Header.Count; c++)
        {
            x.WriteStartElement("col", Main);
            var n = (c + 1).ToString(CultureInfo.InvariantCulture);
            x.WriteAttributeString("min", n);
            x.WriteAttributeString("max", n);
            x.WriteAttributeString("width", Width(sheet, c).ToString(CultureInfo.InvariantCulture));
            x.WriteAttributeString("customWidth", "1");
            x.WriteEndElement();
        }
        x.WriteEndElement();

        x.WriteStartElement("sheetData", Main);
        Row(x, 1, [.. sheet.Header], HeaderStyle);
        for (var r = 0; r < sheet.Rows.Count; r++) Row(x, r + 2, sheet.Rows[r], null);
        x.WriteEndElement();

        x.WriteStartElement("autoFilter", Main);
        x.WriteAttributeString("ref", Range(sheet));
        x.WriteEndElement();

        // Ids such as 90000031 are text on purpose; without this Excel flags each one as a number stored as text.
        x.WriteStartElement("ignoredErrors", Main);
        x.WriteStartElement("ignoredError", Main);
        x.WriteAttributeString("sqref", Range(sheet));
        x.WriteAttributeString("numberStoredAsText", "1");
        x.WriteEndElement();
        x.WriteEndElement();
        x.WriteEndElement();
    }

    private static void Row(XmlWriter x, int row, IReadOnlyList<object?> cells, int? style)
    {
        x.WriteStartElement("row", Main);
        x.WriteAttributeString("r", row.ToString(CultureInfo.InvariantCulture));
        for (var c = 0; c < cells.Count; c++)
        {
            if (cells[c] is not { } value) continue;
            x.WriteStartElement("c", Main);
            x.WriteAttributeString("r", Column(c) + row.ToString(CultureInfo.InvariantCulture));
            switch (value)
            {
                case DateTimeOffset t:
                    x.WriteAttributeString("s", TimeStyle.ToString(CultureInfo.InvariantCulture));
                    x.WriteElementString("v", Main, t.UtcDateTime.ToOADate().ToString("R", CultureInfo.InvariantCulture));
                    break;
                case int or long or double:
                    x.WriteElementString("v", Main, Convert.ToDouble(value, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture));
                    break;
                default:
                    x.WriteAttributeString("s", (style ?? TextStyle).ToString(CultureInfo.InvariantCulture));
                    x.WriteAttributeString("t", "inlineStr");
                    x.WriteStartElement("is", Main);
                    x.WriteStartElement("t", Main);
                    x.WriteAttributeString("xml", "space", null, "preserve");
                    x.WriteString(Escaped(CellText(value.ToString() ?? "")));
                    x.WriteEndElement();
                    x.WriteEndElement();
                    break;
            }
            x.WriteEndElement();
        }
        x.WriteEndElement();
    }

    /// <summary>The text as a cell holds it: without characters XML cannot carry, and within Excel's length per cell.</summary>
    public static string CellText(string text)
    {
        var kept = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (char.IsHighSurrogate(ch) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) kept.Append(ch).Append(text[++i]);
            else if (XmlConvert.IsXmlChar(ch)) kept.Append(ch);
        }
        if (kept.Length <= MaxCellText) return kept.ToString();
        var cut = MaxCellText - CutNote.Length;
        if (char.IsHighSurrogate(kept[cut - 1])) cut--; // never split a pair
        return kept.ToString(0, cut) + CutNote;
    }

    /// <summary>
    /// Excel reads <c>_xHHHH_</c> in cell text as an escaped character (ST_Xstring), so text that holds one literally
    /// escapes its underscore as <c>_x005F_</c>; the cell then shows the text as given.
    /// </summary>
    public static string Escaped(string text) => EscapeLike().Replace(text, "_x005F_$1");

    [System.Text.RegularExpressions.GeneratedRegex("_(x[0-9A-Fa-f]{4}_)")]
    private static partial System.Text.RegularExpressions.Regex EscapeLike();

    // Approximate width in characters: a CJK or other wide character counts two.
    private static double Width(Sheet sheet, int column)
    {
        static int Display(object? v) => v switch
        {
            null => 0,
            DateTimeOffset => 19,
            _ => (v.ToString() ?? "").Sum(ch => ch >= 0x1100 ? 2 : 1),
        };
        var widest = sheet.Rows.Select(r => column < r.Count ? Display(r[column]) : 0).Append(Display(sheet.Header[column])).Max();
        return Math.Clamp(widest + 2, 8, 60);
    }

    private static string Range(Sheet sheet) => $"A1:{Column(Math.Max(sheet.Header.Count, 1) - 1)}{sheet.Rows.Count + 1}";

    private static string Absolute(string range) => string.Join(':', range.Split(':').Select(r => "$" + string.Concat(r.TakeWhile(char.IsLetter)) + "$" + string.Concat(r.SkipWhile(char.IsLetter))));

    private static string Column(int index)
    {
        var name = "";
        for (var n = index + 1; n > 0; n = (n - 1) / 26) name = (char)('A' + ((n - 1) % 26)) + name;
        return name;
    }
}
