using Aml.Core.Screening;
using Aml.Core.Shared;

namespace Aml.Tests.Synthetic;

/// <summary>One attached news file of a real-shape Case as the generator wrote it: the file, its kind and its header values.</summary>
public sealed record RealShapeNews(string File, bool CourtRelease, PartialDate Published, string Publisher, string Title, LegalStage Stage);

/// <summary>
/// A real-shape Case with the values the readers must get beside <see cref="SyntheticCase"/>'s: the customer's alias,
/// title and birth place ("-" or blank: none), the Profile's notes (their labelled parts) and every news file in order,
/// and the bank's own decision of the candidate row.
/// </summary>
public sealed record RealShapeCase(SyntheticCase Case, string? Alias, string? Title, string? BirthPlace, IReadOnlyList<string> NoteLabels,
    IReadOnlyList<RealShapeNews> News, string Decision, string Reason);

/// <summary>
/// The real-shape synthetic set (docs/plan/poc-delivery-plan.md#real-shape-synthetic-set): Cases written in the layout of
/// a real export where the end-to-end set uses a plain one: a customer file with 客戶別名, 職稱 and 出生地點, a Profile
/// with 已故?, a Resident of row and the vendor's notes (on one line or as a section), news files numbered 新聞資訊1_,
/// 新聞資訊2_ (a media article with its full header and trailer, and for every other Case a court press release), and
/// the bank's decision per candidate row. Invented values only; its own seed and Cases, apart from the other sets.
/// </summary>
public sealed partial class SyntheticCaseGenerator
{
    public const int RealShapeSeed = 20261130;
    public const int RealShapeCount = 20;

    private static readonly string[] Courts = ["臺灣高等法院", "臺灣臺北地方法院", "臺灣高雄地方法院"];

    /// <summary>
    /// The Cases after the first <see cref="RealShapeCount"/>, one per entry: a customer born 2005 to 2008 (under 14 on any
    /// date of the item, 2016 to 2018) against a same-named stranger read in the item as a sentenced director in a list, a
    /// defendant in a court release, a victim or a judge; and an adult customer who is the sentenced director.
    /// </summary>
    public static readonly DatedRole[] DatedRoleCases =
        [DatedRole.ChildDirector, DatedRole.ChildDirector, DatedRole.ChildDirector, DatedRole.ChildDirector, DatedRole.ChildDefendant, DatedRole.ChildDefendant,
         DatedRole.ChildVictim, DatedRole.ChildVictim, DatedRole.ChildJudge, DatedRole.ChildJudge, DatedRole.AdultDirector, DatedRole.AdultDirector];

    public enum DatedRole { ChildDirector, ChildDefendant, ChildVictim, ChildJudge, AdultDirector }

    public static IReadOnlyList<RealShapeCase> RealShape()
    {
        var g = new SyntheticCaseGenerator(SyntheticAssumptions.Default with { Seed = RealShapeSeed });
        return [.. Enumerable.Range(0, RealShapeCount).Select(i => g.RealShapeCase($"REAL-{i + 1:000}", i)),
            .. DatedRoleCases.Select((kind, k) => g.RealShapeCase($"REAL-{RealShapeCount + k + 1:000}", 2 * k, kind))];
    }

