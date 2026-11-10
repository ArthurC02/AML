using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aml.Core.Ports;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Aml.Infra.Models;

/// <summary>
/// Runs the exported news models (bert-base-chinese fine-tuned, ADR-0015) with ONNX Runtime. The directory holds
/// <c>ner/</c> and <c>ev/</c>, each with <c>model.onnx</c>, <c>config.json</c> and <c>vocab.txt</c>; the models stay outside
/// the repository. Normalization, tokens, windows and decoding follow the Python training code, so .NET gives the same spans.
/// </summary>
public sealed class OnnxNewsModel : INewsModel, IDisposable
{
    private const int MaxTokens = 510, Step = 384, EventMaxTokens = 192, EventCrop = 150;
    private readonly InferenceSession ner, ev;
    private readonly BertWordPiece tokenizer;
    private readonly string[] tags, eventLabels;

    public OnnxNewsModel(string directory)
    {
        // Half the cores: measured faster than all of them on a 12-core machine.
        var options = new SessionOptions { IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2) };
        ner = new InferenceSession(Path.Combine(directory, "ner", "model.onnx"), options);
        ev = new InferenceSession(Path.Combine(directory, "ev", "model.onnx"), options);
        tokenizer = new BertWordPiece(Path.Combine(directory, "ner", "vocab.txt"));
        tags = Labels(Path.Combine(directory, "ner", "config.json"));
        eventLabels = Labels(Path.Combine(directory, "ev", "config.json"));
        Version = $"ner:{Hash(Path.Combine(directory, "ner", "model.onnx"))} ev:{Hash(Path.Combine(directory, "ev", "model.onnx"))}";
    }

    public string Version { get; }

    public IReadOnlyList<TaggedSpan> Tag(string text)
    {
        var tokens = Tokens(text);
        if (tokens.Count == 0) return [];
        // Overlapping windows; each token's label probabilities are summed over the windows that hold it.
        var sums = new float[tokens.Count, tags.Length];
        foreach (var (s, e) in Windows(tokens.Count))
        {
            var ids = new long[e - s + 2];
            ids[0] = tokenizer.Classification; ids[^1] = tokenizer.Separator;
            for (var i = s; i < e; i++) ids[i - s + 1] = tokens[i].Id;
            var logits = Run(ner, ids, new long[ids.Length]);
            for (var i = s; i < e; i++) Softmax(logits, (i - s + 1) * tags.Length, tags.Length, sums, i);
        }
        var best = new string[tokens.Count];
        for (var i = 0; i < best.Length; i++)
        {
            var k = 0;
            for (var j = 1; j < tags.Length; j++) if (sums[i, j] > sums[i, k]) k = j;
            best[i] = tags[k];
        }
        return Spans(best).Select(x => new TaggedSpan(tokens[x.From].Start, tokens[x.To - 1].End - tokens[x.From].Start, x.Role)).ToList();
    }

    // One pair per run: on a CPU, padded batches measured slower (64 pairs: 7.5 s batched, 4.2 s one at a time).
    public IReadOnlyList<IReadOnlyDictionary<string, float>> Events(IReadOnlyList<(string Mention, string Sentence)> pairs) =>
        pairs.Select(p =>
        {
            var (ids, types) = EventInput(p.Mention, p.Sentence);
            var logits = Run(ev, ids, types);
            return (IReadOnlyDictionary<string, float>)eventLabels.Select((l, i) => (l, q: 1f / (1f + MathF.Exp(-logits[i]))))
                .ToDictionary(x => x.l, x => x.q, StringComparer.Ordinal);
        }).ToList();

    // [CLS] mention [SEP] sentence [SEP], the sentence cropped to 150 characters around the mention and the whole cut to 192 tokens.
    private (long[] Ids, long[] Types) EventInput(string mention, string sentence)
    {
        mention = Normalize(mention); sentence = Normalize(sentence);
        var at = Math.Max(sentence.IndexOf(mention, StringComparison.Ordinal), 0);
        var from = Math.Max(0, Math.Min(at - EventCrop / 2, sentence.Length - EventCrop));
        var crop = sentence.Substring(from, Math.Min(EventCrop, sentence.Length - from));
        var first = tokenizer.Encode(mention);
        var room = Math.Max(0, EventMaxTokens - 3 - first.Count);
        var ids = new List<long>(EventMaxTokens) { tokenizer.Classification };
        ids.AddRange(first.Select(t => (long)t.Id)); ids.Add(tokenizer.Separator);
        var segment = ids.Count;
        ids.AddRange(tokenizer.Encode(crop).Take(room).Select(t => (long)t.Id)); ids.Add(tokenizer.Separator);
        return ([.. ids], [.. Enumerable.Range(0, ids.Count).Select(i => i < segment ? 0L : 1L)]);
    }

    public void Dispose()
    {
        ner.Dispose();
        ev.Dispose();
    }

    internal List<BertWordPiece.Token> Tokens(string text) => tokenizer.Encode(Normalize(text));

    /// <summary>NFKC per character where it stays one character, so offsets stay valid (as the training data was prepared).</summary>
    internal static string Normalize(string text)
    {
        var b = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            var s = rune.ToString();
            var n = s.Normalize(NormalizationForm.FormKC);
            b.Append(n.Length == s.Length ? n : s);
        }
        return b.ToString();
    }

    internal static IEnumerable<(int Start, int End)> Windows(int n)
    {
        var starts = new List<int>();
        for (var s = 0; s <= Math.Max(n - MaxTokens, 0); s += Step) starts.Add(s);
        if (starts[^1] + MaxTokens < n) starts.Add(n - MaxTokens);
        return starts.Select(s => (s, Math.Min(s + MaxTokens, n)));
    }

    /// <summary>BIO tags to spans: a span starts at B- (or at an I- with no open span) and continues over I- of the same role.</summary>
    internal static IEnumerable<(int From, int To, string Role)> Spans(IReadOnlyList<string> tags)
    {
        int? from = null; string? role = null;
        for (var i = 0; i <= tags.Count; i++)
        {
            var t = i < tags.Count ? tags[i] : "O";
            if (from is { } f && !(t.StartsWith("I-", StringComparison.Ordinal) && t[2..] == role)) { yield return (f, i, role!); from = null; }
            if (t.StartsWith("B-", StringComparison.Ordinal) || (t.StartsWith("I-", StringComparison.Ordinal) && from is null)) { from = i; role = t[2..]; }
        }
    }

    private static float[] Run(InferenceSession session, long[] ids, long[] types)
    {
        var shape = new[] { 1, ids.Length };
        var mask = new long[ids.Length];
        Array.Fill(mask, 1L);
        using var results = session.Run(
        [
            NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(ids, shape)),
            NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(mask, shape)),
            NamedOnnxValue.CreateFromTensor("token_type_ids", new DenseTensor<long>(types, shape)),
        ]);
        return results[0].AsEnumerable<float>().ToArray();
    }

    private static void Softmax(float[] logits, int offset, int count, float[,] sums, int row)
    {
        var max = float.MinValue;
        for (var j = 0; j < count; j++) max = Math.Max(max, logits[offset + j]);
        var total = 0f;
        for (var j = 0; j < count; j++) total += MathF.Exp(logits[offset + j] - max);
        for (var j = 0; j < count; j++) sums[row, j] += MathF.Exp(logits[offset + j] - max) / total;
    }

    private static string[] Labels(string config)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(config));
        var map = doc.RootElement.GetProperty("id2label");
        return [.. Enumerable.Range(0, map.EnumerateObject().Count()).Select(i => map.GetProperty(i.ToString(CultureInfo.InvariantCulture)).GetString()!)];
    }

    private static string Hash(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexStringLower(SHA256.HashData(stream))[..12];
    }
}
