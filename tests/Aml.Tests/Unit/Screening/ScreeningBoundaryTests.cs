using Aml.Core.Identity;
using Aml.Core.Screening;
using Aml.Core.Shared;
using Aml.Core.Subjects;
using static Aml.Core.Screening.ConditionStatus;

namespace Aml.Tests.Unit.Screening;

/// <summary>Branches, boundaries and vocabularies of the source-condition, assessment and Article 22 rules, each pinned so a changed rule fails.</summary>
public class ScreeningBoundaryTests
{
    private static SourceProfile Profile(params SourceCategory[] categories) =>
        new("DJ:1", Gender.Male, ["陳志明"], [], ["VN"], [], categories, [], [], [], null, [], []);

    private static readonly SourceCategory Pep = new("Politically Exposed Person", "Minister");
    private static readonly SourceCategory Fraud = new("Special Interest Person (SIP)", "Fraud");
    private static readonly SourceCategory Spouse = new("Relative or Close Associate (RCA)", "Spouse");
    private static readonly SourceCategory Unmapped = new("Special Interest Person (SIP)", null);

    private static ListFlags Flags(FlagValue pep = FlagValue.Missing, FlagValue blacklist = FlagValue.Missing,
        FlagValue adverse = FlagValue.Missing, FlagValue rca = FlagValue.Missing) => new(pep, blacklist, adverse, rca);

    private static IReadOnlyList<SourceCondition> Determine(SourceProfile p, ListFlags? flags = null, bool isRca = false, string? news = null) =>
        SourceConditions.Determine(p, isRca, news, flags);

    private static SourceCondition Condition(ListType type, ConditionStatus status = Hit,
        ConditionBasis basis = ConditionBasis.CategoryMapping, params string[] codes) =>
        new(type, status, basis, [], LegalStage.Unknown, null, SourceConditions.PolicyVersion, codes);

    private static IdentityResult Identity(IdentityDecision decision = IdentityDecision.Review, int row = 6) =>
        new(new Dictionary<EvidenceGroup, Evidence>(), null, row, decision, []);

    // ---- Source conditions -------------------------------------------------------------------------------------

    [Fact]
    public void A_flag_and_its_own_category_give_one_condition() =>
        Assert.Single(Determine(Profile(Pep), Flags(pep: FlagValue.Yes)));

    [Fact]
    public void Blacklist_yes_with_an_undecided_type_is_one_unknown_condition()
    {
        var c = Assert.Single(Determine(Profile(Unmapped), Flags(blacklist: FlagValue.Yes)));
        Assert.Equal((ListType.Unknown, Unknown), (c.Type, c.Status));
        Assert.Equal([SourceConditions.BlacklistTypeUndecided], c.ReasonCodes);
    }

    [Fact]
    public void Blacklist_no_with_an_undecided_type_is_not_hit_without_a_code()
    {
        var c = Assert.Single(Determine(Profile(), Flags(blacklist: FlagValue.No)));
        Assert.Equal((ListType.Unknown, NotHit), (c.Type, c.Status));
        Assert.Empty(c.ReasonCodes);
    }

    [Fact]
    public void Blacklist_yes_on_a_sanction_entry_carries_no_undecided_code()
    {
        var c = Assert.Single(Determine(Profile() with { OfficialList = new("OFAC", "SDN Sanctions List", "Active", null) }, Flags(blacklist: FlagValue.Yes)));
        Assert.Equal((ListType.Sanction, Hit), (c.Type, c.Status));
        Assert.Empty(c.ReasonCodes);
    }

    [Theory]
    [InlineData("Wanted Persons", "Active", ListType.OfficialWantedList, LegalStage.Wanted)]
    [InlineData("Debarment", "Active", ListType.OtherOfficialList, LegalStage.Unknown)]
    [InlineData("Wanted Persons", "Inactive", ListType.OtherOfficialList, LegalStage.Unknown)]
    public void Blacklist_yes_takes_its_type_from_the_official_list_entry(string list, string status, ListType type, LegalStage stage)
    {
        var c = Assert.Single(Determine(Profile() with { OfficialList = new("MJIB", list, status, null) }, Flags(blacklist: FlagValue.Yes)));
        Assert.Equal((type, stage), (c.Type, c.LegalStage));
    }

