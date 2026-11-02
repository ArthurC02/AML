using System.Text.RegularExpressions;
using Aml.Core.Normalization;
using Aml.Core.Shared;
using Aml.Core.Subjects;

namespace Aml.Infra.Txt;

/// <summary>
/// Reads one case folder in the supplied .txt export layout (see samples/golden/README.md).
/// ponytail: .txt only; an HTML reader joins this when the HTML samples arrive (open question: input format).
/// </summary>
public static partial class TxtCaseReader
{
    public static CaseInput Read(string folder)
    {
        var caseFile = Directory.GetFiles(folder, "案件資訊*.txt").Single();
        var (subjectRow, rows) = ReadCaseTable(File.ReadAllLines(caseFile));

        var subject = subjectRow.Category == "客戶"
            ? ReadCustomer(folder)
            : ReadRelatedParty(Path.Combine(folder, "客戶-關聯人背景資料.txt"), subjectRow.Category, subjectRow.ReferenceNumber);

        var profiles = rows.Select(r => r.ProfileId).Distinct()
            .ToDictionary(id => id, id => ReadProfile(Path.Combine(folder, $"名單資料_{id}.txt")));

        var news = rows.Select(r => r.ProfileId).Distinct()
            .Select(id => (id, path: Path.Combine(folder, $"新聞資訊_{id}.txt")))
            .Where(x => File.Exists(x.path))
            .ToDictionary(x => x.id, x => File.ReadAllText(x.path));

        var legacy = new List<ImportedDecision>();
        if (subjectRow.ListMatch is { } m) legacy.Add(new("是否符合名單", m));
        foreach (var reason in rows.Select(r => r.LegacyReason).OfType<string>().Distinct()) legacy.Add(new("理由", reason));

        return new CaseInput(subject, rows, profiles, news, legacy);
    }

    private sealed record SubjectRow(string Category, string? ReferenceNumber, string? ListMatch);

    // ponytail: candidate column order and the PEP/黑名單/負面新聞 flags are pending confirmation; flags are treated as MISSING.
    private static (SubjectRow, List<CandidateRow>) ReadCaseTable(string[] lines)
    {
        var s = lines[1].Split('\t');
        var subject = new SubjectRow(s[2].Trim(), Normalize.NullIfPlaceholder(s.ElementAtOrDefault(3)), Normalize.NullIfPlaceholder(s.ElementAtOrDefault(5)));

        var start = Array.FindIndex(lines, l => l.StartsWith("疑似符合的名單資料", StringComparison.Ordinal));
        var rows = new List<CandidateRow>();
        foreach (var line in lines.Skip(start + 1))
        {
            var c = line.Split('\t');
            if (c.Length < 9 || !int.TryParse(c[0], out _)) continue;
            rows.Add(new CandidateRow(c[1].Trim(), c[4].Trim(), Normalize.NullIfPlaceholder(c[7]), c[8].Trim() == "是"));
        }
        return (subject, rows);
    }

    // Customer files: label line followed by value line.
    private static ScreenedSubject ReadCustomer(string folder)
    {
        var bg = File.ReadAllLines(Path.Combine(folder, "客戶-背景資料.txt")).Select(l => l.Trim()).ToArray();
        string? V(string key)
        {
            var i = Array.IndexOf(bg, key);
            return i >= 0 && i + 1 < bg.Length ? Normalize.NullIfPlaceholder(bg[i + 1]) : null;
        }

        var names = new[] { V("本地名稱"), V("客戶名稱") }.OfType<string>().ToList();
        var nationalities = new[] { V("國籍 1"), V("國籍 2") }.OfType<string>().Select(Normalize.Country).ToList();
        var docNumber = V("證件號碼");
        var taxId = V("稅號/統一編號") switch
        {
            null => null,
            var t when t.Length < 8 => new FieldValue(t, ValueState.Ambiguous, "MALFORMED"),
            var t => new FieldValue(t, ValueState.Present),
        };

        var addresses = new List<Address>();
        var contactPath = Path.Combine(folder, "客戶-聯絡資料.txt");
        if (File.Exists(contactPath))
        {
            var ct = File.ReadAllLines(contactPath).Select(l => l.Trim()).ToArray();
            for (var i = 0; i + 2 < ct.Length; i++)
                if (ct[i].EndsWith("(國家)", StringComparison.Ordinal) && ct[i + 1] == "地址" && Normalize.NullIfPlaceholder(ct[i + 2]) is { } raw)
                    addresses.Add(MakeAddress(ct[i][..^4], raw));
        }

        return new ScreenedSubject(
            SubjectRole.Customer, null, names, Normalize.Gender(V("性別")), PartialDate.Parse(V("出生日期")),
            nationalities, docNumber is null ? null : new Document(DocumentType(V("證件種類")), docNumber),
            null, taxId, Occupation(V("職業")), Dedupe(addresses, out var dup), dup ? [Findings.DuplicatedAddressLine] : []);
    }

