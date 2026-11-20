using Aml.Core.Cases;
using Aml.Core.Identity;
using Aml.Core.Normalization;
using Aml.Core.Retrieval;
using Aml.Core.Screening;
using Aml.Core.Shared;
using Aml.Core.Subjects;
using Aml.Infra.Txt;

namespace Aml.Tests.Unit.Retrieval;

/// <summary>List index, Profile merging, the Fellegi-Sunter calibration weight, risk scoring and entity evidence.</summary>
public class RetrievalAndScoringTests
{
    private static SourceProfile Profile(string key, string[] names, IReadOnlyList<Document>? docs = null,
        IReadOnlyList<ProfileRelationship>? links = null, string recordType = "Person") =>
        new("DJ:" + key, Gender.Male, names, [], ["TW"], [], [new SourceCategory("Special Interest Person (SIP)", "Fraud")],
            [], [], links ?? [], null, [], [], docs, RecordType: recordType);

    private static readonly ProfileIndex Index = new(
    [
        Profile("1", ["鐘仁聲", "Jong Jen-Sheng", "Zhong Ren-Sheng"]),
        Profile("2", ["陳志明", "Chen Chih-Ming"]),
        Profile("3", ["陳志成", "Chen Chih-Cheng"]),
        Profile("4", ["Nguyễn Thị Hồng Nhung"]),
    ]);

    [Fact]
    public void Exact_alias_after_variant_folding_ranks_first_and_counts_once_per_profile()
    {
        var hits = Index.Search("鍾仁聲");
        Assert.Equal(("DJ:1", ProfileIndex.ExactAlias), (hits[0].ProfileKey, hits[0].Route));
        Assert.Equal(hits.Count, hits.Select(h => h.ProfileKey).Distinct().Count()); // by Profile, not alias row
    }

    [Fact]
    public void Diacritic_free_and_reordered_names_find_the_profile()
    {
        Assert.Equal("DJ:4", Index.Search("NGUYEN THI HONG NHUNG")[0].ProfileKey);
        Assert.Equal("DJ:2", Index.Search("Chih-Ming Chen")[0].ProfileKey);
    }

    [Fact]
    public void Bm25_and_ngram_rank_the_closer_name_first_and_top_n_caps_profiles()
    {
        var hits = Index.Search("陳志明先生");
        Assert.Equal(["DJ:2", "DJ:3"], hits.Take(2).Select(h => h.ProfileKey)); // 陳志明 shares more characters than 陳志成
        Assert.Single(Index.Search("陳志明先生", top: 1));
        Assert.Equal(Index.Search("陳志明先生", top: 100), Index.Search("陳志明先生", top: int.MaxValue)); // an unbounded top returns every match
        Assert.All(hits, h => Assert.Equal(RetrievalMode.IndexSearch, h.Mode));
    }

    [Fact]
    public void A_search_scored_in_parallel_ranks_by_score_then_name_the_same_every_time()
    {
        // Every alias shares the surname 陳, so the query reaches all 6000 and takes the parallel path.
        var index = new ProfileIndex(Enumerable.Range(0, 6000)
            .Select(i => Profile($"{i}", [$"陳{(char)('一' + i % 50)}{(char)('一' + i / 50 % 50)}"])).ToList());
        var first = index.Search("陳一明", 25);
        Assert.Equal(25, first.Count);
        Assert.Equal(first.OrderByDescending(c => c.Score).ThenBy(c => c.MatchedName, StringComparer.Ordinal).ThenBy(c => c.ProfileKey, StringComparer.Ordinal), first);
        for (var run = 0; run < 5; run++) Assert.Equal(first, index.Search("陳一明", 25));
    }

    [Fact]
    public void Profiles_merge_on_a_shared_document_or_a_recorded_link_never_on_name()
    {
        var id = new Document("TW_NATIONAL_ID", "A123456789", "TW");
        var groups = ProfileMerge.Group(
        [
            Profile("1", ["王建宏"], [id]),
            Profile("2", ["Wang Chien-Hung"], [id]),
            Profile("3", ["王建宏"]),                                                     // same name only: stays apart
            Profile("4", ["林美玲"], links: [new ProfileRelationship("5", "林美玲", "Same As")]),
            Profile("5", ["Lin Mei-Ling"]),
        ]);
        Assert.Equal(2, groups.Count);
        Assert.Contains(groups, g => g.ProfileKeys.SequenceEqual(["DJ:1", "DJ:2"]) && g.Basis.SequenceEqual([ProfileMerge.SharedDocument]));
        Assert.Contains(groups, g => g.ProfileKeys.SequenceEqual(["DJ:4", "DJ:5"]) && g.Basis.SequenceEqual([ProfileMerge.SourceLink]));
    }

