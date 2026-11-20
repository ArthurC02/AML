namespace Aml.Core.Screening.Stage;

/// <summary>
/// The mechanics of reading text that know nothing of stages: cutting it into sentences and clauses, folding and compacting
/// characters, finding names, and the mechanics of modals, negations and relations that the news digest and the news
/// models' reading use (their words are in <see cref="StageGlossary"/>).
/// </summary>
internal static class StageText
{
    // ---- Cutting text ----

    internal static readonly char[] SentenceEnds = ['。', '！', '？', '\n', '；'], ClauseEnds = ['，', ','];

    // The (start, length) of each piece of text[from..from+length] between separators, as string.Split cuts it.
    internal static IEnumerable<(int Start, int Length)> Spans(string text, int from, int length, char[] separators)
    {
        var end = from + length;
        for (var start = from; start <= end;)
        {
            var next = text.IndexOfAny(separators, start, end - start);
            if (next < 0) next = end;
            yield return (start, next - start);
            start = next + 1;
        }
    }

    internal static IEnumerable<string> Sentences(string text) =>
        Spans(text, 0, text.Length, SentenceEnds).Select(s => text.Substring(s.Start, s.Length));

    // ---- Characters ----

    internal static string Compact(string s) => string.Concat(s.Where(c => !char.IsWhiteSpace(c)));

    // Full-width letters and digits are read as half-width ones: a name may be written either way (Ａ０５, A05).
    internal static string HalfWidth(string s) => string.Concat(s.Select(c => c is (>= 'Ａ' and <= 'Ｚ') or (>= 'ａ' and <= 'ｚ') or (>= '０' and <= '９') ? (char)(c - 0xFEE0) : c));

    // ---- Words ----

    /// <summary>The text holds one of the words (ordinal).</summary>
    internal static bool ContainsAny(string text, string[] words)
    {
        foreach (var w in words)
            if (text.Contains(w, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>The text opens with one of the words (ordinal).</summary>
    internal static bool StartsWithAny(ReadOnlySpan<char> text, string[] words)
    {
        foreach (var w in words)
            if (text.StartsWith(w, StringComparison.Ordinal)) return true;
        return false;
    }

    // ---- Names ----

    // ponytail: the first 100 names, as in StatedAgeCheck: every sentence and clause is searched for each name.
    internal static bool MentionsAnyName(string sentence, IReadOnlyList<string> names) =>
        names.Take(StageGlossary.MaxNames).Any(n => n.Length > 1 && sentence.Contains(n, StringComparison.OrdinalIgnoreCase));

    /// <summary>Every place the sentence writes one of the forms goes on with 的 / 之 and a relation word: it speaks of that relative, not of the person.</summary>
    internal static bool OnlyAsRelation(string sentence, IEnumerable<string> forms)
    {
        var any = false;
        foreach (var f in forms.Where(f => f.Length > 1))
            for (var at = sentence.IndexOf(f, StringComparison.Ordinal); at >= 0; at = sentence.IndexOf(f, at + 1, StringComparison.Ordinal))
            {
                if (StagePatterns.Relation().Match(sentence, at + f.Length) is not { Success: true } m || m.Index != at + f.Length) return false;
                any = true;
            }
        return any;
    }

    // ---- What qualifies a keyword ----

    // Where the first modal of the clause ends (past the end when there is none); used by the news digest. A modal anywhere
    // before the keyword makes it no event (最重可判處, 檢方請求法院依法從重判處), but a word that only holds the modal's characters is not one.
    internal static int FirstModalEnd(string clause) => FirstModalEnd(clause, StageGlossary.Modals);

    /// <summary>Where the first of these modals ends in the clause, with their guards (past the end when there is none).</summary>
    internal static int FirstModalEnd(string clause, (string Modal, string NotBefore, string NotAfter)[] modals)
    {
        var first = int.MaxValue;
        foreach (var (modal, notBefore, notAfter) in modals)
            for (var at = clause.IndexOf(modal, StringComparison.Ordinal); at >= 0 && at + modal.Length < first;
                 at = clause.IndexOf(modal, at + 1, StringComparison.Ordinal))
            {
                var next = at + modal.Length;
                if (at > 0 && notBefore.Contains(clause[at - 1], StringComparison.Ordinal)) continue;
                if (next < clause.Length && notAfter.Contains(clause[next], StringComparison.Ordinal)) continue;
                first = next;
                break;
            }
        return first;
    }

    // 尚未起訴, 沒有被起訴, 是否遭到起訴: the words just before the keyword, past a passive marker.
    internal static bool Negated(string clause, int keyword)
    {
        var before = clause.AsSpan(0, keyword);
        foreach (var passive in StageGlossary.Passives)
            if (before.EndsWith(passive, StringComparison.Ordinal)) { before = before[..^passive.Length]; break; }
        foreach (var negation in StageGlossary.Negations)
            if (before.EndsWith(negation, StringComparison.Ordinal)) return true;
        return false;
    }
}
