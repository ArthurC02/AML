namespace Aml.Core.Screening.Stage;

/// <summary>
/// The form of a news report: whose a sentence or a clause is. It says what the text holds, never what that means for the
/// stage (<see cref="ReportRules"/>).
/// </summary>
internal static class ReportForm
{
    // ---- Whose a sentence is ----

    /// <summary>
    /// What a sentence of the report says of the person, and whose steps it may tell: a sentence naming them is theirs,
    /// and outside a judgment that only names them as a bystander so is every other sentence.
    /// </summary>
    internal static SentenceScope Scope(string news, int start, int length, IReadOnlyList<string> names, bool bystander)
    {
        var sentence = news.Substring(start, length);
        var own = StageText.MentionsAnyName(sentence, names);
        return new(sentence, own, own || !bystander);
    }

    // ---- Whose a clause is ----

    /// <summary>The clause names the person.</summary>
    internal static bool IsMine(string clause, IReadOnlyList<string> names) => StageText.MentionsAnyName(clause, names);

    /// <summary>
    /// The clauses of a sentence as they are read. A clause that ends with the court pronouncing its ruling is read with
    /// the next one: 今日宣判，陳志明無罪 is one ruling. Read is what the rules read, Clause the clause as written (where
    /// it opens).
    /// </summary>
    internal static IEnumerable<(string Read, string Clause)> Clauses(string news, int start, int length)
    {
        string? pronounced = null;
        foreach (var (at, size) in StageText.Spans(news, start, length, StageText.ClauseEnds))
        {
            var clause = news.Substring(at, size);
            yield return (pronounced + clause, clause);
            pronounced = clause.EndsWith(StageGlossary.Pronounced, StringComparison.Ordinal) ? clause : null;
        }
    }

    /// <summary>The clause opens on a co-defendant (同案的李小四則獲判無罪, 同案被告李小四獲不起訴處分).</summary>
    internal static bool OpensOnOther(string clause) => StageText.StartsWithAny(clause.AsSpan().TrimStart(), StageGlossary.OtherPersonOpeners);
}
