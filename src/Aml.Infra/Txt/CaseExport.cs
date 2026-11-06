using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Aml.Infra.Html;

namespace Aml.Infra.Txt;

/// <summary>
/// One case's input files as the .txt export layout reads them, held in memory: each export file under its export name as
/// decoded text (an HTML page converted by HtmlExport), and every other file as an attachment, kept as it came. Files are
/// taken in name order ignoring case, the order a Windows folder lists them in; two names that differ only by case are one
/// file there, so here they are a duplicate on every platform.
/// </summary>
internal sealed partial class CaseExport
{
    static CaseExport() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); // Big5 fallback

    public const string UnsupportedAttachment = "UNSUPPORTED_ATTACHMENT";
    public const string EncodingFallback = "ENCODING_FALLBACK";

    private const string GoldenCaseLabelFile = "expected.yaml";
    private const char ByteOrderMark = (char)0xFEFF;
    private const int MarkupSniffChars = 256;
    private const int Big5CodePage = 950;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly Dictionary<string, string> _exportTexts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, byte[]> _attachments = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _findings = [];

    /// <summary>SHA-256 of each source file's bytes, by source file name.</summary>
    public IReadOnlyDictionary<string, string> SourceHashes { get; }

    /// <summary>File-level findings, such as a file read in the Big5 fallback encoding.</summary>
    public IReadOnlyList<string> Findings => _findings;

    /// <summary>Every file: export files by export name, attachments by their own name.</summary>
    public IReadOnlyCollection<string> Names { get; }

    private CaseExport(List<(string Name, byte[] Content)> ordered)
    {
        foreach (var (name, content) in ordered) Add(name, content);
        SourceHashes = ordered.ToDictionary(s => s.Name, s => Convert.ToHexString(SHA256.HashData(s.Content))).AsReadOnly();
        Names = [.. _exportTexts.Keys, .. _attachments.Keys];
    }

    public static CaseExport Of(IEnumerable<(string Name, byte[] Content)> sources)
    {
        var ordered = sources.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, _) in ordered)
            if (!seen.Add(name)) throw new CaseInputException(name, null, CaseInputException.Duplicate, $"Two input files named {name}");
        return new CaseExport(ordered);
    }

    private void Add(string name, byte[] content)
    {
        var known = KnownFile().IsMatch(ExportName(name));
        var target = known ? ExportName(name) : name;
        if (Contains(target))
            throw new CaseInputException(target, null, CaseInputException.Duplicate, $"Two input files read as {target}");
        if (!known) _attachments[target] = content;
        else if (IsHtmlName(name) || LooksLikeMarkup(content))
            _exportTexts[target] = CaseInputException.ReadOrMalformed(name, () => HtmlExport.ToExportText(content, HtmlExport.KindOf(Path.GetFileNameWithoutExtension(target)))).TrimStart(ByteOrderMark);
        else
        {
            _exportTexts[target] = Decode(content, out var fallback);
            if (fallback) _findings.Add($"{EncodingFallback}:{name}");
        }
    }

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

    public bool Contains(string name) => _exportTexts.ContainsKey(name) || _attachments.ContainsKey(name);

    /// <summary>
    /// A file's text: an export file as decoded, an attachment as File.ReadAllText reads a file (UTF-8 unless a byte order
    /// mark says otherwise), for a case table that names a file outside the export layout.
    /// </summary>
    /// <exception cref="KeyNotFoundException">No file has this name; check <see cref="Contains"/> first.</exception>
    public string Text(string name)
    {
        if (_exportTexts.TryGetValue(name, out var text)) return text;
        using var reader = new StreamReader(new MemoryStream(_attachments[name]));
        return reader.ReadToEnd();
    }

    /// <summary>A file's lines as File.ReadAllLines splits them: on \r\n, \r or \n, with no empty line after a final break.</summary>
    public string[] Lines(string name)
    {
        var lines = new List<string>();
        using var reader = new StringReader(Text(name));
        while (reader.ReadLine() is { } line) lines.Add(line);
        return [.. lines];
    }

    /// <summary>Attachments, and export files that still hold markup, in name order.</summary>
    public IEnumerable<string> UnsupportedAttachments() =>
        _attachments.Keys.Concat(_exportTexts.Where(f => LooksLikeMarkup(f.Value)).Select(f => f.Key))
            .Where(name => name != GoldenCaseLabelFile)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(name => $"{UnsupportedAttachment}:{name}");

    [GeneratedRegex("^(案件資訊.*|名單資料_\\d+|新聞資訊_\\d+|客戶-背景資料|客戶-聯絡資料|客戶-關聯人背景資料)\\.txt$")]
    private static partial Regex KnownFile();

    private static bool IsHtmlName(string path) => Path.GetExtension(path).ToLowerInvariant() is ".html" or ".htm";

    private static string ExportName(string path) =>
        IsHtmlName(path) || Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileNameWithoutExtension(path) + ".txt"
            : Path.GetFileName(path);

    // Sniffs the head as a StreamReader decodes it: UTF-8 unless a byte order mark says otherwise.
    private static bool LooksLikeMarkup(byte[] content)
    {
        using var reader = new StreamReader(new MemoryStream(content));
        var head = new char[MarkupSniffChars];
        return LooksLikeMarkup(new string(head, 0, reader.Read(head, 0, head.Length)));
    }

    private static bool LooksLikeMarkup(string text) =>
        text.AsSpan(0, Math.Min(text.Length, MarkupSniffChars)).TrimStart([ByteOrderMark, ' ', '\r', '\n', '\t']).StartsWith('<');
}
