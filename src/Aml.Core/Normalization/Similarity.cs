using System.Numerics;
using System.Text;

namespace Aml.Core.Normalization;

/// <summary>
/// String similarity on comparison keys, per code point; a PARTIAL feature inside the NAME group only. Strings of up to
/// 64 code points (every realistic name) run bit-parallel, one bit per position in a 64-bit word; longer ones run the
/// textbook dynamic program. Both give the same result, bit for bit. A string without surrogates is compared by its
/// UTF-16 chars directly, since each char is then one code point; otherwise its code points are decoded first.
/// </summary>
public static class Similarity
{
    private const int WordBits = 64;
    private const int MaxStackallocCodePoints = 256;

    public static int Levenshtein(string x, string y)
    {
        if (!HasSurrogates(x) && !HasSurrogates(y)) return Levenshtein<char>(x, y);
        Span<int> aBuffer = stackalloc int[Math.Min(x.Length, MaxStackallocCodePoints)];
        Span<int> bBuffer = stackalloc int[Math.Min(y.Length, MaxStackallocCodePoints)];
        return Levenshtein(CodePoints(x, aBuffer), CodePoints(y, bBuffer));
    }

    private static int Levenshtein<T>(ReadOnlySpan<T> a, ReadOnlySpan<T> b) where T : unmanaged, IEquatable<T>
    {
        if (a.Length == 0) return b.Length;
        return a.Length <= WordBits ? BitParallelLevenshtein(a, b) : DynamicProgrammingLevenshtein(a, b);
    }

    /// <summary>
    /// Hyyrö (2003), "A Bit-Vector Algorithm for Computing Levenshtein and Damerau Edit Distances", after Myers (1999):
    /// one column of the edit-distance matrix as vertical deltas (+1 in Pv, -1 in Mv) over the positions of <paramref name="a"/>,
    /// advanced one character of <paramref name="b"/> at a time with word operations; the score tracks the last row.
    /// </summary>
    private static int BitParallelLevenshtein<T>(ReadOnlySpan<T> a, ReadOnlySpan<T> b) where T : unmanaged, IEquatable<T>
    {
        var positions = new PositionsOf<T>(a, stackalloc T[a.Length], stackalloc ulong[a.Length]);
        var lastRow = 1UL << (a.Length - 1);
        var plusVertical = a.Length == WordBits ? ulong.MaxValue : (1UL << a.Length) - 1;
        var minusVertical = 0UL;
        var distance = a.Length;
        foreach (var c in b)
        {
            var equal = positions.Of(c);
            var crossVertical = equal | minusVertical;
            var crossHorizontal = (((equal & plusVertical) + plusVertical) ^ plusVertical) | equal;
            var plusHorizontal = minusVertical | ~(crossHorizontal | plusVertical);
            var minusHorizontal = plusVertical & crossHorizontal;
            if ((plusHorizontal & lastRow) != 0) distance++;
            if ((minusHorizontal & lastRow) != 0) distance--;
            // The first row grows by one per character of b: carry +1 into position 0.
            plusHorizontal = (plusHorizontal << 1) | 1;
            minusHorizontal <<= 1;
            plusVertical = minusHorizontal | ~(crossVertical | plusHorizontal);
            minusVertical = plusHorizontal & crossVertical;
        }
        return distance;
    }

