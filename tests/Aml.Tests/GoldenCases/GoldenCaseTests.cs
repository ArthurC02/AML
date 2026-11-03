using Aml.Core.Cases;
using Aml.Core.Identity;
using Aml.Core.Screening;
using Aml.Core.Shared;
using Aml.Core.Subjects;
using Aml.Infra.Txt;
using static Aml.Core.Shared.ComparisonStatus;

namespace Aml.Tests.GoldenCases;

/// <summary>
/// Golden Case regression. Expected values come from samples/golden/GC-xx/expected.yaml, which is authoritative:
/// when a test fails, the policy changes, not the expectation.
/// </summary>
public class GoldenCaseTests
{
    internal static string Folder(string gc)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "samples", "golden"))) dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? throw new DirectoryNotFoundException("samples/golden"), "samples", "golden", gc);
    }

    private static (CaseInput Input, CaseAnalysis Analysis) Run(string gc)
    {
        var input = TxtCaseReader.Read(Folder(gc));
        return (input, CaseAnalyzer.Analyze(input));
    }

    private static void AssertEvidence(IdentityResult r, EvidenceGroup g, ComparisonStatus expected) =>
        Assert.Equal(expected, r.Evidence[g].Status);

    [Fact]
    public void GC01_impossible_age_at_attributed_role_is_no_match_for_both_profiles()
    {
        var (input, a) = Run("GC-01");

        Assert.Equal(3, input.Rows.Count);
        Assert.Equal(["DJ:90000001", "DJ:90000002"], a.Hits.Select(h => h.Profile.ProfileKey));
        Assert.Equal([2, 1], a.Hits.Select(h => h.CandidateRows));
        foreach (var hit in a.Hits)
        {
            AssertEvidence(hit.Identity, EvidenceGroup.Name, Exact);
            AssertEvidence(hit.Identity, EvidenceGroup.Gender, Exact);
            AssertEvidence(hit.Identity, EvidenceGroup.Nationality, Exact);
            AssertEvidence(hit.Identity, EvidenceGroup.DateOfBirth, ProfileMissing);
            Assert.Equal(ReasonCodes.ImpossibleAgeAtReportedEvent, hit.Identity.HardConflict);
            Assert.Equal(IdentityDecision.NoMatch, hit.Identity.Decision);
            Assert.Equal(ScreeningAssessment.NoHit, hit.PairResult);
            // Source condition stays independent of identity.
            var c = Assert.Single(hit.Conditions);
            Assert.Equal(ListType.AdverseMedia, c.Type);
            Assert.Equal(["FINANCIAL_CRIME"], c.RiskCategories);
        }
        var dob = input.Subject.DateOfBirth!.Value;
        Assert.Equal(5, HardConflictGate.MaxAgeAt(dob, new PartialDate(1990, 7)));
        Assert.Equal(6, HardConflictGate.MaxAgeAt(dob, new PartialDate(1991, 3)));
    }

    [Fact]
    public void GC02_fraud_hit_with_insufficient_identity_is_review_row_6()
    {
        var (input, a) = Run("GC-02");

        var hit = Assert.Single(a.Hits);
        var c = Assert.Single(hit.Conditions);
        Assert.Equal(ListType.AdverseMedia, c.Type);
        Assert.Equal(["FRAUD", "TAX_EVASION"], c.RiskCategories);
        Assert.Equal(LegalStage.Alleged, c.LegalStage);
        Assert.Equal(AmlRelevance.Uncertain, c.AmlRelevance);

        var id = hit.Identity;
        AssertEvidence(id, EvidenceGroup.Name, Exact);
        AssertEvidence(id, EvidenceGroup.Gender, Exact);
        AssertEvidence(id, EvidenceGroup.Nationality, Exact);
        AssertEvidence(id, EvidenceGroup.DateOfBirth, ProfileMissing);
        AssertEvidence(id, EvidenceGroup.Document, ProfileMissing);
        AssertEvidence(id, EvidenceGroup.Employment, ProfileMissing);
        AssertEvidence(id, EvidenceGroup.Address, ProfileMissing);
        Assert.Null(id.HardConflict);
        Assert.Equal(6, id.Stage3Row);
        Assert.Equal(IdentityDecision.Review, id.Decision);
        Assert.Equal([ReasonCodes.NoNonPrerequisiteSupport, ReasonCodes.NoBilateralDirectIdentifier], id.ReasonCodes);
        Assert.Equal(ScreeningAssessment.Review, hit.PairResult);

        // Legacy decision is imported only, never evidence.
        Assert.Contains(input.LegacyDecisions, d => d.Value == "確認後不符合" && !d.IndependentlyVerified);
    }

    [Fact]
    public void GC03_multilingual_name_on_active_wanted_list_is_probable_match_row_4()
    {
        var (input, a) = Run("GC-03");

        Assert.Equal(2, input.Rows.Count);
        var hit = Assert.Single(a.Hits);
        var c = Assert.Single(hit.Conditions);
        Assert.Equal(ListType.OfficialWantedList, c.Type);
        Assert.Equal(LegalStage.Wanted, c.LegalStage);

        var id = hit.Identity;
        AssertEvidence(id, EvidenceGroup.Name, Exact);
        AssertEvidence(id, EvidenceGroup.Gender, Exact);
        AssertEvidence(id, EvidenceGroup.Nationality, Exact);
        AssertEvidence(id, EvidenceGroup.DateOfBirth, Partial);
        Assert.Equal(Strength.Low, id.Evidence[EvidenceGroup.DateOfBirth].Strength);
        Assert.Equal(ReasonCodes.BirthYearMatchFullDateNotComparable, id.Evidence[EvidenceGroup.DateOfBirth].Detail);
        AssertEvidence(id, EvidenceGroup.Document, ProfileMissing);
        AssertEvidence(id, EvidenceGroup.Address, Uncertain);
        Assert.Equal(4, id.Stage3Row);
        Assert.Equal(IdentityDecision.ProbableMatch, id.Decision);
        Assert.Equal([ReasonCodes.ConsistentAtAvailablePrecision, ReasonCodes.NoBilateralDirectIdentifier], id.ReasonCodes);
        Assert.Equal(ScreeningAssessment.Review, hit.PairResult);

    }

    [Fact]
    public void GC11_year_only_dob_with_variant_names_is_probable_match_row_4()
    {
        var (input, a) = Run("GC-11");

        var s = input.Subject;
        Assert.Equal(SubjectRole.Customer, s.Role);
        Assert.Equal(ValueState.Ambiguous, s.TaxId!.State);
        Assert.Equal(new PartialDate(1973, 6, 12), s.DateOfBirth);
        var address = s.Addresses[0];
        Assert.Equal(Findings.HistoricalAdminDivision, address.Detail);
        Assert.StartsWith("242新北市新莊區中平里12鄰中華路35巷8號3樓", address.Normalized, StringComparison.Ordinal);

        Assert.Equal(2, input.Rows.Count);
        var hit = Assert.Single(a.Hits);
        Assert.Contains(Findings.ProfileMultipleDob, hit.Profile.Findings);
        var c = Assert.Single(hit.Conditions);
        Assert.Equal(ListType.AdverseMedia, c.Type);
        Assert.Equal(["FINANCIAL_CRIME"], c.RiskCategories);
        Assert.Equal(LegalStage.Unknown, c.LegalStage);

        var id = hit.Identity;
        AssertEvidence(id, EvidenceGroup.Name, Exact);
        AssertEvidence(id, EvidenceGroup.Gender, Exact);
        AssertEvidence(id, EvidenceGroup.Nationality, Exact);
        AssertEvidence(id, EvidenceGroup.DateOfBirth, Partial);
        Assert.Equal(Strength.Low, id.Evidence[EvidenceGroup.DateOfBirth].Strength);
        AssertEvidence(id, EvidenceGroup.Document, ProfileMissing);
        AssertEvidence(id, EvidenceGroup.Employment, ProfileMissing);
        AssertEvidence(id, EvidenceGroup.Address, ProfileMissing);
        Assert.Equal(4, id.Stage3Row);
        Assert.Equal(IdentityDecision.ProbableMatch, id.Decision);
        Assert.Equal([ReasonCodes.ConsistentAtAvailablePrecision, ReasonCodes.NoBilateralDirectIdentifier], id.ReasonCodes);
        Assert.Equal(ScreeningAssessment.Review, hit.PairResult);

        Assert.Contains(input.LegacyDecisions, d => d.Value == "確認後符合" && !d.IndependentlyVerified);
    }

    // Advisory Article 22 recommendation; GC-03 and GC-11 PROBABLE_MATCH stays undetermined because automatic MATCH is disabled.
    [Theory]
    [InlineData("GC-01", "NotOnList")]
    [InlineData("GC-02", null)]
    [InlineData("GC-03", null)]
    [InlineData("GC-11", null)]
    [InlineData("GC-12", null)]
    public void Article_22_recommendation(string gc, string? expected)
    {
        var (_, a) = Run(gc);
        Assert.All(a.Hits, h => Assert.Equal(expected, h.Disposition.Recommendation?.ToString()));
    }

    [Fact]
    public void GC12_related_party_with_common_name_is_review_row_6()
    {
        var (input, a) = Run("GC-12");

        var s = input.Subject;
        Assert.Equal(SubjectRole.RelatedParty, s.Role);
        Assert.Equal("帳戶持有人的法定代理人", s.Relationship);
        Assert.Equal("B123456780", s.ReferenceNumber);
        Assert.Null(s.Document);
        Assert.Null(s.Gender);
        Assert.Contains(Findings.DuplicatedAddressLine, s.Findings);
        Assert.Contains(s.Addresses, x => x.State == ValueState.Ambiguous && x.Detail == Findings.PostalCodeOnly);

        var hit = Assert.Single(a.Hits);
        var c = Assert.Single(hit.Conditions);
        Assert.Equal(ListType.AdverseMedia, c.Type);
        Assert.Equal(["FINANCIAL_CRIME", "TAX_CRIME"], c.RiskCategories);
        Assert.Equal(LegalStage.Indicted, c.LegalStage);

        var id = hit.Identity;
        AssertEvidence(id, EvidenceGroup.Name, Exact);
        AssertEvidence(id, EvidenceGroup.Gender, SubjectMissing);
        AssertEvidence(id, EvidenceGroup.Nationality, Exact);
        AssertEvidence(id, EvidenceGroup.DateOfBirth, ProfileMissing);
        AssertEvidence(id, EvidenceGroup.Document, SubjectMissing);
        AssertEvidence(id, EvidenceGroup.OtherIdentifier, ProfileMissing);
        AssertEvidence(id, EvidenceGroup.Employment, SubjectMissing);
        AssertEvidence(id, EvidenceGroup.Relationship, SubjectMissing);
        AssertEvidence(id, EvidenceGroup.Address, ProfileMissing);
        Assert.Equal(6, id.Stage3Row);
        Assert.Equal(IdentityDecision.Review, id.Decision);
        Assert.Equal([ReasonCodes.NoNonPrerequisiteSupport, ReasonCodes.NoBilateralDirectIdentifier], id.ReasonCodes);
    }
}
