using Aml.Core.Subjects;
using Aml.Infra.Txt;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Aml.Api.Endpoints;

/// <summary>Upload reading shared by the endpoints: accepted extensions, size limit and decoding.</summary>
internal static class Uploads
{
    public const int MaxBytes = 5 * 1024 * 1024;

    public static bool Accepted(string name) => Path.GetExtension(name).ToLowerInvariant() is ".txt" or ".html" or ".htm";

    /// <summary>An item with both ids and accepted files of at most 5 MB each (a UTF-8 byte count is never below the character count, so the cheap length check runs first).</summary>
    public static bool ValidItem(Aml.Services.BatchItemRequest? i) =>
        i is not null && !string.IsNullOrWhiteSpace(i.SubjectRef) && !string.IsNullOrWhiteSpace(i.TriggerEventId) && i.Files is { Count: > 0 }
        && i.Files.All(f => Accepted(f.Key) && Path.GetFileName(f.Key) == f.Key && f.Key.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && f.Value is not null && f.Value.Length <= MaxBytes && System.Text.Encoding.UTF8.GetByteCount(f.Value) <= MaxBytes);

    // Reads uploaded files in memory as raw bytes (file names reduced to their last segment).
    public static async Task<(Dictionary<string, byte[]>? Bytes, ProblemHttpResult? Error)> ReadBytesAsync(IFormFileCollection files, CancellationToken ct)
    {
        if (files.Count == 0)
            return (null, Problems.Of(Problems.InvalidRequest, "No files uploaded", "Send the export files as multipart form field 'files'."));
        if (files.Where(f => !Accepted(f.FileName) || f.Length > MaxBytes).Select(f => new { file = f.FileName, size = f.Length }).ToArray() is { Length: > 0 } refused)
            return (null, Problems.Of(Problems.UnsupportedFile, "Only .txt, .html or .htm files up to 5 MB are accepted",
                "Send only .txt, .html or .htm files of at most 5 MB each.", refused));
        var bytes = new Dictionary<string, byte[]>();
        foreach (var f in files)
        {
            using var buffer = new MemoryStream();
            await f.CopyToAsync(buffer, ct);
            bytes[Path.GetFileName(f.FileName)] = buffer.ToArray();
        }
        return (bytes, null);
    }

    // Uploaded files as text: strict UTF-8, else CP950 (Big5), the same decoding the case reader applies.
    public static async Task<(Dictionary<string, string>? Contents, ProblemHttpResult? Error)> ReadFilesAsync(IFormFileCollection files, CancellationToken ct)
    {
        var (bytes, error) = await ReadBytesAsync(files, ct);
        return (bytes?.ToDictionary(b => b.Key, b => TxtCaseReader.Decode(b.Value, out _)), error);
    }

    // Reads the upload and parses its raw bytes through TxtCaseReader.ReadFiles; the decoded contents are kept with the revision.
    public static async Task<(CaseInput? Input, Dictionary<string, string>? Contents, ProblemHttpResult? Error)> ReadUploadAsync(IFormFileCollection files, CancellationToken ct)
    {
        var (bytes, error) = await ReadBytesAsync(files, ct);
        if (bytes is null) return (null, null, error);
        try
        {
            return (TxtCaseReader.ReadFiles(bytes), bytes.ToDictionary(b => b.Key, b => TxtCaseReader.Decode(b.Value, out _)), null);
        }
        catch (CaseInputException e)
        {
            return (null, null, Problems.Input(e));
        }
    }
}
