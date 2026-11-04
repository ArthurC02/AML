using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Aml.Core.Normalization;
using Aml.Core.Shared;
using Aml.Core.Subjects;
using Aml.Infra.Html;

namespace Aml.Infra.Txt;

/// <summary>
/// Reads one case folder in the supplied .txt export layout (see samples/golden/README.md).
/// ponytail: .txt only; an HTML reader joins this when the HTML samples arrive (open question: input format).
/// </summary>
public static partial class TxtCaseReader
{
    /// <summary>Reads uploaded or queued files (name → content) through a private temp folder; names are reduced to their last segment.</summary>
    public static CaseInput ReadFiles(IReadOnlyDictionary<string, string> files)
    {
        var folder = Directory.CreateTempSubdirectory("aml-input-");
        try
        {
            foreach (var (name, content) in files) File.WriteAllText(Path.Combine(folder.FullName, Path.GetFileName(name)), content);
            return Read(folder.FullName);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    /// <summary>The exceptions <see cref="Read"/> throws for incomplete or malformed input.</summary>
    public static bool IsInputError(Exception e) =>
        e is InvalidOperationException or FileNotFoundException or IndexOutOfRangeException or KeyNotFoundException;

    /// <summary>
    /// Reads a case folder. HTML pages (by extension, or a .txt holding page source) are converted to the export layout in
    /// a private copy first; the source folder is never written. Every file's SHA-256 is kept with the input.
    /// </summary>
    public static CaseInput Read(string folder)
    {
        var hashes = Directory.GetFiles(folder).ToDictionary(f => Path.GetFileName(f), f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))));
        var markup = Directory.GetFiles(folder).Where(f => KnownFile().IsMatch(ExportName(f)) && (IsHtmlName(f) || LooksLikeMarkup(f))).ToList();
        if (markup.Count == 0) return ReadExport(folder) with { SourceHashes = hashes };

