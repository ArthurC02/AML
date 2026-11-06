using System.IO.Compression;
using System.Xml.Linq;
using Aml.Core.Codes;
using Aml.Infra.Excel;

namespace Aml.Tests.GoldenCases;

/// <summary>The case workbook of every Golden Case: seven plain tables that restate the revision, and in the default view no identifier values.</summary>
public class WorkbookTests
{
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    private static byte[] Bytes(string gc, bool full)
    {
        var (c, rev) = CaseViewTests.Stored(gc);
        using var output = new MemoryStream();
        CaseWorkbook.Write(output, c, rev, full);
        return output.ToArray();
    }

    /// <summary>Each sheet by name, in workbook order, as rows of cell text (null where no cell); every part must be well-formed XML.</summary>
    internal static List<(string Name, List<string?[]> Rows)> Read(byte[] xlsx)
    {
        using var zip = new ZipArchive(new MemoryStream(xlsx), ZipArchiveMode.Read);
        XDocument Part(string name) { using var s = zip.GetEntry(name)!.Open(); return XDocument.Load(s); }
        foreach (var entry in zip.Entries) Part(entry.FullName);

        var rels = Part("xl/_rels/workbook.xml.rels").Root!.Elements().ToDictionary(e => (string)e.Attribute("Id")!, e => (string)e.Attribute("Target")!);
        return [.. Part("xl/workbook.xml").Root!.Element(Main + "sheets")!.Elements(Main + "sheet").Select(s =>
        {
            var rows = Part("xl/" + rels[(string)s.Attribute(R + "id")!]).Root!.Element(Main + "sheetData")!.Elements(Main + "row").Select(row =>
            {
                var cells = row.Elements(Main + "c").ToDictionary(cell => Column((string)cell.Attribute("r")!), cell => cell.Value);
                return Enumerable.Range(0, cells.Count == 0 ? 0 : cells.Keys.Max() + 1).Select(i => cells.GetValueOrDefault(i)).ToArray();
            }).ToList();
            return ((string)s.Attribute("name")!, rows);
        })];

        static int Column(string reference) => reference.TakeWhile(char.IsLetter).Aggregate(0, (n, ch) => n * 26 + ch - 'A' + 1) - 1;
    }

    [Theory]
    [MemberData(nameof(SchemaTests.Cases), MemberType = typeof(SchemaTests))]
    public void The_workbook_has_seven_tables_joined_by_the_hit_number(string gc)
    {
        var (_, rev) = CaseViewTests.Stored(gc);
        var hits = rev.Explanation!.Hits;
        var sheets = Read(Bytes(gc, full: true)).ToDictionary(s => s.Name, s => s.Rows);

        Assert.Equal(["說明", "命中", "比對", "證據", "來源", "變更", "代碼"], Read(Bytes(gc, full: false)).Select(s => s.Name));
        Assert.Equal(["類別", "項目", "值"], sheets["說明"][0]);
        Assert.Equal(hits.Count, sheets["命中"].Count - 1);
        Assert.Equal(hits.Sum(h => h.Comparison.Count), sheets["比對"].Count - 1);
        Assert.Equal(hits.Sum(h => h.Support.Count + h.Conflicts.Count), sheets["證據"].Count - 1);
        Assert.Equal(hits.Sum(h => h.Sources.Count), sheets["來源"].Count - 1);
        foreach (var name in new[] { "命中", "比對", "證據", "來源" })
            Assert.All(sheets[name].Skip(1), row => Assert.Contains(hits, h => h.HitNumber.ToString(System.Globalization.CultureInfo.InvariantCulture) == row[0]));
        // Every header of the data sheets is explained in the data dictionary, and every code listed is catalogued.
        var dictionary = sheets["說明"].Where(row => row[0] == "欄位").Select(row => row[1]).ToHashSet();
        foreach (var (name, rows) in sheets.Where(s => s.Key != "說明"))
            Assert.All(rows[0], header => Assert.Contains($"{name}．{header}", dictionary));
        Assert.All(sheets["代碼"].Skip(1), row => Assert.NotNull(CodeCatalog.Find(row[0]!)));
        Assert.True(sheets["代碼"].Count > 1);
    }

