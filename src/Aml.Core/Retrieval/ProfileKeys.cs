namespace Aml.Core.Retrieval;

/// <summary>
/// Profile key (&lt;namespace&gt;&lt;id&gt;) and Profile ID: routes take the ID, responses carry both. DJ: is the list vendor's
/// namespace and the default; WEB: is a person read from a received page (WEB:&lt;pageId&gt;-&lt;n&gt;, external-sources.md#common-path).
/// </summary>
public static class ProfileKeys
{
    public const string Prefix = "DJ:";
    public const string WebPrefix = "WEB:";

    private static readonly string[] Namespaces = [Prefix, WebPrefix];

    /// <summary>The Profile ID of a key or of an ID with or without its namespace; the namespace is matched in any case (dj:123).</summary>
    public static string IdOf(string key) =>
        Namespaces.FirstOrDefault(n => key.StartsWith(n, StringComparison.OrdinalIgnoreCase)) is { } n ? key[n.Length..] : key;

    /// <summary>The Profile key of an ID (DJ: when it names no namespace), or of a key in any case (dj:123 → DJ:123, web:… → WEB:…).</summary>
    public static string KeyOf(string id) =>
        (Namespaces.FirstOrDefault(n => id.StartsWith(n, StringComparison.OrdinalIgnoreCase)) ?? Prefix) + IdOf(id);

    /// <summary>A person read from a received page (WEB:), not a list Profile.</summary>
    public static bool IsWeb(string key) => key.StartsWith(WebPrefix, StringComparison.OrdinalIgnoreCase);
}
