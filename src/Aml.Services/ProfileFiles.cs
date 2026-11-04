using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Aml.Services;

/// <summary>Profile files in a Case's input (名單資料_&lt;id&gt;.txt or .html) and the content hash the list database uses.</summary>
public static partial class ProfileFiles
{
    [GeneratedRegex(@"^名單資料_(\d+)\.(txt|html?)$", RegexOptions.IgnoreCase)]
    private static partial Regex ProfileFile();

    public static string? IdOf(string fileName) =>
        ProfileFile().Match(Path.GetFileName(fileName)) is { Success: true } m ? m.Groups[1].Value : null;

    /// <summary>
    /// Hash of the normalized export text: one newline style, no BOM, no trailing blanks, so CRLF and LF copies, or the
    /// .txt and .html of the same Profile, are one version.
    /// </summary>
    /// <param name="exportText">Turns a Profile file (export or HTML page) into the export text.</param>
    public static string Hash(string content, Func<string, string>? exportText)
    {
        var text = (exportText ?? (s => s))(content.TrimStart('﻿')).TrimStart('﻿');
        var normalized = string.Join('\n', text.ReplaceLineEndings("\n").Split('\n').Select(l => l.TrimEnd())).TrimEnd('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }
}
