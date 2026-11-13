using System.Buffers;
using System.ComponentModel;
using System.Text.RegularExpressions;
using Aml.Core.Normalization;
using Aml.Core.Shared;

namespace Aml.Core.Explanation;

public enum NewsIdentifierKind { Gender, Age, BirthDate, IdNumber, Nationality, Residence, Occupation, Employer }

public sealed record NewsIdentifier(
    [property: Description("GENDER（性別）、AGE（年齡）、BIRTH_DATE（出生日期或年次）、ID_NUMBER（身分證字號，已遮罩）、NATIONALITY（國籍）、RESIDENCE（住居地或籍貫）、OCCUPATION（職業或職稱）、EMPLOYER（任職機構）")] NewsIdentifierKind Kind,
    [property: Description("報導原文；身分證字號時為 null，見 number")][property: Sensitivity(Sensitivity.Personal)] string? Text,
    [property: Description("身分證字號，已遮罩；其他種類時為 null")][property: Sensitivity(Sensitivity.Identifier)] string? Number = null);

/// <summary>
/// What the news writes about a person right next to their name (watchlist-and-adverse-information.md#news-digest):
/// the items of a bracket after it (王大明（男，45歲，住臺中市）), the items after a comma that are themselves identifiers
/// (王大明，男，民國70年生，), an age right after it (王大明今年45歲), what is written right before it in its clause
/// (臺中市45歲男子王大明, 越南籍阮某, 司機王大明), and the gender of an anonymised form (王男, 甲女). Restates and decides nothing.
/// </summary>
public static partial class NewsIdentifiers
{
    private const int MaxForms = 20, MaxPerForm = 200, MaxAfter = 200, MaxEngaged = 300, MaxBefore = 24, MaxFound = 20, MaxPeel = 6, MaxCommaItems = 6, MaxBracketItems = 12;

    public static IReadOnlyList<NewsIdentifier> Read(string text, IEnumerable<string> forms)
    {
        var found = new List<NewsIdentifier>();
        void Add(NewsIdentifierKind kind, string value)
        {
            value = value.Trim();
            if (kind == NewsIdentifierKind.IdNumber) value = ExplanationBuilder.Mask(IdCore().Match(value).Value);
            // An ID number is only ever kept masked, in its own field: any other item holding one is dropped.
            else if (IdCore().IsMatch(CjkText.HalfWidth(value))) return;
            if (value.Length == 0 || found.Count >= MaxFound || found.Exists(f => f.Kind == kind && (f.Text ?? f.Number) == value)) return;
            found.Add(kind == NewsIdentifierKind.IdNumber ? new(kind, null, value) : new(kind, value));
        }
        foreach (var form in forms.Where(f => f.Length >= 2).Distinct(StringComparer.Ordinal).Take(MaxForms))
        {
            if (AnonymisedGender().Match(form) is { Success: true } g) Add(NewsIdentifierKind.Gender, g.Groups["g"].Value);
            var at = 0;
            for (var n = 0; n < MaxPerForm && found.Count < MaxFound && (at = text.IndexOf(form, at, StringComparison.Ordinal)) >= 0; n++, at += form.Length)
            {
                After(text, at + form.Length, Add);
                Before(text[Math.Max(0, at - MaxBefore)..at], Add);
                Engaged(text, at + form.Length, Add);
            }
        }
        return found;
    }

    private static void After(string text, int end, Action<NewsIdentifierKind, string> add)
    {
        var rest = text.AsSpan(end, Math.Min(MaxAfter, text.Length - end)).ToString();
        var spaced = rest.Length > 0 && char.IsWhiteSpace(rest[0]) && rest[0] != '\n';
        rest = rest.TrimStart(' ', '　', '\t');
        if (rest.Length == 0) return;
        if (rest[0] is '（' or '(')
        {
            // A judgment's party line may run long (男，民國70年1月1日生，身分證統一編號：…，住…): read up to the bracket's end,
            // or the sentence's, within MaxAfter characters.
            var close = rest.IndexOfAny(['）', ')'], 1);
            var inside = rest[1..(close < 0 ? rest.Length : close)];
            if (inside.AsSpan().IndexOfAny(SentenceEnds) is var stop and >= 0) inside = inside[..stop];
            foreach (var item in inside.Split(ItemSeparators).Take(MaxBracketItems))
                if (Kind(item) is { } k) add(k, item);
            return;
        }
        if (rest[0] is '，' or ',' || spaced)
        {
            // Only items that are identifiers by themselves (王大明，男，45歲，涉嫌… or 王大明 男 45歲): the first other clause
            // ends the list, and so does a bracket.
            var list = rest[0] is '，' or ',' ? rest[1..] : rest;
            if (list.AsSpan().IndexOfAny(ListEnds) is var stop and >= 0) list = list[..stop];
            foreach (var item in list.Split(ListSeparators, StringSplitOptions.RemoveEmptyEntries).Take(MaxCommaItems))
            {
                if (Kind(item) is not { } k || k is NewsIdentifierKind.Occupation or NewsIdentifierKind.Employer) break;
                add(k, item);
            }
            return;
        }
        if (AgeRightAfter().Match(rest) is { Success: true } a) add(NewsIdentifierKind.Age, a.Groups["a"].Value);
        else if (OccupationAfter().Match(rest) is { Success: true } o) add(NewsIdentifierKind.Occupation, o.Value);
    }

