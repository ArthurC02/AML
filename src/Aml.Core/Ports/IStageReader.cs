using Aml.Core.Screening;

namespace Aml.Core.Ports;

/// <summary>What a stage reader read: the Profile person's stage, and the text it quotes for it (null when it quotes none).</summary>
public sealed record StageAnswer(LegalStage Stage, string? Quote = null);

/// <summary>
/// A model that reads the Profile person's legal stage from attached news, beside the rules: the person-stage classifier
/// or the large language model. It votes with the rules (<c>StageVote</c>, watchlist-and-adverse-information.md#three-readers).
/// It is given the news text and the Profile's names only, never a customer field (security-and-privacy.md).
/// </summary>
public interface IStageReader
{
    /// <summary>The reader's name in the source condition: <c>classifier</c> or <c>llm</c>.</summary>
    string Name { get; }

    /// <summary>The stage the text tells of the person with <paramref name="names"/>; <see cref="LegalStage.Unknown"/> when the
    /// reader answered and read no stage for the person. Null only when the answer is missing: the reader failed (model
    /// unavailable, error, timeout, time spent, an answer that cannot be read). A missing answer is never imputed: the vote
    /// then gives UNKNOWN with STAGE_READER_NO_ANSWER (<c>StageVote</c>).</summary>
    StageAnswer? Read(string text, IReadOnlyList<string> names);
}
