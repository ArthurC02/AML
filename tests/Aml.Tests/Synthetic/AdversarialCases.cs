using Aml.Core.Screening;
using Aml.Core.Shared;

namespace Aml.Tests.Synthetic;

/// <summary>
/// One kind of the adversarial set: a stranger a document rule could wrongly MATCH, or the customer in a hard form.
/// Realistic: what real lists and customer records show. Otherwise a theoretical limit: built to probe the rule, not
/// expected in real data (the description says why). The count is a probe size, not a frequency.
/// </summary>
public sealed record AdversarialKind(string Code, bool SamePerson, int Count, bool Realistic, string Description);

/// <summary>
/// The adversarial synthetic set (docs/plan/poc-delivery-plan.md#adversarial-synthetic-set): pairs built for the failure
/// modes of an automatic MATCH on a document, one Sanction Profile per Case, written as the bank's .txt export, truth by
/// construction. Its own seed and its own Cases, so the end-to-end set above stays byte for byte as it was. Taiwan national
/// IDs here are format-valid (letter, gender digit 1 or 2, seven digits, check digit); a Taiwan passport number is nine digits.
/// </summary>
public sealed partial class SyntheticCaseGenerator
{
    public const int AdversarialSeed = 20261129;

    public static readonly IReadOnlyList<AdversarialKind> AdversarialKinds =
    [
        new("SAME_NAME_FULL_DOB", false, 15, true, "same name, the same full birth date; no list document"),
        new("SAME_NAME_BIRTH_YEAR", false, 15, true, "same name, the list gives the customer's birth year only"),
        new("JAN1_LIST_SIDE", false, 15, true, "same name, list birth date 1 January of the customer's year (placeholder), the customer's is another day"),
        new("JAN1_BOTH_SIDES", false, 15, true, "same name, 1 January of the same year on both sides"),
        new("NUMBER_OTHER_TYPE", false, 15, false, "same name, the customer's passport number on the list as a Taiwan national ID: impossible by format (nine digits against a letter and nine digits)"),
        new("NUMBER_OTHER_COUNTRY", false, 15, false, "same name, the customer's passport number on the list as a Japanese passport, no list citizenship: impossible by format (a Japanese passport is two letters and seven digits); a collision with a nine-digit foreign format is unverified"),
        new("NUMBER_ONE_OFF", false, 15, true, "same name, another valid national ID one body digit off (and its own check digit)"),
        new("NUMBER_TRANSPOSED", false, 15, true, "same name, another valid national ID with two adjacent body digits swapped (and its own check digit)"),
        new("RECYCLED_WITH_DOB_GENDER", false, 10, false, "same name, same passport (type, country, number) held by another person; list birth date 3 to 15 years off and the opposite gender: reuse of a Taiwan passport number is unverified"),
        new("RECYCLED_BARE", false, 10, false, "same name, same passport (type, country, number) held by another person; the list carries no birth date and no gender: reuse unverified"),
        new("RECYCLED_SAME_YEAR", false, 10, false, "same name and gender, same passport (type, country, number) held by another person; the list gives the customer's birth year only: reuse unverified"),
        new("RELATIVE_FAR", false, 10, true, "same surname and gender, one given character differs, same address; born 3 or more years apart"),
        new("RELATIVE_NEAR", false, 10, true, "same surname and gender, one given character differs, same address; born one calendar year apart"),
        new("TWIN", false, 15, true, "same surname and gender, one given character differs, same address, same birth date"),
        new("SAME_NAME_DOB_ADDRESS", false, 15, false, "same name, gender, birth date and address, another person; no list document: not expected (twins are not given one name)"),
        new("SAME_NAME_ADDRESS", false, 10, true, "same name and address; no list birth date, no list document"),
        new("NUMBER_NO_COUNTRY", false, 10, true, "same name, same passport number held by another person, no issuing country on either side: lists often omit the country, and a nine-digit number can collide across issuers"),
        new("C_NUMBER_TYPO", true, 15, true, "the customer: national ID one digit off (8) or two digits swapped (7) on the list, failing the check digit; birth date agrees"),
        new("C_NAME_FORM", true, 15, true, "the customer: same national ID, the list name in Wade-Giles only, Hanyu Pinyin only or simplified script only (5 each)"),
        new("C_NO_LIST_DOB", true, 15, true, "the customer: same national ID, no list birth date"),
        new("C_LIST_DOB_YEAR", true, 15, true, "the customer: same national ID, list birth date at year precision"),
        new("C_PASSPORT_RENEWED", true, 15, true, "the customer: the list holds the old passport number; birth date agrees"),
        new("C_DOB_WITHIN_TWO", true, 15, true, "the customer: same national ID, list birth date one year off (8) or 1 to 5 days off (7)"),
        new("C_DOB_BEYOND_TWO", true, 15, true, "the customer: same national ID, list birth date 3 to 15 years off (data-entry error)"),
        new("C_NUMBER_NO_COUNTRY", true, 10, true, "the customer: same passport number, no issuing country on either side; birth date agrees"),
    ];