    // A judgment's sentencing reasons say what the defendant does: 自陳高職畢業、從事水電工作, 現擔任某公司業務. Read later
    // in the clause naming the person (a comma may open another's: 王大明與李小華共犯，李小華現擔任店員); a role in the
    // offence (車手, 收水) is not an occupation, nor is a relative's (之妻擔任教師).
    private static void Engaged(string text, int end, Action<NewsIdentifierKind, string> add)
    {
        var sentence = text.AsSpan(end, Math.Min(MaxEngaged, text.Length - end));
        if (sentence.IndexOfAny(EngagedEnds) is var stop and >= 0) sentence = sentence[..stop];
        if (Relative().IsMatch(sentence)) return;
        if (EngagedIn().Match(sentence.ToString()) is not { Success: true } m || CrimeRole().IsMatch(m.Groups["o"].Value)) return;
        var job = m.Groups["o"].Value;
        // 從事虛擬貨幣買賣 is conduct, 從事水電工作 and 擔任營業員 an occupation.
        if (!m.Groups["work"].Success && !Occupations.Any(o => job.EndsWith(o, StringComparison.Ordinal)) && !TradeEnd().IsMatch(job)) return;
        add(EmployerItem().IsMatch(job) ? NewsIdentifierKind.Employer : NewsIdentifierKind.Occupation, job);
    }

    // Peels identifiers off the end of the clause before the name: 臺中市 | 45歲 | 男子 | 王大明. Spaces inside it are
    // dropped (judgments space out titles: 法　官).
    private static void Before(string before, Action<NewsIdentifierKind, string> add)
    {
        var cut = before.AsSpan().LastIndexOfAny(ClauseBreaks);
        var run = string.Concat(before[(cut + 1)..].Where(c => !char.IsWhiteSpace(c))).TrimEnd('的');
        for (var i = 0; i < MaxPeel && run.Length > 0; i++)
        {
            Match m;
            if ((m = GenderBefore().Match(run)).Success) add(NewsIdentifierKind.Gender, m.Value[..1]);
            else if ((m = AgeBefore().Match(run)).Success) add(NewsIdentifierKind.Age, m.Groups["a"].Value);
            else if ((m = NationalityBefore().Match(run)).Success) add(NewsIdentifierKind.Nationality, m.Value);
            else if ((m = PlaceBefore().Match(run)).Success) add(NewsIdentifierKind.Residence, m.Groups["p"].Value);
            else if ((m = OccupationBefore().Match(run)).Success) add(NewsIdentifierKind.Occupation, m.Value);
            else return;
            run = run[..m.Index].TrimEnd('的');
        }
    }

    // One bracket or comma item: 男, 45歲, 民國70年1月1日生, 70年次, A123456789, 越南籍, 住臺中市, 某公司負責人, 司機.
    private static NewsIdentifierKind? Kind(string item)
    {
        var h = CjkText.HalfWidth(item.Trim());
        if (h.Length == 0) return null;
        if (GenderItem().IsMatch(h)) return NewsIdentifierKind.Gender;
        if (AgeItem().IsMatch(h)) return NewsIdentifierKind.Age;
        if (BirthItem().IsMatch(h)) return NewsIdentifierKind.BirthDate;
        if (IdItem().IsMatch(h)) return NewsIdentifierKind.IdNumber;
        if (NationalityItem().IsMatch(h)) return NewsIdentifierKind.Nationality;
        if (ResidenceItem().IsMatch(h)) return NewsIdentifierKind.Residence;
        if (EmployerItem().IsMatch(h)) return NewsIdentifierKind.Employer;
        return h.Length <= 12 && (Occupations.Any(o => h.EndsWith(o, StringComparison.Ordinal)) || TradeItem().IsMatch(h)) ? NewsIdentifierKind.Occupation : null;
    }

