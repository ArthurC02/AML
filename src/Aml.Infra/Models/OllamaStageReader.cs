using System.Text.Json;
using System.Text.Json.Nodes;
using Aml.Core.Ports;
using Aml.Core.Screening;

namespace Aml.Infra.Models;

/// <summary>
/// The large language model reader (Gemma 4 through Ollama's /api/chat, watchlist-and-adverse-information.md#three-readers):
/// one call per document with the fixed prompt (Models/stage-prompt.txt, as measured), the Profile person as person 1 with
/// the Profile's names, and a JSON schema the answer must follow. Only the news text and the Profile's names are sent
/// (security-and-privacy.md). A well-formed answer without a known stage for person 1 (an empty persons list included) is the
/// model's answer UNKNOWN. Any failure (HTTP error, timeout through the client's Timeout or the call's own limit, unreadable
/// answer) is null, a missing answer: the vote then gives UNKNOWN with STAGE_READER_NO_ANSWER. Infra has no logger: the host
/// logs the missing answer (Aml.Api.LoggingStageReader), without the failure's own detail.
/// </summary>
/// <param name="http">A client whose BaseAddress is the Ollama endpoint and whose Timeout is the reader's timeout.</param>
public sealed class OllamaStageReader(HttpClient http, string model) : IStageReader
{
    // As the measured run sent them (job45): a long document keeps its first 10,000 and last 6,000 characters.
    internal const int LongText = 16000, Head = 10000, Tail = 6000;
    internal const string CutMark = "\n（中間省略）\n";
    private const string PersonId = "1";

    internal static readonly string Prompt = LoadPrompt();

    private static readonly string[] Stages = ["CONVICTED", "ACQUITTED", "DISMISSED", "INDICTED", "WANTED", "ALLEGED", "UNKNOWN"];

    public string Name => "llm";

    public StageAnswer? Read(string text, IReadOnlyList<string> names) => Read(text, names, Timeout.InfiniteTimeSpan);

    /// <summary>One call cut off after <paramref name="timeout"/> as well as the client's Timeout: what is left of a Case's
    /// language-model budget (BudgetedStageReader).</summary>
    public StageAnswer? Read(string text, IReadOnlyList<string> names, TimeSpan timeout)
    {
        try
        {
            using var limit = new CancellationTokenSource(timeout);
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/chat")
            {
                Content = new StringContent(Body(text, names).ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
            };
            using var response = http.Send(request, limit.Token);
            if (!response.IsSuccessStatusCode) return null;
            using var reader = new StreamReader(response.Content.ReadAsStream(limit.Token));
            return Answer(reader.ReadToEnd());
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return null; // a missing answer: the vote gives UNKNOWN with STAGE_READER_NO_ANSWER; it never blocks the analysis
        }
    }

    /// <summary>The request body: model, prompt, no streaming, the answer's JSON schema, deterministic options, no thinking.</summary>
    internal JsonObject Body(string text, IReadOnlyList<string> names)
    {
        if (text.Length > LongText) text = text[..Head] + CutMark + text[^Tail..];
        var persons = PersonId + "：" + string.Join("、", names.Where(n => n.Length > 0).Distinct(StringComparer.Ordinal));
        var prompt = Prompt.Replace("{{PERSONS}}", persons, StringComparison.Ordinal).Replace("{{TEXT}}", text, StringComparison.Ordinal);
        return new JsonObject
        {
            ["model"] = model,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = prompt }),
            ["stream"] = false,
            ["format"] = Schema(),
            ["options"] = new JsonObject { ["temperature"] = 0, ["seed"] = 1, ["num_ctx"] = 24576, ["num_predict"] = 4096 },
            ["think"] = false,
        };
    }

    private static JsonObject Schema() => new()
    {
        ["type"] = "object",
        ["required"] = new JsonArray("persons"),
        ["properties"] = new JsonObject
        {
            ["persons"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["required"] = new JsonArray("id", "stage", "quote"),
                    ["properties"] = new JsonObject
                    {
                        ["id"] = new JsonObject { ["type"] = "string" },
                        ["stage"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray([.. Stages.Select(s => (JsonNode)s)]) },
                        ["quote"] = new JsonObject { ["type"] = "string" },
                    },
                },
            },
        },
    };

    // Ollama's reply holds the model's JSON as text in message.content; the first entry for person 1 with a known stage counts.
    // A reply that follows the schema but holds no entry for person 1 (an empty list included) is the model's answer that it
    // read no stage: UNKNOWN. A reply that does not parse, lacks persons, or gives person 1 only a stage outside the schema
    // cannot be read, and so is missing.
    private static StageAnswer? Answer(string reply)
    {
        using var outer = JsonDocument.Parse(reply);
        var content = outer.RootElement.GetProperty("message").GetProperty("content").GetString() ?? "";
        using var inner = JsonDocument.Parse(content);
        var unreadable = false;
        foreach (var person in inner.RootElement.GetProperty("persons").EnumerateArray())
        {
            var id = person.TryGetProperty("id", out var i) ? i.ToString() : "";
            if (id != PersonId) continue;
            var stage = person.TryGetProperty("stage", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
            if (stage is null || !Stages.Contains(stage)) { unreadable = true; continue; }
            var quote = person.TryGetProperty("quote", out var q) ? q.ToString() : "";
            return new(Enum.Parse<LegalStage>(stage, ignoreCase: true), quote.Length > 0 ? quote : null);
        }
        return unreadable ? null : new(LegalStage.Unknown);
    }

    private static string LoadPrompt()
    {
        using var stream = typeof(OllamaStageReader).Assembly.GetManifestResourceStream("Models.stage-prompt.txt")
            ?? throw new InvalidOperationException("Embedded resource Models.stage-prompt.txt is missing");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal); // a checkout with CRLF line ends still sends the measured prompt
    }
}