    private static int DynamicProgrammingLevenshtein<T>(ReadOnlySpan<T> a, ReadOnlySpan<T> b) where T : unmanaged, IEquatable<T>
    {
        var prev = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var cur = new int[b.Length + 1];
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1], prev[j]) + 1, prev[j - 1] + (a[i - 1].Equals(b[j - 1]) ? 0 : 1));
            prev = cur;
        }
        return prev[b.Length];
    }

    public static double JaroWinkler(string x, string y)
    {
        if (x == y) return 1;
        if (!HasSurrogates(x) && !HasSurrogates(y)) return JaroWinkler<char>(x, y);
        Span<int> aBuffer = stackalloc int[Math.Min(x.Length, MaxStackallocCodePoints)];
        Span<int> bBuffer = stackalloc int[Math.Min(y.Length, MaxStackallocCodePoints)];
        return JaroWinkler(CodePoints(x, aBuffer), CodePoints(y, bBuffer));
    }

    private static double JaroWinkler<T>(ReadOnlySpan<T> a, ReadOnlySpan<T> b) where T : unmanaged, IEquatable<T>
    {
        if (a.Length == 0 || b.Length == 0) return 0;
        var jaro = a.Length <= WordBits && b.Length <= WordBits ? BitParallelJaro(a, b) : Jaro(a, b);
        if (jaro <= JaroWinklerBoostThreshold) return jaro;
        return jaro + CommonPrefixLength(a, b) * PrefixScale * (1 - jaro);
    }

    public const double JaroWinklerBoostThreshold = 0.7;
    private const int MaxPrefixLength = 4;
    private const double PrefixScale = 0.1;

    private static int CommonPrefixLength<T>(ReadOnlySpan<T> a, ReadOnlySpan<T> b) where T : unmanaged, IEquatable<T>
    {
        var prefix = 0;
        while (prefix < Math.Min(MaxPrefixLength, Math.Min(a.Length, b.Length)) && a[prefix].Equals(b[prefix])) prefix++;
        return prefix;
    }

    /// <summary>
    /// The same greedy matching as <see cref="Jaro"/> (each character of <paramref name="a"/> takes the first unmatched equal
    /// character of <paramref name="b"/> inside the window), with the window and the unmatched positions of b as bit masks:
    /// "first unmatched equal in the window" is the lowest set bit of one AND.
    /// </summary>
    private static double BitParallelJaro<T>(ReadOnlySpan<T> a, ReadOnlySpan<T> b) where T : unmanaged, IEquatable<T>
    {
        var window = Math.Max(0, Math.Max(a.Length, b.Length) / 2 - 1);
        var positionsInB = new PositionsOf<T>(b, stackalloc T[b.Length], stackalloc ulong[b.Length]);
        var matchedA = 0UL;
        var matchedB = 0UL;
        var matches = 0;
        for (var i = 0; i < a.Length; i++)
        {
            var from = Math.Max(0, i - window);
            var to = Math.Min(b.Length, i + window + 1);
            if (from >= to) continue;
            var candidates = positionsInB.Of(a[i]) & ~matchedB & Range(from, to);
            if (candidates == 0) continue;
            matchedB |= candidates & (0 - candidates);
            matchedA |= 1UL << i;
            matches++;
        }
        if (matches == 0) return 0;

        var transpositions = 0;
        var unreadB = matchedB;
        for (var unreadA = matchedA; unreadA != 0; unreadA &= unreadA - 1)
        {
            var i = BitOperations.TrailingZeroCount(unreadA);
            var k = BitOperations.TrailingZeroCount(unreadB);
            unreadB &= unreadB - 1;
            if (!a[i].Equals(b[k])) transpositions++;
        }
        double m = matches;
        return (m / a.Length + m / b.Length + (m - transpositions / 2.0) / m) / 3;
    }

    // Bits from (inclusive) to (exclusive), both within one 64-bit word.
    private static ulong Range(int from, int to) =>
        (to - from == WordBits ? ulong.MaxValue : (1UL << (to - from)) - 1) << from;

    private static double Jaro<T>(ReadOnlySpan<T> a, ReadOnlySpan<T> b) where T : unmanaged, IEquatable<T>
    {
        var window = Math.Max(0, Math.Max(a.Length, b.Length) / 2 - 1);
        var ma = new bool[a.Length];
        var mb = new bool[b.Length];
        var matches = 0;
        for (var i = 0; i < a.Length; i++)
            for (var j = Math.Max(0, i - window); j < Math.Min(b.Length, i + window + 1); j++)
                if (!mb[j] && a[i].Equals(b[j])) { ma[i] = mb[j] = true; matches++; break; }
        if (matches == 0) return 0;

        var transpositions = 0;
        for (int i = 0, k = 0; i < a.Length; i++)
        {
            if (!ma[i]) continue;
            while (!mb[k]) k++;
            if (!a[i].Equals(b[k++])) transpositions++;
        }
        double m = matches;
        return (m / a.Length + m / b.Length + (m - transpositions / 2.0) / m) / 3;
    }

    // Any surrogate, paired or lone, sends the string through rune decoding, which reads a lone one as U+FFFD.
    private static bool HasSurrogates(string s) => s.AsSpan().ContainsAnyInRange('\uD800', '\uDFFF');

    /// <summary>The code points of <paramref name="s"/>, written into <paramref name="buffer"/> when it is long enough.</summary>
    private static ReadOnlySpan<int> CodePoints(string s, Span<int> buffer)
    {
        if (buffer.Length < s.Length) return s.EnumerateRunes().Select(r => r.Value).ToArray();
        var count = 0;
        foreach (var r in s.EnumerateRunes()) buffer[count++] = r.Value;
        return buffer[..count];
    }

    /// <summary>
    /// For a string of at most 64 characters, the positions holding each character as one 64-bit mask (the "pattern match
    /// vector" of bit-parallel matching). A name has few distinct characters, so a short scan finds a mask. The buffers
    /// hold one slot per character of the string, the most distinct characters it can have.
    /// </summary>
    private readonly ref struct PositionsOf<T> where T : unmanaged, IEquatable<T>
    {
        private readonly Span<T> _characters;
        private readonly Span<ulong> _masks;
        private readonly int _count;

        public PositionsOf(ReadOnlySpan<T> s, Span<T> characters, Span<ulong> masks)
        {
            _characters = characters;
            _masks = masks;
            _count = 0;
            for (var i = 0; i < s.Length; i++)
            {
                var at = _characters[.._count].IndexOf(s[i]);
                if (at < 0)
                {
                    at = _count++;
                    _characters[at] = s[i];
                    _masks[at] = 0;
                }
                _masks[at] |= 1UL << i;
            }
        }

        public ulong Of(T character)
        {
            var at = _characters[.._count].IndexOf(character);
            return at < 0 ? 0 : _masks[at];
        }
    }
}
