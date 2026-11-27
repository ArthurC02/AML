using System.Net;
using System.Text;
using Aml.Core.Cases;
using Aml.Core.Identity;
using Aml.Core.Pages;
using Aml.Core.Ports;
using Aml.Core.Scoring;
using Aml.Core.Screening;
using Aml.Core.Shared;
using Aml.Infra.Models;
using Aml.Infra.Txt;
using Aml.Infra.Web;

namespace Aml.Tests.Synthetic;

/// <summary>
/// Scenario 2 end to end on the synthetic set (docs/plan/poc-delivery-plan.md#received-url-matched-against-the-customer):
/// every page of <see cref="SyntheticCaseGenerator.ReceivedPages"/> fetched from memory through the page path, read, pinned
/// into its customer's Case files and analysed by <see cref="CaseAnalyzer"/> as a Case naming the page; each person the page
/// names scored against the truth, and every undecided or wrong result put at the first stage of the chain that lost it.
/// With the rules alone (CI), and with the trained news models when AML_NEWS_MODELS names them.
/// </summary>
public class SyntheticPageCaseTests
{
    private static readonly DateOnly AsOf = SyntheticAssumptions.Default.AsOf;

    private sealed record Measure(Dictionary<string, int> Counts, Dictionary<string, int> Losses);

