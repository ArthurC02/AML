using System.Text;
using Aml.Core.Scoring;
using Aml.Core.Screening;
using Aml.Core.Shared;

namespace Aml.Tests.Synthetic;

/// <summary>What a candidate Profile is: a list record, or an adverse-information record whose attached text is of this kind.</summary>
public enum RecordKind { Sanction, Pep, Rca, WantedList, News, PressRelease, CourtRuling }

/// <summary>How complete a Profile of one kind is: the share with each field, and the precision of its birth date.</summary>
public sealed record ProfileCompleteness(double Dob, double DobDay, double DobMonth, double Gender, double Citizenship, double Document, double Address);

/// <summary>What an attached text states next to the person's name: age, birth date (民國, full), gender.</summary>
public sealed record TextCompleteness(double Age, double BirthDate, double Gender);

/// <summary>
/// THE ASSUMPTIONS of the synthetic end-to-end set (docs/plan/poc-delivery-plan.md#synthetic-end-to-end-set), in one
/// place. Every rate is an assumption, not a measurement of real data, except where the doc cites the repo for it. A
/// precision measured on this set holds only under these numbers.
/// </summary>
public sealed record SyntheticAssumptions
{
    public static readonly SyntheticAssumptions Default = new();

    public int Seed { get; init; } = 20261126;
    public DateOnly AsOf { get; init; } = new(2026, 11, 26);

    /// <summary>Cases per kind of the first candidate Profile, generated in this order.</summary>
    public IReadOnlyList<(RecordKind Kind, int Count)> Cases { get; init; } =
    [
        (RecordKind.Sanction, 40),
        (RecordKind.Pep, 40),
        (RecordKind.Rca, 30),
        (RecordKind.WantedList, 30),
        (RecordKind.News, 60),
        (RecordKind.PressRelease, 50),
        (RecordKind.CourtRuling, 50),
    ];

    public double SamePerson { get; init; } = 0.5;               // the first Profile is the customer
    public double SecondStrangerProfile { get; init; } = 0.2;    // the Case also lists a same-name stranger's Profile
    public double StrangerSameFullName { get; init; } = 0.7;     // else a near name: one given character differs

    // A different person with the same name agrees by chance on:
    public double StrangerSameGender { get; init; } = 0.9;
    public double StrangerSameBirthYear { get; init; } = 0.1;
    public double StrangerSameBirthDay { get; init; } = 0.01;    // the whole date, given the same year

    // The same person, written differently on the Profile:
    public double NameRomanizedOnly { get; init; } = 0.15;       // list records only: no Chinese name
    public double NamePinyin { get; init; } = 0.05;              // list records only: Hanyu Pinyin instead of the passport form
    public double NameSimplified { get; init; } = 0.10;
    public double NameAlias { get; init; } = 0.10;               // the primary name is another, the customer's is an alias
    public double ProfileDobTypo { get; init; } = 0.03;          // a recording error in the Profile's birth date
    public double OldAddress { get; init; } = 0.5;
    public double CustomerPassport { get; init; } = 0.1;         // else a national ID
    public double PassportRenewed { get; init; } = 0.3;          // the Profile holds the old passport number
    public double CustomerDobMissing { get; init; } = 0.02;
    public double CustomerGenderMissing { get; init; } = 0.02;
    public double CustomerDocumentMissing { get; init; } = 0.02;

    // Source condition:
    public double SanctionDelisted { get; init; } = 0.25;        // the condition does not hold
    public double FormerPep { get; init; } = 0.25;               // still holds (認定標準第 5 條)
    public double RcaRelatedParty { get; init; } = 0.5;          // an RCA Case screens the customer's related party
    public double FlagsSet { get; init; } = 0.2;                 // candidate flags filled, consistent with the truth; else empty

    // Text:
    public double MultiPersonText { get; init; } = 0.3;          // the text names a second person with another outcome
    public double OtherInSameSentence { get; init; } = 0.5;      // of those: the other's outcome in the person's sentence
    public double MaskedNameInRelease { get; init; } = 0.3;      // a press release writes 陳姓男子
    public double StatedAgeOffByOne { get; init; } = 0.1;
    public double Html { get; init; } = 0.2;                     // customer and Profile files as HTML pages

    // Received pages stating an age (scenario 2, ReceivedPages): where the page states its publication date.
    public double PagePublishedInMetadata { get; init; } = 0.5;  // meta article:published_time
    public double PagePublishedInText { get; init; } = 0.3;      // else a date line under the title (half 民國); the rest state none

    public IReadOnlyDictionary<RecordKind, ProfileCompleteness> Profile { get; init; } = new Dictionary<RecordKind, ProfileCompleteness>
    {
        [RecordKind.Sanction] = new(Dob: 0.7, DobDay: 0.6, DobMonth: 0.1, Gender: 0.9, Citizenship: 0.95, Document: 0.4, Address: 0.3),
        [RecordKind.Pep] = new(0.5, 0.3, 0.1, 0.95, 1.0, 0.05, 0.2),
        [RecordKind.Rca] = new(0.4, 0.2, 0.1, 0.9, 0.9, 0.0, 0.1),
        [RecordKind.WantedList] = new(0.6, 0.5, 0.1, 0.9, 1.0, 0.3, 0.3),
        [RecordKind.News] = new(0.3, 0.1, 0.1, 0.8, 0.9, 0.0, 0.3),
        [RecordKind.PressRelease] = new(0.3, 0.1, 0.1, 0.8, 0.9, 0.0, 0.3),
        [RecordKind.CourtRuling] = new(0.4, 0.3, 0.1, 0.8, 0.9, 0.0, 0.3),
    };

    public IReadOnlyDictionary<RecordKind, TextCompleteness> Text { get; init; } = new Dictionary<RecordKind, TextCompleteness>
    {
        [RecordKind.News] = new(Age: 0.6, BirthDate: 0.0, Gender: 0.5),
        [RecordKind.PressRelease] = new(0.7, 0.0, 0.8),
        [RecordKind.CourtRuling] = new(0.0, 0.9, 0.9),
    };

