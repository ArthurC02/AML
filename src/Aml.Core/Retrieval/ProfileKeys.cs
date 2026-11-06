namespace Aml.Core.Retrieval;

/// <summary>Profile key (DJ:&lt;id&gt;) and Profile ID: routes take the ID, responses carry both.</summary>
public static class ProfileKeys
{
    public const string Prefix = "DJ:";

    /// <summary>The Profile ID of a key or of an ID with or without the prefix; the prefix is matched in any case (dj:123).</summary>
    public static string IdOf(string key) => key.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) ? key[Prefix.Length..] : key;

    /// <summary>The Profile key of an ID, or of a key in any case (dj:123 → DJ:123).</summary>
    public static string KeyOf(string id) => Prefix + IdOf(id);
}
