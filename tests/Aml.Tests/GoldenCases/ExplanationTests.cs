using Aml.Core.Cases;
using Aml.Core.Explanation;
using Aml.Core.Shared;
using Aml.Infra.Txt;
using static Aml.Core.Shared.ComparisonStatus;

namespace Aml.Tests.GoldenCases;

/// <summary>The explanation restates recorded facts per Golden Case; it never adds or hides one.</summary>
public class ExplanationTests
{
    private static HitExplanation Hit(string gc, int index = 0)
    {
        var input = TxtCaseReader.Read(GoldenCaseTests.Folder(gc));
        return ExplanationBuilder.Build(input, CaseAnalyzer.Analyze(input)).Hits[index];
    }

    private static readonly string[] FixedOrder = ["中文姓名", "英文姓名／拼音", "國籍", "出生日期", "性別", "證件", "地址", "職業／雇主", "其他識別碼"];

    private static ComparisonRow Row(HitExplanation h, string field) => h.Comparison.Single(r => r.Field == field);

    [Fact]
    public void Comparison_table_has_the_fixed_order_in_every_case()
    {
        foreach (var gc in new[] { "GC-01", "GC-02", "GC-03", "GC-11", "GC-12" })
            Assert.Equal(FixedOrder, Hit(gc).Comparison.Select(r => r.Field));
    }

    [Fact]
    public void GC11_shows_year_only_agreement_variant_names_and_the_gap()
    {
        var h = Hit("GC-11");

        Assert.Equal(1, h.HitNumber);
        Assert.Equal(["鐘仁聲", "钟仁声"], h.MatchedNames);
        Assert.Equal(Exact, Row(h, "中文姓名").Status);
        Assert.Contains("鍾任聲", Row(h, "中文姓名").ProfileValue, StringComparison.Ordinal);
        Assert.Equal(SubjectMissing, Row(h, "其他識別碼").Status);

        var dob = Row(h, "出生日期");
        Assert.Equal("1973-06-12", dob.SubjectValue);
        Assert.Equal("1973、1972", dob.ProfileValue);
        Assert.Equal(Partial, dob.Status);
        Assert.Equal(ReasonCodes.BirthYearMatchFullDateNotComparable, dob.Difference);

        // The document number is masked.
        Assert.Equal("TW_NATIONAL_ID A12*****89", Row(h, "證件").SubjectValue);
        Assert.Contains(Findings.HistoricalAdminDivision, h.DataQualityFindings);
        Assert.Contains("DATE_OF_BIRTH（LOW", h.Summary.KeyEvidenceOrGaps, StringComparison.Ordinal);
        Assert.Contains("雙方沒有可共同比對的直接識別資料", h.Summary.KeyEvidenceOrGaps, StringComparison.Ordinal);
        Assert.Contains("PROBABLE_MATCH（第三階段第 4 列）", h.Summary.Conclusion, StringComparison.Ordinal);
        Assert.Contains("對外結果 REVIEW", h.Summary.Conclusion, StringComparison.Ordinal);
    }

    [Fact]
    public void GC12_shows_related_party_no_support_and_reference_number_not_a_document()
    {
        var h = Hit("GC-12");

        Assert.Equal("關聯人（帳戶持有人的法定代理人） 陳志明", h.ScreenedSubject);
        Assert.Empty(h.Support);
        Assert.Equal(SubjectMissing, Row(h, "性別").Status);
        Assert.Equal(ProfileMissing, Row(h, "出生日期").Status);
        Assert.Equal("參考號碼 B12*****80", Row(h, "其他識別碼").SubjectValue);
        Assert.Equal("NOT_A_DOCUMENT", Row(h, "其他識別碼").Difference);
        Assert.Contains("受僱於 Heng Yuan Life Industry Company Limited", Row(h, "職業／雇主").ProfileValue, StringComparison.Ordinal);
        Assert.Contains(Findings.PostalCodeOnly, h.DataQualityFindings);
        Assert.Contains(Findings.DuplicatedAddressLine, h.DataQualityFindings);
        Assert.Contains("沒有任何佐證", h.Summary.KeyEvidenceOrGaps, StringComparison.Ordinal);
        Assert.Contains("ADVERSE_MEDIA（FINANCIAL_CRIME、TAX_CRIME；法律階段 INDICTED）", h.Summary.ExternalData, StringComparison.Ordinal);
        Assert.Contains("REVIEW（第三階段第 6 列）", h.Summary.Conclusion, StringComparison.Ordinal);

        Assert.Collection(h.Sources,
            s =>
            {
                Assert.Equal("DJ:90000012#source-1", s.Id);
                Assert.Equal("Liberty Times (Chinese - Traditional)", s.Name);
                Assert.Equal("2026-09-06", s.PublishedOn);
                Assert.Equal("2026-09-10", s.RetrievedOn);
                Assert.StartsWith("https://global.factiva.com/", s.Url, StringComparison.Ordinal);
                Assert.Equal("PROFILE_RECORD", s.UsedFor);
            },
            s =>
            {
                Assert.Equal("LIBTOT0020260905EXAMPLE12", s.Id);
                Assert.StartsWith("自由時報 (繁體)：虛構銷售額", s.Name, StringComparison.Ordinal);
                Assert.Equal("2026-09-06", s.PublishedOn);
                Assert.Equal("LEGAL_STAGE", s.UsedFor);
                Assert.True(s.AttachedToProfile);
            });
        // No identifier from the source appears unmasked.
        Assert.DoesNotContain("B123456780", System.Text.Json.JsonSerializer.Serialize(h), StringComparison.Ordinal);
    }

    [Fact]
    public void GC01_shows_the_hard_conflict_and_the_dated_role()
    {
        var h = Hit("GC-01");

        Assert.Equal(ReasonCodes.ImpossibleAgeAtReportedEvent, h.HardConflict);
        Assert.Null(h.Stage3Row);
        Assert.Empty(h.Sources); // GC-01 cites no source and has no attached news
        Assert.Contains("董事長，宏遠投資股份有限公司（1990-07 起）", Row(h, "職業／雇主").ProfileValue, StringComparison.Ordinal);
        Assert.Contains("硬衝突 IMPOSSIBLE_AGE_AT_REPORTED_EVENT", h.Summary.KeyEvidenceOrGaps, StringComparison.Ordinal);
        Assert.Contains("NO_MATCH（第一階段硬衝突", h.Summary.Conclusion, StringComparison.Ordinal);
    }

    [Fact]
    public void GC03_shows_name_match_after_diacritic_folding_and_address_as_soft_difference()
    {
        var h = Hit("GC-03");

        Assert.Equal(Exact, Row(h, "英文姓名／拼音").Status);
        Assert.Equal(SubjectMissing, Row(h, "中文姓名").Status);
        var address = Row(h, "地址");
        Assert.Equal(Uncertain, address.Status);
        Assert.Equal("VN", address.ProfileValue);
        Assert.Equal("SOFT_DIFFERENCE", address.Difference);
        Assert.Contains("OFFICIAL_WANTED_LIST（法律階段 WANTED）", h.Summary.ExternalData, StringComparison.Ordinal);
    }
}