    /// <summary>The legal stages an attached text of each kind tells, equally often.</summary>
    public IReadOnlyDictionary<RecordKind, LegalStage[]> Stages { get; init; } = new Dictionary<RecordKind, LegalStage[]>
    {
        [RecordKind.News] = [LegalStage.Alleged, LegalStage.Indicted, LegalStage.Convicted, LegalStage.Acquitted],
        [RecordKind.PressRelease] = [LegalStage.Alleged, LegalStage.Indicted, LegalStage.Dismissed],
        [RecordKind.CourtRuling] = [LegalStage.Convicted, LegalStage.Acquitted],
    };
}

/// <summary>The values a reader must extract from the files, as the generator wrote them.</summary>
public sealed record ExpectedSubject(IReadOnlyList<string> Names, Gender? Gender, PartialDate? Dob, string Nationality, string? DocumentNumber);

public sealed record ExpectedProfile(IReadOnlyList<string> Names, Gender? Gender, IReadOnlyList<PartialDate> Dobs, IReadOnlyList<string> Citizenship,
    IReadOnlyList<string> DocumentNumbers, string? OfficialListStatus, PartialDate? NewsPublished);

/// <summary>
/// The truth of one candidate: identity (same person or not), the source condition (list type, whether it holds, the
/// legal stage the record tells), the variations applied, and the fields the readers must get.
/// </summary>
public sealed record CandidateTruth(string ProfileId, RecordKind Kind, bool SamePerson, ListType ListType, bool ConditionHolds, LegalStage? Stage,
    IReadOnlyList<string> Tags, ExpectedProfile Fields, int CandidateRows)
{
    public string ProfileKey => "DJ:" + ProfileId;

    /// <summary>The combination-matrix cell the truth gives: NOT_APPLICABLE, NO_HIT for another person, HIT for the customer.</summary>
    public ScreeningAssessment Expected => !ConditionHolds ? ScreeningAssessment.NotApplicable : SamePerson ? ScreeningAssessment.Hit : ScreeningAssessment.NoHit;

    public SourceKind ScoredAs => Kind switch
    {
        RecordKind.News => SourceKind.AllowlistedNews,
        RecordKind.PressRelease => SourceKind.PressRelease,
        RecordKind.CourtRuling => SourceKind.CourtJudgment,
        _ => SourceKind.OfficialList,
    };
}

public sealed record SyntheticCase(string Id, bool Html, IReadOnlyDictionary<string, string> Files, ExpectedSubject Subject, IReadOnlyList<CandidateTruth> Candidates);

/// <summary>A source document of a path not built yet (a received URL, grounding search, periodic judgments), with its truth.</summary>
public sealed record ExternalFixture(string Scenario, string CustomerCase, string Kind, string Url, string Content, IReadOnlyList<(string WrittenName, bool IsCustomer, LegalStage Stage)> Persons);

/// <summary>One person a received page names: the Case's customer or another person, a suspect or not, and the legal stage the page tells.</summary>
public sealed record PagePersonTruth(string WrittenName, bool IsCustomer, bool Suspect, LegalStage Stage)
{
    /// <summary>The combination-matrix cell the truth gives: NOT_APPLICABLE for a non-suspect, NO_HIT for another person, HIT for the customer.</summary>
    public ScreeningAssessment Expected => !Suspect ? ScreeningAssessment.NotApplicable : IsCustomer ? ScreeningAssessment.Hit : ScreeningAssessment.NoHit;
}

/// <summary>A page received by URL for a Case's customer, of one variant, with the truth of every person it names.</summary>
public sealed record ReceivedPage(string CustomerCase, string Variant, string Url, string Content, IReadOnlyList<PagePersonTruth> Persons, PartialDate? PublishedOn = null);

/// <summary>
/// Deterministic generator of the synthetic end-to-end set: invented persons only (names from a fixed table of common
/// characters, made-up numbers and places), written as the bank's .txt / .html export the readers parse. Nothing is read
/// from samples/ or any real data. Same assumptions, same seed: the same files byte for byte.
/// </summary>
public sealed partial class SyntheticCaseGenerator
{
    private readonly SyntheticAssumptions a;
    private readonly Random r;
    private int profileSeq;

    private SyntheticCaseGenerator(SyntheticAssumptions assumptions)
    {
        a = assumptions;
        r = new Random(assumptions.Seed);
    }

    public static IReadOnlyList<SyntheticCase> Generate(SyntheticAssumptions? assumptions = null)
    {
        var g = new SyntheticCaseGenerator(assumptions ?? SyntheticAssumptions.Default);
        var cases = new List<SyntheticCase>();
        foreach (var (kind, n) in g.a.Cases)
            for (var i = 0; i < n; i++) cases.Add(g.Case($"SYN-{kind}-{i + 1:000}", kind));
        return cases;
    }

    // ---- Invented persons ----

    // Character, simplified form, Wade-Giles (passport form), Hanyu Pinyin.
    private static readonly (string Hant, string Hans, string Wg, string Py)[] Surnames =
    [
        ("陳", "陈", "CHEN", "CHEN"), ("林", "林", "LIN", "LIN"), ("黃", "黄", "HUANG", "HUANG"), ("張", "张", "CHANG", "ZHANG"),
        ("吳", "吴", "WU", "WU"), ("劉", "刘", "LIU", "LIU"), ("蔡", "蔡", "TSAI", "CAI"), ("楊", "杨", "YANG", "YANG"),
        ("許", "许", "HSU", "XU"), ("謝", "谢", "HSIEH", "XIE"), ("郭", "郭", "KUO", "GUO"), ("曾", "曾", "TSENG", "ZENG"),
    ];

    private static readonly (string Hant, string Hans, string Wg, string Py)[] MaleGiven =
    [
        ("建", "建", "CHIEN", "JIAN"), ("宏", "宏", "HUNG", "HONG"), ("俊", "俊", "CHUN", "JUN"), ("傑", "杰", "CHIEH", "JIE"),
        ("冠", "冠", "KUAN", "GUAN"), ("宇", "宇", "YU", "YU"), ("偉", "伟", "WEI", "WEI"), ("誠", "诚", "CHENG", "CHENG"),
        ("豪", "豪", "HAO", "HAO"), ("國", "国", "KUO", "GUO"),
    ];