    [Fact]
    public void Blacklist_yes_on_an_official_list_category_is_other_official_list() =>
        Assert.Equal(ListType.OtherOfficialList,
            Assert.Single(Determine(Profile(new SourceCategory("Exclusion List", null)), Flags(blacklist: FlagValue.Yes))).Type);

    [Fact]
    public void Adverse_news_yes_adds_an_adverse_media_condition_from_the_flag() =>
        Assert.Equal([(ListType.AdverseMedia, Hit, ConditionBasis.Flag), (ListType.Pep, Hit, ConditionBasis.CategoryMapping)],
            Determine(Profile(Pep), Flags(adverse: FlagValue.Yes)).Select(c => (c.Type, c.Status, c.Basis)));

    [Fact]
    public void Is_rca_alone_adds_an_rca_condition()
    {
        var c = Assert.Single(Determine(Profile(), isRca: true));
        Assert.Equal((ListType.Rca, Hit), (c.Type, c.Status));
        Assert.Equal([SourceConditions.FlagIsRca], c.ReasonCodes);
    }

    [Fact]
    public void An_unmapped_category_is_no_flag_category_conflict() =>
        Assert.Equal([(ListType.Pep, ""), (ListType.Unknown, SourceConditions.UnmappedCategory)],
            Determine(Profile(Unmapped), Flags(pep: FlagValue.No)).Select(c => (c.Type, string.Join(",", c.ReasonCodes))));

    [Fact]
    public void A_flag_no_against_one_of_two_categories_is_a_conflict() =>
        Assert.All(Determine(Profile(Pep, Fraud), Flags(pep: FlagValue.No)), c => Assert.Equal([SourceConditions.FlagCategoryConflict], c.ReasonCodes));

    [Fact]
    public void A_flag_yes_matching_the_category_is_no_conflict_even_beside_another_flag_no() =>
        Assert.All(Determine(Profile(Pep), Flags(pep: FlagValue.Yes, adverse: FlagValue.No)), c => Assert.Empty(c.ReasonCodes));

    [Fact]
    public void Is_rca_no_alone_marks_only_the_rca_condition()
    {
        var conditions = Determine(Profile(Pep, Spouse), Flags(rca: FlagValue.No));
        Assert.Empty(conditions.Single(c => c.Type == ListType.Pep).ReasonCodes);
        Assert.Equal([SourceConditions.FlagCategoryConflict], conditions.Single(c => c.Type == ListType.Rca).ReasonCodes);
    }

    [Theory]
    [InlineData("State Owned Enterprise", null, ListType.Sie)]
    [InlineData("State Invested Enterprise", null, ListType.Sie)]
    [InlineData("Official List", null, ListType.OtherOfficialList)]
    [InlineData("Exclusion List", null, ListType.OtherOfficialList)]
    [InlineData("Special Interest Person (SIP)", "Official List", ListType.OtherOfficialList)]
    [InlineData("Special Interest Person (SIP)", "Exclusion List", ListType.OtherOfficialList)]
    [InlineData("Unclassified", null, ListType.Unknown)]
    public void Categories_map_to_list_types(string category, string? sub, ListType type) =>
        Assert.Equal(type, Assert.Single(Determine(Profile(new SourceCategory(category, sub)))).Type);

    [Theory]
    [InlineData("Financial Crime", "FINANCIAL_CRIME"), InlineData("Tax Crime", "TAX_CRIME"), InlineData("Trafficking", "TRAFFICKING")]
    [InlineData("Human Trafficking", "HUMAN_TRAFFICKING"), InlineData("Narcotics", "DRUG_TRAFFICKING"), InlineData("Drug Trafficking", "DRUG_TRAFFICKING")]
    [InlineData("Organised Crime", "ORGANISED_CRIME"), InlineData("Terror", "TERRORISM"), InlineData("Terrorism", "TERRORISM")]
    [InlineData("Corruption", "CORRUPTION"), InlineData("Bribery & Corruption", "BRIBERY"), InlineData("Money Laundering", "MONEY_LAUNDERING")]
    [InlineData("fraud", "FRAUD"), InlineData("War Crimes", "WAR_CRIMES"), InlineData("Cybercrime", "CYBERCRIME")]
    [InlineData("Environmental Crime", "ENVIRONMENTAL_CRIME"), InlineData("Corporate Crime", "CORPORATE_CRIME")]
    public void Every_sip_crime_subcategory_is_adverse_media_with_its_risk_category(string sub, string risk)
    {
        var c = Assert.Single(Determine(Profile(new SourceCategory("Special Interest Person (SIP)", sub))));
        Assert.Equal(ListType.AdverseMedia, c.Type);
        Assert.Equal([risk], c.RiskCategories);
    }