    // Letter codes of the Taiwan national ID, 10 to 35 in this order.
    private const string IdLetters = "ABCDEFGHJKLMNPQRSTUVXYWZIO";

    private static char IdCheckDigit(string id)
    {
        var code = IdLetters.IndexOf(id[0], StringComparison.Ordinal) + 10;
        var sum = code / 10 + code % 10 * 9;
        for (var i = 1; i <= 8; i++) sum += (id[i] - '0') * (9 - i);
        return (char)('0' + (10 - sum % 10) % 10);
    }

    /// <summary>A Taiwan national ID of valid form: letter, gender digit 1 or 2, seven digits, check digit.</summary>
    public static bool IsValidNationalId(string id) =>
        id.Length == 10 && IdLetters.Contains(id[0], StringComparison.Ordinal) && id[1] is '1' or '2' && id[2..].All(char.IsAsciiDigit) && IdCheckDigit(id) == id[9];

    private static string WithCheckDigit(string id) => id[..9] + IdCheckDigit(id);

    /// <summary>The adversarial Cases, kind by kind in <see cref="AdversarialKinds"/> order, one candidate each.</summary>
    public static IReadOnlyList<SyntheticCase> Adversarial()
    {
        var g = new SyntheticCaseGenerator(SyntheticAssumptions.Default with { Seed = AdversarialSeed });
        var cases = new List<SyntheticCase>();
        foreach (var k in AdversarialKinds)
            for (var i = 0; i < k.Count; i++) cases.Add(g.AdversarialCase($"ADV-{k.Code}-{i + 1:000}", k, i));
        return cases;
    }

    private sealed record ListSide(List<string> Chinese, List<(string Given, string Surname)> Roman, Gender? Gender, List<PartialDate> Dobs,
        bool Citizenship, string? Address, List<(string Type, string Number, string Country)> Documents);

