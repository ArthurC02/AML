using Aml.Core.Cases;
using Aml.Core.Subjects;
using Aml.Core.Explanation;

namespace Aml.Tests.GoldenCases;

/// <summary>The codes an analysis and its explanation actually emit, each reduced to its catalog key.</summary>
internal static class EmittedCodes
{
    public static IEnumerable<string> Of(CaseInput input, CaseAnalysis a)
    {
        var e = ExplanationBuilder.Build(input, a);
        var values = a.Hits.SelectMany(h =>
                h.Identity.ReasonCodes.Concat(h.PairReasonCodes).Concat(h.Disposition.ReasonCodes).Concat(h.Disposition.PendingChecks)
                .Concat(h.Identity.Evidence.Values.Select(v => v.Detail).OfType<string>())
                .Concat(h.Conditions.SelectMany(c => c.ReasonCodes.Concat(c.RiskCategories).Concat(new[] { c.PepType }.OfType<string>())))
                .Concat(h.Profile.Findings).Concat(new[] { h.Identity.HardConflict }.OfType<string>()))
            .Concat(e.Hits.SelectMany(h => h.DataQualityFindings
                .Concat(h.Comparison.Select(r => r.Difference).OfType<string>().SelectMany(d => d.Split('；')))
                .Concat(h.Sources.Select(s => s.UsedFor)).Concat(h.WouldChangeIf.Select(w => w.ReasonCode))))
            .Concat(input.Subject.Findings);
        return values.Select(Aml.Core.Codes.CodeCatalog.Key).Distinct();
    }
}