    [Fact]
    public void Fellegi_sunter_calibration_weights_follow_the_placeholder_table_and_weigh_prerequisites()
    {
        var gc11 = CaseAnalyzer.Analyze(TxtCaseReader.Read(GoldenCases.GoldenCaseTests.Folder("GC-11"))).Hits[0].Identity;
        var weight = FellegiSunter.Score(gc11.Evidence);
        // name, gender and nationality EXACT, a year-precision DOB: nothing else weighs
        Assert.Equal([EvidenceGroup.Name, EvidenceGroup.Gender, EvidenceGroup.Nationality, EvidenceGroup.DateOfBirth], weight.Groups.Select(g => g.Group).Order());
        Assert.Equal(Math.Log2(0.91 * 50), Assert.Single(weight.Groups, g => g.Group == EvidenceGroup.DateOfBirth).Weight, 6); // DOB m 0.91, LOW u 1/50

        var conflict = FellegiSunter.Score(new Dictionary<EvidenceGroup, Evidence>
        {
            [EvidenceGroup.Document] = new(EvidenceGroup.Document, Direction.Conflict, ComparisonStatus.Conflict),
        });
        Assert.True(conflict.TotalWeight < 0);
    }

    [Fact]
    public void A_pattern_keeps_support_conflict_and_compared_prerequisites_and_leaves_every_other_group_out()
    {
        var pattern = FellegiSunter.Pattern(new Dictionary<EvidenceGroup, Evidence>
        {
            [EvidenceGroup.Name] = new(EvidenceGroup.Name, Direction.Prerequisite, ComparisonStatus.Exact),
            [EvidenceGroup.Gender] = new(EvidenceGroup.Gender, Direction.Prerequisite, ComparisonStatus.SubjectMissing),
            [EvidenceGroup.DateOfBirth] = new(EvidenceGroup.DateOfBirth, Direction.Support, ComparisonStatus.Exact, Strength.High),
            [EvidenceGroup.Document] = new(EvidenceGroup.Document, Direction.Conflict, ComparisonStatus.Conflict),
            [EvidenceGroup.Address] = new(EvidenceGroup.Address, Direction.Neutral, ComparisonStatus.Uncertain),
        });
        Assert.Equal([(EvidenceGroup.Name, true), (EvidenceGroup.DateOfBirth, true), (EvidenceGroup.Document, false)], pattern.OrderBy(x => x.Key).Select(x => (x.Key, x.Value)));
    }

    // Placeholder m and u (FellegiSunter, identity-adjudication.md#calibration-weight): NAME 0.76 and 1/4,200,
    // GENDER 0.99 and 0.5, NATIONALITY 0.99 and 0.1. Agreement weighs log2(m/u), disagreement log2((1−m)/(1−u)).
    [Theory]
    [InlineData(EvidenceGroup.Name, ComparisonStatus.Exact, null, true, 0.76, 1.0 / 4_200)]
    [InlineData(EvidenceGroup.Name, ComparisonStatus.Partial, Findings.RomanizationVariant, false, 0.76, 1.0 / 4_200)]
    [InlineData(EvidenceGroup.Name, ComparisonStatus.Partial, EvidenceDetails.NotIdenticalAfterNormalization, false, 0.76, 1.0 / 4_200)]
    [InlineData(EvidenceGroup.Name, ComparisonStatus.Partial, NameComparison.NotComparableCrossScript, null, 0, 0)]
    [InlineData(EvidenceGroup.Name, ComparisonStatus.SubjectMissing, null, null, 0, 0)]
    [InlineData(EvidenceGroup.Gender, ComparisonStatus.Exact, null, true, 0.99, 0.5)]
    [InlineData(EvidenceGroup.Gender, ComparisonStatus.Conflict, ReasonCodes.GenderConflict, false, 0.99, 0.5)]
    [InlineData(EvidenceGroup.Gender, ComparisonStatus.ProfileMissing, null, null, 0, 0)]
    [InlineData(EvidenceGroup.Nationality, ComparisonStatus.Exact, null, true, 0.99, 0.1)]
    [InlineData(EvidenceGroup.Nationality, ComparisonStatus.Conflict, null, false, 0.99, 0.1)]
    [InlineData(EvidenceGroup.Nationality, ComparisonStatus.SubjectMissing, null, null, 0, 0)]
    public void A_prerequisite_agrees_when_exact_disagrees_when_compared_otherwise_and_is_left_out_when_not_compared(
        EvidenceGroup group, ComparisonStatus status, string? detail, bool? agrees, double m, double u)
    {
        var evidence = new Dictionary<EvidenceGroup, Evidence> { [group] = new(group, Direction.Prerequisite, status, Strength.None, detail) };
        var pattern = FellegiSunter.Pattern(evidence);
        var weights = FellegiSunter.Score(evidence).Groups;
        if (agrees is not { } a)
        {
            Assert.Empty(pattern);
            Assert.Empty(weights);
            return;
        }
        Assert.Equal(a, pattern[group]);
        var w = Assert.Single(weights);
        Assert.Equal((group, m, u), (w.Group, w.M, w.U));
        Assert.Equal(a ? Math.Log2(m / u) : Math.Log2((1 - m) / (1 - u)), w.Weight, 12);
    }

