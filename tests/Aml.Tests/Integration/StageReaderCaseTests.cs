using System.Text.Json;
using System.Text.Json.Nodes;
using Aml.Core.Cases;
using Aml.Core.Ports;
using Aml.Core.Screening;
using Aml.Core.Screening.Stage;
using Aml.Infra.Models;
using Aml.Infra.Txt;
using Aml.Services;

namespace Aml.Tests.Integration;

/// <summary>
/// The rules and the stage models voting on one Case, through the service and the store
/// (watchlist-and-adverse-information.md#three-readers); and the trained classifier against its training run.
/// </summary>
public class StageReaderCaseTests : ServiceTestBase
{
    private sealed class Reader(string name, LegalStage stage) : IStageReader
    {
        public string Name => name;
        public StageAnswer? Read(string text, IReadOnlyList<string> names) => new(stage);
    }

    // GC-02 with the news saying the Profile person was indicted (as AUG-02-05): the rules read INDICTED.
    private async Task<AnalysisRecord> Analyse(string key, params IStageReader[] readers)
    {
        var ct = TestContext.Current.CancellationToken;
        var files = GoldenCaseFiles.Read("GC-02");
        const string News = "新聞資訊_90000021.txt";
        files[News] = files[News].Replace("檢方已分案偵辦。", "檢方已起訴林美玲。", StringComparison.Ordinal);
        var created = await new CaseService(_cases, TimeProvider.System, stageReaders: readers).CreateAsync("s-" + key, "e-" + key, TxtCaseReader.ReadFiles(files), ct, files);
        return (await _cases.GetAnalysisAsync(created.CaseId, 1, ct))!;
    }

    [Fact]
    public async Task Two_models_against_the_rules_leave_the_stage_unknown_and_the_stored_case_shows_every_reader()
    {
        var rulesOnly = await Analyse("rules");
        var voted = await Analyse("vote", new Reader("classifier", LegalStage.Convicted), new Reader("llm", LegalStage.Convicted));

        var before = Assert.Single(Assert.Single(rulesOnly.Result.Hits).Conditions);
        var after = Assert.Single(Assert.Single(voted.Result.Hits).Conditions);
        Assert.Equal((LegalStage.Indicted, StageBasis.Keyword, null), (before.LegalStage, before.LegalStageBasis, before.StageReaders));
        Assert.Equal((LegalStage.Unknown, StageBasis.VoteMajority), (after.LegalStage, after.LegalStageBasis));
        Assert.Equal([StageVote.ReadersDisagree], after.ReasonCodes);
        Assert.Equal([new("rules", LegalStage.Indicted, StageBasis.Keyword), new("classifier", LegalStage.Convicted), new StageReaderResult("llm", LegalStage.Convicted)],
            after.StageReaders!);
        // The stage and its disagreement are the only things the vote changes: the pair, its review and the identity stand.
        Assert.Empty(before.ReasonCodes);
        Assert.Equal(JsonSerializer.Serialize(before with { LegalStage = after.LegalStage, LegalStageBasis = after.LegalStageBasis, StageReaders = after.StageReaders, ReasonCodes = after.ReasonCodes }),
            JsonSerializer.Serialize(after));
        Assert.Equal(JsonSerializer.Serialize(Assert.Single(rulesOnly.Result.Hits) with { Conditions = [] }), JsonSerializer.Serialize(Assert.Single(voted.Result.Hits) with { Conditions = [] }));
        Assert.Equal(rulesOnly.CaseResult, voted.CaseResult);
        Assert.Contains("各讀取者：rules INDICTED、classifier CONVICTED、llm CONVICTED", Assert.Single(voted.Explanation!.Hits).Summary.ExternalData, StringComparison.Ordinal);
        // The stored record still meets its published schema.
        var json = JsonSerializer.Serialize(voted.Result, CaseJson.Options);
        Assert.Empty(GoldenCases.SchemaCheck.Errors(CaseJson.Schema(typeof(CaseAnalysis)), JsonNode.Parse(json), "$"));
    }

    private sealed class Recording : IStageReader
    {
        public List<(string Text, IReadOnlyList<string> Names)> Calls { get; } = [];
        public string Name => "llm";

        public StageAnswer? Read(string text, IReadOnlyList<string> names)
        {
            Calls.Add((text, [.. names]));
            return null;
        }
    }

    /// <summary>A model is given the attached news and the Profile's names, and no customer field (security-and-privacy.md: customer data stays in the bank).</summary>
    [Fact]
    public async Task A_stage_reader_receives_only_the_news_and_the_profile_names()
    {
        var ct = TestContext.Current.CancellationToken;
        var files = GoldenCaseFiles.Read("GC-11");
        var input = TxtCaseReader.ReadFiles(files);
        var reader = new Recording();
        await new CaseService(_cases, TimeProvider.System, stageReaders: [reader]).CreateAsync("s-privacy", "e-privacy", input, ct, files);

        var (profileId, news) = Assert.Single(input.NewsByProfileId);
        var (text, names) = Assert.Single(reader.Calls);
        Assert.Equal(news.Text, text);
        Assert.Equal(input.Profiles[profileId].Names, names);

        // Every customer value that is not also one of the Profile's names: identifiers, birth date as written in Taiwan, addresses, work, relatives.
        var s = input.Subject;
        var customer = new List<string?> { s.Document?.Number, s.ReferenceNumber, s.TaxId?.Value, s.Occupation };
        customer.AddRange(s.Names.Except(names));
        customer.AddRange(s.Addresses.SelectMany(a => new[] { a.Raw, a.Normalized }));
        customer.AddRange((s.Employments ?? []).Select(x => x.Organization));
        customer.AddRange((s.Relatives ?? []).Select(x => x.Name));
        if (s.DateOfBirth is { Year: var y, Month: { } m, Day: { } d })
            customer.AddRange([$"{y}-{m:00}-{d:00}", $"{y}/{m}/{d}", $"{y}/{m:00}/{d:00}", $"{y - 1911}年{m}月{d}日", $"{y - 1911}/{m:00}/{d:00}", $"{y}{m:00}{d:00}"]);
        var values = customer.OfType<string>().Where(v => v.Trim().Length >= 3).ToList();
        Assert.True(values.Count >= 8, $"only {values.Count} customer values to look for");
        Assert.All(values, v => Assert.False(text.Contains(v, StringComparison.Ordinal) || names.Any(n => n.Contains(v, StringComparison.Ordinal)), "a customer value reached the stage reader"));
    }

