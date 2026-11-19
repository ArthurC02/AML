using System.Text.Json;
using Aml.Core.Cases;
using Aml.Core.Explanation;
using Aml.Core.Ports;
using Aml.Core.Screening;
using Aml.Core.Shared;
using Aml.Infra.Models;
using Aml.Infra.Txt;
using Aml.Services;

namespace Aml.Tests.Integration;

/// <summary>
/// The news models and the rest of the system on one Case, end to end through the service and the store
/// (watchlist-and-adverse-information.md#news-models, ADR-0012). What the system has to give is the decision on one known
/// Profile person and its explanation; the models only restate the news beside the rules. So a Case analysed with the
/// models must differ from the same Case without them in the models' own readout and nothing else.
/// </summary>
public class NewsModelCaseTests : ServiceTestBase
{
    private const string News = "新聞資訊_90000021.txt";

    // GC-02 with the news saying the Profile person was indicted (as AUG-02-05).
    private static Dictionary<string, string> IndictedCase()
    {
        var files = Directory.GetFiles(GoldenCases.GoldenCaseTests.Folder("GC-02"), "*.txt").ToDictionary(p => Path.GetFileName(p), File.ReadAllText);
        Assert.Contains("檢方已分案偵辦。", files[News], StringComparison.Ordinal);
        files[News] = files[News].Replace("檢方已分案偵辦。", "檢方已起訴林美玲。", StringComparison.Ordinal);
        return files;
    }

    private async Task<AnalysisRecord> Analyse(Dictionary<string, string> files, INewsModel? model, string key)
    {
        var ct = TestContext.Current.CancellationToken;
        var created = await new CaseService(_cases, TimeProvider.System, newsModel: model).CreateAsync("s-" + key, "e-" + key, TxtCaseReader.ReadFiles(files), ct, files);
        return (await _cases.GetAnalysisAsync(created.CaseId, 1, ct))!;
    }

    /// <summary>Everything a consumer can act on, with the models' own readout taken out.</summary>
    private static string WithoutTheReadout(AnalysisRecord r) => JsonSerializer.Serialize(new
    {
        r.ExecutionStatus,
        r.CaseResult,
        r.Result,
        r.Quality,
        Explanation = r.Explanation! with { Hits = [.. r.Explanation.Hits.Select(h => h with { NewsDigest = h.NewsDigest is { } d ? d with { Model = null } : null })] },
        Lineage = r.Lineage! with { NewsModelVersion = null },
    });

    private static void AssertSameCase(AnalysisRecord rulesOnly, AnalysisRecord withModels)
    {
        Assert.Null(Assert.Single(rulesOnly.Explanation!.Hits).NewsDigest!.Model);
        Assert.Null(rulesOnly.Lineage!.NewsModelVersion);
        Assert.Equal(WithoutTheReadout(rulesOnly), WithoutTheReadout(withModels));
    }

    [Fact]
    public async Task The_models_add_their_readout_and_change_nothing_else()
    {
        var files = IndictedCase();
        // A model that tags the Profile person a suspect and reads the indictment sentence as a conviction: louder than the text.
        var model = new Unit.Screening.FakeNewsModel(new Dictionary<string, string> { ["林美玲"] = "PER-Suspect" }, ("林美玲", "起訴", "Conviction", 0.99f));

        var rulesOnly = await Analyse(files, null, "rules");
        var withModels = await Analyse(files, model, "models");

        AssertSameCase(rulesOnly, withModels);
        var hit = Assert.Single(withModels.Result.Hits);
        Assert.Equal(LegalStage.Indicted, Assert.Single(hit.Conditions).LegalStage); // the rules' reading, not the model's conviction
        var entity = Assert.Single(Assert.Single(withModels.Explanation!.Hits).NewsDigest!.Model!.Entities);
        Assert.Equal(("林美玲", true, NewsEventType.Conviction), (entity.Name, entity.ProfileName, Assert.Single(entity.Events).Type));
        Assert.Equal("ner:test ev:test", withModels.Lineage!.NewsModelVersion);
    }

    /// <summary>
    /// The trained models themselves, which live outside the repository: set AML_NEWS_MODELS to a directory holding
    /// <c>ner/</c> and <c>ev/</c> to run it. It checks what the readout owes the reviewer on this Case: the Profile person
    /// is found and comes first, the step the news states for them is listed, and the models agree with the rules.
    /// </summary>
    [Fact]
    public async Task The_trained_models_read_the_profile_persons_step_and_leave_the_case_as_it_is()
    {
        var directory = Environment.GetEnvironmentVariable("AML_NEWS_MODELS");
        Assert.SkipWhen(string.IsNullOrEmpty(directory), "AML_NEWS_MODELS is not set: the trained models are outside the repository");
        var files = IndictedCase();
        using var model = new OnnxNewsModel(directory!);

        var rulesOnly = await Analyse(files, null, "rules");
        var withModels = await Analyse(files, model, "models");

        AssertSameCase(rulesOnly, withModels);
        Assert.Equal(LegalStage.Indicted, Assert.Single(Assert.Single(withModels.Result.Hits).Conditions).LegalStage);
        var readout = Assert.Single(withModels.Explanation!.Hits).NewsDigest!.Model!;
        var first = readout.Entities[0];
        Assert.Equal(("林美玲", true, false, NewsParty.Suspect), (first.Name, first.ProfileName, first.Organisation, first.Party));
        Assert.Contains(first.Events, e => e is { Type: NewsEventType.Indictment, Certainty: NewsCertainty.Stated });
        Assert.DoesNotContain(first.Events, e => e.Type is NewsEventType.Conviction or NewsEventType.Acquittal); // nothing the news does not say
        Assert.Matches(@"^ner:[0-9a-f]{12}\S* ev:[0-9a-f]{12}", withModels.Lineage!.NewsModelVersion); // both files by their hash, then the input options
        Assert.Equal(readout.Version, withModels.Lineage.NewsModelVersion);
    }
}
