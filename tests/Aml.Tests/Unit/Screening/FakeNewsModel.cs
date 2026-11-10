using Aml.Core.Ports;

namespace Aml.Tests.Unit.Screening;

/// <summary>
/// Stands in for the ONNX news models: tags every occurrence of the given strings with their role, and gives each event
/// label the probability set for a (mention, sentence word) pair, else 0.
/// </summary>
internal sealed class FakeNewsModel(IReadOnlyDictionary<string, string> roles, params (string Mention, string Word, string Label, float P)[] events) : INewsModel
{
    public string Version => "ner:test ev:test";

    public int Classifications { get; private set; }

    // Like a tagger, one span per place: the longest wins.
    public IReadOnlyList<TaggedSpan> Tag(string text)
    {
        var kept = new List<TaggedSpan>();
        foreach (var s in roles.SelectMany(r => Occurrences(text, r.Key).Select(at => new TaggedSpan(at, r.Key.Length, r.Value)))
                     .OrderBy(s => s.Start).ThenByDescending(s => s.Length))
            if (kept.Count == 0 || s.Start >= kept[^1].Start + kept[^1].Length) kept.Add(s);
        return kept;
    }

    public IReadOnlyList<IReadOnlyDictionary<string, float>> Events(IReadOnlyList<(string Mention, string Sentence)> pairs)
    {
        Classifications += pairs.Count;
        return pairs.Select(p => (IReadOnlyDictionary<string, float>)events
            .Where(e => e.Mention == p.Mention && p.Sentence.Contains(e.Word, StringComparison.Ordinal))
            .GroupBy(e => e.Label).ToDictionary(g => g.Key, g => g.Max(e => e.P))).ToList();
    }

    private static IEnumerable<int> Occurrences(string text, string s)
    {
        for (var i = text.IndexOf(s, StringComparison.Ordinal); i >= 0; i = text.IndexOf(s, i + s.Length, StringComparison.Ordinal)) yield return i;
    }
}
