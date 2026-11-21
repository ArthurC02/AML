using Aml.Core.Ports;

namespace Aml.Core.Screening.Stage;

/// <summary>
/// The rules and the configured stage models (<see cref="IStageReader"/>) on the Profile person's stage in attached news
/// (watchlist-and-adverse-information.md#three-readers). The readers are the rules plus every model that answered.
/// <list type="table">
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

    /// <summary>The voted stage and basis, and whether the readers disagree, from the rules' reading and the models' stages.</summary>
    internal static (LegalStage Stage, string? Basis, bool Disagree) Decide(StageReading rules, IReadOnlyList<LegalStage> models)
    {
        if (models.Count == 0) return (rules.Stage, rules.Basis, false);
        if (models.Count == 1) return (rules.Stage, rules.Basis, models[0] != rules.Stage);
        LegalStage[] all = [rules.Stage, .. models];
        var distinct = all.Distinct().Count();
        if (distinct == 1) return (rules.Stage, StageBasis.VoteUnanimous, false);
        return (LegalStage.Unknown, distinct < all.Length ? StageBasis.VoteMajority : StageBasis.VoteSplit, true);
    }

    /// <summary>Asks every model, then votes; each reader's stage (and quote) is kept for the explanation.</summary>
    internal static (StageReading Reading, bool Disagree, IReadOnlyList<StageReaderResult> Readers) Read(
        StageReading rules, string news, IReadOnlyList<string> names, IReadOnlyList<IStageReader> models)
    {
        var squashed = Squash(news);
        var readers = new List<StageReaderResult> { new(Rules, rules.Stage, rules.Basis) };
        foreach (var model in models)
            if (model.Read(news, names) is { } answer)
            {
                var quote = string.IsNullOrEmpty(answer.Quote) ? null : answer.Quote;
                readers.Add(new(model.Name, answer.Stage, Quote: quote, QuoteInText: quote is null ? null : Squash(quote) is { Length: > 0 } q && squashed.Contains(q, StringComparison.Ordinal)));
            }
        var (stage, basis, disagree) = Decide(rules, [.. readers.Skip(1).Select(r => r.Stage)]);
        return (new(stage, basis), disagree, readers);
    }

    private static string Squash(string s) => string.Concat(s.Where(c => !char.IsWhiteSpace(c)));
}
