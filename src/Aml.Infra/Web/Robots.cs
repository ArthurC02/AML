using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Aml.Infra.Web;

/// <summary>
/// A site's robots.txt rules for one crawler (RFC 9309): the groups naming the crawler's product token, else the * groups;
/// the longest matching Allow or Disallow wins, Allow on a tie; * matches any characters and a final $ ends the path.
/// Paths are compared percent-encoded (UTF-8, upper-case hex), as a URL's path is sent.
/// </summary>
public sealed partial class Robots
{
    /// <summary>The most of a robots.txt read (RFC 9309 asks for at least 500 KiB).</summary>
    public const int MaxBytes = 512 * 1024;

    public static readonly Robots AllowAll = new([], null);
    public static readonly Robots DisallowAll = new([new Rule("/", Allow: false)], null);

    private sealed record Rule(string Path, bool Allow)
    {
        private Regex? _pattern;

        public bool Matches(string path) => (_pattern ??= new Regex(
            "^" + Regex.Escape(Encoded(Path.TrimEnd('$'))).Replace(@"\*", ".*", StringComparison.Ordinal) + (Path.EndsWith('$') ? "$" : ""),
            RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)).IsMatch(path);
    }

    private sealed class Group
    {
        public List<string> Agents { get; } = [];
        public List<Rule> Rules { get; } = [];
        public TimeSpan? Delay { get; set; }
    }

    private readonly List<Rule> _rules;

    private Robots(List<Rule> rules, TimeSpan? crawlDelay) => (_rules, CrawlDelay) = (rules, crawlDelay);

    /// <summary>The site's Crawl-delay, at most a minute.</summary>
    public TimeSpan? CrawlDelay { get; }

    /// <param name="agent">The crawler's User-Agent; its product tokens (AmlPoC-research in "Mozilla/5.0 (compatible; AmlPoC-research/0.1)") name it.</param>
    public static Robots Parse(string text, string agent)
    {
        var groups = new List<Group>();
        Group? current = null;
        var lastWasAgent = false;
        foreach (var raw in text.Split('\n').Take(10_000))
        {
            var line = raw.Split('#')[0].Trim();
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0) continue;
            var key = line[..colon].Trim().ToLowerInvariant();
            var value = line[(colon + 1)..].Trim();
            switch (key)
            {
                case "user-agent":
                    if (value.Length == 0) break;
                    if (!lastWasAgent || current is null) groups.Add(current = new Group());
                    current.Agents.Add(value.ToLowerInvariant());
                    lastWasAgent = true;
                    break;
                case "allow" or "disallow":
                    lastWasAgent = false;
                    if (current is not null && value.Length > 0 && current.Rules.Count < 1_000) current.Rules.Add(new Rule(value, key == "allow"));
                    break;
                case "crawl-delay":
                    lastWasAgent = false;
                    if (current is not null && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && s >= 0)
                        current.Delay = TimeSpan.FromSeconds(Math.Min(s, 60));
                    break;
                default:
                    // Sitemap, Host and the like belong to no group and do not end one.
                    break;
            }
        }
        var tokens = ProductToken().Matches(agent).Select(m => m.Value.ToLowerInvariant()).Where(t => t is not ("mozilla" or "compatible")).ToHashSet();
        var chosen = groups.Where(g => g.Agents.Exists(tokens.Contains)).ToList();
        if (chosen.Count == 0) chosen = groups.Where(g => g.Agents.Contains("*")).ToList();
        return new Robots(chosen.SelectMany(g => g.Rules).ToList(), chosen.Select(g => g.Delay).FirstOrDefault(d => d is not null));
    }

    /// <param name="pathAndQuery">As sent: percent-encoded (Uri.PathAndQuery).</param>
    public bool Allows(string pathAndQuery)
    {
        var path = UpperHex(pathAndQuery);
        (int Length, bool Allow) best = (-1, true);
        foreach (var rule in _rules)
            if ((rule.Path.Length > best.Length || rule.Path.Length == best.Length && rule.Allow) && rule.Matches(path))
                best = (rule.Path.Length, rule.Allow);
        return best.Allow;
    }

    // A rule written with non-ASCII characters (Disallow: /政策/) matches the percent-encoded path the URL sends.
    private static string Encoded(string rule)
    {
        var sb = new StringBuilder();
        Span<byte> bytes = stackalloc byte[4];
        foreach (var rune in rule.EnumerateRunes())
            if (rune.IsAscii) sb.Append((char)rune.Value);
            else
            {
                var n = rune.EncodeToUtf8(bytes);
                foreach (var b in bytes[..n]) sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        return UpperHex(sb.ToString());
    }

    private static string UpperHex(string s) => Escape().Replace(s, m => m.Value.ToUpperInvariant());

    [GeneratedRegex("%[0-9a-fA-F]{2}")]
    private static partial Regex Escape();

    [GeneratedRegex(@"[A-Za-z_\-]+")]
    private static partial Regex ProductToken();
}
