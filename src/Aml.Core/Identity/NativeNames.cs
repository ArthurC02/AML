using System.Text;
using Aml.Core.Normalization;
using Aml.Core.Shared;

namespace Aml.Core.Identity;

/// <summary>
/// Native-script name conflict (identity-adjudication.md#native-script-name-conflict): the romanised names agree while the
/// Chinese-character names differ, so the same romanisation may stand for another person. A signal on the NAME evidence,
/// never a conclusion. Silent wherever the two native names are not comparable.
/// </summary>
public static class NativeNames
{
    public const string Conflict = ReasonCodes.NativeNameConflict;

    /// <summary>
    /// The first customer and Profile Han forms of equal length that differ, when all of these hold: the names agree by
    /// romanisation (<paramref name="nameStatus"/> EXACT, or PARTIAL ROMANIZATION_VARIANT); both sides have a Han form; every
    /// form holding a Han character on either side is complete (Han only, no mask, no unknown or private-use character, not
    /// anonymised); no customer Han form may be the same as a Profile Han form (same length and every position possibly the
    /// same character, <see cref="MayBeSameCharacter"/>, in either name order, spaces and middle dots ignored); no customer
    /// and Profile form are one character apart (the shorter is the longer with one character left out); and at least one
    /// pair has the same length. Otherwise null.
    /// </summary>
    public static (string Customer, string Profile)? Find(IReadOnlyList<string> subject, IReadOnlyList<string> profile, ComparisonStatus nameStatus, string? nameDetail)
    {
        if (nameStatus != ComparisonStatus.Exact && nameDetail != Findings.RomanizationVariant) return null;
        var s = subject.Where(HasHan).ToList();
        var p = profile.Where(HasHan).ToList();
        if (s.Count == 0 || p.Count == 0 || !s.All(IsComplete) || !p.All(IsComplete)) return null;
        if (s.Any(a => p.Any(b => MayBeSame(a, b) || OneApart(a, b)))) return null;
        return s.SelectMany(a => p.Where(b => Length(a) == Length(b)), (a, b) => ((string, string)?)(a, b)).FirstOrDefault();
    }

    /// <summary>
    /// Two characters may be the same name character: they fold equal under the name comparison's table, or are joined,
    /// directly or through others, by the hand-curated list Data/possibly-same-characters.txt (one-to-many Simplified ↔
    /// Traditional, Taiwan, Hong Kong, old-form and shinjitai variants). Wider than name equality, and used only to silence.
    /// </summary>
    public static bool MayBeSameCharacter(Rune a, Rune b) => Class(a) == Class(b);

    // Equal length and every position possibly the same, in either name order: not a conflict (and not agreement).
    private static bool MayBeSame(string a, string b) =>
        NameComparison.NameKeys(a).Any(x => NameComparison.NameKeys(b).Any(y => x.EnumerateRunes().Select(Class).SequenceEqual(y.EnumerateRunes().Select(Class))));

    private static readonly Dictionary<int, int> Parent = BuildClasses();

    private static int Fold(Rune r) => Rune.GetRuneAt(NameComparison.NameKey(r.ToString(), foldVariants: true), 0).Value;

    private static int Class(Rune r)
    {
        var c = Fold(r);
        while (Parent.TryGetValue(c, out var p) && p != c) c = p;
        return c;
    }

    private static Dictionary<int, int> BuildClasses()
    {
        var parent = new Dictionary<int, int>();
        int Root(int c)
        {
            while (parent.TryGetValue(c, out var p) && p != c) c = p;
            return c;
        }
        foreach (var line in EmbeddedTables.Lines("possibly-same-characters.txt"))
        {
            var pair = line.EnumerateRunes().Select(Fold).ToArray();
            var (x, y) = (Root(pair[0]), Root(pair[1]));
            if (x != y) parent[x] = y;
        }
        return parent;
    }

    /// <summary>The NAME evidence carries the conflict (its detail lists the code beside any relaxation finding).</summary>
    public static bool In(Evidence? name) => name?.Detail?.Split('；').Contains(Conflict) == true;

    private static bool HasHan(string name) => NameComparison.NameKey(name).EnumerateRunes().Any(IsHan);

    // A form of any other script or with an unrenderable, masked or anonymised character is not a full native name.
    private static bool IsComplete(string name) =>
        !StatedAgeCheck.NamesNobody(name) && NameComparison.NameKey(name).EnumerateRunes().All(IsHan);

    private const int SupplementaryIdeographsStart = 0x20000;
    private const int SupplementaryIdeographsEnd = 0x3FFFF;

    private static bool IsHan(Rune r) =>
        r.IsBmp ? CjkText.IsCjk((char)r.Value) : r.Value is >= SupplementaryIdeographsStart and <= SupplementaryIdeographsEnd;

    private static int Length(string name) => NameComparison.NameKey(name).EnumerateRunes().Count();

    // An omitted or added character (張偉 against 張文偉) is a data-entry difference, not another name: silent.
    private static bool OneApart(string a, string b) =>
        NameComparison.NameKeys(a, true).Any(x => NameComparison.NameKeys(b, true).Any(y => DropsOne(x, y) || DropsOne(y, x)));

    private static bool DropsOne(string longer, string shorter)
    {
        var l = longer.EnumerateRunes().Select(Class).ToArray();
        var s = shorter.EnumerateRunes().Select(Class).ToArray();
        if (l.Length != s.Length + 1) return false;
        return Enumerable.Range(0, l.Length).Any(skip => l.Where((_, i) => i != skip).SequenceEqual(s));
    }
}
