using Aml.Core.Subjects;
using Aml.Infra.Txt;

namespace Aml.Api.Endpoints;

/// <summary>Upload reading shared by the endpoints: accepted extensions, size limit and decoding.</summary>
internal static class Uploads
{
    public const int MaxBytes = 5 * 1024 * 1024;

    public static bool Accepted(string name) => Path.GetExtension(name).ToLowerInvariant() is ".txt" or ".html" or ".htm";

    // Reads uploaded files in memory as raw bytes (file names reduced to their last segment).
    public static async Task<(Dictionary<string, byte[]>? Bytes, string? Error)> ReadBytesAsync(IFormFileCollection files, CancellationToken ct)
    {
        if (files.Count == 0) return (null, "No files uploaded");
        if (files.Any(f => !Accepted(f.FileName) || f.Length > MaxBytes))
            return (null, "Only .txt, .html or .htm files up to 5 MB are accepted");
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
    public static async Task<(Dictionary<string, string>? Contents, string? Error)> ReadFilesAsync(IFormFileCollection files, CancellationToken ct)
    {
        var (bytes, error) = await ReadBytesAsync(files, ct);
        return (bytes?.ToDictionary(b => b.Key, b => TxtCaseReader.Decode(b.Value, out _)), error);
    }

    // Reads the upload and parses its raw bytes through TxtCaseReader.ReadFiles; the decoded contents are kept with the revision.
    public static async Task<(CaseInput? Input, Dictionary<string, string>? Contents, string? Error)> ReadUploadAsync(IFormFileCollection files, CancellationToken ct)
    {
        var (bytes, error) = await ReadBytesAsync(files, ct);
        if (bytes is null) return (null, null, error);
        try
        {
            return (TxtCaseReader.ReadFiles(bytes), bytes.ToDictionary(b => b.Key, b => TxtCaseReader.Decode(b.Value, out _)), null);
        }
        catch (Exception e) when (TxtCaseReader.IsInputError(e))
        {
            return (null, null, "Input files are incomplete or not in the expected .txt layout");
        }
    }
}
