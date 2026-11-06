using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Aml.Services;

public static partial class ProfileFiles
{
    private const char ByteOrderMark = '﻿';

    [GeneratedRegex(@"^名單資料_(\d+)\.(txt|html?)$", RegexOptions.IgnoreCase)]
    private static partial Regex ProfileFile();

    public static string? IdOf(string fileName) =>
        ProfileFile().Match(Path.GetFileName(fileName)) is { Success: true } m ? m.Groups[1].Value : null;

    /// <summary>CRLF and LF copies, or the .txt and .html of the same Profile, hash as one version.</summary>
    public static string Hash(string content, Func<string, string>? exportText)
    {
        var text = (exportText ?? (s => s))(content.TrimStart(ByteOrderMark)).TrimStart(ByteOrderMark);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(WithLfAndNoTrailingBlanks(text))));
    }

    private static string WithLfAndNoTrailingBlanks(string text) =>
        string.Join('\n', text.ReplaceLineEndings("\n").Split('\n').Select(l => l.TrimEnd())).TrimEnd('\n');
}
