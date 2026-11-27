using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aml.Core.Ports;
using Aml.Core.Screening;
using Aml.Infra.Models;

namespace Aml.Tests.Unit.Extraction;

/// <summary>The large language model reader against a stand-in Ollama server (no network): what it sends and how it reads the reply.</summary>
public class OllamaStageReaderTests
{
    private sealed class Server(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> reply) : HttpMessageHandler
    {
        public JsonNode? Body { get; private set; }
        public Uri? Uri { get; private set; }

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            (Uri, Body) = (request.RequestUri, JsonNode.Parse(request.Content!.ReadAsStream(cancellationToken)));
            return reply(request, cancellationToken);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Send(request, cancellationToken));
    }

    private static HttpResponseMessage Reply(string content, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(new { model = "gemma4", message = new { role = "assistant", content }, done = true })) };

    private static (StageAnswer? Answer, Server Server) Read(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> reply, string text = "檢方今日起訴陳志明。",
        TimeSpan? timeout = null)
    {
        var server = new Server(reply);
        using var http = new HttpClient(server) { BaseAddress = new Uri("http://ollama.test:11434"), Timeout = timeout ?? TimeSpan.FromSeconds(120) };
        return (new OllamaStageReader(http, "gemma4:12b-it-q4_K_M").Read(text, ["陳志明", "陳男"]), server);
    }

    [Fact]
    public void A_good_answer_gives_the_stage_and_quote_of_person_one()
    {
        var (answer, _) = Read((_, _) => Reply("""{"persons":[{"id":"2","stage":"CONVICTED","quote":"x"},{"id":"1","stage":"INDICTED","quote":"起訴陳志明"}]}"""));
        Assert.Equal(new StageAnswer(LegalStage.Indicted, "起訴陳志明"), answer);
        Assert.Equal(new StageAnswer(LegalStage.Unknown), Read((_, _) => Reply("""{"persons":[{"id":"1","stage":"UNKNOWN","quote":""}]}""")).Answer);
    }

    // A well-formed answer without a stage for person 1 is the model's answer: UNKNOWN, not a missing one.
    [Theory]
    [InlineData("""{"persons":[]}""")]
    [InlineData("""{"persons":[{"id":"2","stage":"CONVICTED","quote":"x"}]}""")]
    public void A_well_formed_answer_without_person_one_is_unknown(string content) =>
        Assert.Equal(new StageAnswer(LegalStage.Unknown), Read((_, _) => Reply(content)).Answer);

    // An answer that cannot be read is missing: one exception type for every way it fails, with no inner exception (it may quote the reply).
    [Theory]
    [InlineData("not json")]
    [InlineData("""{"persons":[{"id":"1","stage":"ARRESTED","quote":""}]}""")]
    [InlineData("""{"people":[]}""")]
    [InlineData("""{"persons":{}}""")]
    [InlineData("""{"persons":5}""")]
    public void An_unreadable_answer_throws_invalid_data(string content) =>
        Assert.Null(Assert.Throws<InvalidDataException>(() => Read((_, _) => Reply(content))).InnerException);

    // Each failure throws its own exception type, which the host logs (LoggingStageReader); the vote takes any as missing.
    [Fact]
    public void A_server_error_a_transport_error_and_a_timeout_each_throw_their_own_type()
    {
        Assert.Throws<HttpRequestException>(() => Read((_, _) => Reply("", HttpStatusCode.InternalServerError)));
        Assert.Throws<HttpRequestException>(() => Read((_, _) => throw new HttpRequestException("refused")));
        Assert.Throws<TimeoutException>(() => Read((_, ct) => { ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(30)); ct.ThrowIfCancellationRequested(); return Reply("{}"); },
            timeout: TimeSpan.FromMilliseconds(50)));
    }

    /// <summary>A call's own limit (what is left of a Case's budget) cuts off a held answer; an answer within it counts.</summary>
    [Fact]
    public void A_call_past_its_own_limit_times_out_and_one_within_it_gives_the_stage()
    {
        using var gate = new ManualResetEventSlim();
        var server = new Server((_, ct) =>
        {
            WaitHandle.WaitAny([gate.WaitHandle, ct.WaitHandle]);
            ct.ThrowIfCancellationRequested();
            return Reply("""{"persons":[{"id":"1","stage":"INDICTED","quote":""}]}""");
        });
        using var http = new HttpClient(server) { BaseAddress = new Uri("http://ollama.test:11434"), Timeout = TimeSpan.FromSeconds(120) };
        var reader = new OllamaStageReader(http, "gemma4:12b-it-q4_K_M");
        try
        {
            var held = Task.Run(() => reader.Read("檢方今日起訴陳志明。", ["陳志明"], TimeSpan.FromMilliseconds(50)), TestContext.Current.CancellationToken);
            Assert.True(((IAsyncResult)held).AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(30))); // safety bound only, not the TimeoutException below
            Assert.IsType<TimeoutException>(held.Exception?.InnerException);
        }
        finally
        {
            gate.Set();
        }
        Assert.Equal(new StageAnswer(LegalStage.Indicted), reader.Read("檢方今日起訴陳志明。", ["陳志明"], TimeSpan.FromSeconds(120)));
    }

    [Fact]
    public void It_sends_the_prompt_the_schema_and_the_measured_options()
    {
        var (_, server) = Read((_, _) => Reply("""{"persons":[]}"""));
        var body = server.Body!;
        Assert.Equal("http://ollama.test:11434/api/chat", server.Uri!.ToString());
        Assert.Equal(("gemma4:12b-it-q4_K_M", false, false), ((string?)body["model"], (bool)body["stream"]!, (bool)body["think"]!));
        Assert.Equal("""{"temperature":0,"seed":1,"num_ctx":24576,"num_predict":4096}""", body["options"]!.ToJsonString());
        Assert.Equal("""{"type":"object","required":["persons"],"properties":{"persons":{"type":"array","items":{"type":"object","required":["id","stage","quote"],"properties":{"id":{"type":"string"},"stage":{"type":"string","enum":["CONVICTED","ACQUITTED","DISMISSED","INDICTED","WANTED","ALLEGED","UNKNOWN"]},"quote":{"type":"string"}}}}}}""",
            body["format"]!.ToJsonString());
        var prompt = (string)body["messages"]![0]!["content"]!;
        Assert.Contains("<<<人員清單開始>>>\n1：陳志明、陳男\n<<<人員清單結束>>>", prompt, StringComparison.Ordinal);
        Assert.Contains("<<<文件開始>>>\n檢方今日起訴陳志明。\n<<<文件結束>>>", prompt, StringComparison.Ordinal);
        Assert.StartsWith("你是協助閱讀臺灣法律文件的助手。", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void A_long_document_keeps_its_first_10000_and_last_6000_characters()
    {
        var text = new string('甲', 10000) + new string('乙', 1) + new string('丙', 6000);
        var prompt = (string)Read((_, _) => Reply("""{"persons":[]}"""), text).Server.Body!["messages"]![0]!["content"]!;
        Assert.Contains("<<<文件開始>>>\n" + new string('甲', 10000) + "\n（中間省略）\n" + new string('丙', 6000) + "\n<<<文件結束>>>", prompt, StringComparison.Ordinal);
        var exactly = new string('甲', 16000); // not over the limit: sent whole
        Assert.Contains("\n" + exactly + "\n", (string)Read((_, _) => Reply("""{"persons":[]}"""), exactly).Server.Body!["messages"]![0]!["content"]!, StringComparison.Ordinal);
    }
}
