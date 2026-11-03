using Aml.Core.Identity;
using Aml.Core.Normalization;
using Aml.Core.Shared;
using Aml.Core.Subjects;
using static Aml.Core.Shared.ComparisonStatus;

namespace Aml.Tests.Core;

/// <summary>Rules without a dedicated Golden Case yet, tested on minimal synthetic data.</summary>
public class IdentityRuleTests
{
    private static ScreenedSubject Subject(string name, PartialDate? dob = null) =>
        new(SubjectRole.Customer, null, [name], Gender.Male, dob ?? new PartialDate(1980, 5, 20), ["TW"],
            null, null, null, null, [], []);

    private static SourceProfile Profile(params string[] names) =>
        new("DJ:1", Gender.Male, names, [], ["TW"], [], [new SourceCategory("Special Interest Person (SIP)", "Fraud")],
            [], [], [], null, [], []);

    private static NewsItem News(string text, PartialDate? on = null) =>
        new("1", "DOC1", "標題", "某報", on ?? new PartialDate(2026, 3, 1), text);

    [Theory]
    [InlineData("鍾仁聲", "鐘仁聲")]
    [InlineData("张家豪", "張家豪")]
    [InlineData("ＷＡＮＧ ＭＩＮＧ", "Wang Ming")]   // full-width Latin, NFKC
    public void Controlled_variants_and_NFKC_make_names_exact(string subject, string profile)
    {
        var (status, _) = Normalize.CompareNames([subject], [profile]);
        Assert.Equal(Exact, status);
    }

    [Fact]
    public void Variant_match_is_disclosed_and_distinct_surnames_never_fold()
    {
        Assert.Equal((Exact, Findings.VariantCharacters), Normalize.CompareNames(["鍾仁聲"], ["鐘仁聲"]));
        Assert.Equal((Exact, (string?)null), Normalize.CompareNames(["鐘仁聲"], ["鐘仁聲"]));
        Assert.Equal(Partial, Normalize.CompareNames(["涂志明"], ["塗志明"]).Status);
        Assert.Equal(Partial, Normalize.CompareNames(["鍾仁聲"], ["鐘任聲"]).Status); // 仁/任 differ in reading and are not folded
    }

    [Theory]
    [InlineData("CHUNG JEN-SHENG", "Zhong Ren Sheng")]   // Taiwanese passport vs Hanyu Pinyin
    [InlineData("JHONG JENN SHENG", "Ren Sheng Zhong")]  // Tongyong-style spelling, given-first order
    [InlineData("Hsieh Chih-Ming", "XIE ZHI MING")]       // Wade-Giles
    public void Romanization_variants_are_partial_and_disclosed(string subject, string profile) =>
        Assert.Equal((Partial, Findings.RomanizationVariant), Normalize.CompareNames([subject], [profile]));

    [Fact]
    public void Romanization_needs_every_syllable_and_never_reaches_chinese()
    {
        Assert.NotEqual(Findings.RomanizationVariant, Normalize.CompareNames(["CHUNG JEN"], ["Zhong Ren Sheng"]).Detail);
        Assert.NotEqual(Findings.RomanizationVariant, Normalize.CompareNames(["CHUNG JEN SHENG"], ["Zhong Lin Sheng"]).Detail);
        Assert.NotEqual(Findings.RomanizationVariant, Normalize.CompareNames(["CHUNG JEN SHENG"], ["鍾仁聲"]).Detail);
    }

    [Fact]
    public void Name_similarity_is_disclosed_on_partial_only()
    {
        var (status, detail) = Normalize.CompareNames(["陳志明"], ["陳志銘"]);
        Assert.Equal(Partial, status);
        Assert.Equal("NOT_IDENTICAL_AFTER_NORMALIZATION（Jaro-Winkler 0.82，Levenshtein 1）", detail);
        Assert.Equal(3, Similarity.Levenshtein("KITTEN", "SITTING"));
        Assert.Equal(0.961, Similarity.JaroWinkler("MARTHA", "MARHTA"), 3);
    }

