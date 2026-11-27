using System.Text;

namespace Aml.Core.Normalization;

public static class CjkText
{
    public static string[] Sentences(string text) => text.Split('。', '！', '？', '\n', '；');

    public static bool IsCjk(char c) => c is >= CjkExtensionAStart and <= CjkUnifiedIdeographsEnd;

    /// <summary>One of the commonest Taiwanese surnames (the stage rules' list), for readers outside Core.</summary>
    public static bool IsCommonSurname(char c) => Screening.Stage.StageGlossary.CommonSurnames.Contains(c, StringComparison.Ordinal);

    private const char CjkExtensionAStart = '㐀';
    private const char CjkUnifiedIdeographsEnd = '鿿';

    /// <summary>
    /// A masked name (王○○, 王Ｏ明) with every masking character written ○: sources write the mask as ○, 〇, ◯, Ｏ or O.
    /// A name is masked when it holds an ideograph and every other character is one of those; any other name is returned as is.
    /// </summary>
    public static string MaskedForm(string name) =>
        name.Any(IsCjk) && name.Any(IsMask) && name.All(c => IsCjk(c) || IsMask(c))
            ? string.Concat(name.Select(c => IsMask(c) ? '○' : c)) : name;

    private static bool IsMask(char c) => c is '○' or '〇' or '◯' or 'Ｏ' or 'O';

    /// <summary>
    /// A name that hides who it is: one holding 某 (王某某), or an ideograph name whose other characters are all masks,
    /// those of <see cref="MaskedForm"/> or ＊, *, Ｘ, X (王○明, 王**, 王XX). A Latin name is never masked.
    /// </summary>
    public static bool IsMasked(string name) =>
        name.Contains('某', StringComparison.Ordinal)
        || (name.Any(IsCjk) && name.Any(IsNameMask) && name.All(c => IsCjk(c) || IsNameMask(c)));

    private static bool IsNameMask(char c) => IsMask(c) || c is '＊' or '*' or 'Ｘ' or 'X';

    public static string HalfWidth(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s) sb.Append(c is >= '０' and <= '９' ? (char)(c - '０' + '0') : c);
        return sb.ToString();
    }
}
