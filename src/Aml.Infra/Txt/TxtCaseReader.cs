using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Aml.Core.Normalization;
using Aml.Core.Retrieval;
using Aml.Core.Shared;
using Aml.Core.Subjects;
using Aml.Infra.Html;

namespace Aml.Infra.Txt;

/// <summary>
/// Reads one case folder in the supplied .txt export layout (see samples/golden/README.md).
/// HTML pages reach this reader after Infra/Html/HtmlExport converts them to the same layout.
/// </summary>
public static partial class TxtCaseReader
{
    static TxtCaseReader() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>Reads uploaded or queued files (name → content) through a private temp folder; names are reduced to their last segment.</summary>
    public static CaseInput ReadFiles(IReadOnlyDictionary<string, string> files) =>
        ReadFiles(files.ToDictionary(f => f.Key, f => Encoding.UTF8.GetBytes(f.Value)));

    /// <summary>Reads uploaded files as their raw bytes, so the encoding is decided here (<see cref="Decode"/>).</summary>
    public static CaseInput ReadFiles(IReadOnlyDictionary<string, byte[]> files)
    {
        var folder = Directory.CreateTempSubdirectory("aml-input-");
        try
        {
            foreach (var (name, content) in files) File.WriteAllBytes(Path.Combine(folder.FullName, Path.GetFileName(name)), content);
            return Read(folder.FullName);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    /// <summary>
    /// The required file a folder lacks or holds twice: exactly one 案件資訊*.txt, then, once the subject category is known,
    /// the subject's own file (客戶 → 客戶-背景資料.txt, a related party → 客戶-關聯人背景資料.txt). A missing Profile file is
    /// not a problem: that candidate is NOT_EVALUATED.
    /// </summary>
    public static (string File, string Problem)? RequiredFileProblem(IReadOnlyCollection<string> names, string? subjectCategory)
    {
        var caseFiles = names.Count(n => n.StartsWith("案件資訊", StringComparison.Ordinal) && n.EndsWith(".txt", StringComparison.Ordinal));
        if (caseFiles != 1) return ("案件資訊.txt", caseFiles == 0 ? CaseInputException.Missing : CaseInputException.Duplicate);
        if (subjectCategory is null) return null;
        var own = subjectCategory == "客戶" ? "客戶-背景資料.txt" : "客戶-關聯人背景資料.txt";
        return names.Contains(own) ? null : (own, CaseInputException.Missing);
    }

    private static void Require(string folder, string? subjectCategory)
    {
        if (RequiredFileProblem(Directory.GetFiles(folder).Select(Path.GetFileName).OfType<string>().ToList(), subjectCategory) is var (file, problem))
            throw new CaseInputException(file, null, problem, $"{file}: {problem}");
    }

    // Anything else the reader throws on one file is reported against that file as MALFORMED; the cause stays as the inner exception, not in the message.
    private static T Reading<T>(string file, Func<T> read)
    {
        try
        {
            return read();
        }
        catch (Exception e) when (e is IndexOutOfRangeException or KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException or IOException)
        {
            throw new CaseInputException(file, null, CaseInputException.Malformed, $"{file} is not in the export layout.", e);
        }
    }

    /// <summary>
    /// Reads a case folder through a private copy; the source folder is never written. Export files are decoded as strict
    /// UTF-8, else CP950 (Big5) with an <c>ENCODING_FALLBACK:&lt;file&gt;</c> finding. HTML pages (by extension in any case,
    /// or a .txt holding page source) are converted to the export layout, AngleSharp deciding their encoding. Every file's
    /// SHA-256 is kept with the input.
    /// </summary>
    public static CaseInput Read(string folder)
    {
        var files = Directory.GetFiles(folder);
        var hashes = files.ToDictionary(f => Path.GetFileName(f), f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))));
        var copy = Directory.CreateTempSubdirectory("aml-input-");
        try
        {
            var findings = new List<string>();
            foreach (var f in files)
            {
                var known = KnownFile().IsMatch(ExportName(f));
                var target = Path.Combine(copy.FullName, known ? ExportName(f) : Path.GetFileName(f));
                if (File.Exists(target))
                    throw new CaseInputException(Path.GetFileName(target), null, CaseInputException.Duplicate, $"Two input files read as {Path.GetFileName(target)}");
                if (!known) File.Copy(f, target);
                else if (IsHtmlName(f) || LooksLikeMarkup(f))
                    File.WriteAllText(target, Reading(Path.GetFileName(f), () => HtmlExport.ToExportText(File.ReadAllBytes(f), HtmlExport.KindOf(Path.GetFileNameWithoutExtension(target)))));
                else
                {
                    File.WriteAllText(target, Decode(File.ReadAllBytes(f), out var fallback));
                    if (fallback) findings.Add($"{EncodingFallback}:{Path.GetFileName(f)}");
                }
            }
            return ReadExport(copy.FullName, findings) with { SourceHashes = hashes };
        }
        finally
        {
            copy.Delete(recursive: true);
        }
    }

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Strict UTF-8 (BOM dropped); bytes that are not UTF-8 are decoded as CP950 (Big5) and <paramref name="fallback"/> is set.</summary>
    public static string Decode(byte[] bytes, out bool fallback)
    {
        fallback = false;
        try
        {
            return StrictUtf8.GetString(bytes).TrimStart('﻿');
        }
        catch (DecoderFallbackException)
        {
            fallback = true;
            return Encoding.GetEncoding(950).GetString(bytes);
        }
    }

    private static bool IsHtmlName(string path) => Path.GetExtension(path).ToLowerInvariant() is ".html" or ".htm";

    // 名單資料_1.html → 名單資料_1.txt, 案件資訊.TXT → 案件資訊.txt; any other extension keeps the name.
    private static string ExportName(string path) =>
        IsHtmlName(path) || Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileNameWithoutExtension(path) + ".txt"
            : Path.GetFileName(path);

    private static CaseInput ReadExport(string folder, IEnumerable<string> fileFindings)
    {
        Require(folder, null);
        var caseFile = Directory.GetFiles(folder, "案件資訊*.txt").Single();
        var (subjectRow, rows, records, caseFindings) = Reading(Path.GetFileName(caseFile), () => ReadCaseTable(File.ReadAllLines(caseFile)));
        Require(folder, subjectRow.Category);

        var subject = subjectRow.Category == "客戶"
            ? Reading("客戶-背景資料.txt", () => ReadCustomer(folder))
            : Reading("客戶-關聯人背景資料.txt", () => ReadRelatedParty(Path.Combine(folder, "客戶-關聯人背景資料.txt"), subjectRow.Category, subjectRow.ReferenceNumber));
        // Case-level findings travel with the subject, so the explanation shows them on every hit.
        subject = subject with { Findings = [.. subject.Findings, .. caseFindings, .. fileFindings, .. Inventory(folder)] };

        // A candidate whose Profile file is missing is left out here and reported as NOT_EVALUATED by the analyzer.
        var profiles = rows.Select(r => r.ProfileId).Distinct()
            .Select(id => (id, path: Path.Combine(folder, $"名單資料_{id}.txt")))
            .Where(x => File.Exists(x.path))
            .ToDictionary(x => x.id, x => Reading(Path.GetFileName(x.path), () => ReadProfile(x.path)));

        var news = rows.Select(r => r.ProfileId).Distinct()
            .Select(id => (id, path: Path.Combine(folder, $"新聞資訊_{id}.txt")))
            .Where(x => File.Exists(x.path))
            .ToDictionary(x => x.id, x => Reading(Path.GetFileName(x.path), () => ReadNews(x.id, x.path)));

        var legacy = new List<ImportedDecision>();
        if (subjectRow.ListMatch is { } m) legacy.Add(new("是否符合名單", m));
        foreach (var reason in rows.Select(r => r.LegacyReason).OfType<string>().Distinct()) legacy.Add(new("理由", reason));
        legacy.AddRange(records.Select(r => new ImportedDecision(ProcessingRecord, r)));

        return new CaseInput(subject, rows, profiles, news, legacy);
    }

    public const string UnsupportedAttachment = "UNSUPPORTED_ATTACHMENT";
    public const string LegacyRecordBoundaryUnclear = "LEGACY_RECORD_BOUNDARY_UNCLEAR";
    public const string Unparseable = Findings.Unparseable;
    public const string FieldConflict = "FIELD_CONFLICT";
    public const string Ambiguous = Findings.Ambiguous;
    /// <summary><c>ENCODING_FALLBACK:&lt;file&gt;</c>: the file is not UTF-8 and was read as CP950 (Big5).</summary>
    public const string EncodingFallback = "ENCODING_FALLBACK";
    public const string FieldAbsent = "FIELD_ABSENT";
    public const string FieldBlank = "FIELD_BLANK";
    public const string PlaceholderDash = "PLACEHOLDER_DASH";
    public const string PlaceholderText = "PLACEHOLDER_TEXT";
    public const string Malformed = "MALFORMED";
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
            !f.Present ? $"{FieldAbsent}:{f.Field}"
            : string.IsNullOrWhiteSpace(f.Raw) ? $"{FieldBlank}:{f.Field}"
            : Normalize.IsDash(f.Raw!) ? $"{PlaceholderDash}:{f.Field}"
            : $"{PlaceholderText}:{f.Field}");

    private static PartialDate? Date(string field, string? raw, List<string> findings)
    {
        var d = PartialDate.Parse(raw);
        if (d is null && raw is not null) findings.Add($"{Unparseable}:{field}");
        return d;
    }

    private sealed record SubjectRow(string Category, string? ReferenceNumber, string? ListMatch);

    // ponytail: the two-row header expansion is an unconfirmed assumption (open question): a parent label with sub-labels
    // spans them in order, 是否符合名單？ → 全不符合, 全符合 and Name Property → PEP, 黑名單, 負面新聞. GC-11 and GC-12 show this
    // header but no flag values; confirm with an export that has flags set.
    private static readonly Dictionary<string, string[]> SubHeaders = new()
    {
        ["是否符合名單？"] = ["全不符合", "全符合"],
        ["Name Property"] = ["PEP", "黑名單", "負面新聞"],
    };

    private static readonly string[] RequiredCandidateColumns = ["編號", "姓名/名稱", "名單資料編號", "理由", "Is RCA?"];

    /// <summary>
    /// The case row and the 疑似符合的名單資料 section, whose columns are found by header label. A missing section or
    /// required column is an input error. A non-blank row that cannot be read is <c>UNPARSEABLE:candidate_row:&lt;line&gt;</c>
    /// (1-based line of 案件資訊): too few cells or a 編號 that is not a number (full-width digits are read) leaves it out;
    /// more cells than the header keeps it with its flags UNPARSEABLE, since they cannot be placed.
    /// </summary>
    private static (SubjectRow, List<CandidateRow>, List<string> Records, List<string> Findings) ReadCaseTable(string[] lines)
    {
        var s = lines[1].Split('\t');
        var subject = new SubjectRow(s[2].Trim(), Normalize.NullIfPlaceholder(s.ElementAtOrDefault(3)), Normalize.NullIfPlaceholder(s.ElementAtOrDefault(5)));

        var start = Array.FindIndex(lines, l => l.StartsWith("疑似符合的名單資料", StringComparison.Ordinal));
        if (start < 0 || start + 1 >= lines.Length)
            throw new CaseInputException("案件資訊.txt", "疑似符合的名單資料", CaseInputException.MissingSection, "案件資訊 has no 疑似符合的名單資料 section");
        var recordStart = Array.FindIndex(lines, l => l.Trim().StartsWith(ProcessingRecord, StringComparison.Ordinal));
        var end = recordStart < 0 ? lines.Length : recordStart;
        static string[] Cells(string line) => line.Split('\t').Select(c => c.Trim()).ToArray();
        static bool Numbered(string[] c) => int.TryParse(Normalize.HalfWidth(c[0]), out _);

        var findings = new List<string>();
        var header = Cells(lines[start + 1]);
        // A second header row: sub-labels only (the .txt export), or the whole row with parent labels carried down (HTML rowspan).
        var sub = start + 2 < end && lines[start + 2].Trim().Length > 0 && !Numbered(Cells(lines[start + 2])) ? Cells(lines[start + 2]) : null;
        var columns = sub is null ? header
            : sub.Length == header.Length ? sub
            : header.SelectMany(h => SubHeaders.TryGetValue(h, out var kids) ? kids : [h]).ToArray();
        var flagsReadable = sub is null || sub.Where(c => !header.Contains(c)).SequenceEqual(header.Distinct().Where(SubHeaders.ContainsKey).SelectMany(h => SubHeaders[h]));
        if (!flagsReadable) findings.Add($"{Unparseable}:candidate_header");
        if (RequiredCandidateColumns.FirstOrDefault(r => !columns.Contains(r)) is { } absent)
            throw new CaseInputException("案件資訊.txt", "疑似符合的名單資料", CaseInputException.MissingColumn, $"疑似符合的名單資料 has no {absent} column");
        var minCells = RequiredCandidateColumns.Max(r => Array.IndexOf(columns, r)) + 1;

        var rows = new List<CandidateRow>();
        for (var i = start + (sub is null ? 2 : 3); i < end; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            var c = Cells(lines[i]);
            if (c.Length < minCells || !Numbered(c))
            {
                findings.Add($"{Unparseable}:candidate_row:{i + 1}");
                continue;
            }
            string? Col(string name) => Array.IndexOf(columns, name) is var k and >= 0 ? c.ElementAtOrDefault(k) : null;
            var wide = c.Length > columns.Length;
            if (wide) findings.Add($"{Unparseable}:candidate_row:{i + 1}");
            var rca = wide ? FlagValue.Unparseable : Normalize.Flag(Col("Is RCA?"));
            var flags = wide || !flagsReadable ? new ListFlags(FlagValue.Unparseable, FlagValue.Unparseable, FlagValue.Unparseable, rca)
                : new ListFlags(Normalize.Flag(Col("PEP")), Normalize.Flag(Col("黑名單")), Normalize.Flag(Col("負面新聞")), rca);
            rows.Add(new CandidateRow(Col("姓名/名稱")!, Normalize.HalfWidth(Col("名單資料編號")!), Normalize.NullIfPlaceholder(Col("理由")), rca == FlagValue.Yes, flags));
        }
        var (records, recordFindings) = recordStart < 0 ? (new List<string>(), new List<string>()) : ReadProcessingRecord(lines[(recordStart + 1)..]);
        return (subject, rows, records, [.. findings, .. recordFindings]);
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

    // The customer background labels the reader knows; a value line that is one of them means the value was blank.
    private static readonly HashSet<string> CustomerLabels =
    [
        "基本資料", "詳細資料", "客戶號碼", "客戶別名", "性別", "年收入", "稅號/統一編號", "客戶名稱", "出生日期", "證件種類", "國籍 1",
        "發照地", "本地名稱", "出生地點", "證件號碼", "國籍 2", "客戶類型", "職業",
    ];

    // Customer files: label line followed by value line.
    private static ScreenedSubject ReadCustomer(string folder)
    {
        var bg = File.ReadAllLines(Path.Combine(folder, "客戶-背景資料.txt")).Select(l => l.Trim()).ToArray();
        // The raw value line, "" when the next line is another label (FIELD_BLANK); null when the label is absent.
        string? Raw(string key) => Array.IndexOf(bg, key) is var i and >= 0 && i + 1 < bg.Length ? (CustomerLabels.Contains(bg[i + 1]) ? "" : bg[i + 1]) : null;
        string? V(string key) => Normalize.NullIfPlaceholder(Raw(key));

        var findings = MissingFields(CustomerFields.Select(f => (f, Array.IndexOf(bg, f) >= 0, Raw(f)))).ToList();
        var names = new[] { V("本地名稱"), V("客戶名稱") }.OfType<string>().ToList();
        var nationalities = new[] { Country("國籍 1", V("國籍 1"), findings), Country("國籍 2", V("國籍 2"), findings) }.OfType<string>().ToList();
        var docNumber = V("證件號碼");
        var taxId = V("稅號/統一編號") switch
        {
            null => null,
            var t when t.Length < 8 => new FieldValue(t, ValueState.Ambiguous, Malformed),
            var t => new FieldValue(t, ValueState.Present),
        };

        var addresses = new List<Address>();
        var contactPath = Path.Combine(folder, "客戶-聯絡資料.txt");
        if (File.Exists(contactPath))
            Reading(Path.GetFileName(contactPath), () =>
            {
                var ct = File.ReadAllLines(contactPath).Select(l => l.Trim()).ToArray();
                for (var i = 0; i + 2 < ct.Length; i++)
                    if (ct[i].EndsWith("(國家)", StringComparison.Ordinal) && ct[i + 1] == "地址" && Normalize.NullIfPlaceholder(ct[i + 2]) is { } raw)
                    {
                        // The block's own 國家 line, before the next "(國家)" heading.
                        var end = Array.FindIndex(ct, i + 1, l => l.EndsWith("(國家)", StringComparison.Ordinal));
                        var k = Array.FindIndex(ct, i + 3, (end < 0 ? ct.Length : end) - i - 3, l => l == "國家");
                        addresses.Add(MakeAddress(ct[i][..^4], raw, k >= 0 && k + 1 < ct.Length ? Normalize.NullIfPlaceholder(ct[k + 1]) : null, findings));
                    }
                return addresses;
            });

        var dob = Date("出生日期", V("出生日期"), findings);

        return new ScreenedSubject(
            SubjectRole.Customer, null, names, Normalize.Gender(V("性別")), dob,
            nationalities, docNumber is null ? null : new Document(DocumentType(V("證件種類")), docNumber, IssuingCountry(V("發照地"), findings)),
            null, taxId, Occupation(V("職業")), Dedupe(addresses, out var dup), AddressFindings(addresses, dup, findings), PartyType: V("客戶類型"));
    }

    private static List<string> AddressFindings(List<Address> addresses, bool duplicated, List<string> findings)
    {
        if (duplicated) findings.Add(Findings.DuplicatedAddressLine);
        findings.AddRange(addresses.Where(a => a.Conflict).Select(a => $"{FieldConflict}:{a.Role}").Distinct());
        return findings;
    }

    // Related-party file: "key ：" (or "key :") lines, value on the following line(s), joined with a space when it spans
    // several; an empty value is followed directly by the next key.
    private static ScreenedSubject ReadRelatedParty(string path, string relationship, string? referenceNumber)
    {
        var fields = new List<(string Key, string? Value)>();
        foreach (var line in File.ReadAllLines(path))
        {
            var t = line.Trim();
            if (t.EndsWith('：') || t.EndsWith(':')) fields.Add((t[..^1].Trim(), null));
            else if (fields.Count > 0 && t.Length > 0) fields[^1] = (fields[^1].Key, fields[^1].Value is { } v ? $"{v} {t}" : t);
        }
        string? V(string key) => Normalize.NullIfPlaceholder(fields.FirstOrDefault(f => f.Key == key).Value);

        var findings = MissingFields(RelatedPartyFields
            .Select(k => (k, fields.Any(f => f.Key == k), fields.FirstOrDefault(f => f.Key == k).Value))).ToList();
        var addresses = fields
            .Where(f => f.Value is not null && AddressKey().IsMatch(f.Key))
            .Select(f =>
            {
                var role = AddressKey().Match(f.Key).Groups[1].Value;
                return MakeAddress(role, f.Value!, V(role + "(國家)"), findings);
            })
            .ToList();
        var docNumber = V("證件號碼");
        var dob = Date("出生日期", V("出生日期"), findings);

        return new ScreenedSubject(
            SubjectRole.RelatedParty, relationship, new[] { V("姓名/名稱") }.OfType<string>().ToList(),
            Normalize.Gender(V("性別")), dob,
            new[] { Country("國籍", V("國籍"), findings) }.OfType<string>().ToList(),
            docNumber is null ? null : new Document(DocumentType(V("證件種類")), docNumber, IssuingCountry(V("發照地"), findings)),
            referenceNumber ?? V("參考號碼"), null, null, Dedupe(addresses, out var dup), AddressFindings(addresses, dup, findings), PartyType: V("類型"));
    }

    [GeneratedRegex("^(永久地址|通訊位址) \\d$")]
    private static partial Regex AddressKey();

    /// <summary>
    /// The country comes from the address block's own country field; without one, only a Taiwan city or county in the
    /// text makes it TW. A country field that contradicts the text is FIELD_CONFLICT; both facts are kept. A country field
    /// that maps to no country is AMBIGUOUS:&lt;role&gt;(國家) and leaves the country unknown.
    /// </summary>
    private static Address MakeAddress(string role, string raw, string? countryField, List<string> findings)
    {
        if (Normalize.IsPostalCodeOnly(raw)) return new Address(role, raw, null, null, ValueState.Ambiguous, Findings.PostalCodeOnly);
        var (normalized, finding) = Normalize.TaiwanAddress(raw);
        var taiwan = TaiwanCity().IsMatch(normalized);
        var country = countryField is null ? (taiwan ? "TW" : null) : Country($"{role}(國家)", countryField, findings);
        var conflict = country is not null && countryField is not null && (country == "TW" ? !raw.Any(Normalize.IsCjk) : taiwan);
        return new Address(role, raw, normalized, country, ValueState.Present, finding) { Conflict = conflict };
    }

    // A country code, or null: placeholders are missing; a value that maps to no country adds AMBIGUOUS:<field>.
    private static string? Country(string field, string? raw, List<string> findings)
    {
        if (Normalize.NullIfPlaceholder(raw) is not { } value) return null;
        var code = Normalize.Country(value);
        if (code is null) findings.Add($"{Ambiguous}:{field}");
        return code;
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
    private static string? IssuingCountry(string? raw, List<string> findings) =>
        raw is not null && CountryCode().Match(raw) is { Success: true } m ? m.Groups[1].Value : Country("發照地", raw, findings);

    [GeneratedRegex(@"\[([A-Z]{2})\]")]
    private static partial Regex CountryCode();

    // Profile ID type + country → the bank's document type, so both sides share one key.
    private static string ProfileDocumentType(string type, string? country) => (type.Trim().ToUpperInvariant(), country) switch
    {
        ("NATIONAL ID" or "NATIONAL IDENTIFICATION NUMBER", "TW") => "TW_NATIONAL_ID",
        ("RESIDENT CERTIFICATE", "TW") => "TW_RESIDENT_CERTIFICATE",
        ("PASSPORT" or "PASSPORT NO.", _) => "PASSPORT",
        ("REGISTRATION NUMBER" or "COMPANY REGISTRATION" or "COMPANY IDENTIFICATION NUMBER" or "統一編號", _) => "REGISTRATION_NUMBER",
        ("IMO" or "IMO NUMBER" or "IMO NO.", _) => "IMO_NUMBER",
        var (other, _) => other,
    };

    // "批發及零售業(6)" → "批發及零售業"
    private static string? Occupation(string? raw) => raw is null ? null : TrailingCode().Replace(raw, "");

    [GeneratedRegex("\\(\\d+\\)$")]
    private static partial Regex TrailingCode();

    // Profile file: "key:\tvalue" lines, or "key:" followed by a tab-separated table.
    public static SourceProfile ReadProfile(string path) => ParseProfile(File.ReadAllText(path));

    /// <summary>A Profile file's export text: an HTML page converted, an export as it is. The content hash is taken on it.</summary>
    public static string ProfileExportText(string content) =>
        content.TrimStart().TrimStart((char)0xFEFF).StartsWith('<') ? HtmlExport.ToExportText(content, HtmlExport.Kind.Profile) : content;

    /// <summary>One Profile file's content, as the export or as an HTML page.</summary>
    public static SourceProfile ParseProfile(string content)
    {
        content = ProfileExportText(content);
        var scalars = new Dictionary<string, string>();
        var tables = new Dictionary<string, List<string[]>>();
        string? section = null;
        foreach (var line in content.ReplaceLineEndings("\n").Split('\n'))
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
        static bool Numbered(string[] r) => int.TryParse(Normalize.HalfWidth(r[0]), out _);
        var findings = new List<string>();
        // Numbered rows of a table; a row with fewer than width cells is UNPARSEABLE:<table> and left out, the Profile is still read.
        List<string[]> Rows(string key, int width, IEnumerable<string[]>? source = null)
        {
            var rows = (source ?? T(key)).Where(Numbered).ToList();
            if (rows.Any(r => r.Length < width)) findings.Add($"{Unparseable}:{key}");
            return rows.Where(r => r.Length >= width).ToList();
        }

        var names = new List<string>();
        foreach (var key in new[] { "主要名稱", "拼寫變化" })
            foreach (var r in T(key).Where(r => Numbered(r) || r[0] == "OSN"))
            {
                if (r.Length < (Numbered(r) ? 3 : 2)) findings.Add($"{Unparseable}:{key}");
                else names.Add(Numbered(r) ? $"{r[2]} {r[1]}" : r[1]); // surname first, as the source lists it
            }

        var countries = Rows("國家資料", 3);
        var dates = Rows("日期", 2);
        var dobRows = dates.Where(r => r[1] == "Date of Birth").Select(r => r.ElementAtOrDefault(2)).ToList();
        var dobs = dobRows.Select(PartialDate.Parse).OfType<PartialDate>().ToList();
        if (dobs.Count > 1) findings.Add(Findings.ProfileMultipleDob);
        if (dobs.Count < dobRows.Count) findings.Add($"{Unparseable}:Date of Birth");
        // A role date that cannot be read is UNPARSEABLE:職位; an end date in a future year is an open term, not an error.
        PartialDate? RoleDate(string? raw)
        {
            var d = PartialDate.Parse(raw);
            if (d is null && Normalize.NullIfPlaceholder(raw) is { } v
                && !(v.Length >= 4 && int.TryParse(v[..4], out var y) && y > DateTime.UtcNow.Year)) findings.Add($"{Unparseable}:職位");
            return d;
        }
        var roles = Rows("職位", 3)
            .Select(r => new ProfileRole(r[1], r[2], RoleDate(r.ElementAtOrDefault(3)), RoleDate(r.ElementAtOrDefault(4)))).ToList();
        var relationships = tables.Where(t => t.Value.FirstOrDefault()?.Contains("Profile_ID") == true)
            .SelectMany(t => Rows(t.Key, 5, t.Value))
            .Select(r => new ProfileRelationship(r[1], r[2], r[4], r[3] == "Entity")).ToList();
        var deathRows = dates.Where(r => r[1] == "Date of Death").ToList();
        var death = deathRows.Select(r => PartialDate.Parse(r.ElementAtOrDefault(2))).FirstOrDefault(d => d is not null);
        if (deathRows.Count > 0 && death is null) findings.Add($"{Unparseable}:Date of Death");
        // ponytail: the ID table layout (編號 類型 號碼 國家) is assumed until an export shows one; no sample has it yet.
        var documents = Rows("身分證明", 4)
            .Select(r => { var c = Country("身分證明", r[3], findings); return new Document(ProfileDocumentType(r[1], c), r[2], c); }).ToList();
        var listRows = T("名單資訊").Where(r => r[0] != "機關").ToList();
        if (listRows.Any(r => r.Length < 5)) findings.Add($"{Unparseable}:名單資訊");
        var official = listRows.Where(r => r.Length >= 5)
            .Select(r => new OfficialListEntry(r[0], r[1], r[2], Normalize.NullIfPlaceholder(r[4]))).FirstOrDefault();
        var addressRows = Rows("地址", 3);
        var citizenship = countries.Where(r => r[1] == "Citizenship").Select(r => Country("Citizenship", r[2], findings)).OfType<string>().ToList();
        // An unmapped address country is left out, so AddressCountries can be shorter than AddressLines; the explanation
        // then shows the lines without countries.
        var addressCountries = addressRows.Select(r => Country("地址", r[^1], findings)).OfType<string>().ToList();
        var allegation = countries.Where(r => r[1] == "Country of Reported Allegation")
            .Select(r => Country("Country of Reported Allegation", r[2], findings)).OfType<string>().ToList();
        var categories = Rows("類別", 2).Select(r => new SourceCategory(r[1], Normalize.NullIfPlaceholder(r.ElementAtOrDefault(2)))).ToList();
        var sources = Rows("來源", 2).Select(r => Source($"DJ:{scalars["Profile_ID"]}#source-{r[0]}", r[1], r.ElementAtOrDefault(2))).ToList();
        // ponytail: the 其他識別碼 table layout (編號 類型 號碼) is assumed; no sample has one yet.
        var otherIds = Rows("其他識別碼", 3).Select(r => new OtherIdentifier(r[1], r[2])).ToList();

        return new SourceProfile(
            ProfileKey: ProfileKeys.KeyOf(scalars["Profile_ID"]),
            Gender: Normalize.Gender(scalars.GetValueOrDefault("性別")),
            Names: names.Distinct().ToList(),
            DatesOfBirth: dobs,
            Citizenship: citizenship,
            AddressCountries: addressCountries,
            Categories: categories,
            Keywords: scalars.TryGetValue("關鍵字", out var kw) ? kw.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) : [],
            Roles: roles,
            Relationships: relationships,
            OfficialList: official,
            Findings: findings.Distinct().ToList(),
            Sources: sources,
            Documents: documents,
            AddressLines: addressRows.Select(r => string.Join(", ", r[1..^1].Where(x => Normalize.NullIfPlaceholder(x) is not null))).ToList(),
            DateOfDeath: death is { Year: > 0 } ? death : null,
            RecordType: scalars.GetValueOrDefault("記錄類型", "Person"),
            AllegationCountries: allegation,
            OtherIdentifiers: otherIds);
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

    // News export: id line, section, title, reporter, length, "2026 年 9 月 6 日" (or "2026年9月6日"), publisher, …, "文件 <id>".
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

    [GeneratedRegex("^(\\d{4})\\s*年\\s*(\\d{1,2})\\s*月\\s*(\\d{1,2})\\s*日$")]
    private static partial Regex NewsDate();

    [GeneratedRegex("^文件 (\\S+)$")]
    private static partial Regex NewsDocument();

    // "Profile_ID: 1", "性別:\tMale", "類別:"; excludes free text such as "Watchlist ProfileNotes: ..." (key has a space).
    [GeneratedRegex("^([^\\s:：]+\\??)[:：]\\s*(.*)$")]
    private static partial Regex KeyLine();
}