    private static readonly (string Hant, string Hans, string Wg, string Py)[] FemaleGiven =
    [
        ("淑", "淑", "SHU", "SHU"), ("芬", "芬", "FEN", "FEN"), ("怡", "怡", "YI", "YI"), ("君", "君", "CHUN", "JUN"),
        ("佳", "佳", "CHIA", "JIA"), ("穎", "颖", "YING", "YING"), ("雅", "雅", "YA", "YA"), ("婷", "婷", "TING", "TING"),
        ("惠", "惠", "HUI", "HUI"), ("萍", "萍", "PING", "PING"),
    ];

    // Full names used in the repo's own tests and examples are never generated, so no synthetic person shares one.
    private static readonly HashSet<string> Reserved = ["陳志明", "林美玲", "許雅婷", "王大明", "鐘仁聲", "陳建宏"];

    private static readonly (string City, string Letter, string[] Districts)[] Places =
    [
        ("臺北市", "A", ["大安區", "信義區", "中山區"]), ("新北市", "F", ["板橋區", "新莊區", "三重區"]), ("桃園市", "H", ["中壢區", "桃園區"]),
        ("臺中市", "B", ["西屯區", "北屯區"]), ("臺南市", "D", ["東區", "永康區"]), ("高雄市", "E", ["苓雅區", "左營區", "三民區"]),
    ];

    private static readonly string[] Roads = ["中正路", "民生路", "復興路", "光明路", "成功路", "和平路"];

    private sealed record Person(int Surname, int[] Given, Gender Gender, PartialDate Dob, int Place, int District, string Road, int No,
        string NationalId, string Passport)
    {
        public (string Hant, string Hans, string Wg, string Py)[] GivenChars =>
            [.. Given.Select(i => (Gender == Core.Shared.Gender.Male ? MaleGiven : FemaleGiven)[i])];

        public string Hant => Surnames[Surname].Hant + string.Concat(GivenChars.Select(c => c.Hant));
        public string Hans => Surnames[Surname].Hans + string.Concat(GivenChars.Select(c => c.Hans));
        public string SurnameHant => Surnames[Surname].Hant;
        public string RomanGiven => string.Join("-", GivenChars.Select(c => Cap(c.Wg)));
        public string RomanSurname => Cap(Surnames[Surname].Wg);
        public string PinyinGiven => Cap(string.Concat(GivenChars.Select(c => c.Py)));
        public string PinyinSurname => Cap(Surnames[Surname].Py);
        public string BankEnglish => $"{Surnames[Surname].Wg} {string.Join("-", GivenChars.Select(c => c.Wg))}";
        public string City => Places[Place].City;
        public string Address => $"{City}{Places[Place].Districts[District]}{Road}{No}號";

        private static string Cap(string s) => s[..1] + s[1..].ToLowerInvariant();
    }

    private bool P(double p) => r.NextDouble() < p;
    private T Pick<T>(IReadOnlyList<T> xs) => xs[r.Next(xs.Count)];

    private Person NewPerson()
    {
        while (true)
        {
            var gender = P(0.5) ? Gender.Male : Gender.Female;
            var given = new[] { r.Next(10), r.Next(10) };
            if (given[0] == given[1]) continue;
            var place = r.Next(Places.Length);
            var p = new Person(r.Next(Surnames.Length), given, gender, RandomDob(r.Next(1955, 1999)), place, r.Next(Places[place].Districts.Length),
                Pick(Roads), r.Next(1, 300), $"{Places[place].Letter}{(gender == Gender.Male ? 1 : 2)}{r.Next(10_000_000, 99_999_999)}", $"3{r.Next(10_000_000, 99_999_999)}");
            if (!Reserved.Contains(p.Hant)) return p;
        }
    }

    private PartialDate RandomDob(int year) => new(year, r.Next(1, 13), r.Next(1, 29));

    /// <summary>A different person with the same name (or a near one), agreeing by chance at the assumed rates.</summary>
    private Person Stranger(Person c)
    {
        var other = NewPerson();
        var sameName = P(a.StrangerSameFullName);
        var gender = P(a.StrangerSameGender) ? c.Gender : other.Gender == c.Gender ? (c.Gender == Gender.Male ? Gender.Female : Gender.Male) : other.Gender;
        var given = sameName && gender == c.Gender ? c.Given : [c.Given[0], (c.Given[1] + 1 + r.Next(9)) % 10];
        if (given[0] == given[1]) given = [given[0], (given[1] + 1) % 10];
        var dob = P(a.StrangerSameBirthYear) ? (P(a.StrangerSameBirthDay) ? c.Dob : RandomDob(c.Dob.Year)) : other.Dob;
        return other with { Surname = c.Surname, Given = given, Gender = gender, Dob = dob };
    }

    // ---- One Case ----

    private SyntheticCase Case(string id, RecordKind kind)
    {
        var customer = NewPerson();
        var html = P(a.Html);
        var relatedParty = kind == RecordKind.Rca && P(a.RcaRelatedParty);
        var candidates = new List<(CandidateTruth Truth, string File, string? News)>();
        var same = P(a.SamePerson);
        candidates.Add(Candidate(kind, customer, same ? customer : Stranger(customer), same, html));
        if (P(a.SecondStrangerProfile))
            candidates.Add(Candidate(kind, customer, Stranger(customer), false, html));

        var files = new Dictionary<string, string>();
        var (subjectFile, subject) = SubjectFile(customer, relatedParty, html);
        files[relatedParty ? "客戶-關聯人背景資料" + (html ? ".html" : ".txt") : "客戶-背景資料" + (html ? ".html" : ".txt")] = subjectFile;
        if (!relatedParty) files["客戶-聯絡資料.txt"] = $"永久地址(國家)\n地址\n{customer.Address}\n國家\nTaiwan\n";
        files["案件資訊.txt"] = CaseTable(subject.Names[0], relatedParty, candidates.Select(c => c.Truth).ToList());
        foreach (var (truth, file, news) in candidates)
        {
            files[$"名單資料_{truth.ProfileId}" + (html ? ".html" : ".txt")] = file;
            if (news is not null) files[$"新聞資訊_{truth.ProfileId}.txt"] = news;
        }
        return new SyntheticCase(id, html, files, subject, candidates.Select(c => c.Truth).ToList());
    }

