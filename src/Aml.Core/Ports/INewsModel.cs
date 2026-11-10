namespace Aml.Core.Ports;

/// <summary>A name the tagger found: character offsets into the text and a role such as <c>PER-Suspect</c> or <c>ORG-Other</c>.</summary>
public sealed record TaggedSpan(int Start, int Length, string Role);

/// <summary>
/// The small news models run inside the bank (ADR-0015): a person and organisation tagger and a per-person event classifier.
/// Their output is kept apart from any decision (ADR-0023).
/// </summary>
public interface INewsModel
{
    /// <summary>Model versions, recorded in the lineage of a revision that used them.</summary>
    string Version { get; }

    /// <summary>Names in <paramref name="text"/> with their roles, in text order.</summary>
    IReadOnlyList<TaggedSpan> Tag(string text);

    /// <summary>
    /// For each (mention, sentence) pair, in order: each event label (such as <c>Indictment</c> or <c>Detention-Negated</c>)
    /// with the probability that the sentence says it of the mention.
    /// </summary>
    IReadOnlyList<IReadOnlyDictionary<string, float>> Events(IReadOnlyList<(string Mention, string Sentence)> pairs);
}
