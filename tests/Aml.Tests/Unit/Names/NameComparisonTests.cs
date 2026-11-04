using Aml.Core.Normalization;
using Aml.Core.Shared;
using static Aml.Core.Shared.ComparisonStatus;

namespace Aml.Tests.Unit.Names;

/// <summary>Name keys, comparison relaxations, romanization, variant folding, similarity and reading keys.</summary>
public class NameComparisonTests
{
    private static (ComparisonStatus Status, string? Detail) Compare(string subject, string profile) =>
        Normalize.CompareNames([subject], [profile]);

    [Theory]                                // supplementary-plane and private-use characters stay in the key
    [InlineData("黃𥕢明", "黃明")]
    [InlineData("陳𡔙明", "陳𠀀明")]
    [InlineData("陳明", "陳明")]
    public void Rare_characters_are_kept_per_code_point(string subject, string profile) =>
        Assert.NotEqual(Exact, Compare(subject, profile).Status);

    [Theory]                                // an unknown character is never EXACT, not even against itself
    [InlineData("黃?明")]
    [InlineData("黃＊明")]
    [InlineData("黃�明")]
    public void Unknown_characters_are_never_exact(string name)
    {
        Assert.NotEqual(Exact, Compare(name, name).Status);
        Assert.NotEqual(Exact, Compare(name, "黃明").Status);
    }

    [Fact]
    public void Name_key_keeps_supplementary_characters_and_marks_unknown_ones()
    {
        Assert.Equal("黃𥕢明", Normalize.NameKey("黃𥕢明"));
        Assert.Equal("黃?明", Normalize.NameKey("黃＊明"));
    }

    [Theory]                                // diacritic folding
    [InlineData("Nguyễn Văn Hùng", "Nguyễn Văn Hưng")]
    [InlineData("José", "Jose")]
    public void Match_only_after_diacritic_folding_is_disclosed(string subject, string profile)
    {
        Assert.Equal((Exact, Normalize.DiacriticVariant), Compare(subject, profile));
        Assert.Equal((Exact, (string?)null), Compare(subject, subject));
    }

    [Theory]                                // name order
    [InlineData("CHIH-MING　CHEN", "CHEN CHIH-MING")]
    [InlineData("穆罕默德·阿里", "阿里·穆罕默德")]
    [InlineData("Ming Chen", "Chen Ming")]
    public void Match_only_after_reordering_is_disclosed(string subject, string profile) =>
        Assert.Equal((Exact, Normalize.NameOrderVariant), Compare(subject, profile));

    [Fact]
    public void Source_order_match_has_no_order_finding()
    {
        Assert.Equal((Exact, (string?)null), Compare("Chen Ming", "CHEN  MING"));
        Assert.Equal((Exact, $"{Findings.VariantCharacters}；{Normalize.NameOrderVariant}"), Compare("志明 陈", "陳 志明"));
    }

    [Theory]                                // romanization
    [InlineData("Chen Zhiming", "Chen Chih-Ming")]
    [InlineData("Ouyang Xiu", "Ou-Yang Hsiu")]
    [InlineData("Chiang Ching-kuo", "Jiang Jingguo")]
    [InlineData("Lee Ming-Hui", "Li Minghui")]
    [InlineData("Hsueh Liou", "Syue Liu")]
    public void Romanization_segments_syllables_across_systems(string subject, string profile) =>
        Assert.Equal((Partial, Findings.RomanizationVariant), Compare(subject, profile));

    [Fact]
    public void Different_taiwanese_syllables_are_not_a_romanization_variant() =>
        Assert.NotEqual(Findings.RomanizationVariant, Compare("Chen Chih-Ming", "Chen Chi-Ming").Detail);

    [Fact]                                  // variant characters
    public void Opencc_one_to_one_characters_fold()
    {
        Assert.Equal((Exact, Findings.VariantCharacters), Compare("蒋经国", "蔣經國"));
        Assert.Equal(Partial, Compare("后志明", "後志明").Status);
        Assert.Equal(Partial, Compare("余志明", "餘志明").Status);
        Assert.Equal(Partial, Compare("涂志明", "塗志明").Status);
    }

    [Fact]                                  // similarity
    public void Cross_script_names_are_not_compared()
    {
        Assert.Equal((Partial, Normalize.NotComparableCrossScript), Compare("陳志明", "CHEN Chih-Ming"));
        Assert.StartsWith("NOT_IDENTICAL_AFTER_NORMALIZATION", Normalize.CompareNames(["陳志明", "CHEN Chih-Ming"], ["陳志銘"]).Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Prefix_bonus_needs_jaro_above_threshold()
    {
        Assert.Equal(0.5, Similarity.JaroWinkler("ABCDEFGH", "ABXYZWVU"), 6);   // Jaro 0.5, shared prefix AB ignored
        Assert.Equal(0.961, Similarity.JaroWinkler("MARTHA", "MARHTA"), 3);
    }

    [Fact]                                  // reading keys
    public void Reading_keys_meet_across_scripts()
    {
        var han = Romanization.ReadingKeys("陳水扁");
        Assert.Contains("CHENSHUIBIAN", han);
        Assert.Contains("CHENSHUIBIAN", Romanization.ReadingKeys("Chen Shui-bian"));
        Assert.NotEmpty(han.Intersect(Romanization.ReadingKeys("Shui-bian Chen")));
        Assert.Contains("ZENGGUOFAN", Romanization.ReadingKeys("曾國藩"));    // polyphone expanded
        Assert.InRange(Romanization.ReadingKeys("單單單單單").Count, 1, Romanization.MaxReadingKeys);
        Assert.Empty(Romanization.ReadingKeys("黃?明"));
        Assert.Empty(Romanization.ReadingKeys("Smith-Jones"));
    }
}
