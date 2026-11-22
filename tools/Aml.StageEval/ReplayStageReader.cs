using System.Text.Json;
using Aml.Core.Ports;
using Aml.Core.Screening;

namespace Aml.StageEval;

/// <summary>The language model reader replayed: the cached answer for (doc, person id), null when absent or not a stage.</summary>
internal sealed class ReplayStageReader : IStageReader
{
    private readonly Dictionary<(string, string), StageAnswer> answers = [];

    /// <summary>The (doc, person id) the next <see cref="Read"/> answers for.</summary>
    public (string Doc, string Person) Key { get; set; }

    public string Name => "llm";

    public ReplayStageReader(string file)
    {
        if (!File.Exists(file)) { Console.Error.WriteLine($"no Gemma file {file}: llm absent"); return; }
        foreach (var line in File.ReadLines(file))
        {
            using var row = JsonDocument.Parse(line);
            var doc = row.RootElement.GetProperty("doc").GetString()!;
            if (!row.RootElement.TryGetProperty("persons", out var persons) || persons.ValueKind != JsonValueKind.Array) continue;
            foreach (var p in persons.EnumerateArray())
            {
                var stage = p.TryGetProperty("stage", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
                if (stage is null || !Enum.TryParse<LegalStage>(stage, true, out var st) || !Enum.IsDefined(st)) continue; // MISSING etc.
                var quote = p.TryGetProperty("quote", out var q) && q.ValueKind == JsonValueKind.String ? q.GetString() : null;
                answers.TryAdd((doc, p.GetProperty("id").ToString()), new(st, string.IsNullOrEmpty(quote) ? null : quote));
            }
        }
    }

    public StageAnswer? Read(string text, IReadOnlyList<string> names) => answers.GetValueOrDefault(Key);
}
