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

    public static bool Accepted(string name) => Path.GetExtension(name).ToLowerInvariant() is ".txt" or ".html" or ".htm";

    public static bool ValidItem(BatchItemRequest? item) =>
        item is not null
        && !string.IsNullOrWhiteSpace(item.SubjectRef)
        && !string.IsNullOrWhiteSpace(item.TriggerEventId)
        && item.Files is { Count: > 0 }
        && item.Files.All(f => Accepted(f.Key) && IsPlainFileName(f.Key) && f.Value is not null && WithinMaxBytes(f.Value));

    private static bool IsPlainFileName(string name) =>
        Path.GetFileName(name) == name && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    // A UTF-8 byte count is never below the character count, so the cheap length check runs first.
    private static bool WithinMaxBytes(string text) => text.Length <= MaxBytes && Encoding.UTF8.GetByteCount(text) <= MaxBytes;

    public static async Task<(Dictionary<string, byte[]>? Bytes, ProblemHttpResult? Error)> ReadBytesAsync(IFormFileCollection files, CancellationToken ct)
    {
        if (files.Count == 0)
            return (null, Problems.Of(Problems.InvalidRequest, "No files uploaded", "Send the export files as multipart form field 'files'."));
        if (files.Where(f => !Accepted(f.FileName) || f.Length > MaxBytes).Select(f => new { file = f.FileName, size = f.Length }).ToArray() is { Length: > 0 } refused)
            return (null, Problems.Of(Problems.UnsupportedFile, $"Only .txt, .html or .htm files up to {MaxSizeText} are accepted",
                $"Send only .txt, .html or .htm files of at most {MaxSizeText} each.", refused));
        var bytes = new Dictionary<string, byte[]>();
        foreach (var f in files)
        {
            using var buffer = new MemoryStream();
            await f.CopyToAsync(buffer, ct);
            bytes[Path.GetFileName(f.FileName)] = buffer.ToArray();
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