        var copy = Directory.CreateTempSubdirectory("aml-html-");
        try
        {
            foreach (var f in Directory.GetFiles(folder).Except(markup)) File.Copy(f, Path.Combine(copy.FullName, Path.GetFileName(f)));
            foreach (var f in markup)
            {
                var name = ExportName(f);
                File.WriteAllText(Path.Combine(copy.FullName, name), HtmlExport.ToExportText(File.ReadAllText(f), HtmlExport.KindOf(Path.GetFileNameWithoutExtension(name))));
            }
            return ReadExport(copy.FullName) with { SourceHashes = hashes };
        }
        finally
        {
            copy.Delete(recursive: true);
        }
    }

    private static bool IsHtmlName(string path) => Path.GetExtension(path) is ".html" or ".htm";

    // 名單資料_1.html → 名單資料_1.txt; a .txt keeps its name.
    private static string ExportName(string path) => IsHtmlName(path) ? Path.GetFileNameWithoutExtension(path) + ".txt" : Path.GetFileName(path);

    private static CaseInput ReadExport(string folder)
    {
        var caseFile = Directory.GetFiles(folder, "案件資訊*.txt").Single();
        var (subjectRow, rows, records, caseFindings) = ReadCaseTable(File.ReadAllLines(caseFile));

        var subject = subjectRow.Category == "客戶"
            ? ReadCustomer(folder)
            : ReadRelatedParty(Path.Combine(folder, "客戶-關聯人背景資料.txt"), subjectRow.Category, subjectRow.ReferenceNumber);
        // Case-level findings travel with the subject, so the explanation shows them on every hit.
        subject = subject with { Findings = [.. subject.Findings, .. caseFindings, .. Inventory(folder)] };

        // A candidate whose Profile file is missing is left out here and reported as NOT_EVALUATED by the analyzer.
        var profiles = rows.Select(r => r.ProfileId).Distinct()
            .Select(id => (id, path: Path.Combine(folder, $"名單資料_{id}.txt")))
            .Where(x => File.Exists(x.path))
            .ToDictionary(x => x.id, x => ReadProfile(x.path));

        var news = rows.Select(r => r.ProfileId).Distinct()
            .Select(id => (id, path: Path.Combine(folder, $"新聞資訊_{id}.txt")))
            .Where(x => File.Exists(x.path))
            .ToDictionary(x => x.id, x => ReadNews(x.id, x.path));

        var legacy = new List<ImportedDecision>();
        if (subjectRow.ListMatch is { } m) legacy.Add(new("是否符合名單", m));
        foreach (var reason in rows.Select(r => r.LegacyReason).OfType<string>().Distinct()) legacy.Add(new("理由", reason));
        legacy.AddRange(records.Select(r => new ImportedDecision(ProcessingRecord, r)));

        return new CaseInput(subject, rows, profiles, news, legacy);
    }

    public const string UnsupportedAttachment = "UNSUPPORTED_ATTACHMENT";
    public const string LegacyRecordBoundaryUnclear = "LEGACY_RECORD_BOUNDARY_UNCLEAR";
    public const string Unparseable = "UNPARSEABLE";
    public const string FieldConflict = "FIELD_CONFLICT";
    private const string ProcessingRecord = "客戶檢核處理紀錄";

    [GeneratedRegex("^(案件資訊.*|名單資料_\\d+|新聞資訊_\\d+|客戶-背景資料|客戶-聯絡資料|客戶-關聯人背景資料)\\.txt$")]
    private static partial Regex KnownFile();

    /// <summary>
    /// Every file is inventoried; one the reader does not read is disclosed as unread, never treated as "no data".
    /// The type is decided by content: a .txt holding page source is not an export.
    /// </summary>
    private static IEnumerable<string> Inventory(string folder)
    {
        foreach (var path in Directory.GetFiles(folder))
        {
            var name = Path.GetFileName(path);
            if (name == "expected.yaml") continue; // the Golden Case label beside the inputs, not an attachment
            if (!KnownFile().IsMatch(name) || LooksLikeMarkup(path)) yield return $"{UnsupportedAttachment}:{name}";
        }
    }

    private static bool LooksLikeMarkup(string path)
    {
        using var reader = new StreamReader(path);
        var head = new char[256];
        var n = reader.Read(head, 0, head.Length);
        return new string(head, 0, n).TrimStart('﻿', ' ', '\r', '\n', '\t').StartsWith('<');
    }

    private static readonly string[] CustomerFields = ["本地名稱", "客戶名稱", "性別", "出生日期", "國籍 1", "證件號碼"];
    private static readonly string[] RelatedPartyFields = ["姓名/名稱", "性別", "出生日期", "國籍", "證件號碼"];

    /// <summary>MISSING with its cause (absent, blank, placeholder) for the fields identity uses.</summary>
    private static IEnumerable<string> MissingFields(IEnumerable<(string Field, bool Present, string? Raw)> fields) =>
        fields.Where(f => !f.Present || Normalize.NullIfPlaceholder(f.Raw) is null).Select(f =>
            !f.Present ? $"FIELD_ABSENT:{f.Field}"
            : string.IsNullOrWhiteSpace(f.Raw) ? $"FIELD_BLANK:{f.Field}"
            : f.Raw!.Trim() == "-" ? $"PLACEHOLDER_DASH:{f.Field}"
            : $"PLACEHOLDER_TEXT:{f.Field}");

    private static PartialDate? Date(string field, string? raw, List<string> findings)
    {
        var d = PartialDate.Parse(raw);
        if (d is null && raw is not null) findings.Add($"{Unparseable}:{field}");
        return d;
    }

    private sealed record SubjectRow(string Category, string? ReferenceNumber, string? ListMatch);

    // ponytail: candidate column order and the PEP/黑名單/負面新聞 flags are pending confirmation; flags are treated as MISSING.
    private static (SubjectRow, List<CandidateRow>, List<string> Records, List<string> Findings) ReadCaseTable(string[] lines)
    {
        var s = lines[1].Split('\t');
        var subject = new SubjectRow(s[2].Trim(), Normalize.NullIfPlaceholder(s.ElementAtOrDefault(3)), Normalize.NullIfPlaceholder(s.ElementAtOrDefault(5)));

        var start = Array.FindIndex(lines, l => l.StartsWith("疑似符合的名單資料", StringComparison.Ordinal));
        var recordStart = Array.FindIndex(lines, l => l.Trim().StartsWith(ProcessingRecord, StringComparison.Ordinal));
        var rows = new List<CandidateRow>();
        foreach (var line in lines.Skip(start + 1).Take((recordStart < 0 ? lines.Length : recordStart) - start - 1))
        {
            var c = line.Split('\t');
            if (c.Length < 9 || !int.TryParse(c[0], out _)) continue;
            // ponytail: column order assumed until the bank confirms it: … 理由, Is RCA?, Name Property, PEP, 黑名單, 負面新聞.
            // The samples stop at Is RCA?, so their flags read MISSING and the category mapping decides.
            var flags = new ListFlags(Normalize.Flag(c.ElementAtOrDefault(10)), Normalize.Flag(c.ElementAtOrDefault(11)), Normalize.Flag(c.ElementAtOrDefault(12)));
            rows.Add(new CandidateRow(c[1].Trim(), c[4].Trim(), Normalize.NullIfPlaceholder(c[7]), Normalize.Flag(c[8]) == FlagValue.Yes, flags));
        }
        var (records, findings) = recordStart < 0 ? (new List<string>(), new List<string>()) : ReadProcessingRecord(lines[(recordStart + 1)..]);
        return (subject, rows, records, findings);
    }

    /// <summary>
    /// 客戶檢核處理紀錄: its table rows become ImportedDecision only. The exclusion ends where the table ends; a heading
    /// without a table, or text after the table that may continue it, is flagged for the reviewer to confirm.
    /// </summary>
    private static (List<string>, List<string>) ReadProcessingRecord(string[] lines)
    {
        var body = lines.SkipWhile(string.IsNullOrWhiteSpace).ToList();
        var table = body.TakeWhile(l => l.Contains('\t', StringComparison.Ordinal)).ToList();
        var rest = body.Skip(table.Count).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        var records = table.Skip(1).Select(l => string.Join(" | ", l.Split('\t').Select(c => c.Trim()).Where(c => c.Length > 0))).ToList();
        return (records, table.Count < 2 || rest.Count > 0 ? [LegacyRecordBoundaryUnclear] : []);
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
                {
                    // The block's own 國家 line, before the next "(國家)" heading.
                    var end = Array.FindIndex(ct, i + 1, l => l.EndsWith("(國家)", StringComparison.Ordinal));
                    var k = Array.FindIndex(ct, i + 3, (end < 0 ? ct.Length : end) - i - 3, l => l == "國家");
                    addresses.Add(MakeAddress(ct[i][..^4], raw, k >= 0 && k + 1 < ct.Length ? Normalize.NullIfPlaceholder(ct[k + 1]) : null));
                }
        }

        var findings = MissingFields(CustomerFields
            .Select(f => (f, Array.IndexOf(bg, f) >= 0, Array.IndexOf(bg, f) is var i and >= 0 && i + 1 < bg.Length ? bg[i + 1] : null))).ToList();
        var dob = Date("出生日期", V("出生日期"), findings);

        return new ScreenedSubject(
            SubjectRole.Customer, null, names, Normalize.Gender(V("性別")), dob,
            nationalities, docNumber is null ? null : new Document(DocumentType(V("證件種類")), docNumber, IssuingCountry(V("發照地"))),
            null, taxId, Occupation(V("職業")), Dedupe(addresses, out var dup), AddressFindings(addresses, dup, findings));
    }

    private static List<string> AddressFindings(List<Address> addresses, bool duplicated, List<string> findings)
    {
        if (duplicated) findings.Add(Findings.DuplicatedAddressLine);
        findings.AddRange(addresses.Where(a => a.Conflict).Select(a => $"{FieldConflict}:{a.Role}").Distinct());
        return findings;
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
            .Select(f =>
            {
                var role = AddressKey().Match(f.Key).Groups[1].Value;
                return MakeAddress(role, f.Value!, V(role + "(國家)"));
            })
            .ToList();
        var docNumber = V("證件號碼");
        var findings = MissingFields(RelatedPartyFields
            .Select(k => (k, fields.Any(f => f.Key == k), fields.FirstOrDefault(f => f.Key == k).Value))).ToList();
        var dob = Date("出生日期", V("出生日期"), findings);

        return new ScreenedSubject(
            SubjectRole.RelatedParty, relationship, new[] { V("姓名/名稱") }.OfType<string>().ToList(),
            Normalize.Gender(V("性別")), dob,
            new[] { V("國籍") }.OfType<string>().Select(Normalize.Country).ToList(),
            docNumber is null ? null : new Document(DocumentType(V("證件種類")), docNumber, IssuingCountry(V("發照地"))),
            referenceNumber ?? V("參考號碼"), null, null, Dedupe(addresses, out var dup), AddressFindings(addresses, dup, findings));
    }

    [GeneratedRegex("^(永久地址|通訊位址) \\d$")]
    private static partial Regex AddressKey();

    /// <summary>
    /// The country comes from the address block's own country field; without one, only a Taiwan city or county in the
    /// text makes it TW. A country field that contradicts the text is FIELD_CONFLICT; both facts are kept.
    /// </summary>
    private static Address MakeAddress(string role, string raw, string? countryField)
    {
        if (Normalize.IsPostalCodeOnly(raw)) return new Address(role, raw, null, null, ValueState.Ambiguous, Findings.PostalCodeOnly);
        var (normalized, finding) = Normalize.TaiwanAddress(raw);
        var taiwan = TaiwanCity().IsMatch(normalized);
        var country = countryField is null ? (taiwan ? "TW" : null) : Normalize.Country(countryField);
        var conflict = countryField is not null && (country == "TW" ? !raw.Any(c => c is >= '㐀' and <= '鿿') : taiwan);
        return new Address(role, raw, normalized, country, ValueState.Present, finding) { Conflict = conflict };
    }

    [GeneratedRegex("^\\d{0,6}\\s?(?:[台臺]北|新北|桃園|[台臺]中|[台臺]南|高雄|基隆|新竹|嘉義)市|^\\d{0,6}\\s?(?:新竹|苗栗|彰化|南投|雲林|嘉義|屏東|宜蘭|花蓮|[台臺]東|澎湖|金門|連江)縣")]
    private static partial Regex TaiwanCity();

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
        "護照" => "PASSPORT",
        _ => raw,
    };

    // "國家: [TW] Taiwan" → TW
    private static string? IssuingCountry(string? raw) =>
        raw is null ? null : CountryCode().Match(raw) is { Success: true } m ? m.Groups[1].Value : Normalize.Country(raw);

    [GeneratedRegex(@"\[([A-Z]{2})\]")]
    private static partial Regex CountryCode();

    // Profile ID type + country → the bank's document type, so both sides share one key.
    private static string ProfileDocumentType(string type, string country) => (type.Trim().ToUpperInvariant(), country) switch
    {
        ("NATIONAL ID" or "NATIONAL IDENTIFICATION NUMBER", "TW") => "TW_NATIONAL_ID",
        ("RESIDENT CERTIFICATE", "TW") => "TW_RESIDENT_CERTIFICATE",
        ("PASSPORT" or "PASSPORT NO.", _) => "PASSPORT",
        var (other, _) => other,
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
        var dobRows = T("日期").Where(r => Numbered(r) && r[1] == "Date of Birth").Select(r => r.ElementAtOrDefault(2)).ToList();
        var dobs = dobRows.Select(PartialDate.Parse).OfType<PartialDate>().ToList();
        var findings = new List<string>();
        if (dobs.Count > 1) findings.Add(Findings.ProfileMultipleDob);
        if (dobs.Count < dobRows.Count) findings.Add($"{Unparseable}:Date of Birth");
        var roles = T("職位").Where(Numbered)
            .Select(r => new ProfileRole(r[1], r[2], PartialDate.Parse(r.ElementAtOrDefault(3)), PartialDate.Parse(r.ElementAtOrDefault(4)))).ToList();
        var relationships = tables.Where(t => t.Value.FirstOrDefault()?.Contains("Profile_ID") == true)
            .SelectMany(t => t.Value.Where(Numbered))
            .Select(r => new ProfileRelationship(r[1], r[2], r[4], r[3] == "Entity")).ToList();
        var death = T("日期").Where(r => Numbered(r) && r[1] == "Date of Death").Select(r => PartialDate.Parse(r[2])).OfType<PartialDate>().FirstOrDefault();
        // ponytail: the ID table layout (編號 類型 號碼 國家) is assumed until an export shows one; no sample has it yet.
        var documents = T("身分證明").Where(r => Numbered(r) && r.Length >= 4)
            .Select(r => { var c = Normalize.Country(r[3]); return new Document(ProfileDocumentType(r[1], c), r[2], c); }).ToList();
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
            Findings: findings,
            Sources: T("來源").Where(Numbered).Select(r => Source($"DJ:{scalars["Profile_ID"]}#source-{r[0]}", r[1], r.ElementAtOrDefault(2))).ToList(),
            Documents: documents,
            AddressLines: T("地址").Where(Numbered).Select(r => string.Join(", ", r[1..^1].Where(x => Normalize.NullIfPlaceholder(x) is not null))).ToList(),
            DateOfDeath: death is { Year: > 0 } ? death : null,
            RecordType: scalars.GetValueOrDefault("記錄類型", "Person"),
            // ponytail: the 其他識別碼 table layout (編號 類型 號碼) is assumed; no sample has one yet.
            AllegationCountries: countries.Where(r => r[1] == "Country of Reported Allegation").Select(r => Normalize.Country(r[2])).ToList(),
            OtherIdentifiers: T("其他識別碼").Where(r => Numbered(r) && r.Length >= 3).Select(r => new OtherIdentifier(r[1], r[2])).ToList());
    }

    // "Liberty Times (Chinese - Traditional), 06-Sep-2026, on Factiva.com (https://…)" + "2026/9/10 下午 01:00:15"
    private static SourceReference Source(string id, string text, string? retrieved)
    {
        var parts = text.Split(", ");
        var published = parts.Skip(1)
            .Select(p => DateTime.TryParseExact(p.Trim(), "dd-MMM-yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null)
            .FirstOrDefault(d => d is not null);
        var url = SourceUrl().Match(text) is { Success: true } m ? m.Groups[1].Value : null;
        var retrievedOn = Normalize.NullIfPlaceholder(retrieved)?.Split(' ')[0] is { } r && PartialDate.Parse(r) is { } rd ? rd.ToString() : null;
        return new SourceReference(id, parts[0].Trim(), url, published, retrievedOn);
    }

    [GeneratedRegex("\\((https?://[^)\\s]+)\\)")]
    private static partial Regex SourceUrl();

    // News export: id line, section, title, reporter, length, "2026 年 9 月 6 日", publisher, …, "文件 <id>".
    private static NewsItem ReadNews(string profileId, string path)
    {
        var lines = File.ReadAllLines(path).Select(l => l.Trim()).ToArray();
        var dateIndex = Array.FindIndex(lines, l => NewsDate().IsMatch(l));
        PartialDate? published = null;
        if (dateIndex >= 0)
        {
            var g = NewsDate().Match(lines[dateIndex]).Groups;
            published = new PartialDate(Int(g[1].Value), Int(g[2].Value), Int(g[3].Value));
        }
        var docId = lines.Select(l => NewsDocument().Match(l)).FirstOrDefault(m => m.Success)?.Groups[1].Value;
        return new NewsItem(profileId, docId, lines.ElementAtOrDefault(2) ?? "",
            dateIndex >= 0 ? lines.ElementAtOrDefault(dateIndex + 1) : null, published, string.Join('\n', lines));
    }

    private static int Int(string s) => int.Parse(s, CultureInfo.InvariantCulture);

    [GeneratedRegex("^(\\d{4}) 年 (\\d{1,2}) 月 (\\d{1,2}) 日$")]
    private static partial Regex NewsDate();

    [GeneratedRegex("^文件 (\\S+)$")]
    private static partial Regex NewsDocument();

    // "Profile_ID: 1", "性別:\tMale", "類別:"; excludes free text such as "Watchlist ProfileNotes: ..." (key has a space).
    [GeneratedRegex("^([^\\s:：]+\\??)[:：]\\s*(.*)$")]
    private static partial Regex KeyLine();
}
