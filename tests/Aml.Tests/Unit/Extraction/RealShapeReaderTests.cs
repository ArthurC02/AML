using Aml.Core.Cases;
using Aml.Core.Explanation;
using Aml.Core.Identity;
using Aml.Core.Shared;
using Aml.Core.Subjects;
using Aml.Infra.Txt;

namespace Aml.Tests.Unit.Extraction;

/// <summary>
/// The readers on files in the shape of a real export, with invented values only: the Profile's notes, every attached
/// news file, the court press-release and media article headers, the customer fields read beside the compared ones, and
/// the bank's own per-row decision (extraction-and-normalization.md).
/// </summary>
public class RealShapeReaderTests
{
    private const string Pid = "79000001";
    private const string Hant = "鍾語蘭";

    private static string Customer(string alias = "LAN LAN", string title = "", string birthPlace = "臺北市") => string.Join('\n',
        "基本資料", "客戶號碼", "C1234567", "客戶別名", alias, "性別", "女", "客戶名稱", "CHUNG YU LAN", "出生日期", "1975/7/15",
        "證件種類", "身分證統一編號", "國籍 1", "Taiwan", "發照地", "[TW] Taiwan", "本地名稱", Hant, "出生地點", birthPlace, "證件號碼", "A223456788",
        "國籍 2", "-", "詳細資料", "客戶類型", "個人", "職業", "學生(1)", "職稱", title, "稅號/統一編號", "12345678", "年收入", "0") + "\n";

    private const string CaseTable =
        "編號\t被偵測到的名字\t資料類別\t關聯方參考號碼\t資料來源\t是否符合名單？\n"
        + "1\t" + Hant + "\t客戶\t-\t客戶名稱\t-\n\n疑似符合的名單資料\n"
        + "編號\t姓名/名稱\t相似度分數\tSDN禁制類別\t名單資料編號\t是否符合名單？\t理由\tIs RCA?\tName Property\n"
        + "全不符合\t全符合\tPEP\t黑名單\t負面新聞\n"
        + "1\t" + Hant + "\t100\tDJ PRIMARY\t" + Pid + "\t未符合\t-\t年齡不符\t不是\t\t\t\n"
        + "2\t钟语兰\t100\tDJ ALT\t" + Pid + "\t未符合\t-\t年齡不符\t不是\t\t\t\n";

    private const string NotesLine = "Watchlist ProfileNotes: Last Reviewed Date: 14-Sep-2026 Record Status: Updated Keywords: breach of trust, forgery "
        + "People mentioned: Wang Da-wei Companies mentioned: Sample Land Co.; Sample Holdings Co. "
        + "Profile: Chung Yu-lan was one of eight individuals charged by the Sample District Prosecutors Office on September 11, 2026. "
        + "Associated Entity Details: Sample Land Co. (Chairman)";

    private static string Profile(string notes = NotesLine, string deceased = "False") => string.Join('\n',
        $"Profile_ID:\t{Pid}", "記錄類型:\tPerson", "性別:\tFemale", $"已故?:\t{deceased}", "狀態:\tActive",
        "主要名稱:", "編號\t名字\t姓", "1\tYu-lan\tChung", $"OSN\t{Hant}",
        "國家資料:", "編號\t類型\t國家", "1\tCountry of Reported Allegation\tTaiwan", "2\tResident of\tJapan", "3\tCitizenship\tTaiwan",
        "類別:", "編號\t描述1\t描述2", "1\tSpecial Interest Person (SIP)\tFinancial Crime",
        notes) + "\n";

    private const string Article = "名單資料編號：" + Pid + "\n範例日報-社會,\n前縣府官員二審改判無罪\n王小明\n522 字\n2017 年 5 月 9 日\n範例日報 (繁體)\nSMPL\n中文(繁體)\n"
        + "(c) 2017 Sample Daily. All rights reserved.\n"
        + "〔記者王小明／新北報導〕前範例縣政府局長陳淑華被控收賄，高等法院今天二審改判陳淑華無罪。\n"
        + "陳女表示感謝法官明察。\n"
        + "Sample Provider Inc.\n文件 SMPL000001\nUI 3.2.1\n";