    private static async Task<Measure> Run(INewsModel? model)
    {
        var cases = SyntheticCaseGenerator.Generate().ToDictionary(c => c.Id);
        var pages = SyntheticCaseGenerator.ReceivedPages([.. cases.Values]);
        var byUrl = pages.ToDictionary(f => f.Url, f => f.Content, StringComparer.Ordinal);
        var stub = new WebStub(u => u.AbsolutePath == "/robots.txt" ? (HttpStatusCode.NotFound, "text/plain", [])
            : byUrl.TryGetValue(u.AbsoluteUri, out var page) ? (HttpStatusCode.OK, "text/html", Encoding.UTF8.GetBytes(page)) : (HttpStatusCode.NotFound, "text/html", []));
        using var fetcher = new PoliteFetcher("test-agent", TimeSpan.Zero, WebStub.Dns(), stub);

        var counts = new Dictionary<string, int>();
        var losses = new Dictionary<string, int>();
        void Lose(string where) => losses[where] = losses.GetValueOrDefault(where) + 1;
        foreach (var f in pages)
        {
            Count("pages");
            var (canonical, _) = PageUrl.Read(f.Url);
            var fetched = await WebPages.ReadAsync(fetcher, new Uri(canonical!), TestContext.Current.CancellationToken);
            if (fetched.Text is null)
            {
                foreach (var _ in f.Persons) Lose("page not read");
                continue;
            }
            Count("pages read");
            var pageId = PageUrl.PageId(canonical!);
            var reading = PageReadings.Read(pageId, fetched.Text, model, []);
            var fetchedAt = new DateTimeOffset(AsOf.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            var (publishedOn, publishedFrom) = PageReadings.Published(fetched, fetchedAt);
            Count($"publication date {CaseJson.Code(publishedFrom)}" + (publishedOn == f.PublishedOn ? "" : $", truth {f.PublishedOn?.ToString() ?? "none"}"));
            var record = new PageRecord(pageId, canonical!, PageStatus.Read, default, null, null, null, fetched.FinalUrl, fetched.MediaType, fetched.Title,
                fetchedAt, fetched.ContentHash, fetched.Text, reading.Persons, reading.OtherEvents, reading.Model, publishedOn, publishedFrom);
            var input = TxtCaseReader.ReadFiles(new Dictionary<string, string>(cases[f.CustomerCase].Files) { [PageSnapshots.FileName(pageId)] = PageSnapshots.Write(record) });
            var analysis = CaseAnalyzer.Analyze(input, asOf: AsOf);
            var screened = analysis.Pages!.Single().Persons;
            Count("page persons screened", screened.Count);
            Count("page persons screened, not a candidate", screened.Count(s => !s.Candidate));
            Count("page persons screened, MODEL_READ", screened.Count(s => s.ReadBy == PersonReadBy.ModelRead));

            foreach (var t in f.Persons)
            {
                var who = t.IsCustomer ? t.Suspect ? "customer" : "customer not a suspect" : "another person";
                Count($"{who}: persons");
                var read = record.Persons!.FirstOrDefault(p => p.Forms.Contains(t.WrittenName));
                if (read is null)
                {
                    if (t.IsCustomer) Lose($"{who}: not found on the page");
                    else Count("another person: excluded (not read)");
                    continue;
                }
                var hit = analysis.Hits.FirstOrDefault(h => h.Profile.ProfileKey == read.ProfileKey);
                if (hit is null)
                {
                    if (t.IsCustomer) Lose($"{who}: wrongly excluded by name");
                    else Count("another person: excluded by name");
                    continue;
                }
                Count($"{who}: candidate");
                var c = hit.Conditions.Single();
                var conditionRight = t.Suspect ? c.Status == ConditionStatus.Hit : c.Status == ConditionStatus.NotHit;
                if (conditionRight) Count("condition right");
                if (t.Suspect && c.Status == ConditionStatus.Hit) Count(c.LegalStage == t.Stage ? "stage right" : $"stage {CaseJson.Code(c.LegalStage)} for {CaseJson.Code(t.Stage)}");
                // Measurement only: the stated birth date taken as the record's own field, under the two-year rule of a Profile DOB.
                var whatIf = IdentityDecisionPolicy.Decide(input.Subject, hit.Profile with { DatesOfBirth = read.StatedBirthDates }, PageCandidates.News(record, read)).Decision;
                if (whatIf != hit.Identity.Decision) Count($"what-if stated DOB as a field: {who} {CaseJson.Code(hit.Identity.Decision)} → {CaseJson.Code(whatIf)}");
                if (hit.Identity.Evidence.TryGetValue(EvidenceGroup.StatedAge, out var age)) Count($"stated age: {who} {age.Detail}");
                // Measurement only: the age taken as a birth-year field (either year it allows at the publication date), under the
                // two-year rule of a Profile DOB.
                if (read.StatedAge is { } stated && record.PublishedOn is { Precision: DatePrecision.Day } at)
                {
                    var asField = hit.Profile with { DatesOfBirth = [new PartialDate(at.Year - stated - 1), new PartialDate(at.Year - stated)] };
                    var ageIf = IdentityDecisionPolicy.Decide(input.Subject, asField, PageCandidates.News(record, read));
                    Count($"what-if stated age as a birth-year field: {who} {CaseJson.Code(hit.Identity.Decision)} → {CaseJson.Code(ageIf.Decision)}, DOB {CaseJson.Code(ageIf.Evidence[EvidenceGroup.DateOfBirth].Direction)}");
                }
                // Measurement only: the scoring rule with the page's kind (a received news page, the news factor) for its values.
                var band = ScoringRule.Identity(hit.Identity, entityRecord: false, record: SourceKind.AllowlistedNews, text: SourceKind.AllowlistedNews).Band;
                Count($"score band: {who} {CaseJson.Code(band)}");

                var result = hit.PairResult;
                if (result == ScreeningAssessment.Review)
                {
                    Lose(!conditionRight ? $"{who}: condition {CaseJson.Code(c.Status)}"
                        : $"{who}: identity {CaseJson.Code(hit.Identity.Decision)}, row {hit.Identity.Stage3Row}: {hit.Identity.ReasonCodes[0]}");
                    continue;
                }
                Count("pairs decided");
                if (result == t.Expected) Count($"decided right: {CaseJson.Code(result)}");
                else Lose($"wrong automatic {CaseJson.Code(result)}: {who}, truth {CaseJson.Code(t.Expected)}");
            }
        }
        TestContext.Current.TestOutputHelper?.WriteLine(string.Join("\n", counts.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => $"{x.Key}: {x.Value}"))
            + "\n-- losses --\n" + string.Join("\n", losses.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => $"{x.Key}: {x.Value}")));
        return new(counts, losses);

