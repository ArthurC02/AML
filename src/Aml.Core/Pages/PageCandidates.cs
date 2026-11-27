using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aml.Core.Cases;
using Aml.Core.Normalization;
using Aml.Core.Retrieval;
using Aml.Core.Shared;
using Aml.Core.Subjects;

namespace Aml.Core.Pages;

/// <summary>How the screened subject's names compared with one person of a page, and whether that made the person a candidate.</summary>
public sealed record PagePersonScreening(
    [property: Description("網頁人物鍵（WEB:<pageId>-<n>）")] string ProfileKey,
    [property: Description("網頁所寫的姓名")][property: Sensitivity(Sensitivity.Personal)] string Name,
    [property: Description("誰讀出此人：RULE_READ（規則）或 MODEL_READ（只有新聞模型）")] PersonReadBy ReadBy,
    [property: Description("受檢對象姓名與此人姓名寫法的比對：EXACT 才成為候選人")] ComparisonStatus NameStatus,
    [property: Description("比對細節，見 /api/codes；遮蔽的姓名為 REDACTED_NAME")] string? NameDetail,
    [property: Description("檢索方式：SOURCE_PAGE（案件指名的收到網頁）")] RetrievalMode Mode,
    [property: Description("是否成為候選人（此人是一個 hit）：姓名 EXACT 時為 true")] bool Candidate);

/// <summary>One page a revision screened the subject against: every person it names, candidate or not.</summary>
public sealed record PageScreening(
    [property: Description("網頁編號")] string PageId,
    [property: Description("網頁上的每一個人與比對結果；不是候選人的人也列出，不會靜默略過")] IReadOnlyList<PagePersonScreening> Persons);

/// <summary>
/// A person read from a received page as a source record matched like a list Profile (external-sources.md#common-path):
/// a candidate when one of its written names is EXACT against the subject's (the identity pipeline's own name comparison),
/// mapped to a <see cref="SourceProfile"/> that carries only names, and to the page's text as the attached news, so the
/// age, birth date and gender stated next to the name are compared exactly as for attached news.
/// </summary>
public static class PageCandidates
{
    public const string RedactedName = "REDACTED_NAME";

    /// <summary>Profile finding of a person only the news models found: the name was read by a model, not by the rules.</summary>
    public const string ModelRead = "MODEL_READ";

    // Masking characters of published judgments and some reports (王○明): the name key drops them, so 王○明 would read as 王明.
    private static readonly char[] MaskCharacters = ['○', '◯'];

    public static PageScreening Screen(ScreenedSubject subject, PageRecord page) =>
        new(page.PageId, [.. (page.Persons ?? []).Select(p => Screen(subject, p))]);

    private static PagePersonScreening Screen(ScreenedSubject subject, PagePerson p)
    {
        var names = Names(p);
        var (status, detail) = names.Count == 0 ? (ComparisonStatus.ProfileMissing, RedactedName) : NameComparison.CompareNames(subject.Names, names);
        return new(p.ProfileKey, p.Name, p.ReadBy, status, detail, RetrievalMode.SourcePage, status == ComparisonStatus.Exact);
    }

    /// <summary>The person as a source record: its unmasked written names, the full name first; nothing else is a field of the page.</summary>
    public static SourceProfile Profile(PagePerson p) =>
        new(p.ProfileKey, Gender: null, Names(p), DatesOfBirth: [], Citizenship: [], AddressCountries: [], Categories: [], Keywords: [], Roles: [],
            Relationships: [], OfficialList: null, Findings: p.ReadBy == PersonReadBy.ModelRead ? [ModelRead] : [], Sources: []);

    /// <summary>
    /// The page as the person's attached text: its URL, title, host, the publication date the page states as the report date
    /// (null when MISSING, never the fetch date) and the fetch date as the retrieval date.
    /// </summary>
    public static NewsItem News(PageRecord page, PagePerson p) =>
        new(ProfileKeys.IdOf(p.ProfileKey), page.PageId, page.Title ?? "", Uri.TryCreate(page.Url, UriKind.Absolute, out var u) ? u.Host : null, page.PublishedOn,
            page.Text ?? "", page.Url, page.FetchedAt?.UtcDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>The page and person behind a WEB: key of this input; null for any other key.</summary>
    public static (PageRecord Page, PagePerson Person)? Find(CaseInput input, string profileKey) =>
        !ProfileKeys.IsWeb(profileKey) ? null
        : (input.Pages ?? []).SelectMany(page => (page.Persons ?? []).Select(p => (page, p))).FirstOrDefault(x => x.p.ProfileKey == profileKey) is ({ } page, { } person)
            ? (page, person) : null;

    private static List<string> Names(PagePerson p) =>
        [.. new[] { p.Name }.Concat(p.Forms).Where(f => f.Length > 0 && f.IndexOfAny(MaskCharacters) < 0).Distinct(StringComparer.Ordinal)];
}

/// <summary>
/// The page version a Case revision used, kept with the Case's input files (Runtime Query half, never on the page record):
/// one file per page, 網頁_&lt;pageId&gt;.json, holding the page record as read. A re-analysis of the stored input reads the same
/// text and persons; a page fetched again is used only when a new analysis names it again.
/// </summary>
public static class PageSnapshots
{
    private const string Prefix = "網頁_", Extension = ".json";

    public static string FileName(string pageId) => Prefix + pageId + Extension;

    public static bool IsFile(string name) =>
        Path.GetFileName(name) is var n && n.StartsWith(Prefix, StringComparison.Ordinal) && n.EndsWith(Extension, StringComparison.OrdinalIgnoreCase);

    public static string Write(PageRecord page) => JsonSerializer.Serialize(page, CaseJson.Options);

    public static PageRecord Read(string json) =>
        JsonSerializer.Deserialize<PageRecord>(json, CaseJson.Options) is { Status: PageStatus.Read, Text: not null } page
            ? page : throw new JsonException("Not a page read with its text");

    /// <summary>SHA-256 of the main text (UTF-8), upper-case hex: the version of the text, whatever else of the body changed.</summary>
    public static string TextHash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