    private const string Release = "名單資料編號：" + Pid + "\n公 告 日：106.05.09 分享至 Facebook 分享至 Line\n發布單位：臺灣高等法院\n"
        + "標 題：臺灣高等法院有關105年度上訴字第1234號被告" + Hant + "等貪污案件之新聞稿。\n檔案下載：\n1060509新聞稿.pdf\n"
        + "本院審理被告" + Hant + "等貪污案件，上訴駁回，於今(9日)宣判。\n";

    private static CaseInput Read(string? profile = null, string? customer = null, Dictionary<string, string>? news = null)
    {
        var files = new Dictionary<string, string>
        {
            ["客戶-背景資料.txt"] = customer ?? Customer(),
            ["案件資訊.txt"] = CaseTable,
            [$"名單資料_{Pid}.txt"] = profile ?? Profile(),
        };
        foreach (var (name, text) in news ?? []) files[name] = text;
        return TxtCaseReader.ReadFiles(files);
    }

    // ---- Profile notes ----

    [Fact]
    public void Notes_on_one_line_are_read_whole_and_split_by_their_labels()
    {
        var p = Read().Profiles[Pid];
        var notes = p.Notes!;
        Assert.Equal(NotesLine["Watchlist ProfileNotes: ".Length..], notes.Raw);
        Assert.Equal($"名單資料_{Pid}.txt:18", notes.Location);
        Assert.Equal(
        [
            ("Last Reviewed Date", "14-Sep-2026"), ("Record Status", "Updated"), ("Keywords", "breach of trust, forgery"), ("People mentioned", "Wang Da-wei"),
            ("Companies mentioned", "Sample Land Co.; Sample Holdings Co."),
            ("Profile", "Chung Yu-lan was one of eight individuals charged by the Sample District Prosecutors Office on September 11, 2026."),
            ("Associated Entity Details", "Sample Land Co. (Chairman)"),
        ], notes.Parts.Select(x => (x.Label!, x.Text)));
        Assert.Empty(p.Findings);
        // Not interpreted: no role, date or keyword of the Profile comes from them.
        Assert.Equal((0, 0), (p.Roles.Count, p.Keywords.Count));
    }

    [Fact]
    public void Notes_as_a_section_with_wrapped_lines_are_one_text()
    {
        // A wrapped line opening on a label (Keywords:, Profile:) still belongs to the notes, not to the Profile's keys.
        var section = "ProfileNotes:\nLast Reviewed Date: 14-Sep-2026\nKeywords: breach of trust\nProfile: Chung Yu-lan was charged\non September 11, 2026.";
        var notes = Read(Profile(section)).Profiles[Pid].Notes!;
        Assert.Equal("Last Reviewed Date: 14-Sep-2026\nKeywords: breach of trust\nProfile: Chung Yu-lan was charged\non September 11, 2026.", notes.Raw);
        Assert.Equal([("Last Reviewed Date", "14-Sep-2026"), ("Keywords", "breach of trust"), ("Profile", "Chung Yu-lan was charged on September 11, 2026.")],
            notes.Parts.Select(x => (x.Label!, x.Text)));
    }