    private SyntheticCase AdversarialCase(string id, AdversarialKind kind, int i)
    {
        var form = i % 3; // C_NAME_FORM: Wade-Giles, Pinyin, simplified
        var c = NewPerson();
        while (kind.Code == "JAN1_LIST_SIDE" && c.Dob is { Month: 1, Day: 1 }
            || kind.Code == "C_NAME_FORM" && (form < 2 && Spelled(c.PinyinSurname + c.PinyinGiven) == Spelled(c.RomanSurname + c.RomanGiven) || form == 2 && c.Hans == c.Hant))
            c = NewPerson();
        if (kind.Code == "JAN1_BOTH_SIDES") c = c with { Dob = new PartialDate(c.Dob.Year, 1, 1) };
        c = c with { NationalId = WithCheckDigit(c.NationalId) };

        var nationalId = ("身分證統一編號", c.NationalId, "-");
        var passport = ("護照", c.Passport, "[TW] Taiwan");
        var doc = nationalId;
        var tags = new List<string> { "adv:" + kind.Code };
        var list = new ListSide([c.Hant], [(c.RomanGiven, c.RomanSurname)], c.Gender, [], true, null, []);
        PartialDate Shift(int years) => new(c.Dob.Year + years, c.Dob.Month, c.Dob.Day);
        int Sign() => P(0.5) ? 1 : -1;
        ListSide Related(PartialDate dob) => list with { Chinese = [Relative(c).Hant], Roman = [], Dobs = [dob], Address = c.Address };

        switch (kind.Code)
        {
            case "SAME_NAME_FULL_DOB": list = list with { Dobs = [c.Dob] }; break;
            case "SAME_NAME_BIRTH_YEAR": list = list with { Dobs = [new(c.Dob.Year)] }; break;
            case "JAN1_LIST_SIDE" or "JAN1_BOTH_SIDES": list = list with { Dobs = [new(c.Dob.Year, 1, 1)] }; break;
            case "NUMBER_OTHER_TYPE": doc = passport; list = list with { Documents = [("National ID", c.Passport, "Taiwan")] }; break;
            case "NUMBER_OTHER_COUNTRY": doc = passport; list = list with { Citizenship = false, Documents = [("Passport", c.Passport, "Japan")] }; break;
            case "NUMBER_ONE_OFF": list = list with { Documents = [("National ID", WithCheckDigit(OneOff(c.NationalId, 2, 8)), "Taiwan")] }; break;
            case "NUMBER_TRANSPOSED": list = list with { Documents = [("National ID", WithCheckDigit(Transposed(c.NationalId, 2, 8)), "Taiwan")] }; break;
            case "RECYCLED_WITH_DOB_GENDER":
                doc = passport;
                list = list with { Gender = c.Gender == Gender.Male ? Gender.Female : Gender.Male, Dobs = [Shift(Sign() * r.Next(3, 16))], Documents = [("Passport", c.Passport, "Taiwan")] };
                break;
            case "RECYCLED_BARE": doc = passport; list = list with { Gender = null, Documents = [("Passport", c.Passport, "Taiwan")] }; break;
            case "RECYCLED_SAME_YEAR": doc = passport; list = list with { Dobs = [new(c.Dob.Year)], Documents = [("Passport", c.Passport, "Taiwan")] }; break;
            // A parent 20 to 35 years older, or a sibling 3 to 8 years apart; a sibling one calendar year apart; a twin.
            case "RELATIVE_FAR": list = Related(new(c.Dob.Year + (P(0.5) ? -r.Next(20, 36) : Sign() * r.Next(3, 9)), r.Next(1, 13), r.Next(1, 29))); break;
            case "RELATIVE_NEAR": list = Related(new(c.Dob.Year + Sign(), r.Next(1, 13), r.Next(1, 29))); break;
            case "TWIN": list = Related(c.Dob); break;
            case "SAME_NAME_DOB_ADDRESS": list = list with { Dobs = [c.Dob], Address = c.Address }; break;
            case "SAME_NAME_ADDRESS": list = list with { Address = c.Address }; break;
            case "NUMBER_NO_COUNTRY": doc = ("護照", c.Passport, "-"); list = list with { Documents = [("Passport", c.Passport, "-")] }; break;
            case "C_NUMBER_TYPO":
                tags.Add(i % 2 == 0 ? "typo:one-off" : "typo:transposed");
                // An entry error: the list number fails the check digit (a typo giving another valid ID is the stranger kind).
                var typo = c.NationalId;
                while (IsValidNationalId(typo)) typo = i % 2 == 0 ? OneOff(c.NationalId, 2, 9) : Transposed(c.NationalId, 2, 9);
                list = list with { Dobs = [c.Dob], Documents = [("National ID", typo, "Taiwan")] };
                break;
            case "C_NAME_FORM":
                tags.Add(form switch { 0 => "name:wade-giles-only", 1 => "name:pinyin-only", _ => "name:simplified-only" });
                list = list with
                {
                    Chinese = form == 2 ? [c.Hans] : [],
                    // Wade-Giles as a list writes it, the syllables apart (Chien Hung), not the bank's hyphenated CHIEN-HUNG.
                    Roman = form switch { 0 => [(c.RomanGiven.Replace('-', ' '), c.RomanSurname)], 1 => [(c.PinyinGiven, c.PinyinSurname)], _ => [] },
                    Dobs = [c.Dob],
                    Documents = [("National ID", c.NationalId, "Taiwan")],
                };
                break;
            case "C_NO_LIST_DOB": list = list with { Documents = [("National ID", c.NationalId, "Taiwan")] }; break;
            case "C_LIST_DOB_YEAR": list = list with { Dobs = [new(c.Dob.Year)], Documents = [("National ID", c.NationalId, "Taiwan")] }; break;
            case "C_PASSPORT_RENEWED":
                doc = passport;
                var old = c.Passport;
                while (old == c.Passport) old = $"3{r.Next(10_000_000, 99_999_999)}";
                list = list with { Dobs = [c.Dob], Documents = [("Passport", old, "Taiwan")] };
                break;
            case "C_DOB_WITHIN_TWO":
                tags.Add(i % 2 == 0 ? "dob:year-off" : "dob:days-off");
                var day = c.Dob.Day!.Value;
                list = list with { Dobs = [i % 2 == 0 ? Shift(Sign()) : new(c.Dob.Year, c.Dob.Month, day > 5 ? day - r.Next(1, 6) : day + r.Next(1, 6))], Documents = [("National ID", c.NationalId, "Taiwan")] };
                break;
            case "C_DOB_BEYOND_TWO": list = list with { Dobs = [Shift(Sign() * r.Next(3, 16))], Documents = [("National ID", c.NationalId, "Taiwan")] }; break;
            case "C_NUMBER_NO_COUNTRY": doc = ("護照", c.Passport, "-"); list = list with { Dobs = [c.Dob], Documents = [("Passport", c.Passport, "-")] }; break;
            default: throw new InvalidOperationException(kind.Code);
        }

        var profileId = $"79{++profileSeq:000000}";
        var names = list.Chinese.Concat(list.Roman.Select(x => $"{x.Surname} {x.Given}")).ToList();
        var fields = new ExpectedProfile(names, list.Gender, list.Dobs, list.Citizenship ? ["TW"] : [], [.. list.Documents.Select(d => d.Number)], "Active", null);
        var truth = new CandidateTruth(profileId, RecordKind.Sanction, kind.SamePerson, ListType.Sanction, true, null, tags, fields, names.Count > 1 ? 2 : 1);
        var (subjectFile, subject) = AdversarialSubject(c, doc);
        var files = new Dictionary<string, string>
        {
            ["客戶-背景資料.txt"] = subjectFile,
            ["客戶-聯絡資料.txt"] = $"永久地址(國家)\n地址\n{c.Address}\n國家\nTaiwan\n",
            ["案件資訊.txt"] = CaseTable(subject.Names[0], false, [truth]),
            [$"名單資料_{profileId}.txt"] = AdversarialProfile(profileId, list),
        };
        return new SyntheticCase(id, false, files, subject, [truth]);
    }

