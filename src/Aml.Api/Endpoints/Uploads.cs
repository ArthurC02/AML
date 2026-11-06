using System.Text;
using Aml.Core.Subjects;
using Aml.Infra.Txt;
using Aml.Services;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Aml.Api.Endpoints;

internal static class Uploads
{
    private const int BytesPerMegabyte = 1024 * 1024;

    public const int MaxBytes = 5 * BytesPerMegabyte;

    public static readonly string MaxSizeText = $"{MaxBytes / BytesPerMegabyte} MB";

    /// <summary>Longest subjectRef or triggerEventId: an identifier of the bank's systems, not free text.</summary>
    public const int MaxRefLength = 128;

    private const int MaxNameLength = 255, MaxShownNameLength = 100;

    public static readonly string FileFix =
        $"Send each file once, as .txt, .html or .htm of at most {MaxSizeText}, under a plain name (no folder, control or reserved characters \\ / : * ? \" < > |).";

    public static bool Accepted(string name) => Path.GetExtension(name).ToLowerInvariant() is ".txt" or ".html" or ".htm";

    /// <summary>Every problem of one item, in the shape of a problem's errors[]; empty when the item is usable.</summary>
    public static List<object> ItemErrors(BatchItemRequest? item)
    {
        if (item is null) return [new { parameter = "body" }];
        var errors = RefErrors(item.SubjectRef, item.TriggerEventId);
        if (item.Files is not { Count: > 0 }) errors.Add(new { parameter = "files" });
        else errors.AddRange(FileErrors(item.Files.Select(f => (f.Key, f.Value is null ? -1L : Utf8Size(f.Value))).ToList()));
        return errors;
    }

    /// <summary>A missing, overlong or control-character subjectRef or triggerEventId; one rule for every intake.</summary>
    public static List<object> RefErrors(string? subjectRef, string? triggerEventId) =>
        new[] { ("subjectRef", subjectRef), ("triggerEventId", triggerEventId) }
            .Where(r => string.IsNullOrWhiteSpace(r.Item2) || r.Item2.Length > MaxRefLength || r.Item2.Trim() != r.Item2 || r.Item2.Any(IsInvisible))
            .Select(r => (object)new { parameter = r.Item1, problem = string.IsNullOrWhiteSpace(r.Item2) ? "MISSING" : "MALFORMED" })
            .ToList();

    /// <summary>
    /// Files that cannot be read: type, size (-1: no content), a name that is not plain, or two names that read as one
    /// (letter case is ignored). The same rule for multipart and JSON; the name shown is cut to 100 characters.
    /// </summary>
    public static IEnumerable<object> FileErrors(IReadOnlyList<(string Name, long Size)> files)
    {
        var repeated = files.GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, size) in files)
        {
            var problem = !Accepted(name) ? "TYPE" : size < 0 ? "MISSING" : size > MaxBytes ? "SIZE"
                : !IsPlainFileName(name) ? "NAME" : repeated.Contains(name) ? "DUPLICATE" : null;
            if (problem is not null) yield return new { file = name.Length > MaxShownNameLength ? name[..(char.IsHighSurrogate(name[MaxShownNameLength - 1]) ? MaxShownNameLength - 1 : MaxShownNameLength)] : name, size = Math.Max(size, 0), problem };
        }
    }

    // A control or format character (U+200B, U+202E …): invisible in a log or a screen, so "A" and "A​" would look alike.
    private static bool IsInvisible(char c) => char.IsControl(c) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format;

    // One fixed character set, not the OS's: a name is accepted or refused the same on Windows and Linux. ".txt" alone has no name.
    private static bool IsPlainFileName(string name) =>
        name.Length is > 0 and <= MaxNameLength && Path.GetFileNameWithoutExtension(name).Trim().Length > 0
        && name.IndexOfAny(['\\', '/', ':', '*', '?', '"', '<', '>', '|']) < 0 && !name.Any(IsInvisible);

    // A UTF-8 byte count is never below the character count, so a text far over the limit is not counted.
    private static long Utf8Size(string text) => text.Length > MaxBytes ? text.Length : Encoding.UTF8.GetByteCount(text);

    /// <summary>The file name a client sent, without any folder (either separator, on any OS).</summary>
    private static string NameOf(string clientName) => clientName[(clientName.LastIndexOfAny(['/', '\\']) + 1)..];

    public static async Task<(Dictionary<string, byte[]>? Bytes, ProblemHttpResult? Error)> ReadBytesAsync(IFormFileCollection files, CancellationToken ct)
    {
        if (files.Count == 0)
            return (null, Problems.Of(Problems.InvalidRequest, "No files uploaded", "Send the export files as multipart form field 'files'.", new { parameter = "files" }));
        // Two files reading as one name are refused, not one dropped; a client's folder is never echoed back.
        var names = files.Select(f => NameOf(f.FileName)).ToList();
        if (FileErrors(names.Zip(files, (n, f) => (n, f.Length)).ToList()).ToArray() is { Length: > 0 } refused)
            return (null, Problems.Of(Problems.UnsupportedFile, "Some files cannot be read", FileFix, refused));
        var bytes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var (f, name) in files.Zip(names))
        {
            using var buffer = new MemoryStream();
            await f.CopyToAsync(buffer, ct);
            bytes[name] = buffer.ToArray();
        }
        return (bytes, null);
    }

    public static async Task<(Dictionary<string, string>? Contents, ProblemHttpResult? Error)> ReadFilesAsync(IFormFileCollection files, CancellationToken ct)
    {
        var (bytes, error) = await ReadBytesAsync(files, ct);
        return (bytes is null ? null : DecodeAll(bytes), error);
    }

    public static async Task<(CaseInput? Input, Dictionary<string, string>? Contents, ProblemHttpResult? Error)> ReadUploadAsync(IFormFileCollection files, CancellationToken ct)
    {
        var (bytes, error) = await ReadBytesAsync(files, ct);
        if (bytes is null) return (null, null, error);
        return (TxtCaseReader.ReadFiles(bytes), DecodeAll(bytes), null);
    }

    private static Dictionary<string, string> DecodeAll(Dictionary<string, byte[]> bytes) =>
        bytes.ToDictionary(b => b.Key, b => TxtCaseReader.Decode(b.Value, out _));
}