    /// <summary>A reader that answers what another one answered: the third vote agreeing with the classifier.</summary>
    private sealed class Echo(IStageReader of) : IStageReader
    {
        public string Name => "llm";
        public StageAnswer? Read(string text, IReadOnlyList<string> names) => of.Read(text, names);
    }

    /// <summary>
    /// The guard against the rules' over-reading (watchlist-and-adverse-information.md#stage-from-attached-news): the rules
    /// read INDICTED for a Profile person who is only the victim or a relative of the indicted person; the trained classifier
    /// (AML_STAGE_MODEL; skipped without it) does not, so with a third reader agreeing with it the vote leaves the stage UNKNOWN.
    /// </summary>
    [Theory]
    [InlineData("victim")]
    [InlineData("relative")]
    public async Task The_classifier_stops_the_rules_reading_another_persons_indictment(string key)
    {
        var directory = Environment.GetEnvironmentVariable("AML_STAGE_MODEL");
        Assert.SkipWhen(string.IsNullOrEmpty(directory), "AML_STAGE_MODEL is not set: the trained model is outside the repository");
        var ct = TestContext.Current.CancellationToken;
        using var classifier = new OnnxStageClassifier(directory!);
        var (files, input) = NewsModelCaseTests.CaseWith(key);
        var created = await new CaseService(_cases, TimeProvider.System, stageReaders: [classifier, new Echo(classifier)]).CreateAsync("s-guard-" + key, "e-guard-" + key, input, ct, files);
        var c = Assert.Single(Assert.Single((await _cases.GetAnalysisAsync(created.CaseId, 1, ct))!.Result.Hits).Conditions);

        TestContext.Current.SendDiagnosticMessage($"{key}: {string.Join(", ", c.StageReaders!.Select(r => $"{r.Reader} {r.Stage}"))}");
        Assert.Equal(LegalStage.Indicted, c.StageReaders![0].Stage);
        Assert.NotEqual(LegalStage.Indicted, c.StageReaders[1].Stage);
        Assert.Equal((LegalStage.Unknown, StageBasis.VoteMajority), (c.LegalStage, c.LegalStageBasis));
        Assert.Equal([StageVote.ReadersDisagree], c.ReasonCodes);
    }

    /// <summary>
    /// The trained classifier in .NET gives what it gave in training (tokens and ONNX graph): on each dev row's name and
    /// evidence, the label of the final epoch's predictions. Set AML_STAGE_MODEL to the ONNX folder, AML_STAGE_PARITY_ROWS
    /// to the dev rows (stage-clf data/dev.jsonl) and AML_STAGE_PARITY_PRED to their predictions (out/pred-dev-e4.jsonl);
    /// skipped without them.
    /// </summary>
    [Fact]
    public void The_classifier_gives_its_training_predictions_on_the_dev_rows()
    {
        var (model, rows, predictions) = (Environment.GetEnvironmentVariable("AML_STAGE_MODEL"), Environment.GetEnvironmentVariable("AML_STAGE_PARITY_ROWS"),
            Environment.GetEnvironmentVariable("AML_STAGE_PARITY_PRED"));
        Assert.SkipWhen(string.IsNullOrEmpty(model) || string.IsNullOrEmpty(rows) || string.IsNullOrEmpty(predictions),
            "AML_STAGE_MODEL, AML_STAGE_PARITY_ROWS or AML_STAGE_PARITY_PRED is not set: the trained model and its data are outside the repository");
        using var classifier = new OnnxStageClassifier(model!);
        var pairs = File.ReadLines(rows!).Zip(File.ReadLines(predictions!), (r, p) => (Row: JsonNode.Parse(r)!, Pred: JsonNode.Parse(p)!)).ToList();
        Assert.All(pairs, x => Assert.Equal(((string?)x.Row["doc"], (string?)x.Row["person"]?.ToString()), ((string?)x.Pred["doc"], (string?)x.Pred["person"]?.ToString())));

        var same = pairs.Count(x => classifier.Classify((string)x.Row["name"]!, (string)x.Row["evidence"]!) == Enum.Parse<LegalStage>((string)x.Pred["pred"]!, ignoreCase: true));

        TestContext.Current.SendDiagnosticMessage($"same label {same} of {pairs.Count}");
        Assert.True(pairs.Count > 0 && same >= 0.99 * pairs.Count, $"same label {same} of {pairs.Count}");
        // From a whole text too: the evidence is built and read (what a one-line made-up text gives is not the point here).
        Assert.NotNull(classifier.Read("臺北地檢署今日依詐欺罪嫌起訴陳志明。", ["陳志明"]));
    }
}