    [Theory]
    [MemberData(nameof(SchemaTests.Cases), MemberType = typeof(SchemaTests))]
    public void The_default_view_holds_no_identifier_values_and_the_same_input_gives_the_same_bytes(string gc)
    {
        var (_, rev) = CaseViewTests.Stored(gc);
        var summary = Bytes(gc, full: false);
        // Identifiers, and every personal value but the Profile's main name (in which a value proves nothing).
        var names = rev.Explanation!.Hits.Select(h => h.ProfileName).OfType<string>().ToList();
        var identifiers = CaseViewTests.Identifiers(rev).Concat(CaseViewTests.Values(rev, Core.Shared.Sensitivity.Personal)
            .Where(v => v.Length > 1 && !names.Any(n => n.Contains(v, StringComparison.Ordinal)))).Distinct().ToList();

        // Names the column, not the value, so a failure keeps the personal data out of the test log.
        var leaks = Read(summary).SelectMany(s => s.Rows.Skip(1).SelectMany(row => row.Select((cell, i) => (s.Name, Header: s.Rows[0].ElementAtOrDefault(i), Item: row.ElementAtOrDefault(1), cell))))
            .Where(x => x.cell is { } v && identifiers.Any(id => v.Contains(id, StringComparison.Ordinal)))
            .Select(x => x.Name == "說明" ? $"說明．{x.Item}" : $"{x.Name}．{x.Header}").Distinct();
        Assert.Empty(leaks);
        Assert.Equal(summary, Bytes(gc, full: false));
        Assert.Equal(Bytes(gc, full: true), Bytes(gc, full: true));
    }

    [Fact]
    public void Text_is_never_a_formula_and_is_cut_to_what_a_cell_holds()
    {
        const string formula = "=HYPERLINK(\"http://example.invalid\",\"x\")";
        using var output = new MemoryStream();
        Xlsx.Write(output, [new Sheet("s", ["a", "b"], [[formula, "0012\u0001"]])], DateTimeOffset.UnixEpoch);
        using var zip = new ZipArchive(new MemoryStream(output.ToArray()), ZipArchiveMode.Read);
        using var sheet = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();
        var cells = XDocument.Load(sheet).Descendants(Main + "c").Skip(2).ToList();

        Assert.Empty(XDocument.Load(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open()).Descendants(Main + "f"));
        Assert.All(cells, c => Assert.Equal(("inlineStr", "3"), ((string?)c.Attribute("t"), (string?)c.Attribute("s"))));
        Assert.Equal([formula, "0012"], cells.Select(c => c.Value));

        var cut = Xlsx.CellText(new string('x', Xlsx.MaxCellText - Xlsx.CutNote.Length - 1) + "😀" + new string('y', 100));
        Assert.Equal(Xlsx.MaxCellText - 1, cut.Length);
        Assert.EndsWith("x" + Xlsx.CutNote, cut, StringComparison.Ordinal);
        Assert.Equal("ok", Xlsx.CellText("ok"));
        Assert.Throws<ArgumentException>(() => Xlsx.Write(Stream.Null, [new Sheet("a/b", [], [])], DateTimeOffset.UnixEpoch));
        Assert.Throws<ArgumentException>(() => Xlsx.Write(Stream.Null, [new Sheet(new string('s', 32), [], [])], DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void Times_are_utc_dates_and_numbers_are_numbers()
    {
        using var output = new MemoryStream();
        Xlsx.Write(output, [new Sheet("s", ["t", "n"], [[new DateTimeOffset(2026, 11, 7, 8, 0, 0, TimeSpan.FromHours(8)), 0.25]])], DateTimeOffset.UnixEpoch);
        using var zip = new ZipArchive(new MemoryStream(output.ToArray()), ZipArchiveMode.Read);
        var cells = XDocument.Load(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open()).Descendants(Main + "c").Skip(2).ToList();

        Assert.Equal([("2", new DateTime(2026, 11, 7, 0, 0, 0).ToOADate().ToString("R", System.Globalization.CultureInfo.InvariantCulture)), (null, "0.25")],
            cells.Select(c => ((string?)c.Attribute("s"), c.Value)));
        Assert.All(cells, c => Assert.Null(c.Attribute("t")));
    }
}
