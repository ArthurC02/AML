using Aml.Infra.Txt;
using Aml.Services;
using Aml.Tests.Synthetic;

namespace Aml.Tests.Integration;

/// <summary>
/// The synthetic set's in-process measurement is the product's path: one Case of each kind, and every HTML Case of the
/// first kinds, imported through CaseService on SQLite and read back as the Case detail give the same hits as
/// SyntheticMeasure computed (docs/plan/poc-delivery-plan.md#synthetic-end-to-end-set).
/// </summary>
public sealed class SyntheticCaseServiceTests : ServiceTestBase
{
    [Fact]
    public async Task Imported_synthetic_cases_store_what_the_measurement_computed()
    {
        var ct = TestContext.Current.CancellationToken;
        var asOf = SyntheticAssumptions.Default.AsOf;
        var service = new CaseService(_cases, new ManualTimeProvider(new DateTimeOffset(asOf.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)), _lists, TxtCaseReader.ProfileExportText);
        var all = SyntheticCaseGenerator.Generate();
        var chosen = all.GroupBy(c => c.Candidates[0].Kind).SelectMany(g => g.Where(c => c.Html).Take(1).Append(g.First(c => !c.Html))).ToList();
        var measured = SyntheticMeasure.Run(chosen, asOf);
        Assert.Equal(14, chosen.Count);

        foreach (var c in chosen)
        {
            var created = await service.CreateAsync("syn-" + c.Id, "evt-" + c.Id, TxtCaseReader.ReadFiles(c.Files), ct, c.Files);
            var detail = (await service.GetDetailAsync(created.CaseId, ct))!;
            var stored = detail.Effective!.Result.Hits.Select(h => (h.Profile.ProfileKey, h.Identity.Decision, h.PairResult, string.Join(",", h.Conditions.Select(x => $"{x.Type}:{x.Status}:{x.LegalStage}"))));
            var expected = measured.Where(o => o.Case == c).Select(o => (o.Truth.ProfileKey, o.Hit!.Identity.Decision, o.Hit.PairResult, string.Join(",", o.Hit.Conditions.Select(x => $"{x.Type}:{x.Status}:{x.LegalStage}"))));
            Assert.Equal(expected, stored);
        }
    }
}