    private static readonly SearchValues<char> SentenceEnds = SearchValues.Create("。！？；\n");
    private static readonly SearchValues<char> EngagedEnds = SearchValues.Create("。！？；\n，,");
    private static readonly SearchValues<char> ListEnds = SearchValues.Create("。！？；\n（(");
    private static readonly char[] ItemSeparators = ['，', ',', '、', '；', ';', '/', '／'];
    private static readonly char[] ListSeparators = ['，', ',', ' ', '　', '\t'];
    private static readonly SearchValues<char> ClauseBreaks = SearchValues.Create("。！？；\n，,、「」『』（）()：:");

    // Not party words (被告, 被害人, 證人): those are roles, read by the digest.
    private static readonly string[] Occupations =
    [
        "副董事長", "董事長", "總經理", "副總經理", "負責人", "執行長", "監察人", "業務員", "董事", "會計", "立法委員", "立委", "議員", "議長",
        "市長", "縣長", "鄉長", "鎮長", "村長", "里長", "部長", "局長", "處長", "署長", "院長", "主任", "經理", "主管", "理專", "襄理",
        "協理", "副總", "總監", "廠長", "店長", "組長", "課長", "科長", "秘書", "助理", "專員", "行員", "營業員", "職員", "員工", "店員",
        "司機", "工程師", "醫師", "護理師", "藥師", "教師", "老師", "教授", "校長", "律師", "會計師", "代書", "地政士", "仲介", "房仲",
        "顧問", "業者", "老闆", "老闆娘", "商人", "工人", "粗工", "技工", "技師", "廚師", "學生", "軍人", "公務員", "主委", "委員",
        "檢察官", "法官", "警員", "員警", "警官", "記者", "保全", "移工", "看護", "書記官長", "書記官", "檢察長", "審判長", "理事長",
        "分局長", "大隊長", "隊長", "司長", "次長", "主任委員", "典獄長", "總統", "主任秘書", "研究員", "總幹事", "客服", "小編", "攤販",
        "學徒", "零工", "清潔員", "美甲師", "美髮師", "設計師", "業務", "外送員", "通譯",
    ];

    private static readonly string OccupationAlternatives =
        $"(?:副|前|代理|常務|政務|主任|執業|承辦)?(?:{string.Join('|', Occupations.OrderByDescending(o => o.Length))})";

    private const string AgeNumber = "(?:\\d{1,3}|[〇零一二兩三四五六七八九十廿卅百]{1,5})\\s?(?:足|周|週)?歲";
    private const string Countries = "中華民國|中國大陸|大陸|中國|香港|澳門|越南|印尼|泰國|菲律賓|馬來西亞|新加坡|日本|南韓|韓國|北韓|緬甸|柬埔寨|寮國|印度|美國|加拿大|英國|法國|德國|俄羅斯|澳洲|紐西蘭|巴西|土耳其|奈及利亞|外";
    private const string Places = "臺北|台北|新北|桃園|臺中|台中|臺南|台南|高雄|基隆|新竹|苗栗|彰化|南投|雲林|嘉義|屏東|宜蘭|花蓮|臺東|台東|澎湖|金門|連江|馬祖";
    private const string GenderNouns = "男子|女子|男性|女性|男童|女童";

    [GeneratedRegex(@"^(?:[\p{IsCJKUnifiedIdeographs}A-Za-zＡ-Ｚ](?<g>[男女])|\p{IsCJKUnifiedIdeographs}{1,2}姓(?<g>[男女])\p{IsCJKUnifiedIdeographs}?)$")]
    private static partial Regex AnonymisedGender();

    [GeneratedRegex($@"^\s?(?:今年|現年|時年)\s?(?<a>{AgeNumber})|^(?<a>\d{{1,3}}\s?歲)")]
    private static partial Regex AgeRightAfter();

    [GeneratedRegex($"(?:{GenderNouns})$")]
    private static partial Regex GenderBefore();

