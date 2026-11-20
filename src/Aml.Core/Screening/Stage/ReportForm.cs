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
}