    private RealShapeCase RealShapeCase(string id, int i, DatedRole? dated = null)
    {
        var customer = NewPerson();
        var child = dated is not (null or DatedRole.AdultDirector);
        if (child) customer = customer with { Dob = RandomDob(r.Next(2005, 2009)) };
        var same = dated is null ? i % 2 == 0 : dated == DatedRole.AdultDirector;
        // A dated-role stranger has the customer's very name and gender: only the item's date and role tell them apart.
        var p = same ? customer : dated is null ? Stranger(customer) : NewPerson() with { Surname = customer.Surname, Given = customer.Given, Gender = customer.Gender };
        var pid = $"79{++profileSeq:000000}";

        // Customer: the export's labels in its order; "-" is its empty marker, a blank title an empty line.
        var alias = P(0.5) ? customer.RomanGiven.Replace('-', ' ').ToUpperInvariant() : null;
        var title = P(0.5) ? "經理" : null;
        var birthPlace = P(0.7) ? customer.City : null;
        string D(PartialDate d) => $"{d.Year}/{d.Month}/{d.Day}";
        var customerFile = string.Join('\n',
            "基本資料", "客戶號碼", $"C{r.Next(1_000_000, 9_999_999)}", "客戶別名", alias ?? "-", "性別", customer.Gender == Gender.Male ? "男" : "女",
            "客戶名稱", customer.BankEnglish, "出生日期", D(customer.Dob), "證件種類", "身分證統一編號", "國籍 1", "Taiwan", "發照地", "-",
            "本地名稱", customer.Hant, "出生地點", birthPlace ?? "-", "證件號碼", customer.NationalId, "國籍 2", "-",
            "詳細資料", "客戶類型", "個人", "職業", "服務業(7)", "職稱", title ?? "", "稅號/統一編號", "-", "年收入", "0") + "\n";

        // The bank's own decision of the row, never evidence.
        var (decision, reason) = same ? ("符合", "確認後符合") : ("未符合", "年齡不符");
        var caseTable = "編號\t被偵測到的名字\t資料類別\t關聯方參考號碼\t資料來源\t是否符合名單？\n"
            + $"1\t{customer.Hant}\t客戶\t-\t客戶名稱\t-\n\n疑似符合的名單資料\n"
            + "編號\t姓名/名稱\t相似度分數\tSDN禁制類別\t名單資料編號\t是否符合名單？\t理由\tIs RCA?\tName Property\n"
            + "全不符合\t全符合\tPEP\t黑名單\t負面新聞\n"
            + $"1\t{p.Hant}\t100\tDJ PRIMARY\t{pid}\t{(same ? "-" : decision)}\t{(same ? decision : "-")}\t{reason}\t不是\t\t\t\n";

        // Profile, with the vendor's notes: on one line after "Watchlist ProfileNotes:", or as a section with wrapped lines.
        var reviewed = new DateOnly(2026, r.Next(1, 11), r.Next(1, 28));
        var notes = $"Last Reviewed Date: {reviewed.ToString("dd-MMM-yyyy", System.Globalization.CultureInfo.InvariantCulture)} "
            + "Keywords: fraud, breach of trust Companies mentioned: Sample Trading Co. "
            + $"Profile: {p.RomanSurname} {p.RomanGiven} was charged by the Sample District Prosecutors Office with fraud.";
        var noteLabels = new[] { "Last Reviewed Date", "Keywords", "Companies mentioned", "Profile" };
        var notesText = i % 3 == 0
            ? "ProfileNotes:\n" + notes.Replace(" Keywords:", "\nKeywords:", StringComparison.Ordinal).Replace(" with fraud.", "\nwith fraud.", StringComparison.Ordinal)
            : "Watchlist ProfileNotes: " + notes;
        var profileFile = string.Join('\n',
            $"Profile_ID:\t{pid}", "記錄類型:\tPerson", $"性別:\t{(p.Gender == Gender.Male ? "Male" : "Female")}", "已故?:\tFalse", "狀態:\tActive",
            "主要名稱:", "編號\t名字\t姓", $"1\t{p.RomanGiven}\t{p.RomanSurname}", $"OSN\t{p.Hant}",
            "國家資料:", "編號\t類型\t國家", "1\tCountry of Reported Allegation\tTaiwan", "2\tResident of\tTaiwan", "3\tCitizenship\tTaiwan",
            "類別:", "編號\t描述1\t描述2", "1\tSpecial Interest Person (SIP)\tFraud", notesText) + "\n";

        var files = new Dictionary<string, string> { ["客戶-背景資料.txt"] = customerFile, ["案件資訊.txt"] = caseTable, [$"名單資料_{pid}.txt"] = profileFile };
        var news = new List<RealShapeNews>();

        // 新聞資訊1_: a media article, header and trailer as a news database exports it.
        var on = new PartialDate(dated is null ? r.Next(2016, 2026) : r.Next(2016, 2019), r.Next(1, 13), r.Next(1, 29));
        var headline = $"{p.City[..2]}詐欺案起訴";
        var body = dated switch
        {
            DatedRole.ChildDirector or DatedRole.AdultDirector => $"〔記者王小明／{p.City[..2]}報導〕{p.City[..2]}地方法院今天判決範例公司負責人林大華、董事{p.Hant}等4人各有期徒刑四月。",
            DatedRole.ChildVictim => $"〔記者王小明／{p.City[..2]}報導〕林大華涉嫌以投資名義詐取款項，被害人{p.Hant}報案後，警方依詐欺罪嫌將林大華移送地檢署。",
            DatedRole.ChildJudge => $"〔記者王小明／{p.City[..2]}報導〕{p.City[..2]}地方法院法官{p.Hant}今天宣判。",
            _ => $"〔記者王小明／{p.City[..2]}報導〕{p.Hant}涉嫌以投資名義詐取款項，{p.City[..2]}地檢署偵查終結，今天依詐欺罪將{p.Hant}起訴。",
        };
        if (dated == DatedRole.ChildDefendant)
        {
            var court = Pick(Courts);
            var releaseTitle = $"{court}有關{on.Year - 1912}年度訴字第{r.Next(100, 999)}號被告{p.Hant}詐欺案件之新聞稿。";
            files[$"新聞資訊1_{pid}.txt"] = string.Join('\n', $"名單資料編號：{pid}", $"公 告 日：{on.Year - 1911}.{on.Month:D2}.{on.Day:D2}",
                $"發布單位：{court}", $"標 題：{releaseTitle}", "檔案下載：", $"{on.Year - 1911}{on.Month:D2}{on.Day:D2}新聞稿.pdf",
                $"本院審理被告{p.Hant}詐欺案件，今日宣判，判處{p.Hant}有期徒刑一年。") + "\n";
            news.Add(new($"新聞資訊1_{pid}.txt", true, on, court, releaseTitle, LegalStage.Convicted));
        }
        else
        {
            files[$"新聞資訊1_{pid}.txt"] = string.Join('\n', $"名單資料編號：{pid}", "範例日報-社會,", headline, "王小明", $"{body.Length} 字",
                $"{on.Year} 年 {on.Month} 月 {on.Day} 日", "範例日報 (繁體)", "SMPL", "中文(繁體)", $"(c) {on.Year} Sample Daily. All rights reserved.",
                body, "Sample Provider Inc.", $"文件 SMPL{r.Next(100000, 999999)}", "UI 3.2.1") + "\n";
            news.Add(new($"新聞資訊1_{pid}.txt", false, on, "範例日報 (繁體)", headline, LegalStage.Indicted));
        }

        // 新聞資訊2_ for every other Case: the court's press release a year later, header labels with spaces inside.
        if (i % 2 == 1)
        {
            var at = new PartialDate(on.Year + 1 > 2026 ? 2026 : on.Year + 1, on.Month, on.Day);
            var court = Pick(Courts);
            var releaseTitle = $"{court}有關{at.Year - 1912}年度訴字第{r.Next(100, 999)}號被告{p.Hant}詐欺案件之新聞稿。";
            files[$"新聞資訊2_{pid}.txt"] = string.Join('\n', $"名單資料編號：{pid}", $"公 告 日：{at.Year - 1911}.{at.Month:D2}.{at.Day:D2} 分享至 Facebook 分享至 Line",
                $"發布單位：{court}", $"標 題：{releaseTitle}", "檔案下載：", $"{at.Year - 1911}{at.Month:D2}{at.Day:D2}新聞稿.pdf",
                $"本院審理被告{p.Hant}詐欺案件，今日宣判，判處{p.Hant}有期徒刑一年。") + "\n";
            news.Add(new($"新聞資訊2_{pid}.txt", true, at, court, releaseTitle, LegalStage.Convicted));
        }

        var expected = new ExpectedProfile([p.Hant, $"{p.RomanSurname} {p.RomanGiven}"], p.Gender, [], ["TW"], [], null, on);
        var truth = new CandidateTruth(pid, RecordKind.News, same, ListType.AdverseMedia, true, news.Count > 1 || dated is not null ? null : LegalStage.Indicted,
            [.. same ? [] : new[] { p.Hant == customer.Hant ? "stranger:same-name" : "stranger:near-name" }, .. dated is { } d ? new[] { $"dated-role:{d}" } : []], expected, 1);
        var subject = new ExpectedSubject([customer.Hant, customer.BankEnglish], customer.Gender, customer.Dob, "TW", customer.NationalId);
        return new RealShapeCase(new SyntheticCase(id, false, files, subject, [truth]), alias, title, birthPlace, noteLabels, news, decision, reason);
    }
}
