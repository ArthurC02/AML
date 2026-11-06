using System.Buffers.Text;
using System.Globalization;
using System.Text;

namespace Aml.Core.Cases;

public static class PageToken
{
    public const int DefaultSize = 20;
    public const int MaxSize = 100;

    private const char Separator = '|';

    /// <summary>Expects the rows read one past the page: that extra row only signals a next page.</summary>
    public static (List<T> Page, string? Next) Page<T>(IReadOnlyList<T> rows, int size, Func<T, (DateTimeOffset At, string Id)> key)
    {
        var page = rows.Take(size).ToList();
        var hasNextPage = rows.Count > size;
        return (page, hasNextPage && key(page[^1]) is var (at, id) ? Encode(at, id) : null);
    }

    public static string Encode(DateTimeOffset at, string id) =>
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{at.UtcTicks}{Separator}{id}")));

    public static bool TryDecode(string token, out (DateTimeOffset At, string Id) key)
    {
        key = default;
        if (!TryDecodeText(token, out var text)) return false;
        var bar = text.IndexOf(Separator, StringComparison.Ordinal);
        if (bar <= 0 || bar == text.Length - 1
            || !long.TryParse(text.AsSpan(0, bar), NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            || ticks > DateTimeOffset.MaxValue.UtcTicks) return false;
        key = (new DateTimeOffset(ticks, TimeSpan.Zero), text[(bar + 1)..]);
        return true;
    }

    private static bool TryDecodeText(string token, out string text)
    {
        text = "";
        if (!Base64Url.IsValid(token)) return false;
        try
        {
            text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(Base64Url.DecodeFromChars(token));
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}