    [Fact]
    public void A_score_sums_the_weights_of_every_group_compared()
    {
        var evidence = new Dictionary<EvidenceGroup, Evidence>
        {
            [EvidenceGroup.Name] = new(EvidenceGroup.Name, Direction.Prerequisite, ComparisonStatus.Exact),
            [EvidenceGroup.Gender] = new(EvidenceGroup.Gender, Direction.Prerequisite, ComparisonStatus.Conflict),
            [EvidenceGroup.Nationality] = new(EvidenceGroup.Nationality, Direction.Prerequisite, ComparisonStatus.Exact),
            [EvidenceGroup.DateOfBirth] = new(EvidenceGroup.DateOfBirth, Direction.Support, ComparisonStatus.Exact, Strength.Medium),
            [EvidenceGroup.Document] = new(EvidenceGroup.Document, Direction.Conflict, ComparisonStatus.Conflict),
            [EvidenceGroup.Address] = new(EvidenceGroup.Address, Direction.Neutral, ComparisonStatus.Uncertain),
        };
        var expected =
            Math.Log2(0.76 / (1.0 / 4_200))     // NAME agrees: 11.640
            + Math.Log2(0.01 / 0.5)             // GENDER disagrees: −5.644
            + Math.Log2(0.99 / 0.1)             // NATIONALITY agrees: 3.307
            + Math.Log2(0.91 / (1.0 / 440))     // DOB agrees at MONTH precision (MEDIUM, u 1/440): 8.645
            + Math.Log2(0.04 / (1 - 1e-7));     // DOCUMENT disagrees (m 0.96, u 1e-7): −4.644
        var score = FellegiSunter.Score(evidence);
        Assert.Equal(5, score.Groups.Count);
        Assert.Equal(expected, score.TotalWeight, 9);
        Assert.Equal(13.305, score.TotalWeight, 3);
    }

    // Five groups, name included, drawn from known m, u and a match share of 0.1; EM recovers them from either start.
    [Fact]
    public void Em_over_five_groups_recovers_m_and_u_and_reaches_one_answer_from_two_starts()
    {
        var truth = new (EvidenceGroup Group, double M, double U)[]
        {
            (EvidenceGroup.Name, 0.8, 0.01), (EvidenceGroup.Gender, 0.95, 0.5), (EvidenceGroup.Nationality, 0.9, 0.1),
            (EvidenceGroup.DateOfBirth, 0.9, 0.05), (EvidenceGroup.Document, 0.95, 0.01),
        };
        var random = new Random(20261120);
        var patterns = Enumerable.Range(0, 5000).Select(i =>
        {
            var match = i % 10 == 0;
            var p = new Dictionary<EvidenceGroup, bool>();
            foreach (var (g, m, u) in truth)
                if (g != EvidenceGroup.Document || random.NextDouble() < 0.5) // a document is on both sides for half the pairs
                    p[g] = random.NextDouble() < (match ? m : u);
            return (IReadOnlyDictionary<EvidenceGroup, bool>)p;
        }).ToList();

        var low = FellegiSunter.EstimateParameters(patterns, matchShare: 0.05);
        var high = FellegiSunter.EstimateParameters(patterns, matchShare: 0.3);

        // Tolerance: about three sampling standard errors (500 matches, 4,500 non-matches).
        foreach (var estimate in new[] { low, high })
        {
            Assert.InRange(estimate.Iterations, 1, 199); // converged before the cap
            Assert.Equal(0.1, estimate.MatchShare, 0.02);
            foreach (var (g, m, u) in truth)
            {
                var e = Assert.Single(estimate.Groups, x => x.Group == g);
                Assert.Equal(m, e.M, 0.06);
                Assert.Equal(u, e.U, 0.025);
            }
        }
        Assert.Equal(low.MatchShare, high.MatchShare, 1e-6);
        foreach (var (l, h) in low.Groups.Zip(high.Groups))
        {
            Assert.Equal(l.Group, h.Group);
            Assert.Equal(l.M, h.M, 1e-6);
            Assert.Equal(l.U, h.U, 1e-6);
        }
    }

