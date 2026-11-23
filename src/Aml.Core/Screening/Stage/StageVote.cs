using Aml.Core.Ports;

namespace Aml.Core.Screening.Stage;

/// <summary>
/// The rules and the configured stage models (<see cref="IStageReader"/>) on the Profile person's stage in attached news
/// (watchlist-and-adverse-information.md#three-readers). The readers are the rules plus every configured model.
/// <list type="table">
/// <item>a model's answer missing (null): UNKNOWN, no basis, STAGE_READER_NO_ANSWER, and STAGE_READERS_DISAGREE when the
/// readers that answered differ. A missing answer is never imputed from the others.</item>
/// <item>rules alone: the rules' stage and basis, unchanged.</item>
/// <item>rules and one model: the rules' stage and basis; a model that differs adds STAGE_READERS_DISAGREE.</item>
/// <item>three, all the same: that stage, STAGE_VOTE_UNANIMOUS; only unanimity decides.</item>
/// <item>three, two the same (UNKNOWN among them or not): UNKNOWN, STAGE_VOTE_MAJORITY, and STAGE_READERS_DISAGREE.</item>
/// <item>three, all different: UNKNOWN, STAGE_VOTE_SPLIT, and STAGE_READERS_DISAGREE.</item>
/// </list>
/// </summary>
internal static class StageVote
{
    internal const string Rules = "rules";

    /// <summary>Condition reason code: the readers do not agree on the stage, so a reviewer reads it.</summary>
    internal const string ReadersDisagree = "STAGE_READERS_DISAGREE";

    /// <summary>Condition reason code, the missing-answer indicator: a configured model's answer is missing (an error, a
    /// timeout, its time spent, an unreadable answer). It is listed in stageReaders without a stage and the stage is UNKNOWN.</summary>
    internal const string ReaderNoAnswer = "STAGE_READER_NO_ANSWER";

    /// <summary>The voted stage and basis, whether the readers that answered disagree, and whether an answer is missing, from
    /// the rules' reading and each configured model's stage (null: missing).</summary>
    internal static (LegalStage Stage, string? Basis, bool Disagree, bool Missing) Decide(StageReading rules, IReadOnlyList<LegalStage?> models)
    {
        LegalStage[] all = [rules.Stage, .. models.OfType<LegalStage>()];
        var distinct = all.Distinct().Count();
        if (all.Length <= models.Count) return (LegalStage.Unknown, null, distinct > 1, true);
        if (models.Count < 2) return (rules.Stage, rules.Basis, distinct > 1, false);
        if (distinct == 1) return (rules.Stage, StageBasis.VoteUnanimous, false, false);
        return (LegalStage.Unknown, distinct < all.Length ? StageBasis.VoteMajority : StageBasis.VoteSplit, true, false);
    }

    /// <summary>Asks every model, then votes; each reader's stage (and quote) is kept for the explanation, and a model whose
    /// answer is missing is listed without a stage.</summary>
    internal static (StageReading Reading, bool Disagree, bool NoAnswer, IReadOnlyList<StageReaderResult> Readers) Read(
        StageReading rules, string news, IReadOnlyList<string> names, IReadOnlyList<IStageReader> models)
    {
        var squashed = Squash(news);
        var readers = new List<StageReaderResult> { new(Rules, rules.Stage, rules.Basis) };
        foreach (var model in models)
            if (Ask(model, news, names) is { } answer)
            {
                var quote = string.IsNullOrEmpty(answer.Quote) ? null : answer.Quote;
                readers.Add(new(model.Name, answer.Stage, Quote: quote, QuoteInText: quote is null ? null : Squash(quote) is { Length: > 0 } q && squashed.Contains(q, StringComparison.Ordinal)));
            }
            else
                readers.Add(new(model.Name, null));
        var (stage, basis, disagree, missing) = Decide(rules, [.. readers.Skip(1).Select(r => r.Stage)]);
        return (new(stage, basis), disagree, missing, readers);
    }

    // Any failure of a reader is a missing answer; it never blocks the analysis (ADR-0012). No caller token reaches here,
    // so a cancellation is the reader's own (a timeout) and is a failure like any other.
    private static StageAnswer? Ask(IStageReader model, string news, IReadOnlyList<string> names)
    {
        try
        {
            return model.Read(news, names);
        }
#pragma warning disable CA1031 // failure isolation: a reader that throws is a reader that did not answer
        catch (Exception e) when (e is not OutOfMemoryException)
#pragma warning restore CA1031
        {
            return null;
        }
    }

    private static string Squash(string s) => string.Concat(s.Where(c => !char.IsWhiteSpace(c)));
}