    [Fact]
    public void Full_dob_conflict_is_row_1_review_never_no_match()
    {
        var r = IdentityDecisionPolicy.Decide(Subject("陳志明"), Profile("陳志明") with { DatesOfBirth = [new PartialDate(1981, 5, 20)] });
        Assert.Equal(ComparisonStatus.Conflict, r.Evidence[EvidenceGroup.DateOfBirth].Status);
        Assert.Null(r.HardConflict);
        Assert.Equal((1, IdentityDecision.Review), (r.Stage3Row, r.Decision));
        Assert.Equal(ReasonCodes.FullDobConflict, r.ReasonCodes[0]);

        var year = IdentityDecisionPolicy.Decide(Subject("陳志明"), Profile("陳志明") with { DatesOfBirth = [new PartialDate(1975)] });
        Assert.Equal(ReasonCodes.DobConflict, year.ReasonCodes[0]);
    }

    [Fact]
    public void One_agreeing_dob_among_several_is_support_not_conflict()
    {
        var r = IdentityDecisionPolicy.Decide(Subject("陳志明"),
            Profile("陳志明") with { DatesOfBirth = [new PartialDate(1975, 1, 1), new PartialDate(1980, 5, 20)] });
        Assert.Equal(Exact, r.Evidence[EvidenceGroup.DateOfBirth].Status);
        Assert.Equal(4, r.Stage3Row);
    }

    [Fact]
    public void Support_without_agreeing_prerequisites_is_row_5()
    {
        var r = IdentityDecisionPolicy.Decide(Subject("陳志明"), Profile("陳志銘") with { DatesOfBirth = [new PartialDate(1980)] });
        Assert.Equal((5, IdentityDecision.Review), (r.Stage3Row, r.Decision));
        Assert.Equal(ReasonCodes.PartialSupportOnly, r.ReasonCodes[0]);
    }

    [Theory]
    [InlineData("陳志明（45歲）涉嫌詐欺", 45)]
    [InlineData("負責人陳志明今年46歲。", 46)]
    [InlineData("47歲的陳志明遭起訴", 47)]
    public void Stated_age_is_read_next_to_the_name(string text, int expected) =>
        Assert.Equal(expected, StatedAgeCheck.FindStatedAge(text, ["陳志明"]));

    [Fact]
    public void Stated_age_of_someone_else_or_in_another_sentence_is_ignored()
    {
        Assert.Null(StatedAgeCheck.FindStatedAge("王姓男子（52歲）與陳志明合作", ["陳志明"]));
        Assert.Null(StatedAgeCheck.FindStatedAge("陳志明遭起訴。另一名被告45歲。", ["陳志明"]));
    }

    [Fact]
    public void Stated_age_within_two_years_is_compatible_and_never_support()
    {
        // Born 1980-05-20; on 2026-03-01 the subject is 45. Stated 47 → difference 2, compatible.
        var r = IdentityDecisionPolicy.Decide(Subject("陳志明"), Profile("陳志明"), News("陳志明（47歲）涉嫌詐欺。"));
        var age = r.Evidence[EvidenceGroup.StatedAge];
        Assert.Equal(ReasonCodes.AgeCompatibleAtReport, age.Detail);
        Assert.False(age.IsSupport);
        Assert.Equal(6, r.Stage3Row); // nothing else agrees: compatibility adds no support
        Assert.Contains("推算 45 歲", age.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Stated_age_beyond_two_years_is_a_soft_conflict_row_1_never_no_match()
    {
        var r = IdentityDecisionPolicy.Decide(Subject("陳志明"), Profile("陳志明"), News("陳志明（48歲）涉嫌詐欺。"));
        Assert.Equal(ComparisonStatus.Conflict, r.Evidence[EvidenceGroup.StatedAge].Status);
        Assert.Null(r.HardConflict);
        Assert.Equal(1, r.Stage3Row);
        Assert.Equal(IdentityDecision.Review, r.Decision);
        Assert.Equal(ReasonCodes.AgeDifferenceAtReport, r.ReasonCodes[0]);
    }

    [Fact]
    public void Stated_age_without_full_dob_is_not_comparable()
    {
        var r = IdentityDecisionPolicy.Decide(Subject("陳志明", new PartialDate(1980)), Profile("陳志明"), News("陳志明（60歲）涉嫌詐欺。"));
        Assert.Equal(Uncertain, r.Evidence[EvidenceGroup.StatedAge].Status);
        Assert.Equal(6, r.Stage3Row);
    }
}