    // Synthetic pairs drawn from known m, u and match share; EM recovers them without labels.
    [Fact]
    public void Em_recovers_m_u_and_the_match_share_from_unlabelled_patterns()
    {
        var truth = new (EvidenceGroup Group, double M, double U)[]
            { (EvidenceGroup.DateOfBirth, 0.95, 0.02), (EvidenceGroup.Document, 0.9, 0.01), (EvidenceGroup.Employment, 0.7, 0.1) };
        var random = new Random(42);
        var patterns = Enumerable.Range(0, 4000).Select(i =>
        {
            var match = i % 10 == 0; // a share of 0.1
            var p = new Dictionary<EvidenceGroup, bool>();
            foreach (var (g, m, u) in truth)
                if (g != EvidenceGroup.Document || random.NextDouble() < 0.5) // a document is on both sides for half the pairs
                    p[g] = random.NextDouble() < (match ? m : u);
            return (IReadOnlyDictionary<EvidenceGroup, bool>)p;
        }).ToList();

        var estimate = FellegiSunter.EstimateParameters(patterns);

        Assert.Equal(0.1, estimate.MatchShare, 0.02);
        foreach (var (g, m, u) in truth)
        {
            var e = Assert.Single(estimate.Groups, x => x.Group == g);
            Assert.Equal(m, e.M, 0.06);
            Assert.Equal(u, e.U, 0.02);
        }
        Assert.InRange(estimate.Iterations, 1, 199); // converged before the cap
        Assert.True(FellegiSunter.Weight(truth.ToDictionary(t => t.Group, _ => true), estimate) > 0);
        Assert.True(FellegiSunter.Weight(truth.ToDictionary(t => t.Group, _ => false), estimate) < 0);
    }

