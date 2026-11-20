using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aml.Core.Normalization;
using Aml.Core.Ports;
using Aml.Core.Screening;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Aml.Infra.Models;

/// <summary>
/// The person-stage classifier (bert-base-chinese fine-tuned on document and person, ADR-0015,
/// watchlist-and-adverse-information.md#three-readers). The folder holds <c>model.onnx</c>, <c>vocab.txt</c> and
/// <c>labels.json</c> (label order, max_length); the model stays outside the repository. The input is built as the training
/// code built it (stage-clf prep_stage.py row(), train_stage.py ids()): <c>[CLS] name [SEP] evidence [SEP]</c>, token types
/// 0 up to and including the first [SEP], the person's forms in the evidence marked by [unused1] / [unused2] (ids 1, 2).
/// The stage is the label with the highest logit.
/// </summary>
public sealed partial class OnnxStageClassifier : IStageReader, IDisposable
{
    private const int HeadChars = 60, MaxNameTokens = 32, MinFormChars = 2;
    private const long OpenMark = 1, CloseMark = 2; // [unused1], [unused2]
    internal const string Open = "[unused1]", Close = "[unused2]";
    private readonly InferenceSession session;
    private readonly BertWordPiece tokenizer;
    private readonly LegalStage[] labels;
    private readonly int maxLength;

