using System.Buffers.Text;
using System.Globalization;
using System.Text;

namespace Aml.Core.Cases;

/// <summary>Opaque keyset page token: the sort key (created at, id) of the last item on the page.</summary>
public static class PageToken
{
    public const int DefaultSize = 20;
    public const int MaxSize = 100;

    /// <summary>The first <paramref name="size"/> of rows read one past the page, and the token for the next page when that extra row came back.</summary>
    public static (List<T> Page, string? Next) Page<T>(IReadOnlyList<T> rows, int size, Func<T, (DateTimeOffset At, string Id)> key)
    {
        var page = rows.Take(size).ToList();
        return (page, rows.Count > size && key(page[^1]) is var (at, id) ? Encode(at, id) : null);
    }

    public static string Encode(DateTimeOffset at, string id) =>
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{at.UtcTicks}|{id}")));

    /// <summary>False for a token this API did not issue (not base64url, or not a tick count and an id).</summary>
    public static bool TryDecode(string token, out (DateTimeOffset At, string Id) key)
    {
        key = default;
        if (!Base64Url.IsValid(token)) return false;
        string text;
        try
        {
            text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(Base64Url.DecodeFromChars(token));
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
        var bar = text.IndexOf('|', StringComparison.Ordinal);
        if (bar <= 0 || bar == text.Length - 1
            || !long.TryParse(text.AsSpan(0, bar), NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            || ticks > DateTimeOffset.MaxValue.UtcTicks) return false;
        key = (new DateTimeOffset(ticks, TimeSpan.Zero), text[(bar + 1)..]);
        return true;
    }
}
