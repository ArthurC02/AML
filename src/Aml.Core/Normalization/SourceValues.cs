using Aml.Core.Shared;

namespace Aml.Core.Normalization;

public static class SourceValues
{
    /// <summary>A PEP / 黑名單 / 負面新聞 / Is RCA? cell (extraction-and-normalization.md#source-flags).</summary>
    public static Subjects.FlagValue Flag(string? raw) => raw?.Trim() switch
    {
        null or "" => Subjects.FlagValue.Missing,
        var v when IsDash(v) => Subjects.FlagValue.Missing,
        var v when FlagMissing.Contains(v) => Subjects.FlagValue.Missing,
        var v when FlagYes.Contains(v) => Subjects.FlagValue.Yes,
        var v when FlagNo.Contains(v) => Subjects.FlagValue.No,
        _ => Subjects.FlagValue.Unparseable,
    };

    private static readonly HashSet<string> FlagYes = new(StringComparer.OrdinalIgnoreCase) { "是", "✓", "✔", "☑", "V", "Y", "Yes", "TRUE", "1", "checked", "on" };
    private static readonly HashSet<string> FlagNo = new(StringComparer.OrdinalIgnoreCase) { "否", "不是", "X", "✗", "×", "N", "No", "FALSE", "0", "unchecked", "off", "☐" };
    private static readonly HashSet<string> FlagMissing = new(StringComparer.OrdinalIgnoreCase) { "N/A", "NA" };

    /// <summary>ISO 3166-1 alpha-2 code (<see cref="Countries"/>), or null when the value does not map unambiguously (AMBIGUOUS).</summary>
    public static string? Country(string raw) => Countries.Lookup(raw);

    public static Gender? Gender(string? raw) => raw?.Trim() switch
    {
        "男" or "Male" => Shared.Gender.Male,
        "女" or "Female" => Shared.Gender.Female,
        _ => null,
    };

    public static string? NullIfPlaceholder(string? s) =>
        string.IsNullOrWhiteSpace(s) || IsPlaceholder(s) ? null : s.Trim();

    /// <summary>A country field's value, or null when it is a placeholder; a country code comes first, so NA is Namibia.</summary>
    public static string? NullIfPlaceholderCountry(string? s) =>
        s is not null && Countries.Lookup(s) is not null ? s.Trim() : NullIfPlaceholder(s);

    /// <summary>
    /// Text whose sole meaning is "no data". Only the whole value counts: 「未提供（居住國臺灣）」, 無錫市, 無業 and 無國籍 are values.
    /// </summary>
    public static bool IsPlaceholder(string s) => Placeholders.Contains(s.Trim());

    public static bool IsDash(string s) => s.Trim() is "-" or "–" or "—" or "－";

    private static readonly HashSet<string> Placeholders = new(StringComparer.OrdinalIgnoreCase)
    {
        "-", "–", "—", "－", "未提供", "未記載", "不詳", "無", "N/A", "NA", "n.a.", "none", "null",
    };
}
