using Aml.Core.Identity;
using Aml.Core.Normalization;
using Aml.Core.Shared;
using Aml.Core.Subjects;

namespace Aml.Tests.Unit.Extraction;

/// <summary>Extraction and normalization rules (extraction-and-normalization.md) on minimal synthetic data.</summary>
public class ExtractionRuleTests
{
    [Theory]
    [InlineData("四十四", 44)]
    [InlineData("廿二", 22)]
    [InlineData("卅一", 31)]
    [InlineData("十", 10)]
    [InlineData("十二", 12)]
    [InlineData("一百零二", 102)]
    [InlineData("44", 44)]
    [InlineData("七八十", null)]
    [InlineData("二三", null)]
    public void Chinese_numerals_parse_to_one_number_or_none(string text, int? expected) =>
        Assert.Equal(expected, Normalize.ChineseNumber(text));

    [Theory]
    [InlineData("陳志明今年四十五歲", 45)]
    [InlineData("廿二歲的陳志明", 22)]
    [InlineData("陳志明（45足歲）", 45)]
    [InlineData("陳志明現年45周歲", 45)]
    [InlineData("Chen Chih-Ming, aged 45, was indicted", 45)]
    [InlineData("the 45-year-old Chen Chih-Ming", 45)]
    [InlineData("Chen Chih-Ming (45) was indicted", 45)]
    [InlineData("王大同、陳志明分別為44歲、45歲", 45)]
    [InlineData("陳志明與王大同分別44、45歲", 44)]
    public void Stated_ages_in_every_counted_form(string text, int expected) =>
        Assert.Equal(expected, StatedAgeCheck.FindStatedAge(text, ["陳志明", "Chen Chih-Ming"]));

    [Theory]
    [InlineData("陳志明七八十歲")]           // approximation
    [InlineData("陳志明20多歲")]             // range
    [InlineData("未滿18歲的陳志明")]          // threshold
    [InlineData("被害人平均年齡65歲，陳志明否認")] // statistic, not adjacent
    [InlineData("王大同、李四分別為44歲、45歲")]  // the Profile person is not among them
    public void Approximations_thresholds_and_other_people_are_not_stated_ages(string text) =>
        Assert.Null(StatedAgeCheck.FindStatedAge(text, ["陳志明"]));

    [Theory]
    [InlineData("未提供")]
    [InlineData("不詳")]
    [InlineData("無")]
    [InlineData("N/A")]
    [InlineData("—")]
    [InlineData("－")]
    public void Text_meaning_only_no_data_is_missing(string text) => Assert.Null(Normalize.NullIfPlaceholder(text));

    [Theory]                                // source-flag table; words in any letter case
    [InlineData("是", FlagValue.Yes)]
    [InlineData("Yes", FlagValue.Yes)]
    [InlineData("yes", FlagValue.Yes)]
    [InlineData("TRUE", FlagValue.Yes)]
    [InlineData("true", FlagValue.Yes)]
    [InlineData("Checked", FlagValue.Yes)]
    [InlineData("v", FlagValue.Yes)]
    [InlineData(" ✓ ", FlagValue.Yes)]
    [InlineData("否", FlagValue.No)]
    [InlineData("不是", FlagValue.No)]
    [InlineData("no", FlagValue.No)]
    [InlineData("False", FlagValue.No)]
    [InlineData("OFF", FlagValue.No)]
    [InlineData("0", FlagValue.No)]
    [InlineData(null, FlagValue.Missing)]
    [InlineData("", FlagValue.Missing)]
    [InlineData("–", FlagValue.Missing)]
    [InlineData("－", FlagValue.Missing)]
    [InlineData("n/a", FlagValue.Missing)]
    [InlineData("待確認", FlagValue.Unparseable)]
    [InlineData("yess", FlagValue.Unparseable)]
    public void Flag_cells_read_by_the_source_flag_table(string? raw, FlagValue expected) =>
        Assert.Equal(expected, Normalize.SourceFlag(raw));

