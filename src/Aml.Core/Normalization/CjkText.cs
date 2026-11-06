using System.Text;

namespace Aml.Core.Normalization;

public static class CjkText
{
    public static string[] Sentences(string text) => text.Split('。', '！', '？', '\n', '；');

    public static bool IsCjk(char c) => c is >= CjkExtensionAStart and <= CjkUnifiedIdeographsEnd;

    private const char CjkExtensionAStart = '㐀';
    private const char CjkUnifiedIdeographsEnd = '鿿';

    public static string HalfWidth(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s) sb.Append(c is >= '０' and <= '９' ? (char)(c - '０' + '0') : c);
        return sb.ToString();
    }
}
