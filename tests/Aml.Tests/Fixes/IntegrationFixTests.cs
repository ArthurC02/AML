using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aml.Core.Cases;
using Aml.Core.Identity;
using Aml.Core.Screening;
using Aml.Core.Shared;
using Aml.Core.Subjects;
using Aml.Infra.Txt;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;

namespace Aml.Tests.Fixes;

/// <summary>Fixes that span work packages: Is RCA? = NO, raw-byte source hashes, unmapped nationality, Profile hash wiring.</summary>
public sealed class IntegrationFixTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"aml-integration-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;

    public IntegrationFixTests() =>
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.UseSetting("Database:ConnectionString", $"Data Source={_file};Pooling=False"));

    public void Dispose()
    {
        _factory.Dispose();
        SqliteConnection.ClearAllPools();
        File.Delete(_file);
    }

    private static SourceProfile Profile(string category, params string[] citizenship) =>
        new("DJ:1", Gender.Male, ["陳志明"], [], citizenship, [], [new SourceCategory(category, "Spouse")], [], [], [], null, [], []);

    private static ScreenedSubject Subject(IReadOnlyList<string> nationalities, params string[] findings) =>
        new(SubjectRole.Customer, null, ["陳志明"], Gender.Male, new PartialDate(1980, 5, 20), nationalities, null, null, null, null, [], findings);

    [Fact]
    public void An_rca_category_with_is_rca_no_stays_for_review_with_a_conflict()
    {
        var no = SourceConditions.Determine(Profile("Relative or Close Associate (RCA)", "TW"), false, null, ListFlags.None with { Rca = FlagValue.No });
        var rca = Assert.Single(no, c => c.Type == ListType.Rca);
        Assert.Equal(ConditionStatus.Unknown, rca.Status);
        Assert.Contains(SourceConditions.FlagCategoryConflict, rca.ReasonCodes);

        var blank = SourceConditions.Determine(Profile("Relative or Close Associate (RCA)", "TW"), false, null, ListFlags.None);
        Assert.Equal(ConditionStatus.Hit, Assert.Single(blank, c => c.Type == ListType.Rca).Status);
    }

    [Fact]
    public void An_active_wanted_list_entry_does_not_confirm_a_sanction_measure()
    {
        SourceCondition Sanction(string list) => SourceConditions.Determine(
            Profile("Sanctions Lists", "TW") with { OfficialList = new OfficialListEntry("MJIB", list, "Active", null) }, false, null)
            .Single(c => c.Type == ListType.Sanction);

        Assert.Equal(ConditionStatus.Hit, Sanction("SDN List").Status);
        var wanted = Sanction("Wanted List");
        Assert.Equal(ConditionStatus.Unknown, wanted.Status);
        Assert.Contains(SourceConditions.SanctionMeasureNotConfirmed, wanted.ReasonCodes);
    }

    [Fact]
    public void Source_hashes_are_taken_on_the_uploaded_bytes()
    {
        var files = Directory.GetFiles(GoldenCases.GoldenCaseTests.Folder("GC-11")).ToDictionary(f => Path.GetFileName(f), File.ReadAllBytes);
        Dictionary<string, byte[]> Endings(string newline) => files.ToDictionary(f => f.Key, f => f.Key.EndsWith(".txt", StringComparison.Ordinal)
            ? Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(f.Value).ReplaceLineEndings(newline)) : f.Value);

        var (lfFiles, crlfFiles) = (Endings("\n"), Endings("\r\n"));
        var lf = TxtCaseReader.ReadFiles(lfFiles).SourceHashes!;
        var crlf = TxtCaseReader.ReadFiles(crlfFiles).SourceHashes!;
        Assert.All(lfFiles, f => Assert.Equal(Convert.ToHexString(SHA256.HashData(f.Value)), lf[f.Key]));
        Assert.NotEqual(lf["案件資訊.txt"], crlf["案件資訊.txt"]);
    }

    [Fact]
    public void An_unreadable_role_date_is_unparseable_and_a_future_end_is_an_open_term()
    {
        var text = File.ReadAllText(Path.Combine(GoldenCases.GoldenCaseTests.Folder("GC-01"), "名單資料_90000001.txt"));
        var bad = TxtCaseReader.ParseProfile(text.Replace("\t1990/7\t1995/12", "\t1990/13\t1995/12", StringComparison.Ordinal));
        Assert.Contains("UNPARSEABLE:職位", bad.Findings);

        var future = TxtCaseReader.ParseProfile(text.Replace("\t1990/7\t1995/12", $"\t1990/7\t{DateTime.UtcNow.Year + 2}/12", StringComparison.Ordinal));
        Assert.DoesNotContain("UNPARSEABLE:職位", future.Findings);
        Assert.Null(future.Roles[0].To);
    }

    [Fact]
    public void A_full_width_profile_row_number_is_read()
    {
        var text = File.ReadAllText(Path.Combine(GoldenCases.GoldenCaseTests.Folder("GC-01"), "名單資料_90000001.txt"));
        var p = TxtCaseReader.ParseProfile(text.Replace("1\t董事長\t", "１\t董事長\t", StringComparison.Ordinal));
        Assert.Contains(p.Roles, r => r.Title == "董事長");
    }

    [Fact]
    public void An_unmapped_nationality_keeps_the_foreign_pep_presumption_off()
    {
        var pep = new SourceCondition(ListType.Pep, ConditionStatus.Hit, ConditionBasis.CategoryMapping, [], LegalStage.Unknown, null,
            SourceConditions.PolicyVersion, []);
        var identity = new IdentityResult(new Dictionary<EvidenceGroup, Evidence>(), null, 6, IdentityDecision.Review, []);
        var profile = Profile("Politically Exposed Person", "VN");

        Assert.Equal(Recommendation.OnList, DispositionPolicy.Recommend(Subject(["VN"]), profile, [pep], identity, false).Recommendation);
        var ambiguous = DispositionPolicy.Recommend(Subject(["VN"], "AMBIGUOUS:國籍 2"), profile, [pep], identity, false);
        Assert.DoesNotContain(DispositionPolicy.ForeignPepPresumption, ambiguous.ReasonCodes);
    }

    [Fact]
    public async Task An_html_page_and_its_txt_export_are_one_profile_version_through_the_api()
    {
        var ct = TestContext.Current.CancellationToken;
        var http = _factory.CreateClient();
        static MultipartFormDataContent Form(string name, string content)
        {
            var form = new MultipartFormDataContent();
            form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(content)), "files", name);
            return form;
        }

        await http.PostAsync("/api/lists/profiles", Form("名單資料_123.txt", "Profile_ID:\t123\nGender:\tMale\n"), ct);
        var html = await http.PostAsync("/api/lists/profiles", Form("名單資料_123.html",
            "<html><body><table><tr><td>Profile_ID</td><td>123</td></tr><tr><td>Gender</td><td>Male</td></tr></table></body></html>"), ct);
        Assert.Equal(HttpStatusCode.OK, html.StatusCode);
        var report = await html.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.Equal(1, report.GetProperty("profiles")[0].GetProperty("version").GetInt32());

        var merges = await http.GetFromJsonAsync<JsonElement>("/api/lists/merges", ct);
        Assert.Equal(JsonValueKind.Array, merges.GetProperty("rejected").ValueKind);
    }
}