    private (string File, ExpectedSubject Subject) SubjectFile(Person c, bool relatedParty, bool html)
    {
        var gender = P(a.CustomerGenderMissing) ? null : (Gender?)c.Gender;
        var dob = P(a.CustomerDobMissing) ? null : (PartialDate?)c.Dob;
        var passport = P(a.CustomerPassport);
        var doc = P(a.CustomerDocumentMissing) ? null : passport ? c.Passport : c.NationalId;
        string G(Gender? x) => x switch { Gender.Male => "男", Gender.Female => "女", _ => "-" };
        string D(PartialDate? d) => d is { } x ? $"{x.Year}/{x.Month}/{x.Day}" : "-";
        if (relatedParty)
        {
            var fields = new (string, string)[]
            {
                ("姓名/名稱", c.Hant), ("性別", G(gender)), ("出生日期", D(dob)), ("國籍", "Taiwan"),
                ("證件種類", passport ? "護照" : "身分證統一編號"), ("證件號碼", doc ?? "-"), ("類型", "個人"),
            };
            var text = string.Concat(fields.Select(f => $"{f.Item1} ：\n{f.Item2}\n"));
            return (html ? Table(fields) : text, new([c.Hant], gender, dob, "TW", doc));
        }
        var labels = new (string, string)[]
        {
            ("客戶號碼", $"C{r.Next(1_000_000, 9_999_999)}"), ("性別", G(gender)), ("客戶名稱", c.BankEnglish), ("出生日期", D(dob)),
            ("證件種類", passport ? "護照" : "身分證統一編號"), ("國籍 1", "Taiwan"), ("發照地", passport ? "[TW] Taiwan" : "-"),
            ("本地名稱", c.Hant), ("證件號碼", doc ?? "-"), ("國籍 2", "-"), ("客戶類型", "個人"), ("職業", "服務業(7)"),
        };
        var file = html
            ? "<html><body><h2>基本資料</h2>" + Table(labels[..10]) + "<h2>詳細資料</h2>" + Table(labels[10..]) + "</body></html>"
            : "基本資料\n" + string.Concat(labels[..10].Select(l => $"{l.Item1}\n{l.Item2}\n")) + "詳細資料\n" + string.Concat(labels[10..].Select(l => $"{l.Item1}\n{l.Item2}\n"));
        return (file, new([c.Hant, c.BankEnglish], gender, dob, "TW", doc));
    }

    private static string Table(IEnumerable<(string Key, string Value)> rows) =>
        "<table>" + string.Concat(rows.Select(x => $"<tr><th>{x.Key}</th><td>{x.Value}</td></tr>")) + "</table>";