    [Theory]
    [InlineData("Fraud", "FRAUD"), InlineData("tax evasion", "TAX_EVASION"), InlineData("Bribery", "BRIBERY"), InlineData("Corruption", "CORRUPTION")]
    [InlineData("Forgery", "FORGERY"), InlineData("Breach of Trust", "BREACH_OF_TRUST"), InlineData("Money Laundering", "MONEY_LAUNDERING")]
    public void Every_risk_keyword_gives_its_risk_category(string keyword, string risk) =>
        Assert.Equal([risk], Assert.Single(Determine(Profile(new SourceCategory("Adverse Media", null)) with { Keywords = [keyword, "Other"] })).RiskCategories);

    [Fact]
    public void A_former_subcategory_outside_pep_and_rca_categories_makes_no_former_pep() =>
        Assert.False(SourceConditions.IsFormerPep(Profile(new SourceCategory("Special Interest Person (SIP)", "Former Employee Fraud")), ownRoles: false, new DateOnly(2026, 11, 4)));

    [Fact]
    public void One_former_subcategory_among_several_makes_a_former_pep() =>
        Assert.True(SourceConditions.IsFormerPep(Profile(Pep, new SourceCategory("Relative or Close Associate (RCA)", "Former PEP")), ownRoles: false, new DateOnly(2026, 11, 4)));

    [Fact]
    public void One_relation_outside_the_statutory_scope_among_several_is_flagged() =>
        Assert.Equal([SourceConditions.RcaOutsideStatutoryScope],
            Determine(Profile(Pep, new SourceCategory("Relative or Close Associate (RCA)", "Cousin"))).Single(c => c.Type == ListType.Rca).ReasonCodes);

    [Theory]
    [InlineData("陳志明酒駕。", "TW", AmlRelevance.Unrelated)]                                      // jurisdiction from the allegation country
    [InlineData("陳志明酒駕。", null, AmlRelevance.Uncertain)]                                      // no jurisdiction stated
    [InlineData("〔記者丙／台北報導〕陳志明否認犯行。陳志明酒駕被捕。", null, AmlRelevance.Unrelated)] // conduct in the second naming sentence
    public void Relevance_needs_a_jurisdiction_and_the_conduct_in_any_sentence_naming_the_person(string news, string? country, AmlRelevance relevance) =>
        Assert.Equal(relevance, Assert.Single(Determine(Profile(new SourceCategory("Adverse Media", null)) with
        {
            AllegationCountries = country is null ? null : [country],
        }, news: news)).AmlRelevance);

    [Fact]
    public void A_one_character_alias_names_nobody() =>
        Assert.Equal(AmlRelevance.Uncertain, Assert.Single(Determine(Profile(new SourceCategory("Adverse Media", null)) with { Names = ["王大同", "陳"] },
            news: "〔記者丙／台北報導〕陳先生酒駕被捕。")).AmlRelevance);

    [Fact]
    public void The_later_stage_in_one_clause_wins() =>
        Assert.Equal(LegalStage.Convicted, Assert.Single(Determine(Profile(new SourceCategory("Adverse Media", null)),
            news: "陳志明遭起訴後判處有期徒刑二年")).LegalStage);

    // ---- Assessment --------------------------------------------------------------------------------------------

    [Fact]
    public void No_conditions_are_not_applicable()
    {
        var a = AssessmentPolicy.Assess([], Identity());
        Assert.Equal((ScreeningAssessment.NotApplicable, null), (a.Result, a.Priority));
        Assert.Empty(a.ReasonCodes);
    }

    [Fact]
    public void One_unknown_condition_among_hits_is_reported() =>
        Assert.Equal([AssessmentPolicy.SourceConditionUnknown],
            AssessmentPolicy.Assess([Condition(ListType.AdverseMedia), Condition(ListType.Pep, Unknown)], Identity()).ReasonCodes);

    [Theory]
    [InlineData(IdentityDecision.ProbableMatch, 2, ReviewPriority.High)]
    [InlineData(IdentityDecision.Review, 5, ReviewPriority.Normal)]
    [InlineData(IdentityDecision.Review, 6, ReviewPriority.Low)]
    public void Review_priority_follows_the_stage_3_row(IdentityDecision decision, int row, ReviewPriority priority) =>
        Assert.Equal(priority, AssessmentPolicy.Assess([Condition(ListType.AdverseMedia)], Identity(decision, row)).Priority);