    [GeneratedRegex($"(?<![\\d〇零一二兩三四五六七八九十廿卅百]|滿|約|逾|近|超過|未滿|年滿)(?<a>{AgeNumber})$")]
    private static partial Regex AgeBefore();

    [GeneratedRegex($"(?:{Countries})籍$")]
    private static partial Regex NationalityBefore();

    [GeneratedRegex($@"(?<p>(?:{Places})(?:市|縣)?(?:\p{{IsCJKUnifiedIdeographs}}{{1,3}}?[區鄉鎮])?)(?:籍)?$")]
    private static partial Regex PlaceBefore();

    private static readonly Regex OccupationBeforePattern = new($"{OccupationAlternatives}$");
    private static readonly Regex OccupationAfterPattern = new($"^{OccupationAlternatives}");

    private static Regex OccupationBefore() => OccupationBeforePattern;

    private static Regex OccupationAfter() => OccupationAfterPattern;

    [GeneratedRegex(@"^(?:從事|擔任|任職)?\p{IsCJKUnifiedIdeographs}{1,6}(?:業|工作|工)$")]
    private static partial Regex TradeItem();

    [GeneratedRegex(@"(?:從事|現職為?|任職於?|受僱於?|現擔任|擔任)(?<o>\p{IsCJKUnifiedIdeographs}{2,15}?)(?:(?<work>之?工作)|職務|一職|[，,、；)）]|$)")]
    private static partial Regex EngagedIn();

    [GeneratedRegex(@"\p{IsCJKUnifiedIdeographs}[業工員師]$")]
    private static partial Regex TradeEnd();

    [GeneratedRegex(@"車手|收水|收簿|取簿|取款|提款|人頭|水房|話務|機房|詐欺|詐騙|集團|幹部|成員|共犯|角色|工作機會|犯罪|不法|非法|款項|本案|報酬|掩護|罪嫌|用途|之")]
    private static partial Regex CrimeRole();

    [GeneratedRegex(@"^[男女]性?$")]
    private static partial Regex GenderItem();

    [GeneratedRegex($@"^(?:今年|現年|時年)?\s?{AgeNumber}$")]
    private static partial Regex AgeItem();

    [GeneratedRegex(@"^(?:民國)?\s?\d{2,4}\s?年(?:\s?\d{1,2}\s?月(?:\s?\d{1,2}\s?日)?)?\s?出?生$|^\d{2,3}\s?年次$")]
    private static partial Regex BirthItem();

    [GeneratedRegex(@"^(?:(?:國民)?身分證(?:統一編號|字號|號碼)?\s?[:：]?\s?)?[A-ZＡ-Ｚ][12*＊○Ｏ][\d*＊○Ｏ]{8}號?$")]
    private static partial Regex IdItem();

    [GeneratedRegex(@"[A-ZＡ-Ｚ][12１２*＊○Ｏ][\d０-９*＊○Ｏ]{8}")]
    private static partial Regex IdCore();

    [GeneratedRegex(@"^\p{IsCJKUnifiedIdeographs}{1,8}籍(?:人士|男子|女子|勞工|移工|配偶|人)?$")]
    private static partial Regex NationalityItem();

    // An address or a place opens with a city or county: 住臺中市西區民生路1號, 設籍桃園市, 高雄市 (not 住院治療, 前往臺北市).
    [GeneratedRegex($@"^(?:住|居住|住居|設籍|戶籍)?(?:在|於)?(?:{Places})[\p{{IsCJKUnifiedIdeographs}}\d之\-]{{0,30}}$")]
    private static partial Regex ResidenceItem();

    // A company, bank, office or association, alone or with the job there (某公司負責人), never a clause about one
    // (涉嫌詐欺某某銀行款項).
    [GeneratedRegex(@"^(?:任職於?|受僱於?)?(?!.*(?:涉|款|詐|騙|前往|向))[\p{IsCJKUnifiedIdeographs}A-Za-z0-9]{0,20}?(?:股份有限公司|有限公司|公司|銀行|證券|集團|企業社|企業|商行|工作室|事務所|診所|醫院|協會|基金會|農會)\p{IsCJKUnifiedIdeographs}{0,8}$")]
    private static partial Regex EmployerItem();

    [GeneratedRegex(@"[之其](?:妻|夫|子|女|父|母|兄|弟|姊|妹)|配偶|女友|男友")]
    private static partial Regex Relative();
}