    [Theory]
    [InlineData("Watchlist ProfileNotes: Profile: Chung Yu-lan was charged.\nhttps://example.test/a\nCharges: fraud\n名單資訊:", "Profile: Chung Yu-lan was charged.\nhttps://example.test/a\nCharges: fraud")]
    [InlineData("ProfileNotes:\nProfile: Chung Yu-lan was charged.\nCharges: fraud\nhttps://example.test/a\n來源:\n編號\t來源\t日期", "Profile: Chung Yu-lan was charged.\nCharges: fraud\nhttps://example.test/a")]
    // An unknown Chinese key line and the lines after it stay in the notes: nothing is lost.
    [InlineData("Watchlist ProfileNotes: Comments: first part\n備註：後續敘述\n仍是敘述第三行\nKeywords: fraud", "Comments: first part\n備註：後續敘述\n仍是敘述第三行\nKeywords: fraud")]
    [InlineData("Watchlist ProfileNotes: Profile: Chung Yu-lan was charged.\n最後審查日期：2026-01-01\n狀態:\tActive", "Profile: Chung Yu-lan was charged.\n最後審查日期：2026-01-01")]
    public void A_wrapped_line_that_looks_like_a_key_continues_the_notes(string notes, string raw)
    {
        Assert.Equal(raw, Read(Profile(notes)).Profiles[Pid].Notes!.Raw);
    }

    [Fact]
    public void Notes_end_at_the_next_key_of_the_profile_and_text_before_a_label_has_none()
    {
        var notes = Read(Profile("Watchlist ProfileNotes: Reviewed by the vendor. Keywords: fraud\n名單資訊:")).Profiles[Pid].Notes!;
        Assert.Equal([(null, "Reviewed by the vendor."), ("Keywords", "fraud")], notes.Parts.Select(x => (x.Label, x.Text)));
    }

    [Fact]
    public void Notes_over_the_cap_are_cut_with_a_finding()
    {
        var p = Read(Profile("Watchlist ProfileNotes: Profile: " + new string('x', TxtCaseReader.MaxNotesCharacters))).Profiles[Pid];
        Assert.Equal(TxtCaseReader.MaxNotesCharacters, p.Notes!.Raw.Length);
        Assert.Equal(["FIELD_TRUNCATED:ProfileNotes"], p.Findings);
    }

    [Fact]
    public void Notes_are_a_source_of_the_explanation_and_a_profile_without_them_has_none()
    {
        var input = Read();
        var h = Assert.Single(ExplanationBuilder.Build(input, CaseAnalyzer.Analyze(input)).Hits);
        Assert.Equal(($"DJ:{Pid}#notes", "PROFILE_RECORD"), (Assert.Single(h.Sources).Id, h.Sources[0].UsedFor));
        Assert.Null(Read(Profile(notes: "")).Profiles[Pid].Notes);
    }

    // ---- Resident of and 已故? ----

    [Theory]
    [InlineData("False", false, null)]
    [InlineData("True", true, null)]
    [InlineData("-", null, null)]
    [InlineData("Unknown", null, "AMBIGUOUS:已故?")]
    public void Residence_and_the_deceased_flag_are_read_and_decide_nothing(string deceased, bool? read, string? finding)
    {
        var p = Read(Profile(deceased: deceased)).Profiles[Pid];
        Assert.Equal(["JP"], p.ResidenceCountries!);
        Assert.Equal(["TW"], p.Citizenship); // residence is not nationality
        Assert.Equal(read, p.Deceased);
        Assert.Equal(finding is null ? [] : [finding], p.Findings);
    }

    // ---- Every attached news file ----

    [Fact]
    public void Every_news_file_of_a_profile_is_read_in_file_order()
    {
        var input = Read(news: new() { [$"新聞資訊2_{Pid}.txt"] = Release, [$"新聞資訊1_{Pid}.txt"] = Article, [$"新聞資訊10_{Pid}.txt"] = Article, [$"新聞資訊_{Pid}.txt"] = Release });
        Assert.Equal(["RELEASE", "ARTICLE", "RELEASE", "ARTICLE"], input.NewsByProfileId[Pid].Select(n => n.Publisher == "臺灣高等法院" ? "RELEASE" : "ARTICLE"));
        Assert.DoesNotContain(input.Subject.Findings, f => f.StartsWith("UNSUPPORTED_ATTACHMENT", StringComparison.Ordinal));
    }