        void Count(string what, int n = 1) => counts[what] = counts.GetValueOrDefault(what) + n;
    }

    [Fact]
    public async Task Received_pages_matched_against_the_customer_by_the_rules_as_the_plan_states()
    {
        var m = await Run(null);

        // 64 pages read; 72 persons read, each recorded in its revision, 8 of them no candidate (their names do not correspond).
        // Right person found: 32/32 suspect customers and 8/8 customers named as victim or witness are candidates. Wrong person
        // excluded: the 27 other persons with another name never become a pair (19 after 同案的, which no rule reads; 8 by name);
        // the 25 with the customer's very name are candidates. Conditions right 65/65 (the 8 non-suspects NOT_HIT by the role word
        // the rules read), stage right 57/57. Publication date: of the 24 pages stating an age, 11 in the metadata and 8 on a
        // date line, each read as written; the other 45 pages state none (MISSING), and the fetch time never stands in.
        Assert.Equal(Expected(screened: 72, modelRead: 0, notRead: 19), m.Counts);
        // Decided: 8 of 65 pairs (the non-suspects), none wrong. Every other pair waits on identity: a page carries no field
        // beside the name, so nothing beyond it is comparable (row 6), and a birth date or an age written next to the name is a
        // stated value (ADR-0012): the customer's own agrees (row 5, or compatible and no support), a stranger's differs
        // (row 1), and neither concludes. An age on a page without a publication date is recorded, not compared.
        Assert.Equal(ExpectedLosses, m.Losses);
    }

    // The counts both runs share; the persons read differ only by those the models alone find (after 同案的).
    private static Dictionary<string, int> Expected(int screened, int modelRead, int notRead)
    {
        var expected = new Dictionary<string, int>
        {
            ["pages"] = 64,
            ["pages read"] = 64,
            ["publication date METADATA"] = 11,
            ["publication date TEXT"] = 8,
            ["publication date MISSING"] = 45,
            ["page persons screened"] = screened,
            ["page persons screened, not a candidate"] = screened - 64,
            ["page persons screened, MODEL_READ"] = modelRead,
            ["customer: persons"] = 32,
            ["customer: candidate"] = 32,
            ["customer not a suspect: persons"] = 8,
            ["customer not a suspect: candidate"] = 8,
            ["another person: persons"] = 52,
            ["another person: excluded by name"] = 27 - notRead,
            ["another person: candidate"] = 25,
            ["condition right"] = 65,
            ["stage right"] = 57,
            ["pairs decided"] = 8,
            ["decided right: NOT_APPLICABLE"] = 8,
            // The stated age (24 pages): compared at the publication date where the page states one (19), else recorded only.
            ["stated age: customer AGE_COMPATIBLE_AT_REPORT"] = 5,
            ["stated age: customer AGE_NOT_COMPARABLE"] = 3,
            ["stated age: another person AGE_COMPATIBLE_AT_REPORT"] = 8, // every near stranger (1 to 2 years): within ±2
            ["stated age: another person AGE_DIFFERENCE_AT_REPORT"] = 6,
            ["stated age: another person AGE_NOT_COMPARABLE"] = 2,
            ["what-if stated DOB as a field: another person REVIEW → NO_MATCH"] = 4,
            // Measurement only: the age as a birth-year field under the two-year rule. 6 far strangers would conclude NO_MATCH
            // (right); the 8 near strangers and the 5 customers would agree (the strangers' agreement wrong); no customer NO_MATCH.
            ["what-if stated age as a birth-year field: another person REVIEW → NO_MATCH, DOB CONFLICT"] = 6,
            ["what-if stated age as a birth-year field: another person REVIEW → REVIEW, DOB SUPPORT"] = 8,
            ["what-if stated age as a birth-year field: customer REVIEW → REVIEW, DOB SUPPORT"] = 5,
            // The score band (measurement only, values at the news factor 0.6) decides none either: +9 at most, prerequisites not all EXACT.
            ["score band: customer REVIEW"] = 32,
            ["score band: customer not a suspect REVIEW"] = 8,
            ["score band: another person REVIEW"] = 25,
        };
        if (notRead > 0) expected["another person: excluded (not read)"] = notRead;
        return expected;
    }

    private static readonly Dictionary<string, int> ExpectedLosses = new()
    {
        ["customer: identity REVIEW, row 6: NO_NON_PREREQUISITE_SUPPORT"] = 28,
        ["customer: identity REVIEW, row 5: PARTIAL_SUPPORT_ONLY"] = 4,
        ["another person: identity REVIEW, row 1: NEWS_BIRTH_DATE_DIFFERENCE"] = 8,
        ["another person: identity REVIEW, row 1: AGE_DIFFERENCE_AT_REPORT"] = 6,
        ["another person: identity REVIEW, row 6: NO_NON_PREREQUISITE_SUPPORT"] = 11,
    };

    /// <summary>The same with the trained news models (AML_NEWS_MODELS; skipped without it): the persons they find are matched too, marked MODEL_READ.</summary>
    [Fact]
    public async Task Received_pages_matched_against_the_customer_with_the_trained_models_as_the_plan_states()
    {
        var directory = Environment.GetEnvironmentVariable("AML_NEWS_MODELS");
        Assert.SkipWhen(string.IsNullOrEmpty(directory), "AML_NEWS_MODELS is not set: the trained models are outside the repository");
        using var model = new OnnxNewsModel(directory!);
        var m = await Run(model);

        // NER20 / event43: the 19 persons after 同案的 are read too (MODEL_READ) and excluded by name; nothing else moves.
        // The 24 age pages add no person the models alone find.
        Assert.Equal(Expected(screened: 91, modelRead: 19, notRead: 0), m.Counts);
        Assert.Equal(ExpectedLosses, m.Losses);
    }
}