    [Theory]                                // two rows of one Profile, merged flag by flag, in either order
    [InlineData(FlagValue.Yes, FlagValue.Yes, FlagValue.Yes)]
    [InlineData(FlagValue.Yes, FlagValue.No, FlagValue.Yes)]
    [InlineData(FlagValue.Yes, FlagValue.Unparseable, FlagValue.Yes)]
    [InlineData(FlagValue.Yes, FlagValue.Missing, FlagValue.Yes)]
    [InlineData(FlagValue.Unparseable, FlagValue.Unparseable, FlagValue.Unparseable)]
    [InlineData(FlagValue.Unparseable, FlagValue.No, FlagValue.Unparseable)]
    [InlineData(FlagValue.Unparseable, FlagValue.Missing, FlagValue.Unparseable)]
    [InlineData(FlagValue.No, FlagValue.No, FlagValue.No)]
    [InlineData(FlagValue.No, FlagValue.Missing, FlagValue.No)]
    [InlineData(FlagValue.Missing, FlagValue.Missing, FlagValue.Missing)]
    public void Merged_rows_take_yes_then_unparseable_then_no_then_missing(FlagValue a, FlagValue b, FlagValue expected)
    {
        foreach (var (x, y) in new[] { (a, b), (b, a) })
            Assert.Equal(new ListFlags(expected, expected, expected, expected), ListFlags.Merge([new(x, x, x, x), new(y, y, y, y)]));
    }

    [Fact]
    public void Merge_keeps_each_flag_separate() =>
        Assert.Equal(new ListFlags(FlagValue.Yes, FlagValue.Unparseable, FlagValue.No, FlagValue.Missing),
            ListFlags.Merge([new(FlagValue.Yes, FlagValue.No, FlagValue.Missing, FlagValue.Missing), new(FlagValue.No, FlagValue.Unparseable, FlagValue.No, FlagValue.Missing)]));

    [Theory]
    [InlineData("未提供（居住國臺灣）")]
    [InlineData("無錫市")]
    [InlineData("無業")]
    [InlineData("無國籍")]
    public void Text_with_content_is_a_value(string text) => Assert.Equal(text, Normalize.NullIfPlaceholder(text));

    [Theory]
    [InlineData("台中縣大里市中興路二段十二號五樓", "臺中市大里區中興路2段12號5樓")]
    [InlineData("桃園縣中壢市中正路１００號", "桃園市中壢區中正路100號")]
    [InlineData("高雄縣鳳山市光復路一段3號", "高雄市鳳山區光復路1段3號")]
    [InlineData("台南縣永康鄉中華路5號", "臺南市永康區中華路5號")]
    public void Merged_counties_and_chinese_numerals_normalize(string raw, string expected)
    {
        var (normalized, finding) = Normalize.TaiwanAddress(raw);
        Assert.Equal((expected, Findings.HistoricalAdminDivision), (normalized, finding));
    }

    [Fact]
    public void Road_names_with_numerals_are_left_alone()
    {
        Assert.Equal(("臺北市八德路3段12號", (string?)null), Normalize.TaiwanAddress("臺北市八德路三段十二號"));
        Assert.Equal(AddressCompare.TaiwanAddressKey("臺北市中山區中山北路二段5號"), AddressCompare.TaiwanAddressKey("台北市中山區中山北路2段５號"));
    }

    [Fact]
    public void Same_other_identifier_is_low_support_and_a_different_one_never_conflicts()
    {
        var same = EvidenceBuilder.OtherIdentifierEvidence("B123456780", [new OtherIdentifier("Reference", "B123456780")]);
        Assert.Equal((Direction.Support, Strength.Low), (same!.Direction, same.Strength));
        var other = EvidenceBuilder.OtherIdentifierEvidence("B123456780", [new OtherIdentifier("Reference", "C999999999")]);
        Assert.Equal((Direction.Neutral, ComparisonStatus.Uncertain), (other!.Direction, other.Status));
        Assert.Equal(ComparisonStatus.ProfileMissing, EvidenceBuilder.OtherIdentifierEvidence("B123456780", [])!.Status);
        Assert.Null(EvidenceBuilder.OtherIdentifierEvidence(null, []));
    }
}