    [Fact]
    public void A_court_press_release_header_gives_date_issuing_unit_and_title()
    {
        var n = Assert.Single(Read(news: new() { [$"新聞資訊1_{Pid}.txt"] = Release }).NewsByProfileId[Pid]);
        Assert.Equal((new PartialDate(2017, 5, 9), "臺灣高等法院", $"臺灣高等法院有關105年度上訴字第1234號被告{Hant}等貪污案件之新聞稿。", (string?)null),
            (n.PublishedOn, n.Publisher, n.Title, n.DocumentId));
        // The readers see the title and the body, not the header labels or the file name.
        Assert.Equal($"臺灣高等法院有關105年度上訴字第1234號被告{Hant}等貪污案件之新聞稿。\n本院審理被告{Hant}等貪污案件，上訴駁回，於今(9日)宣判。", n.Text);
    }

    [Theory]
    [InlineData("公告日：2017.05.09", 2017, 5, 9)] // a four-digit year is Gregorian
    [InlineData("公 告 日：106.13.09", null, null, null)] // an impossible date is no date
    public void A_release_date_is_read_only_in_its_labelled_place(string line, int? year, int? month, int? day)
    {
        var n = Assert.Single(Read(news: new() { [$"新聞資訊_{Pid}.txt"] = Release.Replace("公 告 日：106.05.09", line, StringComparison.Ordinal) }).NewsByProfileId[Pid]);
        Assert.Equal(year is null ? null : new PartialDate(year.Value, month, day), n.PublishedOn);
        // Elsewhere a dotted date stays unparseable.
        Assert.Null(PartialDate.Parse("106.05.09"));
    }

    [Fact]
    public void A_media_article_header_and_trailer_stay_out_of_the_text()
    {
        var n = Assert.Single(Read(news: new() { [$"新聞資訊_{Pid}.txt"] = Article }).NewsByProfileId[Pid]);
        Assert.Equal(("前縣府官員二審改判無罪", "範例日報 (繁體)", new PartialDate(2017, 5, 9), "SMPL000001"), (n.Title, n.Publisher, n.PublishedOn, n.DocumentId));
        Assert.Equal("前縣府官員二審改判無罪\n〔記者王小明／新北報導〕前範例縣政府局長陳淑華被控收賄，高等法院今天二審改判陳淑華無罪。\n陳女表示感謝法官明察。", n.Text);
    }