    // ---- Article 22 and pending checks -------------------------------------------------------------------------

    private static DispositionRecommendation Recommend(SourceProfile p, string[] nationality, params SourceCondition[] conditions) =>
        DispositionPolicy.Recommend(
            new ScreenedSubject(SubjectRole.Customer, null, ["陳志明"], Gender.Male, new PartialDate(1980, 5, 20), nationality, null, null, null, null, [], []),
            p, conditions, Identity(), hasNews: false);

    private static readonly SourceCondition UnmappedUnknown = Condition(ListType.Unknown, Unknown, ConditionBasis.Unmapped, SourceConditions.UnmappedCategory);

    [Fact]
    public void Another_flag_no_does_not_stop_an_unmapped_category_from_counting_as_possible_pep() =>
        Assert.Equal(Recommendation.OnList,
            Recommend(Profile(), ["VN"], UnmappedUnknown, Condition(ListType.AdverseMedia, NotHit, ConditionBasis.Flag)).Recommendation);

    [Fact]
    public void An_undecided_blacklist_type_is_not_a_possible_pep()
    {
        var d = Recommend(Profile(), ["VN"], Condition(ListType.Unknown, Unknown, ConditionBasis.Flag, SourceConditions.BlacklistTypeUndecided));
        Assert.Null(d.Recommendation);
        Assert.Equal([DispositionPolicy.NotDetermined], d.ReasonCodes);
    }

    [Fact]
    public void An_ambiguous_finding_on_the_other_field_leaves_nationality_classified() =>
        Assert.Equal(Recommendation.OnList, DispositionPolicy.Recommend(
            new ScreenedSubject(SubjectRole.Customer, null, ["陳志明"], null, null, ["VN"], null, null, null, null, [], ["AMBIGUOUS:Citizenship"]),
            Profile() with { Findings = ["AMBIGUOUS:國籍"] }, [Condition(ListType.Pep)], Identity(), hasNews: false).Recommendation);

    [Fact]
    public void A_nationality_value_that_is_not_a_code_needs_confirming()
    {
        var d = Recommend(Profile(), ["VN", "VIETNAM"], Condition(ListType.Pep));
        Assert.Null(d.Recommendation);
        Assert.Equal([DispositionPolicy.ConfirmNationality, DispositionPolicy.EmploymentRelevance, DispositionPolicy.IdentityCheck], d.PendingChecks);
    }

    [Fact]
    public void Conditions_that_are_all_not_hit_with_no_other_reason_resolve_nothing()
    {
        var d = Recommend(Profile(), ["TW"], Condition(ListType.Pep, NotHit));
        Assert.Null(d.Recommendation);
        Assert.Equal([DispositionPolicy.NotDetermined], d.ReasonCodes);
    }

    [Theory]
    [InlineData(Hit, true)]
    [InlineData(NotHit, false)]
    public void Former_pep_influence_needs_the_pep_condition_raised(ConditionStatus pep, bool check) =>
        Assert.Equal(check, Recommend(Profile(new SourceCategory("Politically Exposed Person", "Former PEP")), ["TW"],
            Condition(ListType.Pep, pep), Condition(ListType.AdverseMedia)).PendingChecks.Contains(DispositionPolicy.FormerPepInfluence));

    [Fact]
    public void An_rca_whose_own_roles_ended_is_no_former_pep_influence() =>
        Assert.DoesNotContain(DispositionPolicy.FormerPepInfluence, Recommend(Profile(Spouse) with
        {
            Roles = [new ProfileRole("董事", "宏遠投資股份有限公司", new PartialDate(1990), new PartialDate(2000))],
        }, ["TW"], Condition(ListType.Rca)).PendingChecks);

    [Fact]
    public void A_pep_hit_beside_another_condition_without_a_dob_needs_an_identity_check() =>
        Assert.Equal([DispositionPolicy.EmploymentRelevance, DispositionPolicy.RegionRelevance, DispositionPolicy.IdentityCheck, DispositionPolicy.JudgmentPartyIdentity],
            Recommend(Profile(Pep), ["TW"], Condition(ListType.Pep), Condition(ListType.AdverseMedia)).PendingChecks);
}
