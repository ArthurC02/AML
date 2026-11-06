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

/// <summary>Reads one case folder in the bank's .txt export layout (samples/golden/README.md); HTML pages arrive converted by HtmlExport.</summary>
public static partial class TxtCaseReader
{
    static TxtCaseReader() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private const string CaseInfoPrefix = "案件資訊";
    private const string CaseInfoFile = "案件資訊.txt";
    private const string CustomerFile = "客戶-背景資料.txt";
    private const string RelatedPartyFile = "客戶-關聯人背景資料.txt";
    private const string ContactFile = "客戶-聯絡資料.txt";
    private const string CustomerCategory = "客戶";
    private const string GoldenCaseLabelFile = "expected.yaml";
    private const string TempFolderPrefix = "aml-input-";
    private const int Big5CodePage = 950;
    private const char ByteOrderMark = '\uFEFF';
    private const int MarkupSniffChars = 256;

    public static CaseInput ReadFiles(IReadOnlyDictionary<string, string> files) =>
        ReadFiles(files.ToDictionary(f => f.Key, f => Encoding.UTF8.GetBytes(f.Value)));

    public static CaseInput ReadFiles(IReadOnlyDictionary<string, byte[]> files)
    {
        var folder = Directory.CreateTempSubdirectory(TempFolderPrefix);
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

    /// <summary>A missing Profile file is not a problem here: that candidate is NOT_EVALUATED.</summary>
    public static (string File, string Problem)? RequiredFileProblem(IReadOnlyCollection<string> names, string? subjectCategory)
    {
        var caseFiles = names.Count(n => n.StartsWith(CaseInfoPrefix, StringComparison.Ordinal) && n.EndsWith(".txt", StringComparison.Ordinal));
        if (caseFiles != 1) return (CaseInfoFile, caseFiles == 0 ? CaseInputException.Missing : CaseInputException.Duplicate);
        if (subjectCategory is null) return null;
        var subjectFile = SubjectFileOf(subjectCategory);
        return names.Contains(subjectFile) ? null : (subjectFile, CaseInputException.Missing);
    }

    private static string SubjectFileOf(string subjectCategory) => subjectCategory == CustomerCategory ? CustomerFile : RelatedPartyFile;

    private static void Require(string folder, string? subjectCategory)
    {
        if (RequiredFileProblem(Directory.GetFiles(folder).Select(Path.GetFileName).OfType<string>().ToList(), subjectCategory) is var (file, problem))
            throw new CaseInputException(file, null, problem, $"{file}: {problem}");
    }

    private static T ReadOrMalformed<T>(string file, Func<T> read)
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

    /// <summary>Reads a case folder through a private copy; the source folder is never written.</summary>
    public static CaseInput Read(string folder)
    {
        var files = Directory.GetFiles(folder);
        var hashes = files.ToDictionary(f => Path.GetFileName(f), f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))));
        var copy = Directory.CreateTempSubdirectory(TempFolderPrefix);
        try
        {
            var findings = new List<string>();
            foreach (var file in files) CopyAsExport(file, copy.FullName, findings);
            return ReadExport(copy.FullName, findings) with { SourceHashes = hashes };
        }
        finally
        {
            copy.Delete(recursive: true);
        }
    }

    private static void CopyAsExport(string source, string folder, List<string> findings)
    {
        var known = KnownFile().IsMatch(ExportName(source));
        var target = Path.Combine(folder, known ? ExportName(source) : Path.GetFileName(source));
        if (File.Exists(target))
            throw new CaseInputException(Path.GetFileName(target), null, CaseInputException.Duplicate, $"Two input files read as {Path.GetFileName(target)}");
        if (!known) File.Copy(source, target);
        else if (IsHtmlName(source) || LooksLikeMarkup(source))
            File.WriteAllText(target, ReadOrMalformed(Path.GetFileName(source), () => HtmlExport.ToExportText(File.ReadAllBytes(source), HtmlExport.KindOf(Path.GetFileNameWithoutExtension(target)))));
        else
        {
            File.WriteAllText(target, Decode(File.ReadAllBytes(source), out var fallback));
            if (fallback) findings.Add($"{EncodingFallback}:{Path.GetFileName(source)}");
        }
    }

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static string Decode(byte[] bytes, out bool fallback)
    {
        fallback = false;
        try
        {
            return StrictUtf8.GetString(bytes).TrimStart(ByteOrderMark);
        }
        catch (DecoderFallbackException)
        {
            fallback = true;
            return Encoding.GetEncoding(Big5CodePage).GetString(bytes);
        }
    }

    private static bool IsHtmlName(string path) => Path.GetExtension(path).ToLowerInvariant() is ".html" or ".htm";

    private static string ExportName(string path) =>
        IsHtmlName(path) || Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileNameWithoutExtension(path) + ".txt"
            : Path.GetFileName(path);

    private static CaseInput ReadExport(string folder, IEnumerable<string> fileFindings)
    {
        Require(folder, null);
        var caseFile = Directory.GetFiles(folder, CaseInfoPrefix + "*.txt").Single();
        var (subjectRow, rows, records, caseFindings) = ReadOrMalformed(Path.GetFileName(caseFile), () => ReadCaseTable(File.ReadAllLines(caseFile)));
        Require(folder, subjectRow.Category);

        var subject = ReadSubject(folder, subjectRow);
        var caseLevelFindingsShownOnEveryHit = caseFindings.Concat(fileFindings).Concat(UnsupportedAttachments(folder));
        subject = subject with { Findings = [.. subject.Findings, .. caseLevelFindingsShownOnEveryHit] };

        var profileIds = rows.Select(r => r.ProfileId).Distinct().ToList();
        var profiles = ReadPerProfileFilesPresent(folder, profileIds, "名單資料", (_, path) => ReadProfile(path));
        var news = ReadPerProfileFilesPresent(folder, profileIds, "新聞資訊", ReadNews);
        return new CaseInput(subject, rows, profiles, news, ImportedDecisions(subjectRow, rows, records));
    }

    private static ScreenedSubject ReadSubject(string folder, SubjectRow subjectRow) =>
        subjectRow.Category == CustomerCategory
            ? ReadOrMalformed(CustomerFile, () => ReadCustomer(folder))
            : ReadOrMalformed(RelatedPartyFile, () => ReadRelatedParty(Path.Combine(folder, RelatedPartyFile), subjectRow.Category, subjectRow.ReferenceNumber));

    private static Dictionary<string, T> ReadPerProfileFilesPresent<T>(string folder, IEnumerable<string> profileIds, string filePrefix, Func<string, string, T> read) =>
        profileIds
            .Select(id => (id, path: Path.Combine(folder, $"{filePrefix}_{id}.txt")))
            .Where(x => File.Exists(x.path))
            .ToDictionary(x => x.id, x => ReadOrMalformed(Path.GetFileName(x.path), () => read(x.id, x.path)));

    private static List<ImportedDecision> ImportedDecisions(SubjectRow subjectRow, List<CandidateRow> rows, List<string> records)
    {
        var imported = new List<ImportedDecision>();
        if (subjectRow.ListMatch is { } listMatch) imported.Add(new("是否符合名單", listMatch));
        foreach (var reason in rows.Select(r => r.LegacyReason).OfType<string>().Distinct()) imported.Add(new("理由", reason));
        imported.AddRange(records.Select(r => new ImportedDecision(ProcessingRecord, r)));
        return imported;
    }

    public const string UnsupportedAttachment = "UNSUPPORTED_ATTACHMENT";
    public const string LegacyRecordBoundaryUnclear = "LEGACY_RECORD_BOUNDARY_UNCLEAR";
    public const string Unparseable = Findings.Unparseable;
    public const string FieldConflict = "FIELD_CONFLICT";
    public const string Ambiguous = Findings.Ambiguous;
    public const string EncodingFallback = "ENCODING_FALLBACK";
    public const string FieldAbsent = "FIELD_ABSENT";
    public const string FieldBlank = "FIELD_BLANK";
    public const string PlaceholderDash = "PLACEHOLDER_DASH";
    public const string PlaceholderText = "PLACEHOLDER_TEXT";
    public const string Malformed = "MALFORMED";
    private const string ProcessingRecord = "客戶檢核處理紀錄";

    [GeneratedRegex("^(案件資訊.*|名單資料_\\d+|新聞資訊_\\d+|客戶-背景資料|客戶-聯絡資料|客戶-關聯人背景資料)\\.txt$")]
    private static partial Regex KnownFile();

    private static IEnumerable<string> UnsupportedAttachments(string folder)
    {
        foreach (var path in Directory.GetFiles(folder))
        {
            var name = Path.GetFileName(path);
            if (name == GoldenCaseLabelFile) continue;
            if (!KnownFile().IsMatch(name) || LooksLikeMarkup(path)) yield return $"{UnsupportedAttachment}:{name}";
        }
    }

    private static bool LooksLikeMarkup(string path)
    {
        using var reader = new StreamReader(path);
        var head = new char[MarkupSniffChars];
        var n = reader.Read(head, 0, head.Length);
        return new string(head, 0, n).TrimStart(ByteOrderMark, ' ', '\r', '\n', '\t').StartsWith('<');
    }

    private static readonly string[] CustomerFields = ["本地名稱", "客戶名稱", "性別", "出生日期", "國籍 1", "證件號碼"];
    private static readonly string[] RelatedPartyFields = ["姓名/名稱", "性別", "出生日期", "國籍", "證件號碼"];

    private static IEnumerable<string> MissingFieldFindings(IEnumerable<(string Field, bool Present, string? Raw)> fields) =>
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

    private const int SubjectRowLine = 1;
    private const int SubjectCategoryColumn = 2;
    private const int ReferenceNumberColumn = 3;
    private const int ListMatchColumn = 5;
    private const string CandidateSection = "疑似符合的名單資料";

    private sealed record CandidateHeader(string[] Columns, bool FlagsReadable, int FirstRowLine, int MinCells);

    private static string[] Cells(string line) => line.Split('\t').Select(c => c.Trim()).ToArray();

    private static bool IsNumberedRow(string[] cells) => int.TryParse(Normalize.HalfWidth(cells[0]), out _);

    private static (SubjectRow, List<CandidateRow>, List<string> Records, List<string> Findings) ReadCaseTable(string[] lines)
    {
        var subject = ReadSubjectRow(lines[SubjectRowLine]);

        var sectionStart = Array.FindIndex(lines, l => l.StartsWith(CandidateSection, StringComparison.Ordinal));
        if (sectionStart < 0 || sectionStart + 1 >= lines.Length)
            throw new CaseInputException(CaseInfoFile, CandidateSection, CaseInputException.MissingSection, "案件資訊 has no 疑似符合的名單資料 section");
        var recordStart = Array.FindIndex(lines, l => l.Trim().StartsWith(ProcessingRecord, StringComparison.Ordinal));
        var sectionEnd = recordStart < 0 ? lines.Length : recordStart;

        var findings = new List<string>();
        var header = ReadCandidateHeader(lines, sectionStart, sectionEnd, findings);
        var rows = new List<CandidateRow>();
        for (var i = header.FirstRowLine; i < sectionEnd; i++)
            if (!string.IsNullOrWhiteSpace(lines[i]) && ReadCandidateRow(Cells(lines[i]), header, lineNumber: i + 1, findings) is { } row)
                rows.Add(row);

        var (records, recordFindings) = recordStart < 0 ? (new List<string>(), new List<string>()) : ReadProcessingRecord(lines[(recordStart + 1)..]);
        return (subject, rows, records, [.. findings, .. recordFindings]);
    }

    private static SubjectRow ReadSubjectRow(string line)
    {
        var cells = line.Split('\t');
        return new SubjectRow(
            cells[SubjectCategoryColumn].Trim(),
            Normalize.NullIfPlaceholder(cells.ElementAtOrDefault(ReferenceNumberColumn)),
            Normalize.NullIfPlaceholder(cells.ElementAtOrDefault(ListMatchColumn)));
    }

    private static CandidateHeader ReadCandidateHeader(string[] lines, int sectionStart, int sectionEnd, List<string> findings)
    {
        var header = Cells(lines[sectionStart + 1]);
        var secondLine = sectionStart + 2;
        // A second header row: sub-labels only (the .txt export), or the whole row with parent labels carried down (HTML rowspan).
        var sub = secondLine < sectionEnd && lines[secondLine].Trim().Length > 0 && !IsNumberedRow(Cells(lines[secondLine])) ? Cells(lines[secondLine]) : null;
        var columns = sub is null ? header
            : sub.Length == header.Length ? sub
            : header.SelectMany(h => SubHeaders.TryGetValue(h, out var kids) ? kids : [h]).ToArray();
        var flagsReadable = sub is null || sub.Where(c => !header.Contains(c)).SequenceEqual(header.Distinct().Where(SubHeaders.ContainsKey).SelectMany(h => SubHeaders[h]));
        if (!flagsReadable) findings.Add($"{Unparseable}:candidate_header");
        if (RequiredCandidateColumns.FirstOrDefault(r => !columns.Contains(r)) is { } absent)
            throw new CaseInputException(CaseInfoFile, CandidateSection, CaseInputException.MissingColumn, $"疑似符合的名單資料 has no {absent} column");
        var minCells = RequiredCandidateColumns.Max(r => Array.IndexOf(columns, r)) + 1;
        var firstRowLine = sub is null ? secondLine : secondLine + 1;
        return new CandidateHeader(columns, flagsReadable, firstRowLine, minCells);
    }

    private static CandidateRow? ReadCandidateRow(string[] cells, CandidateHeader header, int lineNumber, List<string> findings)
    {
        if (cells.Length < header.MinCells || !IsNumberedRow(cells))
        {
            findings.Add($"{Unparseable}:candidate_row:{lineNumber}");
            return null;
        }
        string? Column(string name) => Array.IndexOf(header.Columns, name) is var k and >= 0 ? cells.ElementAtOrDefault(k) : null;
        var wider = cells.Length > header.Columns.Length;
        if (wider) findings.Add($"{Unparseable}:candidate_row:{lineNumber}");
        var rca = wider ? FlagValue.Unparseable : Normalize.Flag(Column("Is RCA?"));
        var flags = wider || !header.FlagsReadable ? new ListFlags(FlagValue.Unparseable, FlagValue.Unparseable, FlagValue.Unparseable, rca)
            : new ListFlags(Normalize.Flag(Column("PEP")), Normalize.Flag(Column("黑名單")), Normalize.Flag(Column("負面新聞")), rca);
        return new CandidateRow(Column("姓名/名稱")!, Normalize.HalfWidth(Column("名單資料編號")!), Normalize.NullIfPlaceholder(Column("理由")), rca == FlagValue.Yes, flags);
    }

    private static (List<string>, List<string>) ReadProcessingRecord(string[] lines)
    {
        var body = lines.SkipWhile(string.IsNullOrWhiteSpace).ToList();
        var table = body.TakeWhile(l => l.Contains('\t', StringComparison.Ordinal)).ToList();
        var textAfterTable = body.Skip(table.Count).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        var records = table.Skip(1).Select(l => string.Join(" | ", l.Split('\t').Select(c => c.Trim()).Where(c => c.Length > 0))).ToList();
        var headingWithoutRows = table.Count < 2;
        var boundaryUnclear = headingWithoutRows || textAfterTable.Count > 0;
        return (records, boundaryUnclear ? [LegacyRecordBoundaryUnclear] : []);
    }

    private static readonly HashSet<string> CustomerLabels =
    [
        "基本資料", "詳細資料", "客戶號碼", "客戶別名", "性別", "年收入", "稅號/統一編號", "客戶名稱", "出生日期", "證件種類", "國籍 1",
        "發照地", "本地名稱", "出生地點", "證件號碼", "國籍 2", "客戶類型", "職業",
    ];

    private const int MinTaxIdLength = 8;

    // Customer files: label line followed by value line; a blank value shows as the next label.
    private static ScreenedSubject ReadCustomer(string folder)
    {
        var background = File.ReadAllLines(Path.Combine(folder, CustomerFile)).Select(l => l.Trim()).ToArray();
        string? RawValue(string label)
        {
            var at = Array.IndexOf(background, label);
            if (at < 0 || at + 1 >= background.Length) return null;
            var next = background[at + 1];
            return CustomerLabels.Contains(next) ? string.Empty : next;
        }
        string? Value(string label) => Normalize.NullIfPlaceholder(RawValue(label));

        var findings = MissingFieldFindings(CustomerFields.Select(f => (f, Array.IndexOf(background, f) >= 0, RawValue(f)))).ToList();
        var names = new[] { Value("本地名稱"), Value("客戶名稱") }.OfType<string>().ToList();
        var nationalities = new[] { Country("國籍 1", Value("國籍 1"), findings), Country("國籍 2", Value("國籍 2"), findings) }.OfType<string>().ToList();
        var docNumber = Value("證件號碼");
        var taxId = Value("稅號/統一編號") switch
        {
            null => null,
            var t when t.Length < MinTaxIdLength => new FieldValue(t, ValueState.Ambiguous, Malformed),
            var t => new FieldValue(t, ValueState.Present),
        };

        var contactPath = Path.Combine(folder, ContactFile);
        var addresses = File.Exists(contactPath) ? ReadOrMalformed(ContactFile, () => ReadContactAddresses(contactPath, findings)) : [];

        var dob = Date("出生日期", Value("出生日期"), findings);

        return new ScreenedSubject(
            SubjectRole.Customer, null, names, Normalize.Gender(Value("性別")), dob,
            nationalities, docNumber is null ? null : new Document(DocumentType(Value("證件種類")), docNumber, IssuingCountry(Value("發照地"), findings)),
            null, taxId, Occupation(Value("職業")), Dedupe(addresses, out var dup), AddressFindings(addresses, dup, findings), PartyType: Value("客戶類型"));
    }

    private const string CountryHeadingSuffix = "(國家)";

    private static bool IsCountryHeading(string line) => line.EndsWith(CountryHeadingSuffix, StringComparison.Ordinal);

    // Contact file: a "<role>(國家)" heading, then "地址" and the address, then the block's own "國家" label and value.
    private static List<Address> ReadContactAddresses(string path, List<string> findings)
    {
        var contact = File.ReadAllLines(path).Select(l => l.Trim()).ToArray();
        var addresses = new List<Address>();
        for (var i = 0; i + 2 < contact.Length; i++)
            if (IsCountryHeading(contact[i]) && contact[i + 1] == "地址" && Normalize.NullIfPlaceholder(contact[i + 2]) is { } raw)
                addresses.Add(MakeAddress(contact[i][..^CountryHeadingSuffix.Length], raw, BlockCountry(contact, i), findings));
        return addresses;
    }

    private static string? BlockCountry(string[] contact, int heading)
    {
        var nextHeading = Array.FindIndex(contact, heading + 1, IsCountryHeading);
        var blockEnd = nextHeading < 0 ? contact.Length : nextHeading;
        var afterAddress = heading + 3;
        var countryLabel = Array.FindIndex(contact, afterAddress, blockEnd - afterAddress, l => l == "國家");
        return countryLabel >= 0 && countryLabel + 1 < contact.Length ? Normalize.NullIfPlaceholder(contact[countryLabel + 1]) : null;
    }

    private static List<string> AddressFindings(List<Address> addresses, bool duplicated, List<string> findings)
    {
        if (duplicated) findings.Add(Findings.DuplicatedAddressLine);
        findings.AddRange(addresses.Where(a => a.Conflict).Select(a => $"{FieldConflict}:{a.Role}").Distinct());
        return findings;
    }

    private static ScreenedSubject ReadRelatedParty(string path, string relationship, string? referenceNumber)
    {
        var fields = ReadKeyValueLines(path);
        string? Value(string key) => Normalize.NullIfPlaceholder(fields.FirstOrDefault(f => f.Key == key).Value);

        var findings = MissingFieldFindings(RelatedPartyFields
            .Select(k => (k, fields.Any(f => f.Key == k), fields.FirstOrDefault(f => f.Key == k).Value))).ToList();
        var addresses = fields
            .Where(f => f.Value is not null && AddressKey().IsMatch(f.Key))
            .Select(f =>
            {
                var role = AddressKey().Match(f.Key).Groups[1].Value;
                return MakeAddress(role, f.Value!, Value(role + CountryHeadingSuffix), findings);
            })
            .ToList();
        var docNumber = Value("證件號碼");
        var dob = Date("出生日期", Value("出生日期"), findings);

        return new ScreenedSubject(
            SubjectRole.RelatedParty, relationship, new[] { Value("姓名/名稱") }.OfType<string>().ToList(),
            Normalize.Gender(Value("性別")), dob,
            new[] { Country("國籍", Value("國籍"), findings) }.OfType<string>().ToList(),
            docNumber is null ? null : new Document(DocumentType(Value("證件種類")), docNumber, IssuingCountry(Value("發照地"), findings)),
            referenceNumber ?? Value("參考號碼"), null, null, Dedupe(addresses, out var dup), AddressFindings(addresses, dup, findings), PartyType: Value("類型"));
    }

    // Related-party file: "key ：" (or "key :") lines, the value on the following line(s) joined with a space; an empty value is followed directly by the next key.
    private static List<(string Key, string? Value)> ReadKeyValueLines(string path)
    {
        var fields = new List<(string Key, string? Value)>();
        foreach (var line in File.ReadAllLines(path))
        {
            var trimmed = line.Trim();
            if (trimmed.EndsWith('：') || trimmed.EndsWith(':')) fields.Add((trimmed[..^1].Trim(), null));
            else if (fields.Count > 0 && trimmed.Length > 0) fields[^1] = (fields[^1].Key, fields[^1].Value is { } v ? $"{v} {trimmed}" : trimmed);
        }
        return fields;
    }

    [GeneratedRegex("^(永久地址|通訊位址) \\d$")]
    private static partial Regex AddressKey();

    private static Address MakeAddress(string role, string raw, string? countryField, List<string> findings)
    {
        if (Normalize.IsPostalCodeOnly(raw)) return new Address(role, raw, null, null, ValueState.Ambiguous, Findings.PostalCodeOnly);
        var (normalized, finding) = Normalize.TaiwanAddress(raw);
        var textNamesTaiwanCity = TaiwanCity().IsMatch(normalized);
        var countryFromText = textNamesTaiwanCity ? "TW" : null;
        var country = countryField is null ? countryFromText : Country($"{role}{CountryHeadingSuffix}", countryField, findings);
        var fieldContradictsText = country is not null && countryField is not null && (country == "TW" ? !raw.Any(Normalize.IsCjk) : textNamesTaiwanCity);
        return new Address(role, raw, normalized, country, ValueState.Present, finding) { Conflict = fieldContradictsText };
    }

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

    private static string BankDocumentTypeOfProfileId(string type, string? country) => (type.Trim().ToUpperInvariant(), country) switch
    {
        ("NATIONAL ID" or "NATIONAL IDENTIFICATION NUMBER", "TW") => "TW_NATIONAL_ID",
        ("RESIDENT CERTIFICATE", "TW") => "TW_RESIDENT_CERTIFICATE",
        ("PASSPORT" or "PASSPORT NO.", _) => "PASSPORT",
        ("REGISTRATION NUMBER" or "COMPANY REGISTRATION" or "COMPANY IDENTIFICATION NUMBER" or "統一編號", _) => "REGISTRATION_NUMBER",
        ("IMO" or "IMO NUMBER" or "IMO NO.", _) => "IMO_NUMBER",
        var (other, _) => other,
    };

    private static string? Occupation(string? raw) => raw is null ? null : TrailingCode().Replace(raw, "");

    [GeneratedRegex("\\(\\d+\\)$")]
    private static partial Regex TrailingCode();

    public static SourceProfile ReadProfile(string path) => ParseProfile(File.ReadAllText(path));

    /// <summary>The text a Profile file's content hash is taken on: an HTML page converted, an export as it is.</summary>
    public static string ProfileExportText(string content) =>
        content.TrimStart().TrimStart(ByteOrderMark).StartsWith('<') ? HtmlExport.ToExportText(content, HtmlExport.Kind.Profile) : content;

    private const string ProfileIdKey = "Profile_ID";
    private const string OriginalScriptNameRow = "OSN";
    private const string OfficialListHeaderCell = "機關";
    private const int NumberedNameCells = 3;
    private const int OriginalScriptNameCells = 2;
    private const int CountryRowCells = 3;
    private const int DateRowCells = 2;
    private const int RoleRowCells = 3;
    private const int RelationshipRowCells = 5;
    private const int DocumentRowCells = 4;
    private const int OfficialListRowCells = 5;
    private const int AddressRowCells = 3;
    private const int CategoryRowCells = 2;
    private const int SourceRowCells = 2;
    private const int OtherIdentifierRowCells = 3;

    private sealed class ProfileTables(Dictionary<string, List<string[]>> tables)
    {
        public List<string> Findings { get; } = [];

        public IEnumerable<KeyValuePair<string, List<string[]>>> All => tables;

        public List<string[]> Table(string key) => tables.TryGetValue(key, out var rows) ? rows : [];

        public List<string[]> CompleteNumberedRows(string key, int cells, IEnumerable<string[]>? source = null)
        {
            var numbered = (source ?? Table(key)).Where(IsNumberedRow).ToList();
            if (numbered.Any(r => r.Length < cells)) Findings.Add($"{Unparseable}:{key}");
            return numbered.Where(r => r.Length >= cells).ToList();
        }
    }

    /// <summary>One Profile file, as the export or as an HTML page: "key:\tvalue" lines, or "key:" followed by a tab-separated table.</summary>
    public static SourceProfile ParseProfile(string content)
    {
        var (scalars, tableMap) = ReadProfileSections(ProfileExportText(content));
        var tables = new ProfileTables(tableMap);
        var findings = tables.Findings;

        var names = ReadNames(tables);
        var countries = tables.CompleteNumberedRows("國家資料", CountryRowCells);
        var dates = tables.CompleteNumberedRows("日期", DateRowCells);
        var datesOfBirth = ReadDatesOfBirth(dates, findings);
        var roles = ReadRoles(tables);
        var relationships = ReadRelationships(tables);
        var death = ReadDateOfDeath(dates, findings);
        var documents = ReadDocuments(tables);
        var official = ReadOfficialList(tables);
        var addressRows = tables.CompleteNumberedRows("地址", AddressRowCells);
        var citizenship = CountriesOfKind(countries, "Citizenship", findings);
        var addressCountries = MappedAddressCountries(addressRows, findings);
        var allegation = CountriesOfKind(countries, "Country of Reported Allegation", findings);
        var categories = tables.CompleteNumberedRows("類別", CategoryRowCells)
            .Select(r => new SourceCategory(r[1], Normalize.NullIfPlaceholder(r.ElementAtOrDefault(2)))).ToList();
        var sources = tables.CompleteNumberedRows("來源", SourceRowCells)
            .Select(r => Source($"DJ:{scalars[ProfileIdKey]}#source-{r[0]}", r[1], r.ElementAtOrDefault(2))).ToList();
        var otherIds = ReadOtherIdentifiers(tables);

        return new SourceProfile(
            ProfileKey: ProfileKeys.KeyOf(scalars[ProfileIdKey]),
            Gender: Normalize.Gender(scalars.GetValueOrDefault("性別")),
            Names: names.Distinct().ToList(),
            DatesOfBirth: datesOfBirth,
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
            AddressLines: addressRows.Select(AddressLine).ToList(),
            DateOfDeath: death is { Year: > 0 } ? death : null,
            RecordType: scalars.GetValueOrDefault("記錄類型", "Person"),
            AllegationCountries: allegation,
            OtherIdentifiers: otherIds);
    }

    private static (Dictionary<string, string> Scalars, Dictionary<string, List<string[]>> Tables) ReadProfileSections(string content)
    {
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
                tables[section].Add(Cells(line));
            }
        }
        return (scalars, tables);
    }

    private static List<string> ReadNames(ProfileTables tables)
    {
        var names = new List<string>();
        foreach (var key in new[] { "主要名稱", "拼寫變化" })
            foreach (var row in tables.Table(key).Where(r => IsNumberedRow(r) || r[0] == OriginalScriptNameRow))
            {
                var numbered = IsNumberedRow(row);
                if (row.Length < (numbered ? NumberedNameCells : OriginalScriptNameCells)) tables.Findings.Add($"{Unparseable}:{key}");
                else names.Add(numbered ? $"{row[2]} {row[1]}" : row[1]); // surname first, as the source lists it
            }
        return names;
    }

    private static List<PartialDate> ReadDatesOfBirth(List<string[]> dates, List<string> findings)
    {
        var raw = dates.Where(r => r[1] == "Date of Birth").Select(r => r.ElementAtOrDefault(2)).ToList();
        var parsed = raw.Select(PartialDate.Parse).OfType<PartialDate>().ToList();
        if (parsed.Count > 1) findings.Add(Findings.ProfileMultipleDob);
        if (parsed.Count < raw.Count) findings.Add($"{Unparseable}:Date of Birth");
        return parsed;
    }

    private static PartialDate? ReadDateOfDeath(List<string[]> dates, List<string> findings)
    {
        var rows = dates.Where(r => r[1] == "Date of Death").ToList();
        var death = rows.Select(r => PartialDate.Parse(r.ElementAtOrDefault(2))).FirstOrDefault(d => d is not null);
        if (rows.Count > 0 && death is null) findings.Add($"{Unparseable}:Date of Death");
        return death;
    }

    private static List<ProfileRole> ReadRoles(ProfileTables tables) =>
        tables.CompleteNumberedRows("職位", RoleRowCells)
            .Select(r => new ProfileRole(r[1], r[2], RoleDate(r.ElementAtOrDefault(3), tables.Findings), RoleDate(r.ElementAtOrDefault(4), tables.Findings)))
            .ToList();

    private static PartialDate? RoleDate(string? raw, List<string> findings)
    {
        var date = PartialDate.Parse(raw);
        if (date is null && Normalize.NullIfPlaceholder(raw) is { } value && !IsOpenTermEndingInFutureYear(value)) findings.Add($"{Unparseable}:職位");
        return date;
    }

    private static bool IsOpenTermEndingInFutureYear(string value) =>
        value.Length >= 4 && int.TryParse(value[..4], out var year) && year > DateTime.UtcNow.Year;

    private static List<ProfileRelationship> ReadRelationships(ProfileTables tables) =>
        tables.All.Where(t => t.Value.FirstOrDefault()?.Contains(ProfileIdKey) == true)
            .SelectMany(t => tables.CompleteNumberedRows(t.Key, RelationshipRowCells, t.Value))
            .Select(r => new ProfileRelationship(r[1], r[2], r[4], r[3] == "Entity")).ToList();

    // ponytail: the ID table layout (編號 類型 號碼 國家) is assumed until an export shows one; no sample has it yet.
    private static List<Document> ReadDocuments(ProfileTables tables) =>
        tables.CompleteNumberedRows("身分證明", DocumentRowCells)
            .Select(r =>
            {
                var country = Country("身分證明", r[3], tables.Findings);
                return new Document(BankDocumentTypeOfProfileId(r[1], country), r[2], country);
            })
            .ToList();

    private static OfficialListEntry? ReadOfficialList(ProfileTables tables)
    {
        var rows = tables.Table("名單資訊").Where(r => r[0] != OfficialListHeaderCell).ToList();
        if (rows.Any(r => r.Length < OfficialListRowCells)) tables.Findings.Add($"{Unparseable}:名單資訊");
        return rows.Where(r => r.Length >= OfficialListRowCells)
            .Select(r => new OfficialListEntry(r[0], r[1], r[2], Normalize.NullIfPlaceholder(r[4]))).FirstOrDefault();
    }

    // ponytail: the 其他識別碼 table layout (編號 類型 號碼) is assumed; no sample has one yet.
    private static List<OtherIdentifier> ReadOtherIdentifiers(ProfileTables tables) =>
        tables.CompleteNumberedRows("其他識別碼", OtherIdentifierRowCells).Select(r => new OtherIdentifier(r[1], r[2])).ToList();

    private static List<string> CountriesOfKind(List<string[]> countries, string kind, List<string> findings) =>
        countries.Where(r => r[1] == kind).Select(r => Country(kind, r[2], findings)).OfType<string>().ToList();

    private static List<string> MappedAddressCountries(List<string[]> addressRows, List<string> findings) =>
        addressRows.Select(r => Country("地址", r[^1], findings)).OfType<string>().ToList();

    private static string AddressLine(string[] row) => string.Join(", ", row[1..^1].Where(x => Normalize.NullIfPlaceholder(x) is not null));

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