    [Fact]
    public void Em_rejects_a_match_share_outside_zero_and_one()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FellegiSunter.EstimateParameters([], matchShare: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => FellegiSunter.EstimateParameters([], matchShare: 1));
        Assert.Equal((0, 0.1, 0), (FellegiSunter.EstimateParameters([]).Groups.Count, FellegiSunter.EstimateParameters([]).MatchShare, FellegiSunter.EstimateParameters([]).Iterations));
    }

    [Fact]
    public void A_group_without_parameters_weighs_nothing()
    {
        var estimate = new Estimate([new GroupParameters(EvidenceGroup.DateOfBirth, 0.9, 0.1)], 0.1, 1);
        Assert.Equal(Math.Log2(9), FellegiSunter.Weight(new Dictionary<EvidenceGroup, bool> { [EvidenceGroup.DateOfBirth] = true, [EvidenceGroup.Address] = true }, estimate), 9);
        Assert.Equal(Math.Log2(0.1 / 0.9), FellegiSunter.Weight(new Dictionary<EvidenceGroup, bool> { [EvidenceGroup.DateOfBirth] = false }, estimate), 9);
    }

    [Theory]
    [InlineData(1.0, 2, 1.0, 0.75)]       // every match kept: the lowest match weight
    [InlineData(0.6, 3, 2.0 / 3, 2.0 / 3)] // two of three matches
    [InlineData(0.33, 5, 1.0 / 3, 1.0)]
    public void The_threshold_is_the_highest_weight_that_keeps_the_recall(double minRecall, double weight, double recall, double precision)
    {
        var t = FellegiSunter.Calibrate([(5, true), (3, true), (1, false), (4, false), (2, true)], minRecall)!;
        Assert.Equal((weight, recall, precision), (t.Weight, t.Recall, t.Precision));
    }

    [Fact]
    public void Calibration_needs_a_labelled_match_and_a_recall_in_zero_to_one()
    {
        Assert.Null(FellegiSunter.Calibrate([(1, false)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => FellegiSunter.Calibrate([(1, true)], 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => FellegiSunter.Calibrate([(1, true)], 1.01));
    }

    [Fact]
    public void Recall_at_k_counts_the_queries_whose_profile_is_in_the_first_k()
    {
        // 陳志明先生 ranks DJ:2 first and DJ:3 second.
        Assert.Equal(0.5, Index.RecallAtK([("陳志明先生", "2"), ("陳志明先生", "3")], 1));
        Assert.Equal(1.0, Index.RecallAtK([("陳志明先生", "2"), ("陳志明先生", "3")], 2));
        Assert.Equal(0, Index.RecallAtK([], 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => Index.RecallAtK([], 0));
    }

    private static readonly RiskPolicy SamplePolicy = new("risk-sample-unapproved", Approved: true,
        new Dictionary<ListType, int> { [ListType.AdverseMedia] = 10, [ListType.Sanction] = 50 },
        new Dictionary<string, int> { ["FRAUD"] = 5, ["TAX_EVASION"] = 5 },
        new Dictionary<LegalStage, int> { [LegalStage.Indicted] = 10 },
        MediumFrom: 15, HighFrom: 40);

    [Fact]
    public void Risk_is_scored_only_under_an_approved_policy_and_never_after_no_match()
    {
        var gc02 = TxtCaseReader.Read(GoldenCases.GoldenCaseTests.Folder("GC-02"));
        Assert.Null(CaseAnalyzer.Analyze(gc02).Hits[0].Risk);                                   // no policy
        Assert.Null(CaseAnalyzer.Analyze(gc02, SamplePolicy with { Approved = false }).Hits[0].Risk);

        var risk = CaseAnalyzer.Analyze(gc02, SamplePolicy).Hits[0].Risk!;
        Assert.Equal((20, RiskLevel.Medium), (risk.Score, risk.Level)); // ADVERSE_MEDIA 10 + FRAUD 5 + TAX_EVASION 5; ALLEGED adds nothing

        var gc01 = CaseAnalyzer.Analyze(TxtCaseReader.Read(GoldenCases.GoldenCaseTests.Folder("GC-01")), SamplePolicy);
        Assert.All(gc01.Hits, h => Assert.Null(h.Risk)); // identity NO_MATCH
    }

    private static ScreenedSubject Company(string taxId) =>
        new(SubjectRole.Customer, null, ["恆遠實業股份有限公司"], null, null, ["TW"], null, null,
            new FieldValue(taxId, ValueState.Present), null, [], [], PartyType: "法人");

    [Theory]
    [InlineData("12345678", 2, IdentityDecision.ProbableMatch, ReasonCodes.EntityIdentifierAgreement)]
    [InlineData("87654321", 1, IdentityDecision.Review, ReasonCodes.EntityIdentifierConflict)]
    public void Entity_registration_number_decides_entity_records(string taxId, int row, IdentityDecision decision, string code)
    {
        var entity = Profile("9", ["恆遠實業股份有限公司"], [new Document("REGISTRATION_NUMBER", "12345678", "TW")], recordType: "Entity");
        var id = IdentityDecisionPolicy.Decide(Company(taxId), entity);
        Assert.Equal((row, decision, code), (id.Stage3Row, id.Decision, id.ReasonCodes[0]));
    }

    [Fact]
    public void A_natural_person_is_never_compared_with_an_entity_record()
    {
        var entity = Profile("9", ["恆遠實業股份有限公司"], [new Document("REGISTRATION_NUMBER", "12345678", "TW")], recordType: "Entity");
        var person = Company("12345678") with { PartyType = "個人" };
        Assert.Equal([ReasonCodes.NonPersonSubjectReview], IdentityDecisionPolicy.Decide(person, entity).ReasonCodes);
    }

    [Fact]
    public void Vessel_imo_number_is_compared_without_a_country()
    {
        var vessel = Profile("8", ["OCEAN STAR"], [new Document("IMO_NUMBER", "IMO 9123456", null)], recordType: "Vessel");
        var owner = Company("00000000") with { Document = new Document("IMO_NUMBER", "IMO9123456", "PA") };
        Assert.Equal(IdentityDecision.ProbableMatch, IdentityDecisionPolicy.Decide(owner, vessel).Decision);
    }
}
