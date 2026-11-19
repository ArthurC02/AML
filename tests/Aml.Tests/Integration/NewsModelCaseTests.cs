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

    private const string Judgment = """
        臺灣臺北地方法院刑事判決
        被　　告　林美玲
        被　　告　王大明
        上列被告因詐欺案件，經檢察官提起公訴，本院判決如下：
        主　文
        林美玲犯詐欺取財罪，處有期徒刑一年。
        王大明無罪。
        理　由
        被告林美玲向被害人佯稱投資獲利，致被害人陷於錯誤而匯款。被告王大明被訴部分，不能證明其犯罪。
        """;

    /// <summary>
    /// Made-up news of nine kinds on GC-02's Case, read by the trained models (AML_NEWS_MODELS; skipped without it). The
    /// expectations are what the reviewer of a Case on this Profile person needs, not what the models happen to give: the
    /// person's own stage, nobody else's step, and the rules and the models saying the same. In every row the Case itself
    /// must stay as it is without the models.
    /// </summary>
    [Theory]
    // A judgment with two defendants: the Profile person is convicted, the co-defendant acquitted.
    [InlineData("judgment", Judgment, LegalStage.Convicted, NewsParty.Suspect, "Conviction", "Acquittal", "王大明", "Acquittal", "Conviction")]
    // The Profile person is the victim of someone else's indicted fraud: no step is theirs.
    [InlineData("victim", "王大明涉嫌以假投資詐騙，檢方已起訴王大明。被害人林美玲表示遭騙新臺幣一百萬元。", LegalStage.Unknown, NewsParty.Victim, "", "Indictment,Conviction,Arrest", "王大明", "Indictment", "Conviction")]
    // One name stands for a group: the arrest and the referral are the Profile person's too.
    [InlineData("group", "警方昨日逮捕林美玲等3人，全案依詐欺罪嫌移送臺北地檢署偵辦。", LegalStage.Alleged, NewsParty.Suspect, "Arrest,Referral", "Indictment,Conviction", null, "", "")]
    // The prosecutor decided not to indict.
    [InlineData("dismissed", "林美玲涉嫌詐欺一案，檢察官偵查後認罪嫌不足，對林美玲為不起訴處分。", LegalStage.Dismissed, NewsParty.Suspect, "NonIndictment", "Conviction", null, "", "")]
    // A report's own wording of a judgment, and a reversal on appeal.
    [InlineData("sentence", "林美玲因詐欺遭檢方起訴，臺北地院判林美玲有期徒刑二年。", LegalStage.Convicted, NewsParty.Suspect, "Indictment,Conviction", "Acquittal", null, "", "")]
    [InlineData("acquitted", "林美玲被訴詐欺一案，臺北地院審理後判林美玲無罪。", LegalStage.Acquitted, NewsParty.Suspect, "Acquittal", "Conviction", null, "", "")]
    [InlineData("reversed", "林美玲一審遭判處有期徒刑二年，二審改判無罪。", LegalStage.Acquitted, NewsParty.Suspect, "Conviction,Acquittal", "", null, "", "")]
    // Wanted, with no judicial step told.
    [InlineData("wanted", "林美玲涉嫌詐欺，棄保潛逃，臺北地檢署已對林美玲發布通緝。", LegalStage.Wanted, NewsParty.Suspect, "WantedNotice", "Indictment,Conviction", null, "", "")]
    // The husband is indicted, not the Profile person.
    [InlineData("relative", "林美玲的丈夫王大明涉嫌詐欺，王大明遭檢方起訴。", LegalStage.Unknown, null, "", "Indictment,Conviction", "王大明", "Indictment", "Conviction")]
    public async Task The_trained_models_and_the_rules_read_the_same_case(string key, string text, LegalStage stage, NewsParty? party,
        string stated, string notStated, string? other, string otherStated, string otherNotStated)
    {
        var directory = Environment.GetEnvironmentVariable("AML_NEWS_MODELS");
        Assert.SkipWhen(string.IsNullOrEmpty(directory), "AML_NEWS_MODELS is not set: the trained models are outside the repository");
        var ct = TestContext.Current.CancellationToken;
        var files = IndictedCase();
        var read = TxtCaseReader.ReadFiles(files);
        var input = read with { NewsByProfileId = read.NewsByProfileId.ToDictionary(n => n.Key, n => n.Value with { Text = text.ReplaceLineEndings("\n") }) };
        using var model = new OnnxNewsModel(directory!);
        async Task<AnalysisRecord> Run(INewsModel? m, string k) =>
            (await _cases.GetAnalysisAsync((await new CaseService(_cases, TimeProvider.System, newsModel: m).CreateAsync($"s-{key}-{k}", $"e-{key}-{k}", input, ct, files)).CaseId, 1, ct))!;

        var rulesOnly = await Run(null, "rules");
        var withModels = await Run(model, "models");

        AssertSameCase(rulesOnly, withModels);
        Assert.Equal(stage, Assert.Single(Assert.Single(withModels.Result.Hits).Conditions).LegalStage);
        var entities = Assert.Single(withModels.Explanation!.Hits).NewsDigest!.Model!.Entities;
        static string[] Steps(NewsModelEntity e) => [.. e.Events.Where(x => x.Certainty == NewsCertainty.Stated).Select(x => x.Type.ToString())];
        static string[] List(string csv) => csv.Split(',', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(("林美玲", true), (entities[0].Name, entities[0].ProfileName));
        if (party is not null) Assert.Equal(party, entities[0].Party); Assert.Empty(List(stated).Except(Steps(entities[0])));
        Assert.Empty(List(notStated).Intersect(Steps(entities[0])));
        if (other is null) return;
        var second = Assert.Single(entities, e => e.Name == other);
        Assert.False(second.ProfileName);
        Assert.Empty(List(otherStated).Except(Steps(second)));
        Assert.Empty(List(otherNotStated).Intersect(Steps(second)));
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
