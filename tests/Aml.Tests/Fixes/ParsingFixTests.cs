using System.Text;
using Aml.Core.Cases;
using Aml.Core.Explanation;
using Aml.Core.Normalization;
using Aml.Core.Shared;
using Aml.Core.Subjects;
using Aml.Infra.Html;
using Aml.Infra.Txt;
using Aml.Tests.GoldenCases;

namespace Aml.Tests.Fixes;

/// <summary>WP-F parsing fixes: countries, the candidate table, encodings, HTML, Profile rows, file names and dates.</summary>
public class ParsingFixTests
{
    private const string Case = "案件資訊.txt";
    private const string Customer = "客戶-背景資料.txt";
    private const string Profile11 = "名單資料_90000011.txt";
    private const string Gc11Row1 = "1\t鐘仁聲\t100\tDJ ALT\t90000011\t未符合\t符合\t確認後符合\t不是";

    static ParsingFixTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private static Encoding Big5 => Encoding.GetEncoding(950, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);

    /// <summary>Copies a Golden Case, applies text edits (each must occur once) and raw files, and reads it.</summary>
    private static CaseInput Read(string gc, (string File, string Find, string Replace)[] edits,
        IReadOnlyDictionary<string, byte[]>? raw = null, string[]? remove = null)
    {
        var dir = Directory.CreateTempSubdirectory("aml-fix-");
        try
        {
            foreach (var f in Directory.GetFiles(GoldenCaseTests.Folder(gc)))
                File.Copy(f, Path.Combine(dir.FullName, Path.GetFileName(f)));
            foreach (var name in remove ?? []) File.Delete(Path.Combine(dir.FullName, name));
            foreach (var (file, find, replace) in edits)
            {
                var path = Path.Combine(dir.FullName, file);
                var text = File.ReadAllText(path).ReplaceLineEndings("\n");
                Assert.True(text.Split(find).Length == 2, $"{gc}/{file}: '{find}' must occur exactly once");
                File.WriteAllText(path, text.Replace(find, replace, StringComparison.Ordinal));
            }
            foreach (var (name, bytes) in raw ?? new Dictionary<string, byte[]>()) File.WriteAllBytes(Path.Combine(dir.FullName, name), bytes);
            return TxtCaseReader.Read(dir.FullName);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    private static string Text(string gc, string file) => File.ReadAllText(Path.Combine(GoldenCaseTests.Folder(gc), file)).ReplaceLineEndings("\n");

    private static ComparisonRow Nationality(CaseInput input) =>
        ExplanationBuilder.Build(input, CaseAnalyzer.Analyze(input)).Hits[0].Comparison.Single(r => r.Field == "國籍");

    // P0-3

    [Theory]
    [InlineData("TW", "TW")]
    [InlineData("TWN", "TW")]
    [InlineData("Taiwan", "TW")]
    [InlineData("Taiwan, Province of China", "TW")]
    [InlineData("台灣", "TW")]
    [InlineData("臺灣", "TW")]
    [InlineData("中華民國", "TW")]
    [InlineData("中華民國（臺灣）", "TW")]
    [InlineData("ROC", "TW")]
    [InlineData("Chinese Taipei", "TW")]
    [InlineData("Viet Nam", "VN")]
    [InlineData("VNM", "VN")]
    [InlineData("united states of america", "US")]
    [InlineData("Narnia", null)]
    [InlineData("Korea", null)]
    [InlineData("no", null)]
    public void Country_maps_iso_codes_names_and_taiwan_aliases_else_null(string raw, string? code) =>
        Assert.Equal(code, Normalize.Country(raw));

    [Fact]
    public void TW_and_Taiwan_Province_of_China_do_not_conflict()
    {
        var input = Read("GC-11", [(Profile11, "1\tCitizenship\tTaiwan", "1\tCitizenship\tTaiwan, Province of China")]);
        Assert.Equal(["TW"], input.Profiles["90000011"].Citizenship);
        Assert.Equal(ComparisonStatus.Exact, Nationality(input).Status);
    }

    [Fact]
    public void Unmapped_country_is_ambiguous_and_not_compared()
    {
        var input = Read("GC-11", [(Customer, "國籍 1\nTaiwan", "國籍 1\nNarnia")]);
        Assert.Contains("AMBIGUOUS:國籍 1", input.Subject.Findings);
        Assert.Empty(input.Subject.Nationalities);
        Assert.NotEqual(ComparisonStatus.Conflict, Nationality(input).Status);

        var profile = Read("GC-11", [(Profile11, "1\tCitizenship\tTaiwan", "1\tCitizenship\tNarnia")]).Profiles["90000011"];
        Assert.Empty(profile.Citizenship);
        Assert.Contains("AMBIGUOUS:Citizenship", profile.Findings);
    }

    // P0-9

    [Fact]
    public void Short_candidate_row_is_unparseable_and_the_others_are_read()
    {
        var input = Read("GC-11", [(Case, Gc11Row1, "1\t鐘仁聲\t100\tDJ ALT\t90000011\t未符合\t符合\t確認後符合")]); // 8 cells
        Assert.Contains("UNPARSEABLE:candidate_row:8", input.Subject.Findings);
        Assert.Equal("钟仁声", Assert.Single(input.Rows).MatchedName);
    }

    [Fact]
    public void Full_width_row_number_is_read()
    {
        var input = Read("GC-11", [(Case, Gc11Row1, "１" + Gc11Row1[1..])]);
        Assert.Equal(2, input.Rows.Count);
        Assert.DoesNotContain(input.Subject.Findings, f => f.StartsWith("UNPARSEABLE", StringComparison.Ordinal));
    }

    [Fact]
    public void Missing_candidate_section_is_an_input_error()
    {
        var e = Assert.ThrowsAny<Exception>(() => Read("GC-11", [(Case, "疑似符合的名單資料\n", "")]));
        Assert.True(TxtCaseReader.IsInputError(e));
    }

    // P0-10

    [Fact]
    public void Flags_are_read_under_the_expanded_two_row_header()
    {
        var gc11 = TxtCaseReader.Read(GoldenCaseTests.Folder("GC-11"));
        Assert.All(gc11.Rows, r => Assert.Equal((ListFlags.None with { Rca = FlagValue.No }, false, "確認後符合"), (r.Flags, r.IsRca, r.LegacyReason)));

        var pep = Read("GC-11", [(Case, Gc11Row1, Gc11Row1 + "\t是\t否\t否")]).Rows[0].Flags;
        Assert.Equal(new ListFlags(FlagValue.Yes, FlagValue.No, FlagValue.No, FlagValue.No), pep);
        var blacklist = Read("GC-11", [(Case, Gc11Row1, Gc11Row1 + "\t否\t是")]).Rows[0].Flags;
        Assert.Equal(new ListFlags(FlagValue.No, FlagValue.Yes, FlagValue.Missing, FlagValue.No), blacklist);
    }

    [Fact]
    public void Row_wider_than_the_header_is_kept_with_unparseable_flags()
    {
        var input = Read("GC-11", [(Case, Gc11Row1, Gc11Row1 + "\t是\t否\t否\t否")]);
        Assert.Contains("UNPARSEABLE:candidate_row:8", input.Subject.Findings);
        Assert.Equal(new ListFlags(FlagValue.Unparseable, FlagValue.Unparseable, FlagValue.Unparseable, FlagValue.Unparseable), input.Rows[0].Flags);
    }

    [Fact]
    public void Unexpected_sub_header_makes_the_flags_unparseable()
    {
        var input = Read("GC-11", [(Case, "全不符合\t全符合\tPEP\t黑名單\t負面新聞", "全不符合\t全符合\tPEP\t負面新聞\t黑名單")]);
        Assert.Contains("UNPARSEABLE:candidate_header", input.Subject.Findings);
        Assert.All(input.Rows, r => Assert.Equal(FlagValue.Unparseable, r.Flags!.Pep));
    }

    // X1

    [Fact]
    public void Big5_file_is_read_with_an_encoding_fallback_finding()
    {
        var big5 = new Dictionary<string, byte[]> { [Customer] = Big5.GetBytes(Text("GC-11", Customer)) };
        var input = Read("GC-11", [], big5);
        Assert.Contains("鐘仁聲", input.Subject.Names);
        Assert.Contains("ENCODING_FALLBACK:客戶-背景資料.txt", input.Subject.Findings);

        var uploaded = Directory.GetFiles(GoldenCaseTests.Folder("GC-11"), "*.txt").ToDictionary(f => Path.GetFileName(f), File.ReadAllBytes);
        uploaded[Customer] = big5[Customer];
        Assert.Contains("ENCODING_FALLBACK:客戶-背景資料.txt", TxtCaseReader.ReadFiles(uploaded).Subject.Findings);
    }

    [Fact]
    public void Utf8_file_has_no_encoding_finding() =>
        Assert.DoesNotContain(TxtCaseReader.Read(GoldenCaseTests.Folder("GC-11")).Subject.Findings,
            f => f.StartsWith("ENCODING_FALLBACK", StringComparison.Ordinal));

    // X2

    private static string[] Lines(string html, HtmlExport.Kind kind = HtmlExport.Kind.Customer) =>
        HtmlExport.ToExportText(html, kind).Split('\n');

    [Fact]
    public void Html_bytes_follow_the_meta_charset()
    {
        var page = Big5.GetBytes("<html><head><meta charset=\"big5\"></head><body><table><tr><th>本地名稱</th><td>鐘仁聲</td></tr></table></body></html>");
        Assert.Equal(["本地名稱", "鐘仁聲", ""], HtmlExport.ToExportText(page, HtmlExport.Kind.Customer).Split('\n'));
    }

    [Fact]
    public void Html_br_and_blocks_break_lines()
    {
        Assert.Equal(["甲", "乙", "丙", ""], Lines("<body><div>甲<br>乙<p>丙</p></div></body>"));
        Assert.Equal(["永久地址 1", "台北市 信義區", ""], Lines("<table><tr><th>永久地址 1</th><td>台北市<br>信義區</td></tr></table>"));
    }

    [Fact]
    public void Html_hidden_content_is_not_read()
    {
        const string html = """
            <body><div hidden>secret</div><template><p>tmpl</p></template><script>x()</script>
            <table><tr><th>性別</th><td>男<span style="display: none">女</span><input type="hidden" value="F"><button>編輯</button></td></tr>
            <tr style="visibility:hidden"><th>國籍 1</th><td>Narnia</td></tr></table></body>
            """;
        Assert.Equal(["性別", "男", ""], Lines(html));
    }

    [Fact]
    public void Html_cell_keeps_text_and_control_values()
    {
        Assert.Equal(["發照地", "國家: TW", ""], Lines("<table><tr><th>發照地</th><td>國家: <input value=\"TW\"></td></tr></table>"));
        Assert.Equal(["PEP", "是", ""], Lines("<table><tr><th>PEP</th><td><label><input type=\"checkbox\" checked>PEP</label></td></tr></table>"));
        Assert.Equal(["性別", "女", ""],
            Lines("<table><tr><th>性別</th><td><input type=\"radio\" id=\"m\"><label for=\"m\">男</label><input type=\"radio\" id=\"f\" checked><label for=\"f\">女</label></td></tr></table>"));
    }

    [Fact]
    public void Html_blank_field_is_written_blank_not_dash()
    {
        Assert.Equal(["國籍 2", "", ""], Lines("<table><tr><th>國籍 2</th><td></td></tr></table>"));
        var input = Read("GC-11", [], new Dictionary<string, byte[]>
        {
            ["客戶-背景資料.html"] = Encoding.UTF8.GetBytes("<table><tr><th>本地名稱</th><td>鐘仁聲</td></tr><tr><th>國籍 1</th><td></td></tr></table>"),
        }, remove: [Customer]);
        Assert.Contains("FIELD_BLANK:國籍 1", input.Subject.Findings);
    }

    // X3

    [Fact]
    public void Short_profile_rows_are_unparseable_and_the_case_continues()
    {
        var input = Read("GC-11", [(Profile11, "2\tDate of Birth\t1972", "2\tDate of Birth\t1972\n3\tDate of Death\n1\tDate of Death")]);
        var p = input.Profiles["90000011"];
        Assert.Contains("UNPARSEABLE:Date of Death", p.Findings);
        Assert.Null(p.DateOfDeath);
        Assert.Single(CaseAnalyzer.Analyze(input).Hits);

        var shortCountry = Read("GC-11", [(Profile11, "3\tResident of\tTaiwan", "3\tResident of")]).Profiles["90000011"];
        Assert.Contains("UNPARSEABLE:國家資料", shortCountry.Findings);
    }

    // X4

    [Fact]
    public void Upper_case_html_extension_is_converted()
    {
        static string Tr(string line) => "<tr>" + string.Concat(line.Split('\t').Select(c => $"<td>{c}</td>")) + "</tr>";
        var lines = Text("GC-11", Case).Split('\n');
        var html = $"<table>{Tr(lines[0])}{Tr(lines[1])}</table><h3>疑似符合的名單資料</h3><table>{string.Concat(lines[5..].Where(l => l.Length > 0).Select(Tr))}</table>";
        var input = Read("GC-11", [], new Dictionary<string, byte[]> { ["案件資訊.HTML"] = Encoding.UTF8.GetBytes(html) }, remove: [Case]);
        Assert.Equal(2, input.Rows.Count);
        Assert.Contains("案件資訊.HTML", input.SourceHashes!.Keys);
        Assert.DoesNotContain(input.Subject.Findings, f => f.StartsWith("UNSUPPORTED_ATTACHMENT", StringComparison.Ordinal));
    }

    // X5

    [Fact]
    public void News_date_without_spaces_is_read()
    {
        var input = Read("GC-11", [("新聞資訊_90000011.txt", "2026 年 5 月 7 日", "2026年5月7日")]);
        Assert.Equal(new PartialDate(2026, 5, 7), input.NewsByProfileId["90000011"].PublishedOn);
    }

    // X6

    [Fact]
    public void Customer_value_line_that_is_a_label_means_blank()
    {
        var input = Read("GC-11", [(Customer, "證件種類\n身分證統一編號\n\n國籍 1", "證件種類\n國籍 1")]);
        Assert.Null(input.Subject.Document!.Type);
        Assert.Equal(["TW"], input.Subject.Nationalities);

        var blank = Read("GC-11", [(Customer, "國籍 1\nTaiwan\n發照地", "國籍 1\n發照地")]);
        Assert.Contains("FIELD_BLANK:國籍 1", blank.Subject.Findings);
        Assert.Empty(blank.Subject.Nationalities);
    }

    [Fact]
    public void Related_party_accepts_half_width_colons_and_multi_line_values()
    {
        var input = Read("GC-12",
        [
            ("客戶-關聯人背景資料.txt", "國籍 ：\nTaiwan", "國籍:\nTaiwan"),
            ("客戶-關聯人背景資料.txt", "32556桃園市龍潭區中興里", "32556桃園市龍潭區\n中興里"),
        ]);
        Assert.Equal(["TW"], input.Subject.Nationalities);
        Assert.Contains(input.Subject.Addresses, a => a.Raw == "32556桃園市龍潭區 中興里１鄰中興路８８巷１２號");
    }
}