    private static string CaseTable(string subjectName, bool relatedParty, IReadOnlyList<CandidateTruth> candidates)
    {
        var sb = new StringBuilder();
        sb.Append("編號\t被偵測到的名字\t資料類別\t關聯方參考號碼\t資料來源\t是否符合名單？\n");
        sb.Append(relatedParty ? $"1\t{subjectName}\t配偶\tR0000001\t姓名/名稱\t-\n" : $"1\t{subjectName}\t客戶\t-\t客戶名稱\t-\n");
        sb.Append("\n疑似符合的名單資料\n");
        sb.Append("編號\t姓名/名稱\t相似度分數\tSDN禁制類別\t名單資料編號\t是否符合名單？\t理由\tIs RCA?\tName Property\n");
        sb.Append("全不符合\t全符合\tPEP\t黑名單\t負面新聞\n");
        var n = 0;
        foreach (var c in candidates)
            for (var row = 0; row < c.CandidateRows; row++)
            {
                var flags = c.Tags.Contains("flags") ? Flags(c) : "\t\t";
                sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"{++n}\t{c.Fields.Names[row % c.Fields.Names.Count]}\t{(row == 0 ? 100 : 95)}\t{(row == 0 ? "DJ PRIMARY" : "DJ ALT")}\t{c.ProfileId}\t-\t-\t-\t{(c.Kind == RecordKind.Rca ? "是" : "不是")}\t{flags}\n");
            }
        return sb.ToString();
    }

    // Consistent with the truth: the record's own type YES (a delisted sanction NO), the others NO.
    private static string Flags(CandidateTruth c)
    {
        string F(bool yes) => yes ? "是" : "否";
        var blacklist = c.Kind is RecordKind.Sanction or RecordKind.WantedList && c.ConditionHolds;
        return $"{F(c.Kind == RecordKind.Pep)}\t{F(blacklist)}\t{F(c.Kind is RecordKind.News or RecordKind.PressRelease or RecordKind.CourtRuling)}";
    }

    // ---- One candidate Profile ----

    private (CandidateTruth, string File, string? News) Candidate(RecordKind kind, Person customer, Person p, bool same, bool html)
    {
        var id = $"7{(int)kind}{++profileSeq:000000}";
        var tags = new List<string>();
        if (!same) tags.Add(p.Hant == customer.Hant ? "stranger:same-name" : "stranger:near-name");
        var c = a.Profile[kind];
        var list = kind is RecordKind.Sanction or RecordKind.Pep or RecordKind.Rca or RecordKind.WantedList;

        // Names: the person's, written the way this record writes them.
        var chinese = new List<string>();
        var roman = new List<(string Given, string Surname)>();
        var alias = new List<string>();
        if (same && list && P(a.NameRomanizedOnly)) tags.Add("name:romanized-only");
        else if (same && list && P(a.NamePinyin)) tags.Add("name:pinyin");
        else if (same && P(a.NameSimplified)) tags.Add("name:simplified");
        else if (same && P(a.NameAlias)) tags.Add("name:alias");
        var tag = tags.FirstOrDefault();
        if (tag == "name:pinyin") roman.Add((p.PinyinGiven, p.PinyinSurname));
        else roman.Add((p.RomanGiven, p.RomanSurname));
        if (tag == "name:simplified") chinese.Add(p.Hans);
        else if (tag == "name:alias")
        {
            var former = NewPerson() with { Surname = p.Surname, Gender = p.Gender };
            chinese.Add(former.Hant);
            alias.Add(p.Hant);
        }
        else if (tag is not ("name:romanized-only" or "name:pinyin")) chinese.Add(p.Hant);

        // Birth date, at the record's precision; a recording error on the same person's record.
        var dobs = new List<PartialDate>();
        if (P(c.Dob))
        {
            var d = p.Dob;
            if (same && P(a.ProfileDobTypo)) { d = new PartialDate(d.Year, d.Month, d.Day == 28 ? 1 : d.Day + 1); tags.Add("dob:typo"); }
            var u = r.NextDouble();
            dobs.Add(u < c.DobDay ? d : u < c.DobDay + c.DobMonth ? new PartialDate(d.Year, d.Month) : new PartialDate(d.Year));
            tags.Add("dob:" + dobs[0].Precision.ToString().ToLowerInvariant());
        }
        var gender = P(c.Gender) ? p.Gender : (Gender?)null;
        var citizenship = P(c.Citizenship) ? new List<string> { "Taiwan" } : [];

        // Document: national ID, or a passport, which a renewal replaces with a new number.
        var documents = new List<(string Type, string Number)>();
        if (P(c.Document))
        {
            if (P(0.5)) documents.Add(("National ID", p.NationalId));
            else
            {
                var renewed = same && P(a.PassportRenewed);
                documents.Add(("Passport", renewed ? $"3{r.Next(10_000_000, 99_999_999)}" : p.Passport));
                if (renewed) tags.Add("document:renewed");
            }
        }
        var address = P(c.Address) ? (same && P(a.OldAddress) ? $"{Pick(Roads)}{r.Next(1, 300)}號" : $"{p.Road}{p.No}號") : null;

        // Source condition.
        var (category, sub) = kind switch
        {
            RecordKind.Sanction => ("Sanctions Lists", (string?)null),
            RecordKind.Pep => ("Politically Exposed Person (PEP)", P(a.FormerPep) ? "Former PEP" : null),
            RecordKind.Rca => ("Relative or Close Associate (RCA)", "Spouse of PEP"),
            RecordKind.WantedList => ("Other Official List", null),
            _ => ("Special Interest Person (SIP)", "Fraud"),
        };
        if (sub == "Former PEP") tags.Add("pep:former");
        var delisted = kind == RecordKind.Sanction && P(a.SanctionDelisted);
        if (delisted) tags.Add("sanction:delisted");
        string? official = kind switch
        {
            RecordKind.Sanction => $"Sample Ministry of Finance\tSample Sanctions List\t{(delisted ? "Delisted" : "Active")}\t2024/5/1\t-",
            RecordKind.WantedList => "Sample Police Agency\tWanted Persons List\tActive\t2025/3/1\tFraud",
            _ => null,
        };
        var roles = new List<string>();
        if (kind == RecordKind.Pep)
        {
            var from = new PartialDate(p.Dob.Year + 30 + r.Next(15), 2);
            var former = sub is not null;
            roles.Add($"1\t立法委員\t立法院\t{from.Year}/{from.Month}\t{(former ? $"{from.Year + 4}/1" : "")}");
        }
        var relationships = kind == RecordKind.Rca ? new[] { $"1\t7999{r.Next(1000, 9999)}\t{NewPerson().Hant}\tPerson\tHusband" } : [];
        var flags = P(a.FlagsSet);
        if (flags) tags.Add("flags");

        var listType = kind switch
        {
            RecordKind.Sanction => ListType.Sanction,
            RecordKind.Pep => ListType.Pep,
            RecordKind.Rca => ListType.Rca,
            RecordKind.WantedList => ListType.OfficialWantedList,
            _ => ListType.AdverseMedia,
        };
        LegalStage? stage = kind == RecordKind.WantedList ? LegalStage.Wanted : null;
        string? news = null;
        PartialDate? published = null;
        if (!list)
        {
            stage = Pick(a.Stages[kind]);
            published = new PartialDate(2025, r.Next(1, 13), r.Next(1, 29));
            news = NewsFile(kind, p, stage.Value, published.Value, tags);
        }

        var names = chinese.Concat(roman.Select(x => $"{x.Surname} {x.Given}")).Concat(alias).ToList();
        var file = ProfileFile(id, gender, chinese, roman, alias, citizenship, address, dobs, documents, roles, relationships, category, sub, official, list, html);
        var expected = new ExpectedProfile(names, gender, dobs, citizenship.Select(_ => "TW").ToList(), documents.Select(d => d.Number).ToList(),
            delisted ? "Delisted" : official is null ? null : "Active", published);
        var truth = new CandidateTruth(id, kind, same, listType, !delisted, stage, tags, expected, names.Count > 1 ? 2 : 1);
        return (truth, file, news);
    }

    private static string ProfileFile(string id, Gender? gender, List<string> chinese, List<(string Given, string Surname)> roman, List<string> alias,
        List<string> citizenship, string? address, List<PartialDate> dobs, List<(string Type, string Number)> documents, List<string> roles,
        string[] relationships, string category, string? sub, string? official, bool list, bool html)
    {
        var scalars = new List<(string, string)> { ("Profile_ID", id), ("記錄類型", "Person") };
        if (gender is { } g) scalars.Add(("性別", g == Gender.Male ? "Male" : "Female"));
        scalars.Add(("已故?", "False"));
        scalars.Add(("狀態", "Active"));
        if (!list) scalars.Add(("關鍵字", "Fraud"));

        string D(PartialDate d) => d.Precision switch { DatePrecision.Day => $"{d.Year}/{d.Month}/{d.Day}", DatePrecision.Month => $"{d.Year}/{d.Month}", _ => $"{d.Year}" };
        var tables = new List<(string Key, string Header, List<string> Rows)>
        {
            ("主要名稱", "編號\t名字\t姓", [.. roman.Select((n, i) => $"{i + 1}\t{n.Given}\t{n.Surname}"), .. chinese.Select(n => $"OSN\t{n}")]),
        };
        if (alias.Count > 0) tables.Add(("拼寫變化", "編號\t名字\t姓", [.. alias.Select(n => $"OSN\t{n}")]));
        if (citizenship.Count > 0) tables.Add(("國家資料", "編號\t類型\t國家", ["1\tCitizenship\tTaiwan"]));
        if (address is not null) tables.Add(("地址", "編號\t地址\t城市\t國家", [$"1\t{address}\t-\tTaiwan"]));
        if (dobs.Count > 0) tables.Add(("日期", "編號\t類型\t日期", [.. dobs.Select((d, i) => $"{i + 1}\tDate of Birth\t{D(d)}")]));
        if (documents.Count > 0) tables.Add(("身分證明", "編號\t類型\t號碼\t國家", [.. documents.Select((d, i) => $"{i + 1}\t{d.Type}\t{d.Number}\tTaiwan")]));
        if (roles.Count > 0) tables.Add(("職位", "編號\t職位\t組織\t起始日期\t結束日期", roles));
        if (relationships.Length > 0) tables.Add(("關係人", "編號\tProfile_ID\t姓名\t類型\t關係", [.. relationships]));
        tables.Add(("類別", "編號\t描述1\t描述2", [$"1\t{category}\t{sub ?? ""}"]));
        if (official is not null) tables.Add(("名單資訊", "機關\t名單\t狀態\t決定日期\t罪名", [official]));

        if (!html)
            return string.Concat(scalars.Select(s => $"{s.Item1}:\t{s.Item2}\n"))
                + string.Concat(tables.Select(t => $"{t.Key}:\n{t.Header}\n" + string.Concat(t.Rows.Select(row => row + "\n"))));
        static string Row(string row, string cell) => "<tr>" + string.Concat(row.Split('\t').Select(x => $"<{cell}>{x}</{cell}>")) + "</tr>";
        return "<html><body>" + Table(scalars)
            + string.Concat(tables.Select(t => $"<h3>{t.Key}</h3><table>{Row(t.Header, "th")}{string.Concat(t.Rows.Select(row => Row(row, "td")))}</table>"))
            + "</body></html>";
    }

    // ---- Attached texts: templated prose of a news report, a prosecutors' press release, a court release or judgment ----

    private static readonly string[] Numerals = ["一", "二", "三", "四", "五"];

    private string NewsFile(RecordKind kind, Person p, LegalStage stage, PartialDate published, List<string> tags)
    {
        var t = a.Text[kind];
        var age = AgeAt(p.Dob, published) + (P(a.StatedAgeOffByOne) ? 1 : 0);
        var g = p.Gender == Gender.Male ? "男" : "女";
        var statesAge = P(t.Age);
        var statesGender = P(t.Gender);
        var statesBirth = P(t.BirthDate);
        var bracket = (statesGender, statesAge) switch
        {
            (true, true) => $"（{g}，{age}歲）",
            (false, true) => $"（{age}歲）",
            (true, false) => $"（{g}）",
            _ => "",
        };
        if (statesAge) tags.Add("text:age");
        if (statesGender) tags.Add("text:gender");
        var masked = kind == RecordKind.PressRelease && P(a.MaskedNameInRelease);
        if (masked) tags.Add("text:masked-name");
        var name = masked ? $"{p.SurnameHant}姓{(p.Gender == Gender.Male ? "男子" : "女子")}" : p.Hant;
        var who = name + bracket;
        var other = NewPerson().Hant;
        var multi = P(a.MultiPersonText);
        var sameSentence = multi && P(a.OtherInSameSentence);
        if (multi) tags.Add(sameSentence ? "text:other-same-sentence" : "text:other-next-sentence");
        var city = p.City[..2];
        var term = $"有期徒刑{Pick(Numerals)}年";

        string body;
        if (kind == RecordKind.CourtRuling && P(0.5))
        {
            tags.Add("text:judgment");
            var roc = statesBirth ? $" 民國{p.Dob.Year - 1911}年{p.Dob.Month}月{p.Dob.Day}日生" : "";
            if (statesBirth) tags.Add("text:birth-date");
            var gLine = statesGender ? " " + g : "";
            var order = stage == LegalStage.Convicted ? $"{name}犯詐欺取財罪，處{term}。" : $"{name}無罪。";
            var otherOrder = stage == LegalStage.Convicted ? $"{other}無罪。" : $"{other}犯詐欺取財罪，處{term}。";
            body = $"臺灣{city}地方法院刑事判決\n114年度訴字第{r.Next(100, 999)}號\n公訴人 臺灣{city}地方檢察署檢察官\n被告 {name}{gLine}{roc}\n住{city}市○○區○○路{r.Next(1, 99)}號\n"
                + (multi ? $"被告 {other}\n" : "")
                + "上列被告因詐欺案件，經檢察官提起公訴，本院判決如下：\n主 文\n" + order + "\n" + (multi ? otherOrder + "\n" : "")
                + $"理 由\n一、公訴意旨略以：被告{name}以虛偽交易詐取貨款。";
        }
        else
        {
            var head = kind switch
            {
                RecordKind.News => $"〔本報記者／{city}報導〕",
                RecordKind.PressRelease => $"{city}地方檢察署新聞稿\n",
                _ => $"{city}地方法院新聞稿\n",
            };
            var (sentence, otherOutcome) = (kind, stage) switch
            {
                (RecordKind.News, LegalStage.Alleged) => ($"{who}涉嫌以投資名義向親友詐取款項，{city}市警察局日前將{name}約談到案，依詐欺罪嫌移送{city}地檢署偵辦", "亦遭約談"),
                (RecordKind.News, LegalStage.Indicted) => ($"{who}涉嫌提供人頭帳戶協助洗錢，{city}地檢署偵查終結，今天依洗錢防制法將{name}起訴", "則獲不起訴處分"),
                (RecordKind.News, LegalStage.Convicted) => ($"{who}涉嫌侵占公司貨款，{city}地方法院今天依業務侵占罪判處{name}{term}", "則獲判無罪"),
                (RecordKind.News, _) => ($"{who}被控詐取工程款，{city}地方法院審理後認為罪證不足，判決{name}無罪", $"則被判處{term}"),
                (RecordKind.PressRelease, LegalStage.Alleged) => ($"本署指揮{city}市政府警察局偵辦詐欺集團案，今日搜索{who}住處，並將{name}拘提到案，全案持續偵辦中", "亦經拘提到案"),
                (RecordKind.PressRelease, LegalStage.Indicted) => ($"本署偵辦{who}涉嫌詐欺案件，業已偵查終結，認{name}犯罪嫌疑重大，依刑法詐欺取財罪嫌提起公訴", "因罪嫌不足，予以不起訴處分"),
                (RecordKind.PressRelease, _) => ($"本署偵辦{who}被訴背信案件，業已偵查終結，因犯罪嫌疑不足，對{name}為不起訴處分", "則經提起公訴"),
                (_, LegalStage.Convicted) => ($"本院審理{who}詐欺案件，今日宣判，判處{name}{term}", "則獲判無罪"),
                _ => ($"本院審理{who}被訴洗錢案件，今日宣判，{name}無罪", $"則被判處{term}"),
            };
            body = head + sentence + (sameSentence ? $"，同案的{other}{otherOutcome}。" : "。") + (multi && !sameSentence ? $"同案被告{other}{otherOutcome}。" : "");
        }
        var publisher = kind switch { RecordKind.News => "範例日報", RecordKind.PressRelease => $"{city}地方檢察署", _ => $"{city}地方法院" };
        return $"{r.Next(1, 99)}\n社會\n{city}詐欺案進度\n本報訊\n{body.Length} 字\n{published.Year} 年 {published.Month} 月 {published.Day} 日\n{publisher}\n{body}\n文件 SYN{r.Next(100000, 999999)}\n";
    }

    // Age on a day, both dates full: one year less while the birthday is still to come. Computed here, not by the product.
    private static int AgeAt(PartialDate dob, PartialDate at) =>
        at.Year - dob.Year - ((at.Month!.Value, at.Day!.Value).CompareTo((dob.Month!.Value, dob.Day!.Value)) < 0 ? 1 : 0);

    // ---- The paths not built yet: their source documents with truth, generated and not run ----

    /// <summary>
    /// For the first <paramref name="customers"/> Cases' customers: a news page received by URL (HTML), a published
    /// judgment with masked names (王○明), and a grounding-search result set (relevant pages, same-name strangers, pages
    /// about nobody), each with the persons it names and the truth. Generated only: the paths are not built.
    /// </summary>
    public static IReadOnlyList<ExternalFixture> External(IReadOnlyList<SyntheticCase> cases, int customers = 20, int seed = 20261126)
    {
        var g = new SyntheticCaseGenerator(SyntheticAssumptions.Default with { Seed = seed });
        var result = new List<ExternalFixture>();
        foreach (var c in cases.Take(customers))
        {
            var name = c.Subject.Names[0];
            var masked = name[0] + "○" + name[2..];
            var stranger = g.NewPerson().Hant;
            var stage = g.Pick([LegalStage.Alleged, LegalStage.Indicted, LegalStage.Convicted]);
            var page = $"<html><head><title>詐欺案進度</title></head><body><article><h1>投資詐欺案移送偵辦</h1><p>〔本報記者／臺北報導〕{name}涉嫌以投資名義詐取款項，警方約談到案後依詐欺罪嫌移送地檢署偵辦。</p><p>同案的{stranger}亦遭約談。</p></article></body></html>";
            result.Add(new("RECEIVED_URL", c.Id, "NEWS", $"https://news.example.test/articles/{g.r.Next(100000, 999999)}", page,
                [(name, true, LegalStage.Alleged), (stranger, false, LegalStage.Alleged)]));
            var verdict = stage == LegalStage.Convicted ? $"{masked}犯詐欺取財罪，處有期徒刑一年。" : $"{masked}無罪。";
            var judgment = $"臺灣臺北地方法院刑事判決\n114年度易字第{g.r.Next(100, 999)}號\n公訴人 臺灣臺北地方檢察署檢察官\n被告 {masked} 男 民國70年1月1日生\n住臺北市○○區○○路○號\n上列被告因詐欺案件，經檢察官提起公訴，本院判決如下：\n主 文\n{verdict}\n";
            result.Add(new("PERIODIC_JUDGMENT", c.Id, "JUDGMENT", $"https://judgment.example.test/JY/{g.r.Next(100000, 999999)}", judgment,
                [(masked, false, stage == LegalStage.Convicted ? LegalStage.Convicted : LegalStage.Acquitted)])); // masked: never a candidate by itself
            result.Add(new("GROUNDING_SEARCH", c.Id, "SEARCH_RESULT", $"https://news.example.test/search/{c.Id}/1",
                $"{name}涉詐欺遭起訴 | 範例日報", [(name, true, LegalStage.Indicted)]));
            result.Add(new("GROUNDING_SEARCH", c.Id, "SEARCH_RESULT", $"https://news.example.test/search/{c.Id}/2",
                $"同名不同人：{name}（另一位）獲頒服務獎 | 示範新聞網", [(name, false, LegalStage.Unknown)]));
            result.Add(new("GROUNDING_SEARCH", c.Id, "SEARCH_RESULT", $"https://news.example.test/search/{c.Id}/3",
                "地方新聞：社區活動報導 | 示範新聞網", []));
        }
        return result;
    }

    /// <summary>
    /// Every received page of scenario 2 with the truth of each person it names: the 20 pages of <see cref="External"/>
    /// (the customer after the dateline, another person after 同案的, both suspects), and 20 more written for what those
    /// cannot show, 4 of each variant, for customers of the Cases after the first 20 that have a full birth date and a gender
    /// (a generator of their own, seed 20261127, so the set and <see cref="External"/> stay as they were):
    /// VICTIM and WITNESS (the customer named by 被害人 or 證人 beside another person who is the suspect),
    /// STRANGER_DOB_FAR and STRANGER_DOB_NEAR (a suspect with the customer's very name and gender whose birth date, written
    /// next to the name as 民國, is 3 to 15 years, or exactly one year, from the customer's), and CUSTOMER_DOB (the customer
    /// as a suspect, the birth date written next to the name being the customer's own); then the 24 pages stating an age
    /// (<see cref="AgePages"/>).
    /// </summary>
    public static IReadOnlyList<ReceivedPage> ReceivedPages(IReadOnlyList<SyntheticCase> cases)
    {
        var pages = External(cases).Where(f => f.Scenario == "RECEIVED_URL")
            .Select(f => new ReceivedPage(f.CustomerCase, "CUSTOMER_AND_CO_NAMED", f.Url, f.Content, [.. f.Persons.Select(p => new PagePersonTruth(p.WrittenName, p.IsCustomer, true, p.Stage))]))
            .ToList();
        var g = new SyntheticCaseGenerator(SyntheticAssumptions.Default with { Seed = 20261127 });
        string[] variants = ["VICTIM", "WITNESS", "STRANGER_DOB_FAR", "STRANGER_DOB_NEAR", "CUSTOMER_DOB"];
        var customers = cases.Skip(20).Where(c => c.Subject is { Dob.Precision: DatePrecision.Day, Gender: not null } && c.Subject.Names[0].Length == 3).Take(4 * variants.Length).ToList();
        foreach (var (c, i) in customers.Select((c, i) => (c, i)))
        {
            var (name, dob, sex) = (c.Subject.Names[0], c.Subject.Dob!.Value, c.Subject.Gender == Gender.Male ? "男" : "女");
            var other = g.NewPerson().Hant;
            var variant = variants[i % variants.Length];
            var shift = variant switch { "STRANGER_DOB_FAR" => (g.P(0.5) ? 1 : -1) * g.r.Next(3, 16), "STRANGER_DOB_NEAR" => g.P(0.5) ? 1 : -1, _ => 0 };
            var stated = shift == 0 ? dob : new PartialDate(dob.Year + shift, dob.Month, Math.Min(dob.Day!.Value, 28));
            var roc = $"民國{stated.Year - 1911}年{stated.Month}月{stated.Day}日生";
            var (lead, persons) = variant switch
            {
                "VICTIM" => ($"〔本報記者／臺北報導〕{other}涉嫌以投資名義詐取款項，被害人{name}報案後，警方約談到案依詐欺罪嫌移送地檢署偵辦。",
                    new PagePersonTruth[] { new(other, false, true, LegalStage.Alleged), new(name, true, false, LegalStage.Unknown) }),
                "WITNESS" => ($"〔本報記者／臺中報導〕{other}涉嫌詐欺，遭臺中地檢署提起公訴，證人{name}出庭作證。",
                    [new(other, false, true, LegalStage.Indicted), new(name, true, false, LegalStage.Unknown)]),
                _ => ($"〔本報記者／高雄報導〕被告{name}（{sex}，{roc}）涉嫌詐欺，遭高雄地檢署提起公訴。", [new(name, variant == "CUSTOMER_DOB", true, LegalStage.Indicted)]),
            };
            var page = $"<html><head><title>詐欺案進度</title></head><body><article><h1>詐欺案追蹤</h1><p>{lead}</p></article></body></html>";
            pages.Add(new(c.Id, variant, $"https://news.example.test/variants/{g.r.Next(100000, 999999)}", page, persons));
        }
        pages.AddRange(AgePages(customers.Count, cases));
        return pages;
    }

    /// <summary>
    /// 24 pages stating an age next to the name, 8 of each variant, for the next customers of the same kind (a generator of
    /// its own, seed 20261128, so the pages above stay as they were): AGE_CUSTOMER (the customer as a suspect, the age at the
    /// publication date, one year off at <see cref="SyntheticAssumptions.StatedAgeOffByOne"/>), AGE_STRANGER_NEAR and
    /// AGE_STRANGER_FAR (a suspect with the customer's very name whose age differs by 1 to 2, or 3 to 15, years). The
    /// publication date (within three years before AsOf) is in the metadata, on a date line under the title, or nowhere, at
    /// <see cref="SyntheticAssumptions.PagePublishedInMetadata"/> and <see cref="SyntheticAssumptions.PagePublishedInText"/>.
    /// </summary>
    private static List<ReceivedPage> AgePages(int skip, IReadOnlyList<SyntheticCase> cases)
    {
        var g = new SyntheticCaseGenerator(SyntheticAssumptions.Default with { Seed = 20261128 });
        string[] variants = ["AGE_CUSTOMER", "AGE_STRANGER_NEAR", "AGE_STRANGER_FAR"];
        var customers = cases.Skip(20).Where(c => c.Subject is { Dob.Precision: DatePrecision.Day, Gender: not null } && c.Subject.Names[0].Length == 3)
            .Skip(skip).Take(8 * variants.Length).ToList();
        var pages = new List<ReceivedPage>();
        foreach (var (c, i) in customers.Select((c, i) => (c, i)))
        {
            var name = c.Subject.Names[0];
            var variant = variants[i % variants.Length];
            var on = g.a.AsOf.AddDays(-g.r.Next(1, 3 * 365));
            var published = new PartialDate(on.Year, on.Month, on.Day);
            var age = AgeAt(c.Subject.Dob!.Value, published);
            age += variant switch
            {
                "AGE_STRANGER_NEAR" => (g.P(0.5) ? 1 : -1) * g.r.Next(1, 3),
                "AGE_STRANGER_FAR" => (g.P(0.5) ? 1 : -1) * g.r.Next(3, 16),
                _ => g.P(g.a.StatedAgeOffByOne) ? (g.P(0.5) ? 1 : -1) : 0,
            };
            var where = g.r.NextDouble();
            var (meta, line, stated) = where < g.a.PagePublishedInMetadata ? ($"<meta property=\"article:published_time\" content=\"{on.Year}-{on.Month:D2}-{on.Day:D2}T09:00:00+08:00\">", "", (PartialDate?)published)
                : where < g.a.PagePublishedInMetadata + g.a.PagePublishedInText
                    ? ("", $"<p>{(g.P(0.5) ? $"{on.Year}/{on.Month:D2}/{on.Day:D2} 09:00" : $"民國{on.Year - 1911}年{on.Month}月{on.Day}日")}</p>", published)
                    : ("", "", null);
            var page = $"<html><head><title>詐欺案進度</title>{meta}</head><body><article><h1>詐欺案追蹤</h1>{line}<p>〔本報記者／高雄報導〕被告{name}（{age}歲）涉嫌詐欺，遭高雄地檢署提起公訴。</p></article></body></html>";
            pages.Add(new(c.Id, variant, $"https://news.example.test/ages/{g.r.Next(100000, 999999)}", page,
                [new(name, variant == "AGE_CUSTOMER", true, LegalStage.Indicted)], stated));
        }
        return pages;
    }

    /// <summary>A short stable digest of the whole set, for the determinism test.</summary>
    public static string Digest(IReadOnlyList<SyntheticCase> cases)
    {
        var sb = new StringBuilder();
        foreach (var c in cases)
            foreach (var (name, text) in c.Files.OrderBy(f => f.Key, StringComparer.Ordinal))
                sb.Append(c.Id).Append('/').Append(name).Append('\n').Append(text);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16];
    }
}
