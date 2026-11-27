using System.Net;
using System.Text;
using Aml.Core.Explanation;
using Aml.Core.Pages;
using Aml.Core.Ports;
using Aml.Core.Screening;
using Aml.Infra.Models;
using Aml.Infra.Web;

namespace Aml.Tests.Synthetic;

/// <summary>
/// Scenario 2 on the synthetic set (docs/plan/poc-delivery-plan.md#requirement-scorecard): the 20 news pages
/// <see cref="SyntheticCaseGenerator.External"/> writes for a received URL, served from memory through the page path
/// (URL guard, polite fetch, main text, <see cref="PageReadings"/>; the rules alone, and with the trained news models when
/// AML_NEWS_MODELS names them), each person scored against the page's truth.
/// Measurement only: a miss is counted by kind, never fixed in the fixture.
/// </summary>
public class SyntheticPageTests
{
    private sealed record Measure(int PagesRead, int Pages, int Found, int Persons, int StageRight, int Stated, int Extra, int Suspects,
        int ModelRead, Dictionary<string, int> Misses);

    private static async Task<Measure> Run(INewsModel? model)
    {
        var fixtures = SyntheticCaseGenerator.External(SyntheticCaseGenerator.Generate()).Where(f => f.Scenario == "RECEIVED_URL").ToList();
        var byUrl = fixtures.ToDictionary(f => f.Url, f => f.Content, StringComparer.Ordinal);
        var stub = new WebStub(u => u.AbsolutePath == "/robots.txt" ? (HttpStatusCode.NotFound, "text/plain", [])
            : byUrl.TryGetValue(u.AbsoluteUri, out var page) ? (HttpStatusCode.OK, "text/html", Encoding.UTF8.GetBytes(page)) : (HttpStatusCode.NotFound, "text/html", []));
        using var fetcher = new PoliteFetcher("test-agent", TimeSpan.Zero, WebStub.Dns(), stub);

        var misses = new Dictionary<string, int>();
        void Miss(string kind) => misses[kind] = misses.GetValueOrDefault(kind) + 1;
        int pagesRead = 0, persons = 0, found = 0, stageRight = 0, stated = 0, extra = 0, suspects = 0, modelRead = 0;
        foreach (var f in fixtures)
        {
            var (canonical, _) = PageUrl.Read(f.Url);
            var fetched = await WebPages.ReadAsync(fetcher, new Uri(canonical!), TestContext.Current.CancellationToken);
            if (fetched.Text is null) { Miss("page not read: " + fetched.Problem); continue; }
            pagesRead++;
            var reading = PageReadings.Read(PageUrl.PageId(canonical!), fetched.Text, model, []);
            // The truth states no age, birth date or gender: any stated value is read from nowhere.
            stated += reading.Persons.Count(p => p.StatedAge is not null || p.StatedBirthDates.Count > 0 || p.StatedGender is not null);
            extra += reading.Persons.Count(p => !f.Persons.Any(t => p.Forms.Contains(t.WrittenName)));
            foreach (var (name, isCustomer, stage) in f.Persons)
            {
                persons++;
                var who = isCustomer ? "customer" : "co-named person";
                if (reading.Persons.FirstOrDefault(p => p.Forms.Contains(name)) is not { } read) { Miss($"{who} not found"); continue; }
                found++;
                if (read.Party == NewsParty.Suspect) suspects++;
                else Miss($"{who}: party {read.Party?.ToString() ?? "none"} for Suspect");
                if (read.ReadBy == PersonReadBy.ModelRead) modelRead++;
                if (read.Stage == stage) stageRight++;
                else Miss($"{who}: stage {read.Stage} for {stage}");
            }
        }
        var m = new Measure(pagesRead, fixtures.Count, found, persons, stageRight, stated, extra, suspects, modelRead, misses);
        TestContext.Current.TestOutputHelper?.WriteLine($"pages read {pagesRead}/{fixtures.Count}; persons found {found}/{persons} ({modelRead} model-read); "
            + $"suspect party {suspects}/{found}; stage right {stageRight}/{found}; stated values read {stated}; extra persons {extra}; "
            + $"misses: {string.Join("; ", misses.Select(x => $"{x.Key} {x.Value}"))}");
        return m;
    }

    [Fact]
    public async Task Received_pages_read_by_the_rules_as_the_plan_states()
    {
        var m = await Run(null);

        // Every page is fetched and its main text kept. The customer stands right after the dateline
        // (〔本報記者／臺北報導〕王…涉嫌), where a clause opens: the rules read all 20 and their stage. The other follows 同案的
        // (同案的林…亦遭約談), which no rule takes as a name's place: left to the models. The one co-named person counted found
        // has the customer's very name on that page, so the one person read holds both. No role word stands before either
        // name, so no party is read; nothing is stated or invented.
        Assert.Equal((20, 20, 21, 40, 21, 0, 0, 0, 0), (m.PagesRead, m.Pages, m.Found, m.Persons, m.StageRight, m.Stated, m.Extra, m.Suspects, m.ModelRead));
        Assert.Equal(new Dictionary<string, int>
        {
            ["customer: party none for Suspect"] = 20,
            ["co-named person: party none for Suspect"] = 1,
            ["co-named person not found"] = 19,
        }, m.Misses);
    }

    /// <summary>The same pages with the trained news models (AML_NEWS_MODELS; skipped without it): their persons fill in what the rules miss.</summary>
    [Fact]
    public async Task Received_pages_read_with_the_trained_models_as_the_plan_states()
    {
        var directory = Environment.GetEnvironmentVariable("AML_NEWS_MODELS");
        Assert.SkipWhen(string.IsNullOrEmpty(directory), "AML_NEWS_MODELS is not set: the trained models are outside the repository");
        using var model = new OnnxNewsModel(directory!);

        var m = await Run(model);

        // NER20 / event43: the 19 persons after 同案的 are found by the models alone (MODEL_READ), with the stage the rules
        // read for their forms. The party is the model's: 8 suspects, 8 victims, 1 official, 2 none; the customers, read by
        // the rules, have no role word and so no party.
        Assert.Equal((20, 20, 40, 40, 40, 0, 0, 8, 19), (m.PagesRead, m.Pages, m.Found, m.Persons, m.StageRight, m.Stated, m.Extra, m.Suspects, m.ModelRead));
        Assert.Equal(new Dictionary<string, int>
        {
            ["customer: party none for Suspect"] = 20,
            ["co-named person: party none for Suspect"] = 3,
            ["co-named person: party Victim for Suspect"] = 8,
            ["co-named person: party Official for Suspect"] = 1,
        }, m.Misses);
    }
}
