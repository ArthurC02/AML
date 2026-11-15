using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    private const int MaxTokens = 510, Step = 384, EventMaxTokens = 512, EventCrop = 460;
    private const long MentionOpen = 1, MentionClose = 2; // [unused1], [unused2]
    private readonly InferenceSession ner, ev;
    private readonly BertWordPiece tokenizer;
    private readonly string[] tags, eventLabels;
    private readonly bool nerAsciiLower;
    private readonly InferenceSession? documentRoles;
    private readonly int hiddenSize;
    private readonly InferenceSession? eventContext;
    private readonly int eventHiddenSize;
    private readonly int eventContextVersion;
    private readonly int eventCaptionVersion;

    public OnnxNewsModel(string directory)
    {
        using var eventConfig = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "ev", "config.json")));
        eventCaptionVersion = eventConfig.RootElement.TryGetProperty("aml_event_caption_version", out var caption) ? caption.GetInt32() : 0;
        if (eventCaptionVersion is not (0 or 1)) throw new InvalidDataException("Unsupported event caption observation version");
        if (eventCaptionVersion != 0 && eventConfig.RootElement.TryGetProperty("aml_event_document_context", out var captionContext) && captionContext.GetBoolean())
            throw new InvalidDataException("Caption observations cannot be combined with the event context head");
        // Half the cores: measured faster than all of them on a 12-core machine.
        var options = new SessionOptions { IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2) };
        ner = new InferenceSession(Path.Combine(directory, "ner", "model.onnx"), options);
        ev = new InferenceSession(Path.Combine(directory, "ev", "model.onnx"), options);
        tokenizer = new BertWordPiece(Path.Combine(directory, "ner", "vocab.txt"));
        tags = Labels(Path.Combine(directory, "ner", "config.json"));
        using var nerConfig = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "ner", "config.json")));
        nerAsciiLower = nerConfig.RootElement.TryGetProperty("aml_ner_ascii_lower", out var lower) && lower.GetBoolean();
        if (nerConfig.RootElement.TryGetProperty("aml_ner_document_roles", out var global) && global.GetBoolean())
        {
            var roles = nerConfig.RootElement.GetProperty("aml_ner_document_role_labels").EnumerateArray().Select(x => x.GetString()).ToArray();
            if (tags.Length != 15 || !roles.SequenceEqual(tags.Where(t => t.StartsWith("B-", StringComparison.Ordinal)).Select(t => t[2..]).Prepend("O"))
                || !Enumerable.Range(0, 7).All(i => tags[2 * i + 1] == "B-" + roles[i + 1] && tags[2 * i + 2] == "I-" + roles[i + 1]))
                throw new InvalidDataException("Document-role labels differ from the NER tag order");
            hiddenSize = nerConfig.RootElement.GetProperty("hidden_size").GetInt32();
            if (hiddenSize <= 0 || !ner.OutputMetadata.ContainsKey("hidden")) throw new InvalidDataException("Document roles require encoder hidden states");
            documentRoles = new InferenceSession(Path.Combine(directory, "ner", "document-roles.onnx"), options);
        }
        eventLabels = Labels(Path.Combine(directory, "ev", "config.json"));
        if (eventConfig.RootElement.TryGetProperty("aml_event_document_context", out var context) && context.GetBoolean())
        {
            eventContextVersion = eventConfig.RootElement.GetProperty("aml_event_context_version").GetInt32();
            if (eventContextVersion is not (1 or 2 or 3)
                || eventConfig.RootElement.GetProperty("aml_event_context_ner_onnx_sha256").GetString() != CompleteHash(Path.Combine(directory, "ner", "model.onnx"))
                || eventConfig.RootElement.GetProperty("aml_event_context_ner_config_sha256").GetString() != CompleteHash(Path.Combine(directory, "ner", "config.json"))
                || eventConfig.RootElement.GetProperty("aml_event_context_ner_vocab_sha256").GetString() != CompleteHash(Path.Combine(directory, "ner", "vocab.txt")))
                throw new InvalidDataException("Event document context requires its matching NER encoder");
            hiddenSize = nerConfig.RootElement.GetProperty("hidden_size").GetInt32();
            eventHiddenSize = eventConfig.RootElement.GetProperty("hidden_size").GetInt32();
            if (tags.Length != 15 || hiddenSize <= 0 || eventHiddenSize <= 0 || !ner.OutputMetadata.ContainsKey("hidden") || !ev.OutputMetadata.ContainsKey("pooled"))
                throw new InvalidDataException("Event document context requires encoder representations");
            eventContext = new InferenceSession(Path.Combine(directory, "ev", "event-context.onnx"), options);
        }
        Version = $"ner:{Hash(Path.Combine(directory, "ner", "model.onnx"))}{(nerAsciiLower ? ":ascii-lower" : "")} ev:{Hash(Path.Combine(directory, "ev", "model.onnx"))}{(eventCaptionVersion == 1 ? ":caption-v1" : "")}";
        if (documentRoles is not null) Version += $" roles:{Hash(Path.Combine(directory, "ner", "document-roles.onnx"))}";
        if (eventContext is not null) Version += $" context:{Hash(Path.Combine(directory, "ev", "event-context.onnx"))}";
        if (eventContextVersion == 3) Version += ":context-only";
    }

    public string Version { get; }
    public bool RequiresDocumentContext => eventContext is not null || eventCaptionVersion != 0;

    private (List<BertWordPiece.Token> Tokens, float[,] Probabilities, float[,]? Hidden) ReadNer(string text, bool needHidden)
    {
        var tokens = Tokens(text);
        // Overlapping windows; each token's label probabilities are averaged over the windows that hold it.
        var probs = new float[tokens.Count, tags.Length];
        var windows = new int[tokens.Count];
        var hidden = needHidden ? new float[tokens.Count, hiddenSize] : null;
        if (tokens.Count == 0) return (tokens, probs, hidden);
        foreach (var (s, e) in Windows(tokens.Count))
        {
            var ids = new long[e - s + 2];
            ids[0] = tokenizer.Classification; ids[^1] = tokenizer.Separator;
            for (var i = s; i < e; i++) ids[i - s + 1] = tokens[i].Id;
            var logits = Run(ner, ids, new long[ids.Length], out var states);
            for (var i = s; i < e; i++) { Softmax(logits, (i - s + 1) * tags.Length, tags.Length, probs, i); windows[i]++; }
            if (hidden is not null)
            {
                if (states is null || states.Length != ids.Length * hiddenSize) throw new InvalidDataException("Invalid encoder hidden states");
                for (var i = s; i < e; i++)
                    for (var j = 0; j < hiddenSize; j++) hidden[i, j] += states[(i - s + 1) * hiddenSize + j];
            }
        }
        for (var i = 0; i < tokens.Count; i++)
            for (var j = 0; j < tags.Length; j++) probs[i, j] /= windows[i];
        if (hidden is not null)
            for (var i = 0; i < tokens.Count; i++)
                for (var j = 0; j < hiddenSize; j++) hidden[i, j] /= windows[i];
        return (tokens, probs, hidden);
    }

    public IReadOnlyList<TaggedSpan> Tag(string text)
    {
        var (tokens, probs, hidden) = ReadNer(text, documentRoles is not null);
        if (tokens.Count == 0) return [];
        if (hidden is not null)
        {
            var (groups, features, mask) = RoleGroups(Normalize(text), tokens, probs, hidden, tags);
            if (groups.Count > 0)
            {
                using var result = documentRoles!.Run([
                    NamedOnnxValue.CreateFromTensor("features", new DenseTensor<float>(features.Cast<float>().ToArray(), new[] { groups.Count, features.GetLength(1), features.GetLength(2) })),
                    NamedOnnxValue.CreateFromTensor("mask", new DenseTensor<float>(mask.Cast<float>().ToArray(), new[] { groups.Count, mask.GetLength(1) }))]);
                var values = result[0].AsEnumerable<float>().ToArray();
                if (values.Length != groups.Count * 8 || values.Any(v => !float.IsFinite(v))) throw new InvalidDataException("Invalid document-role logits");
                ApplyRoleBiases(probs, groups, values);
            }
        }
        return Spans(Decode(probs, tags))
            .Select(x => new TaggedSpan(tokens[x.From].Start, tokens[x.To - 1].End - tokens[x.From].Start, x.Role)).ToList();
    }

    /// <summary>
    /// The most probable tag sequence in which every I- follows a B- or I- of its role (Viterbi over log probabilities).
    /// Measured on the held-out documents: more people found and fewer extra names than the most probable tag per token.
    /// </summary>
    internal static string[] Decode(float[,] probs, string[] tags)
    {
        int n = probs.GetLength(0), k = tags.Length;
        bool Allowed(int from, int to) => !tags[to].StartsWith("I-", StringComparison.Ordinal)
            || (from >= 0 && tags[from] != "O" && tags[from][2..] == tags[to][2..]);
        var score = new double[k];
        var back = new int[n, k];
        for (var j = 0; j < k; j++) score[j] = Allowed(-1, j) ? Math.Log(Math.Max(probs[0, j], 1e-9)) : double.NegativeInfinity;
        for (var t = 1; t < n; t++)
        {
            var next = new double[k];
            for (var j = 0; j < k; j++)
            {
                var (best, arg) = (double.NegativeInfinity, 0);
                for (var i = 0; i < k; i++)
                    if (Allowed(i, j) && score[i] > best) (best, arg) = (score[i], i);
                next[j] = best + Math.Log(Math.Max(probs[t, j], 1e-9));
                back[t, j] = arg;
            }
            score = next;
        }
        var path = new string[n];
        var last = 0;
        for (var j = 1; j < k; j++) if (score[j] > score[last]) last = j;
        for (var t = n - 1; t >= 0; t--) { path[t] = tags[last]; if (t > 0) last = back[t, last]; }
        return path;
    }

    // One pair per run: on a CPU, padded batches measured slower (64 pairs: 7.5 s batched, 4.2 s one at a time).
    public IReadOnlyList<IReadOnlyDictionary<string, float>> Events(IReadOnlyList<(string Mention, string Sentence)> pairs)
    {
        if (RequiresDocumentContext) throw new InvalidOperationException("This event model requires the source document");
        return Events(string.Empty, pairs);
    }

    public IReadOnlyList<IReadOnlyDictionary<string, float>> Events(string text, IReadOnlyList<(string Mention, string Sentence)> pairs)
    {
        if (eventContext is null)
        {
            var caption = eventCaptionVersion == 1 ? EventCaption(text) : null;
            return pairs.Select(p =>
            {
                var (ids, types) = EventInput(p.Mention, p.Sentence);
                if (eventCaptionVersion == 1) (ids, types) = CaptionObservation(ids, types, caption, p.Mention);
                var logits = Run(ev, ids, types);
                return (IReadOnlyDictionary<string, float>)eventLabels.Select((l, i) => (l, q: 1f / (1f + MathF.Exp(-logits[i]))))
                    .ToDictionary(x => x.l, x => x.q, StringComparer.Ordinal);
            }).ToList();
        }
        if (pairs.Count == 0) return [];
        // shortcut: encode the document again per event batch; share an immutable result if latency becomes material.
        var (tokens, probabilities, hidden) = ReadNer(text, true);
        var normalized = Normalize(text);
        var contexts = new Dictionary<string, float[,]>(StringComparer.Ordinal);
        return pairs.Select(pair =>
        {
            var mention = Normalize(pair.Mention);
            if (!contexts.TryGetValue(mention, out var values))
            {
                values = EventContexts(normalized, mention, tokens, probabilities, hidden!);
                contexts.Add(mention, values);
            }
            if (eventContextVersion >= 2)
            {
                var flags = EventAnchors(normalized, mention, Normalize(pair.Sentence));
                if (flags.GetLength(0) != values.GetLength(0)) throw new InvalidDataException("Query anchors differ from the source mention contexts");
                var augmented = new float[values.GetLength(0), values.GetLength(1) + 3];
                for (var i = 0; i < values.GetLength(0); i++)
                {
                    for (var j = 0; j < values.GetLength(1); j++) augmented[i, j] = values[i, j];
                    for (var j = 0; j < 3; j++) augmented[i, values.GetLength(1) + j] = flags[i, j];
                }
                values = augmented;
            }
            var (ids, types) = EventInput(pair.Mention, pair.Sentence);
            var logits = Run(ev, ids, types, out var pooled, "pooled");
            if (pooled is null || pooled.Length != eventHiddenSize) throw new InvalidDataException("Invalid event query representation");
            var mask = Enumerable.Repeat(1f, values.GetLength(0)).ToArray();
            using var result = eventContext.Run([
                NamedOnnxValue.CreateFromTensor("query", new DenseTensor<float>(pooled, new[] { 1, eventHiddenSize })),
                NamedOnnxValue.CreateFromTensor("contexts", new DenseTensor<float>(values.Cast<float>().ToArray(), new[] { 1, values.GetLength(0), values.GetLength(1) })),
                NamedOnnxValue.CreateFromTensor("mask", new DenseTensor<float>(mask, new[] { 1, mask.Length }))]);
            var bias = result[0].AsEnumerable<float>().ToArray();
            if (bias.Length != eventLabels.Length || logits.Length != eventLabels.Length || bias.Any(v => !float.IsFinite(v))) throw new InvalidDataException("Invalid event context logits");
            return (IReadOnlyDictionary<string, float>)eventLabels.Select((label, i) => (label, value: 1f / (1f + MathF.Exp(-(eventContextVersion == 3 ? bias[i] : logits[i] + bias[i])))))
                .ToDictionary(v => v.label, v => v.value, StringComparer.Ordinal);
        }).ToList();
    }

    // [CLS] mention [SEP] sentence [SEP]: the sentence cut to 460 characters around the mention, every occurrence of the
    // mention in it wrapped in two marker tokens (which of the people named the question is about), the whole cut to 512 tokens.
    private (long[] Ids, long[] Types) EventInput(string mention, string sentence)
    {
        mention = Normalize(mention); sentence = Normalize(sentence);
        var at = Math.Max(sentence.IndexOf(mention, StringComparison.Ordinal), 0);
        var from = Math.Max(0, Math.Min(at - EventCrop / 2, sentence.Length - EventCrop));
        var crop = sentence.Substring(from, Math.Min(EventCrop, sentence.Length - from));
        var first = tokenizer.Encode(mention).Select(t => (long)t.Id).ToList();
        var second = new List<long>();
        var pieces = mention.Length == 0 ? [crop] : crop.Split(mention, StringSplitOptions.None);
        for (var k = 0; k < pieces.Length; k++)
        {
            if (k > 0) { second.Add(MentionOpen); second.AddRange(first); second.Add(MentionClose); }
            second.AddRange(tokenizer.Encode(pieces[k]).Select(t => (long)t.Id));
        }
        var ids = new List<long>(EventMaxTokens) { tokenizer.Classification };
        ids.AddRange(first); ids.Add(tokenizer.Separator);
        var segment = ids.Count;
        ids.AddRange(second.Take(Math.Max(0, EventMaxTokens - 1 - segment))); ids.Add(tokenizer.Separator);
        return ([.. ids], [.. Enumerable.Range(0, ids.Count).Select(i => i < segment ? 0L : 1L)]);
    }

    internal static string? EventCaption(string text)
    {
        var normalized = Normalize(text);
        var match = Regex.Match(normalized, @"^[ \t\u3000]*主[ \t\u3000\r\n]*文[ \t\u3000]*\r?$", RegexOptions.Multiline | RegexOptions.CultureInvariant);
        return match.Success && match.Index > 0 ? normalized[..match.Index] : null;
    }

    internal static (long[] Ids, long[] Types) CaptionObservation(long[] ids, long[] types, string? caption, string mention)
    {
        if (ids.Length >= EventMaxTokens) return (ids, types);
        mention = Normalize(mention);
        var marker = caption is null || mention.Length == 0 ? 5L : caption.Contains(mention, StringComparison.Ordinal) ? 3L : 4L;
        return ([.. ids, marker], [.. types, 1L]);
    }

    public void Dispose()
    {
        ner.Dispose();
        ev.Dispose();
        documentRoles?.Dispose();
        eventContext?.Dispose();
    }

    internal List<BertWordPiece.Token> Tokens(string text) => tokenizer.Encode(Normalize(text, nerAsciiLower));

    /// <summary>NFKC per character where it stays one character, so offsets stay valid (as the training data was prepared).</summary>
    internal static string Normalize(string text, bool asciiLower = false)
    {
        var b = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            var s = rune.ToString();
            var n = s.Normalize(NormalizationForm.FormKC);
            var value = n.Length == s.Length ? n : s;
            if (asciiLower && value.Length == 1 && value[0] is >= 'A' and <= 'Z') b.Append((char)(value[0] + ('a' - 'A')));
            else b.Append(value);
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

    private static float[] Run(InferenceSession session, long[] ids, long[] types) => Run(session, ids, types, out _);

    private static float[] Run(InferenceSession session, long[] ids, long[] types, out float[]? hidden, string stateName = "hidden")
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
        hidden = results.FirstOrDefault(r => r.Name == stateName)?.AsEnumerable<float>().ToArray();
        return results[0].AsEnumerable<float>().ToArray();
    }

    internal static (List<List<(int From, int To)>> Groups, float[,,] Features, float[,] Mask) RoleGroups(
        string text, IReadOnlyList<BertWordPiece.Token> tokens, float[,] probabilities, float[,] hidden, string[] labels)
    {
        var greedy = Enumerable.Range(0, tokens.Count).Select(i => labels[Enumerable.Range(0, labels.Length).MaxBy(j => probabilities[i, j])]).ToArray();
        var groups = Spans(greedy).GroupBy(s => text[tokens[s.From].Start..tokens[s.To - 1].End], StringComparer.Ordinal)
            .Select(g => g.Select(s => (s.From, s.To)).ToList()).ToList();
        var count = groups.Count == 0 ? 0 : groups.Max(g => g.Count);
        var size = hidden.GetLength(1);
        var features = new float[groups.Count, count, size + 8];
        var mask = new float[groups.Count, count];
        for (var g = 0; g < groups.Count; g++)
            for (var m = 0; m < groups[g].Count; m++)
            {
                var (a, b) = groups[g][m];
                mask[g, m] = 1;
                for (var i = a; i < b; i++)
                {
                    for (var j = 0; j < size; j++) features[g, m, j] += hidden[i, j] / (b - a);
                    features[g, m, size] += probabilities[i, 0] / (b - a);
                    for (var j = 0; j < 7; j++) features[g, m, size + j + 1] += (probabilities[i, 2 * j + 1] + probabilities[i, 2 * j + 2]) / (b - a);
                }
            }
        return (groups, features, mask);
    }

    internal static void ApplyRoleBiases(float[,] probabilities, List<List<(int From, int To)>> groups, float[] biases)
    {
        var classes = probabilities.GetLength(1);
        var logits = new float[classes];
        for (var g = 0; g < groups.Count; g++)
            foreach (var (a, b) in groups[g])
                for (var i = a; i < b; i++)
                {
                    for (var j = 0; j < classes; j++) logits[j] = MathF.Log(Math.Max(probabilities[i, j], 1e-9f)) + biases[g * 8 + (j + 1) / 2];
                    var max = logits.Max();
                    var total = logits.Sum(v => MathF.Exp(v - max));
                    for (var j = 0; j < classes; j++) probabilities[i, j] = MathF.Exp(logits[j] - max) / total;
                }
    }

    internal static float[,] EventContexts(string text, string mention, IReadOnlyList<BertWordPiece.Token> tokens, float[,] probabilities, float[,] hidden)
    {
        var size = hidden.GetLength(1);
        float[] Feature(int a, int b, bool header)
        {
            var values = new float[size + 10];
            if (a == b) return values;
            for (var i = a; i < b; i++)
            {
                for (var j = 0; j < size; j++) values[j] += hidden[i, j] / (b - a);
                values[size] += probabilities[i, 0] / (b - a);
                for (var j = 0; j < 7; j++) values[size + j + 1] += (probabilities[i, 2 * j + 1] + probabilities[i, 2 * j + 2]) / (b - a);
            }
            values[^2] = header ? 1 : 0;
            values[^1] = header ? 0 : 1;
            return values;
        }
        var contexts = new List<float[]> { Feature(0, Math.Min(256, tokens.Count), true) };
        for (var at = mention.Length == 0 ? -1 : text.IndexOf(mention, StringComparison.Ordinal); at >= 0; at = text.IndexOf(mention, at + 1, StringComparison.Ordinal))
        {
            int lo = 0, hi = tokens.Count;
            while (lo < hi) { var m = (lo + hi) / 2; if (tokens[m].End <= at) lo = m + 1; else hi = m; }
            var a = lo;
            lo = 0; hi = tokens.Count;
            while (lo < hi) { var m = (lo + hi) / 2; if (tokens[m].Start < at + mention.Length) lo = m + 1; else hi = m; }
            if (a < lo) contexts.Add(Feature(a, lo, false));
        }
        var output = new float[contexts.Count, size + 10];
        for (var i = 0; i < contexts.Count; i++)
            for (var j = 0; j < size + 10; j++) output[i, j] = contexts[i][j];
        return output;
    }

    internal static float[,] EventAnchors(string text, string mention, string sentence)
    {
        List<(int Start, int End)> Occurrences(string value)
        {
            var spans = new List<(int, int)>();
            for (var at = value.Length == 0 ? -1 : text.IndexOf(value, StringComparison.Ordinal); at >= 0; at = text.IndexOf(value, at + 1, StringComparison.Ordinal))
                spans.Add((at, at + value.Length));
            return spans;
        }
        var mentions = Occurrences(mention);
        var flags = new float[mentions.Count + 1, 3];
        foreach (var (start, end) in Occurrences(sentence))
        {
            int before = 0, after = 0;
            for (var i = 0; i < mentions.Count; i++)
            {
                var (a, b) = mentions[i];
                if (a >= start && b <= end) flags[i + 1, 0] = 1;
                else if (b <= start) before = i + 1;
                else if (a >= end && after == 0) after = i + 1;
            }
            if (before != 0) flags[before, 1] = 1;
            if (after != 0) flags[after, 2] = 1;
        }
        return flags;
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

    private static string Hash(string file) => CompleteHash(file)[..12];

    private static string CompleteHash(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