    [Fact]
    public void Only_the_last_document_line_is_the_trailer()
    {
        var article = Article.Replace("陳女表示感謝法官明察。\n", "陳女表示感謝法官明察。\n文件 A123\n證物另案處理。\n", StringComparison.Ordinal);
        var n = Assert.Single(Read(news: new() { [$"新聞資訊_{Pid}.txt"] = article }).NewsByProfileId[Pid]);
        Assert.Equal("SMPL000001", n.DocumentId);
        Assert.EndsWith("陳女表示感謝法官明察。\n文件 A123\n證物另案處理。", n.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("名單資料編號：1\n範例日報-社會,\n標題\n王小明\n10 字\n2017 年 5 月 9 日")]            // cut after the date line
    [InlineData("名單資料編號：1\n範例日報-社會,\n標題\n王小明\n10 字\n2017 年 5 月 9 日\n範例日報 (繁體)")] // header only
    [InlineData("名單資料編號：1\n範例日報-社會,\n標題\n王小明\n10 字\n2017 年 5 月 9 日\n範例日報 (繁體)\n\n文件 SMPL1\nUI 3.2.1")] // empty body
    public void A_truncated_article_is_read_with_its_date_and_no_body(string article)
    {
        var n = Assert.Single(Read(news: new() { [$"新聞資訊_{Pid}.txt"] = article }).NewsByProfileId[Pid]);
        Assert.Equal((new PartialDate(2017, 5, 9), "標題"), (n.PublishedOn, n.Text));
    }

    [Fact]
    public void A_document_line_out_of_trailer_position_is_body()
    {
        var article = Article.Replace("Sample Provider Inc.\n文件 SMPL000001\nUI 3.2.1\n", "文件 A123\n證物另案處理。\n", StringComparison.Ordinal);
        var n = Assert.Single(Read(news: new() { [$"新聞資訊_{Pid}.txt"] = article }).NewsByProfileId[Pid]);
        Assert.Null(n.DocumentId);
        Assert.EndsWith("陳女表示感謝法官明察。\n文件 A123\n證物另案處理。", n.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_date_line_in_the_body_is_never_the_header_date_and_the_unread_header_is_a_finding()
    {
        var article = "名單資料編號：1\n範例日報-社會,\n標題\n王小明\n10 字\n範例日報 (繁體)\n第一段。\n第二段。\n第三段。\n2017 年 5 月 9 日\n第四段。\n";
        var input = Read(news: new() { [$"新聞資訊_{Pid}.txt"] = article });
        Assert.Null(Assert.Single(input.NewsByProfileId[Pid]).PublishedOn);
        Assert.Contains($"UNPARSEABLE:新聞資訊_{Pid}.txt:header", input.Subject.Findings);
    }

    [Fact]
    public void A_headline_wrapped_past_the_header_window_gives_an_undated_item_with_a_finding()
    {
        var article = Article.Replace("前縣府官員二審改判無罪\n", "前縣府官員\n二審\n改判\n無罪\n", StringComparison.Ordinal);
        var input = Read(news: new() { [$"新聞資訊_{Pid}.txt"] = article });
        Assert.Null(Assert.Single(input.NewsByProfileId[Pid]).PublishedOn);
        Assert.Contains($"UNPARSEABLE:新聞資訊_{Pid}.txt:header", input.Subject.Findings);
        Assert.DoesNotContain(Read(news: new() { [$"新聞資訊_{Pid}.txt"] = Article }).Subject.Findings, f => f.EndsWith(":header", StringComparison.Ordinal));
    }

    [Fact]
    public void Without_a_provider_line_the_last_body_line_stays()
    {
        var n = Assert.Single(Read(news: new() { [$"新聞資訊_{Pid}.txt"] = Article.Replace("Sample Provider Inc.\n", "", StringComparison.Ordinal) }).NewsByProfileId[Pid]);
        Assert.EndsWith("陳女表示感謝法官明察。", n.Text, StringComparison.Ordinal);
        Assert.Equal("SMPL000001", n.DocumentId);
    }

    [Fact]
    public void An_acronym_opening_the_body_is_body_when_no_language_line_follows()
    {
        // No source code or language line: the first body line "ETF" is not a source code by its shape alone.
        var article = Article.Replace("SMPL\n中文(繁體)\n(c) 2017 Sample Daily. All rights reserved.\n", "ETF\n", StringComparison.Ordinal);
        var n = Assert.Single(Read(news: new() { [$"新聞資訊_{Pid}.txt"] = article }).NewsByProfileId[Pid]);
        Assert.StartsWith("前縣府官員二審改判無罪\nETF\n〔記者王小明", n.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_release_label_in_an_article_body_does_not_make_it_a_release()
    {
        var article = Article.Replace("陳女表示感謝法官明察。\n", "公告日：106.05.09\n陳女表示感謝法官明察。\n", StringComparison.Ordinal);
        var n = Assert.Single(Read(news: new() { [$"新聞資訊_{Pid}.txt"] = article }).NewsByProfileId[Pid]);
        Assert.Equal(("範例日報 (繁體)", new PartialDate(2017, 5, 9)), (n.Publisher, n.PublishedOn));
        Assert.Contains("公告日：106.05.09", n.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_download_label_after_the_body_is_body()
    {
        var release = "名單資料編號：" + Pid + "\n公 告 日：106.05.09\n發布單位：臺灣高等法院\n標 題：新聞稿\n本院今日宣判。\n檔案下載：\n1060509新聞稿.pdf\n";
        var n = Assert.Single(Read(news: new() { [$"新聞資訊_{Pid}.txt"] = release }).NewsByProfileId[Pid]);
        Assert.Equal("新聞稿\n本院今日宣判。\n檔案下載：\n1060509新聞稿.pdf", n.Text);
    }

    [Fact]
    public void A_release_without_a_date_is_read_as_plain_text_without_one()
    {
        var release = Release.Replace("公 告 日：106.05.09 分享至 Facebook 分享至 Line", "公 告 日：", StringComparison.Ordinal);
        var n = Assert.Single(Read(news: new() { [$"新聞資訊_{Pid}.txt"] = release }).NewsByProfileId[Pid]);
        Assert.Null(n.PublishedOn);
        Assert.Contains("本院審理被告", n.Text, StringComparison.Ordinal);
    }

    // ---- Customer fields beside the compared ones ----

    [Fact]
    public void Alias_title_and_birth_place_are_read_and_a_dash_or_blank_is_none()
    {
        var s = Read(customer: Customer(title: "董事")).Subject;
        Assert.Equal(["LAN LAN"], s.Aliases);
        Assert.Equal(("董事", "臺北市"), (s.Title, s.BirthPlace));
        Assert.Equal([Hant, "CHUNG YU LAN", "LAN LAN"], s.Names);

        var none = Read(customer: Customer(alias: "-", birthPlace: "-")).Subject;
        Assert.Empty(none.Aliases);
        Assert.Equal((null, null), (none.Title, none.BirthPlace));
        Assert.Equal([Hant, "CHUNG YU LAN"], none.Names);
        Assert.DoesNotContain(none.Findings, f => f.Contains("客戶別名", StringComparison.Ordinal) || f.Contains("職稱", StringComparison.Ordinal));
    }

    [Fact]
    public void An_alias_is_a_customer_name_form_compared_with_the_profile_names()
    {
        // The Profile's only names differ from 本地名稱 and 客戶名稱; the alias is one of them: the name prerequisite is EXACT
        // with it and PARTIAL without. Nothing else changes: an alias is not support.
        var profile = Profile().Replace($"OSN\t{Hant}", "OSN\t鍾小蘭", StringComparison.Ordinal).Replace("1\tYu-lan\tChung", "1\tLAN\tLAN", StringComparison.Ordinal);
        var with = CaseAnalyzer.Analyze(Read(profile)).Hits[0].Identity;
        var without = CaseAnalyzer.Analyze(Read(profile, Customer(alias: "-"))).Hits[0].Identity;
        Assert.Equal((ComparisonStatus.Exact, ComparisonStatus.Partial), (with.Evidence[EvidenceGroup.Name].Status, without.Evidence[EvidenceGroup.Name].Status));
        Assert.DoesNotContain(with.Evidence.Values, v => v.IsSupport);
    }

    [Fact]
    public void A_title_is_shown_with_the_occupation_and_compared_with_nothing()
    {
        var input = Read(customer: Customer(title: "董事"));
        var a = CaseAnalyzer.Analyze(input);
        Assert.Equal("學生、職稱 董事", ExplanationBuilder.Build(input, a).Hits[0].Comparison.Single(r => r.Field == "職業／任職機構").SubjectValue);
        Assert.Equal(CaseAnalyzer.Analyze(Read()).Hits[0].Identity.Evidence[EvidenceGroup.Employment], a.Hits[0].Identity.Evidence[EvidenceGroup.Employment]);
    }

    // ---- The bank's own decision per candidate row ----

    [Fact]
    public void Each_candidate_rows_decision_and_reason_are_imported_never_evidence()
    {
        var input = Read();
        Assert.Equal([("是否符合名單", "未符合", Pid), ("理由", "年齡不符", Pid)], input.ImportedDecisions.Select(d => (d.Field, d.Value, d.ProfileId!)));
        Assert.All(input.Rows, r => Assert.Equal(("未符合", "年齡不符"), (r.LegacyDecision!, r.LegacyReason!)));
        Assert.All(input.ImportedDecisions, d => Assert.False(d.IndependentlyVerified));
    }
}
