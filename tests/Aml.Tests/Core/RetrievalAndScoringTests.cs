using Aml.Core.Cases;
using Aml.Core.Identity;
using Aml.Core.Retrieval;
using Aml.Core.Screening;
using Aml.Core.Shared;
using Aml.Core.Subjects;
using Aml.Infra.Txt;

namespace Aml.Tests.Core;

/// <summary>List index, Profile merging, the Fellegi-Sunter shadow score, risk scoring and entity evidence.</summary>
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
        Assert.Equal("DJ:2", hits[0].ProfileKey); // shares more characters than 陳志成
        Assert.Contains(hits, h => h.ProfileKey == "DJ:3");
        Assert.Single(Index.Search("陳志明先生", top: 1));
        Assert.All(hits, h => Assert.Equal(RetrievalMode.IndexSearch, h.Mode));
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
    public void Fellegi_sunter_shadow_weights_follow_the_strength_table_and_ignore_prerequisites()
    {
        var gc11 = CaseAnalyzer.Analyze(TxtCaseReader.Read(GoldenCases.GoldenCaseTests.Folder("GC-11"))).Hits[0].Identity;
        var shadow = FellegiSunter.Score(gc11.Evidence);
        var dob = Assert.Single(shadow.Groups); // only the year-precision DOB weighs; name, gender and nationality do not
        Assert.Equal(EvidenceGroup.DateOfBirth, dob.Group);
        Assert.Equal(Math.Log2(FellegiSunter.M * 60), dob.Weight, 6);

        var conflict = FellegiSunter.Score(new Dictionary<EvidenceGroup, Evidence>
        {
            [EvidenceGroup.Document] = new(EvidenceGroup.Document, Direction.Conflict, ComparisonStatus.Conflict),
        });
        Assert.True(conflict.TotalWeight < 0);
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