    public OnnxStageClassifier(string directory)
    {
        using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "labels.json")));
        labels = [.. config.RootElement.GetProperty("labels").EnumerateArray().Select(l => Enum.Parse<LegalStage>(l.GetString()!, ignoreCase: true))];
        maxLength = config.RootElement.GetProperty("max_length").GetInt32();
        tokenizer = new BertWordPiece(Path.Combine(directory, "vocab.txt"));
        session = new InferenceSession(Path.Combine(directory, "model.onnx"), new SessionOptions { IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2) });
    }

    public string Name => "classifier";

    public StageAnswer? Read(string text, IReadOnlyList<string> names)
    {
        try
        {
            // The training text was NFKC-normalised per character (prep.normalise), as OnnxNewsModel does.
            var normalized = OnnxNewsModel.Normalize(text);
            var forms = names.Select(n => OnnxNewsModel.Normalize(n)).Where(f => f.EnumerateRunes().Count() >= MinFormChars).Distinct(StringComparer.Ordinal).ToList();
            var name = forms.Count > 0 ? forms[0] : names.Count > 0 ? OnnxNewsModel.Normalize(names[0]) : "";
            var budget = maxLength - 3 - Math.Min(MaxNameTokens, Encode(name).Count);
            var evidence = Evidence(normalized, forms, budget, s => Encode(s).Count, s => NamesAPerson(s, forms));
            return new(Classify(name, evidence));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return null; // a failing reader drops out of the vote; it never blocks the analysis
        }
    }

    /// <summary>The label with the highest logit for this name and evidence text (as a training row holds them).</summary>
    internal LegalStage Classify(string name, string evidence)
    {
        var (ids, types) = Ids(name, evidence);
        var shape = new[] { 1, ids.Length };
        var mask = new long[ids.Length];
        Array.Fill(mask, 1L);
        using var results = session.Run(
        [
            NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(ids, shape)),
            NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(mask, shape)),
            NamedOnnxValue.CreateFromTensor("token_type_ids", new DenseTensor<long>(types, shape)),
        ]);
        var logits = results[0].AsEnumerable<float>().ToArray();
        if (logits.Length != labels.Length) throw new InvalidDataException("Stage logits differ from labels.json");
        var best = 0;
        for (var i = 1; i < logits.Length; i++) if (logits[i] > logits[best]) best = i;
        return labels[best];
    }

    /// <summary>train_stage.py ids(): the name at most 32 tokens, the evidence cut to the rest of max_length.</summary>
    internal (long[] Ids, long[] Types) Ids(string name, string evidence)
    {
        var nameIds = Encode(name).Take(MaxNameTokens).ToList();
        var evidenceIds = Encode(evidence).Take(maxLength - 3 - nameIds.Count);
        long[] ids = [tokenizer.Classification, .. nameIds, tokenizer.Separator, .. evidenceIds, tokenizer.Separator];
        var types = new long[ids.Length];
        for (var i = nameIds.Count + 2; i < ids.Length; i++) types[i] = 1;
        return (ids, types);
    }

    /// <summary>train_stage.py enc(): each marker one id, the text between markers tokenized piece by piece.</summary>
    internal List<long> Encode(string s)
    {
        var ids = new List<long>();
        foreach (var piece in Marker().Split(s))
            if (piece == Open) ids.Add(OpenMark);
            else if (piece == Close) ids.Add(CloseMark);
            else if (piece.Length > 0) ids.AddRange(tokenizer.Encode(piece).Select(t => (long)t.Id));
        return ids;
    }

    /// <summary>
    /// prep_stage.py row(): in text order, (a) the first 60 characters, (b) every sentence holding one of the person's forms,
    /// (c) after each, the next sentence when it names no person. Each occurrence of a form (longest first, no overlaps) is
    /// wrapped in [unused1] [unused2]. Pieces join directly when adjacent, with one space when only whitespace lies between,
    /// with "…" where text was skipped. Over the token budget: the head, the pieces in order up to 60 % of it, then the
    /// last pieces that fit the rest.
    /// </summary>
    /// <remarks>
    /// The one difference from training: there (c) asked whether the next sentence holds a form of any annotated person;
    /// at run time nobody is annotated, so it asks <paramref name="namesAPerson"/>, which the classifier answers with "a form
    /// of the Profile person, or 被告 / 嫌犯 / 犯嫌 / 嫌疑人 / 嫌疑犯 before a common surname" (<see cref="NamesAPerson"/>).
    /// </remarks>
    /// <param name="tokens">The token count of a piece (markers one each).</param>
    internal static string Evidence(string text, IReadOnlyList<string> forms, int budget, Func<string, int> tokens, Func<string, bool> namesAPerson)
    {
        var sentences = Sentences(text);
        var own = sentences.Select(s => forms.Any(f => text.AsSpan(s.Start, s.End - s.Start).Contains(f, StringComparison.Ordinal))).ToList();
        var keep = new SortedSet<int>();
        for (var i = 0; i < sentences.Count; i++)
        {
            if (!own[i]) continue;
            keep.Add(i);
            if (i + 1 < sentences.Count && !namesAPerson(text[sentences[i + 1].Start..sentences[i + 1].End])) keep.Add(i + 1);
        }
        var head = CharsOfRunes(text, HeadChars);
        var units = new List<(int Start, int End)> { (0, head) };
        units.AddRange(keep.Select(i => sentences[i]).Where(s => s.End > head).Select(s => (Math.Max(s.Start, head), s.End)));
        var marks = Marks(text, forms);
        var pieces = units.Select(u => Render(text, u, marks)).ToList();
        var lengths = pieces.Select(p => tokens(p) + 1).ToList();
        var chosen = Enumerable.Range(0, units.Count).ToList();
        if (lengths.Sum() > budget)
        {
            chosen = [0];
            var used = lengths[0];
            for (var i = 1; i < units.Count; i++)
            {
                if (used + lengths[i] > 0.6 * budget) break;
                chosen.Add(i); used += lengths[i];
            }
            var tail = new List<int>();
            for (var i = units.Count - 1; i > chosen[^1]; i--)
            {
                if (used + lengths[i] > budget) break;
                tail.Insert(0, i); used += lengths[i];
            }
            chosen.AddRange(tail);
        }
        var evidence = new StringBuilder();
        int? previous = null;
        foreach (var i in chosen)
        {
            if (previous is { } p && units[i].Start > units[p].End)
                evidence.Append(string.IsNullOrWhiteSpace(text[units[p].End..units[i].Start]) ? " " : "…");
            evidence.Append(pieces[i]);
            previous = i;
        }
        return evidence.ToString();
    }

    /// <summary>The run-time stand-in for "a form of an annotated person": a Profile form, or an accused's name (被告王大同).</summary>
    internal static bool NamesAPerson(string sentence, IReadOnlyList<string> forms) =>
        forms.Any(f => sentence.Contains(f, StringComparison.Ordinal))
        || Accused().Matches(sentence).Any(m => CjkText.IsCommonSurname(m.Groups["surname"].Value[0]));

    // prep_stage.py SENT: runs up to and including one of 。！？；!?; ; a newline ends a run and belongs to none; runs of
    // whitespace only are dropped.
    private static List<(int Start, int End)> Sentences(string text)
    {
        var sentences = new List<(int, int)>();
        for (var i = 0; i < text.Length;)
        {
            if (text[i] == '\n') { i++; continue; }
            var start = i;
            while (i < text.Length && text[i] != '\n' && !IsSentenceEnd(text[i])) i++;
            if (i < text.Length && IsSentenceEnd(text[i])) i++;
            if (!string.IsNullOrWhiteSpace(text[start..i])) sentences.Add((start, i));
        }
        return sentences;
    }

    private static bool IsSentenceEnd(char c) => c is '。' or '！' or '？' or '；' or '!' or '?' or ';';

    // prep_stage.py spans(): every occurrence of every form, longest first then leftmost, kept when it overlaps none kept.
    private static List<(int Start, int End)> Marks(string text, IReadOnlyList<string> forms)
    {
        var candidates = new List<(int Start, int End)>();
        foreach (var f in forms)
            for (var at = text.IndexOf(f, StringComparison.Ordinal); at >= 0; at = text.IndexOf(f, at + 1, StringComparison.Ordinal))
                candidates.Add((at, at + f.Length));
        var taken = new List<(int Start, int End)>();
        foreach (var c in candidates.OrderByDescending(c => c.End - c.Start).ThenBy(c => c.Start))
            if (!taken.Any(t => c.Start < t.End && t.Start < c.End)) taken.Add(c);
        return [.. taken.OrderBy(t => t.Start)];
    }

    private static string Render(string text, (int Start, int End) unit, List<(int Start, int End)> marks)
    {
        var b = new StringBuilder();
        var at = unit.Start;
        foreach (var (s, e) in marks)
            if (unit.Start <= s && e <= unit.End)
            {
                b.Append(text, at, s - at).Append(Open).Append(text, s, e - s).Append(Close);
                at = e;
            }
        return b.Append(text, at, unit.End - at).ToString();
    }

    // The UTF-16 length of the first n code points (Python counts code points).
    private static int CharsOfRunes(string text, int n)
    {
        var at = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (n-- == 0) break;
            at += rune.Utf16SequenceLength;
        }
        return at;
    }

    [GeneratedRegex(@"(\[unused[12]\])")]
    private static partial Regex Marker();

    [GeneratedRegex(@"(?:被告|嫌犯|犯嫌|嫌疑人|嫌疑犯)\s*(?<surname>\p{IsCJKUnifiedIdeographs})")]
    private static partial Regex Accused();

    public void Dispose() => session.Dispose();
}