    // Related-party file: "key ：" lines, value on the following line(s); an empty value is followed directly by the next key.
    private static ScreenedSubject ReadRelatedParty(string path, string relationship, string? referenceNumber)
    {
        var fields = new List<(string Key, string? Value)>();
        foreach (var line in File.ReadAllLines(path))
        {
            var t = line.Trim();
            if (t.EndsWith('：')) fields.Add((t[..^1].Trim(), null));
            else if (fields.Count > 0 && t.Length > 0 && fields[^1].Value is null) fields[^1] = (fields[^1].Key, t);
        }
        string? V(string key) => Normalize.NullIfPlaceholder(fields.FirstOrDefault(f => f.Key == key).Value);

        var addresses = fields
            .Where(f => f.Value is not null && AddressKey().IsMatch(f.Key))
            .Select(f => MakeAddress(AddressKey().Match(f.Key).Groups[1].Value, f.Value!))
            .ToList();
        var docNumber = V("證件號碼");

        return new ScreenedSubject(
            SubjectRole.RelatedParty, relationship, new[] { V("姓名/名稱") }.OfType<string>().ToList(),
            Normalize.Gender(V("性別")), PartialDate.Parse(V("出生日期")),
            new[] { V("國籍") }.OfType<string>().Select(Normalize.Country).ToList(),
            docNumber is null ? null : new Document(DocumentType(V("證件種類")), docNumber),
            referenceNumber ?? V("參考號碼"), null, null, Dedupe(addresses, out var dup), dup ? [Findings.DuplicatedAddressLine] : []);
    }

    [GeneratedRegex("^(永久地址|通訊位址) \\d$")]
    private static partial Regex AddressKey();

    private static Address MakeAddress(string role, string raw)
    {
        if (Normalize.IsPostalCodeOnly(raw)) return new Address(role, raw, null, null, ValueState.Ambiguous, Findings.PostalCodeOnly);
        var (normalized, finding) = Normalize.TaiwanAddress(raw);
        return new Address(role, raw, normalized, "TW", ValueState.Present, finding);
    }

    private static List<Address> Dedupe(List<Address> addresses, out bool duplicated)
    {
        var result = addresses.DistinctBy(a => (a.Role, a.Raw)).ToList();
        duplicated = result.Count < addresses.Count;
        return result;
    }

    private static string? DocumentType(string? raw) => raw switch
    {
        "身分證統一編號" => "TW_NATIONAL_ID",
        "居留證" => "TW_RESIDENT_CERTIFICATE",
        _ => raw,
    };

    // "批發及零售業(6)" → "批發及零售業"
    private static string? Occupation(string? raw) => raw is null ? null : TrailingCode().Replace(raw, "");

    [GeneratedRegex("\\(\\d+\\)$")]
    private static partial Regex TrailingCode();

    // Profile file: "key:\tvalue" lines, or "key:" followed by a tab-separated table.
    public static SourceProfile ReadProfile(string path)
    {
        var scalars = new Dictionary<string, string>();
        var tables = new Dictionary<string, List<string[]>>();
        string? section = null;
        foreach (var line in File.ReadAllLines(path))
        {
            var m = KeyLine().Match(line);
            if (m.Success)
            {
                var key = m.Groups[1].Value.Trim();
                var value = m.Groups[2].Value.Trim();
                if (value.Length > 0) { scalars[key] = value; section = null; }
                else { section = key; tables[key] = []; }
            }
            else if (section is not null && line.Contains('\t', StringComparison.Ordinal))
            {
                tables[section].Add(line.Split('\t').Select(c => c.Trim()).ToArray());
            }
        }

        List<string[]> T(string key) => tables.TryGetValue(key, out var t) ? t : [];
        static bool Numbered(string[] r) => int.TryParse(r[0], out _);

        var names = new List<string>();
        foreach (var r in T("主要名稱").Concat(T("拼寫變化")))
        {
            if (Numbered(r) && r.Length >= 3) names.Add($"{r[2]} {r[1]}"); // surname first, as the source lists it
            else if (r[0] == "OSN") names.Add(r[1]);
        }

        var countries = T("國家資料").Where(Numbered).ToList();
        var dobs = T("日期").Where(r => Numbered(r) && r[1] == "Date of Birth").Select(r => PartialDate.Parse(r[2])).OfType<PartialDate>().ToList();
        var roles = T("職位").Where(Numbered).Select(r => new ProfileRole(r[1], r[2], PartialDate.Parse(r.ElementAtOrDefault(3)))).ToList();
        var relationships = tables.Where(t => t.Value.FirstOrDefault()?.Contains("Profile_ID") == true)
            .SelectMany(t => t.Value.Where(Numbered))
            .Select(r => new ProfileRelationship(r[1], r[2], r[4])).ToList();
        var official = T("名單資訊").Where(r => r[0] != "機關" && r.Length >= 5)
            .Select(r => new OfficialListEntry(r[0], r[1], r[2], Normalize.NullIfPlaceholder(r[4]))).FirstOrDefault();

        return new SourceProfile(
            ProfileKey: "DJ:" + scalars["Profile_ID"],
            Gender: Normalize.Gender(scalars.GetValueOrDefault("性別")),
            Names: names.Distinct().ToList(),
            DatesOfBirth: dobs,
            Citizenship: countries.Where(r => r[1] == "Citizenship").Select(r => Normalize.Country(r[2])).ToList(),
            AddressCountries: T("地址").Where(Numbered).Select(r => Normalize.Country(r[^1])).ToList(),
            Categories: T("類別").Where(Numbered).Select(r => new SourceCategory(r[1], Normalize.NullIfPlaceholder(r.ElementAtOrDefault(2)))).ToList(),
            Keywords: scalars.TryGetValue("關鍵字", out var kw) ? kw.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) : [],
            Roles: roles,
            Relationships: relationships,
            OfficialList: official,
            Findings: dobs.Count > 1 ? [Findings.ProfileMultipleDob] : []);
    }

    // "Profile_ID: 1", "性別:\tMale", "類別:"; excludes free text such as "Watchlist ProfileNotes: ..." (key has a space).
    [GeneratedRegex("^([^\\s:：]+\\??)[:：]\\s*(.*)$")]
    private static partial Regex KeyLine();
}