    private static string Spelled(string roman) => roman.Replace("-", "", StringComparison.Ordinal).ToUpperInvariant();

    /// <summary>Same surname and gender, the first given character kept, another name.</summary>
    private Person Relative(Person c)
    {
        while (true)
        {
            var p = c with { Given = [c.Given[0], r.Next(10)] };
            if (p.Given[0] != p.Given[1] && p.Hant != c.Hant) return p;
        }
    }

    // One digit at a position from..to replaced by another.
    private string OneOff(string n, int from, int to)
    {
        var i = r.Next(from, to + 1);
        return n[..i] + (char)('0' + (n[i] - '0' + 1 + r.Next(9)) % 10) + n[(i + 1)..];
    }

    // Two adjacent different digits within positions from..to swapped.
    private string Transposed(string n, int from, int to)
    {
        while (true)
        {
            var i = r.Next(from, to);
            if (n[i] != n[i + 1]) return n[..i] + n[i + 1] + n[i] + n[(i + 2)..];
        }
    }

    private (string File, ExpectedSubject Subject) AdversarialSubject(Person c, (string Kind, string Number, string Issuing) doc)
    {
        var labels = new (string, string)[]
        {
            ("客戶號碼", $"C{r.Next(1_000_000, 9_999_999)}"), ("性別", c.Gender == Gender.Male ? "男" : "女"), ("客戶名稱", c.BankEnglish),
            ("出生日期", $"{c.Dob.Year}/{c.Dob.Month}/{c.Dob.Day}"), ("證件種類", doc.Kind), ("國籍 1", "Taiwan"), ("發照地", doc.Issuing),
            ("本地名稱", c.Hant), ("證件號碼", doc.Number), ("國籍 2", "-"), ("客戶類型", "個人"), ("職業", "服務業(7)"),
        };
        var file = "基本資料\n" + string.Concat(labels[..10].Select(l => $"{l.Item1}\n{l.Item2}\n")) + "詳細資料\n" + string.Concat(labels[10..].Select(l => $"{l.Item1}\n{l.Item2}\n"));
        return (file, new([c.Hant, c.BankEnglish], c.Gender, c.Dob, "TW", doc.Number));
    }

    private static string AdversarialProfile(string id, ListSide l)
    {
        static string D(PartialDate d) => d.Precision == DatePrecision.Day ? $"{d.Year}/{d.Month}/{d.Day}" : $"{d.Year}";
        var lines = new List<string> { $"Profile_ID:\t{id}", "記錄類型:\tPerson" };
        if (l.Gender is { } g) lines.Add($"性別:\t{(g == Gender.Male ? "Male" : "Female")}");
        lines.AddRange(["已故?:\tFalse", "狀態:\tActive", "主要名稱:", "編號\t名字\t姓"]);
        lines.AddRange(l.Roman.Select((n, i) => $"{i + 1}\t{n.Given}\t{n.Surname}"));
        lines.AddRange(l.Chinese.Select(n => $"OSN\t{n}"));
        if (l.Citizenship) lines.AddRange(["國家資料:", "編號\t類型\t國家", "1\tCitizenship\tTaiwan"]);
        if (l.Address is { } a) lines.AddRange(["地址:", "編號\t地址\t城市\t國家", $"1\t{a}\t-\tTaiwan"]);
        if (l.Dobs.Count > 0) lines.AddRange(["日期:", "編號\t類型\t日期", .. l.Dobs.Select((d, i) => $"{i + 1}\tDate of Birth\t{D(d)}")]);
        if (l.Documents.Count > 0) lines.AddRange(["身分證明:", "編號\t類型\t號碼\t國家", .. l.Documents.Select((d, i) => $"{i + 1}\t{d.Type}\t{d.Number}\t{d.Country}")]);
        lines.AddRange(["類別:", "編號\t描述1\t描述2", "1\tSanctions Lists\t", "名單資訊:", "機關\t名單\t狀態\t決定日期\t罪名",
            "Sample Ministry of Finance\tSample Sanctions List\tActive\t2024/5/1\t-"]);
        return string.Concat(lines.Select(x => x + "\n"));
    }
}
