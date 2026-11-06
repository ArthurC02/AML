using Aml.Core.Cases;
using Aml.Core.Subjects;
using Aml.Core.Codes;
using Aml.Core.Screening;
using Aml.Infra.Txt;
using Aml.Services;

namespace Aml.Tests.GoldenCases;

/// <summary>Every code the Golden Cases emit is in the catalog with a name and a description; augmented cases check theirs in Augment.</summary>
public class CodeCatalogTests
{
    [Fact]
    public void Every_job_error_code_is_in_the_catalog()
    {
        string[] codes = [BatchService.InvalidInput, BatchService.InvalidPayload, BatchService.Timeout, BatchService.LeaseExpired,
            BatchService.ReleaseLimit, BatchService.ExceptionCode(new InvalidOperationException())];
        Assert.All(codes, c => Assert.Equal("JOB_ERROR", CodeCatalog.Find(c)?.Kind));
    }

    [Fact]
    public void Every_source_condition_value_is_in_the_catalog()
    {
        Enum[] values = [.. Enum.GetValues<ListType>().Cast<Enum>(), .. Enum.GetValues<ConditionStatus>().Cast<Enum>(),
            .. Enum.GetValues<ConditionBasis>().Cast<Enum>(), .. Enum.GetValues<LegalStage>().Cast<Enum>()];
        Assert.All(values, v => Assert.NotNull(CodeCatalog.Find(System.Text.Json.JsonSerializer.Serialize(v, v.GetType(), CaseJson.Options).Trim('"'))));
    }

    internal static void AssertKnown(CaseInput input, CaseAnalysis a)
    {
        var missing = EmittedCodes.Of(input, a).Where(c => !CodeCatalog.All.ContainsKey(c)).ToList();
        Assert.True(missing.Count == 0, "Not in CodeCatalog: " + string.Join(", ", missing));
    }

    [Theory]
    [InlineData("GC-01")]
    [InlineData("GC-02")]
    [InlineData("GC-03")]
    [InlineData("GC-11")]
    [InlineData("GC-12")]
    public void Golden_case_codes_are_in_the_catalog(string gc)
    {
        var input = TxtCaseReader.Read(GoldenCaseTests.Folder(gc));
        AssertKnown(input, CaseAnalyzer.Analyze(input));
    }

    [Fact]
    public void Every_entry_has_a_name_a_description_and_its_own_key() =>
        Assert.All(CodeCatalog.All, kv =>
        {
            Assert.Equal(kv.Key, kv.Value.Code);
            Assert.False(string.IsNullOrWhiteSpace(kv.Value.Name) || string.IsNullOrWhiteSpace(kv.Value.Description), kv.Key);
        });

    [Fact]
    public void Every_entry_points_at_an_existing_doc_section()
    {
        var root = Path.GetFullPath(Path.Combine(GoldenCaseTests.Folder("GC-01"), "..", "..", ".."));
        // GitHub heading anchors: lower case, punctuation dropped, spaces to hyphens.
        static string Slug(string heading) => string.Concat(heading.Trim().ToLowerInvariant()
            .Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_')).Replace(' ', '-');
        Assert.All(CodeCatalog.All.Values.Select(e => e.Doc).Distinct(), doc =>
        {
            var (file, anchor) = (doc.Split('#')[0], doc.Split('#')[1]);
            var headings = File.ReadLines(Path.Combine(root, file)).Where(l => l.StartsWith('#')).Select(l => Slug(l.TrimStart('#')));
            Assert.Contains(anchor, headings);
        });
    }
}
