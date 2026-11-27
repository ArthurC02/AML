using Aml.Core.Explanation;
using Aml.Core.Identity;
using Aml.Core.Ports;
using Aml.Core.Screening;
using Aml.Core.Screening.Stage;
using Aml.Core.Shared;
using Aml.Core.Subjects;

namespace Aml.Core.Pages;

public enum PageStatus { Pending, Read, Refused, Failed }

/// <summary>A page received by URL: its state, the fetch outcome and, once READ, its text and the persons it names.</summary>
public sealed record PageRecord(
    string PageId,
    string Url,
    PageStatus Status,
    DateTimeOffset RequestedAt,
    string? JobId,
    string? BatchId,
    string? Problem = null,
    string? FinalUrl = null,
    string? MediaType = null,
    string? Title = null,
    DateTimeOffset? FetchedAt = null,
    string? ContentHash = null,
    string? Text = null,
    IReadOnlyList<PagePerson>? Persons = null,
    IReadOnlyList<PageEvent>? OtherEvents = null,
    NewsModelReadout? Model = null,
    PartialDate? PublishedOn = null,
    PublicationSource? PublishedOnSource = null);

/// <summary>
/// Where a page's publication date was read: its structured metadata (JSON-LD, meta, &lt;time&gt;), a label, date line or dateline
/// of its text, or nowhere (MISSING: none stated, the sources disagree, or the date is after the fetch). UPDATE_DATE: the page
/// states no publication date, and this is the update date it states (更新日期, article:modified_time) in its place. Never the fetch time.
/// </summary>
public enum PublicationSource { Metadata, Text, Missing, UpdateDate }

/// <summary>What a fetch gave: the main text and the publication date it states, or the problem that stopped it and whether asking again later may help.</summary>
public sealed record PageFetch(string? FinalUrl, string? MediaType, string? ContentHash, string? Title, string? Text, string? Problem = null, bool Transient = false,
    PartialDate? PublishedOn = null, PublicationSource PublishedOnSource = PublicationSource.Missing);

/// <summary>The persons a page names, the events no listed person is the actor of, and the news models' reading (null without them).</summary>
public sealed record PageReading(IReadOnlyList<PagePerson> Persons, IReadOnlyList<PageEvent> OtherEvents, NewsModelReadout? Model);

/// <summary>
/// One person the page names, found by the rules of the news digest or by the news models (<see cref="ReadBy"/>);
/// <c>profileKey</c> is WEB:&lt;pageId&gt;-&lt;n&gt;. Every
/// value is as stated on the page next to the name; nothing here compares it with anyone.
/// </summary>
public sealed record PagePerson(
    string ProfileKey,
    string Name,
    IReadOnlyList<string> Forms,
    IReadOnlyList<string> Roles,
    NewsParty? Party,
    IReadOnlyList<PageMention> Mentions,
    IReadOnlyList<PageIdentifier> Identifiers,
    int? StatedAge,
    IReadOnlyList<PartialDate> StatedBirthDates,
    Gender? StatedGender,
    LegalStage Stage,
    string? StageBasis,
    IReadOnlyList<StageReaderResult>? StageReaders,
    IReadOnlyList<string> StageReasonCodes,
    IReadOnlyList<PageEvent> Events,
    PersonReadBy ReadBy);

/// <summary>Who found the person on the page: the digest's rules (RULE_READ), or only the news models (MODEL_READ).</summary>
public enum PersonReadBy { RuleRead, ModelRead }

/// <summary>Where a name stands in the page's text (character offset and length).</summary>
public sealed record PageMention(int At, int Length);

/// <summary>An identifier written next to the name (an ID number masked), and the offset of its text nearest a mention; null when none is found.</summary>
public sealed record PageIdentifier(NewsIdentifierKind Kind, string? Text, string? Number, int? At);

/// <summary>An event of the news digest and the offset of its sentence (null when not found).</summary>
public sealed record PageEvent(NewsEvent Event, int? At);

/// <summary>
/// Reads a page's text as the Case path reads attached news (external-sources.md#common-path): the news digest's rules
/// find every person and event, the news models (when configured) add the persons the rules miss (MODEL_READ), the stated
/// age by <see cref="StatedAgeCheck"/>, birth dates and gender from the identifiers next to a name, and each person's legal
/// stage by the rules, voted with the stage models when any are configured. Nothing is decided here.
/// </summary>
public static class PageReadings
{
    private const int MaxOccurrences = 1000;

    /// <summary>The earliest year a received page's publication date may have; an earlier one is taken as misread.</summary>
    public const int EarliestPublication = 1990;

    public static PageReading Read(string pageId, string text, INewsModel? model, IReadOnlyList<IStageReader> stageReaders)
    {
        var (digest, mentions) = NewsDigests.Read(new NewsItem(pageId, null, "", null, null, text), [], "", model);
        var persons = new List<PagePerson>();
        PagePerson Person(string name, IReadOnlyList<(int At, int Length)> spans, IReadOnlyList<string> roles, NewsParty? party,
            IReadOnlyList<NewsIdentifier> identifiers, IReadOnlyList<PageEvent> events, PersonReadBy readBy)
        {
            var forms = spans.Select(m => text.Substring(m.At, m.Length)).Distinct(StringComparer.Ordinal).ToList();
            var (stage, basis, readers, codes) = Stage(text, forms, stageReaders);
            return new PagePerson($"WEB:{pageId}-{persons.Count + 1}", name, forms, roles, party, [.. spans.Select(m => new PageMention(m.At, m.Length))],
                [.. identifiers.Select(id => new PageIdentifier(id.Kind, id.Text, id.Number, Locate(text, id.Text, spans)))],
                StatedAgeCheck.FindStatedAge(text, forms),
                [.. Stated(identifiers, NewsIdentifierKind.BirthDate).Select(NewsStatedCheck.Parse).OfType<PartialDate>().Distinct()],
                Stated(identifiers, NewsIdentifierKind.Gender).Select(t => t[0]).Where(c => c is '男' or '女').Distinct().ToList() is [var g] ? (g == '男' ? Gender.Male : Gender.Female) : null,
                stage, basis, readers, codes, events, readBy);
        }
        foreach (var (p, spans) in digest.People.Zip(mentions))
            persons.Add(Person(p.Name, spans, p.Roles, p.Party, p.Identifiers, [.. digest.Events.Where(e => e.Actor == p.Name).Select(e => Located(text, e))], PersonReadBy.RuleRead));
        // A person the models found and the rules did not (no mention of theirs touches a rule person's) is a person of the
        // page too, read by the models: its forms where written, its party as the model reads it, no role word, and its
        // events in the models' reading; its stated values and stage are read by the rules as for any person.
        // Entities with longer forms claim their places first, so a short name written inside another entity's longer name
        // (陳明 in 陳明華) is skipped there rather than taking that place and dropping the other person; persons keep the models' order.
        var taken = mentions.SelectMany(s => s).ToList();
        var entities = (digest.Model?.Entities ?? []).Where(e => !e.Organisation).ToList();
        var spansOf = new List<(int At, int Length)>[entities.Count];
        var modelTaken = new List<(int At, int Length)>();
        foreach (var i in Enumerable.Range(0, entities.Count).OrderByDescending(i => entities[i].OtherForms.Append(entities[i].Name).Max(f => f.Length)))
            modelTaken.AddRange(spansOf[i] = Occurrences(text, [entities[i].Name, .. entities[i].OtherForms], taken, modelTaken));
        for (var i = 0; i < entities.Count; i++)
            if (spansOf[i].Count > 0)
                persons.Add(Person(entities[i].Name, spansOf[i], [], entities[i].Party, entities[i].Identifiers, [], PersonReadBy.ModelRead));
        var named = persons.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        return new PageReading(persons, [.. digest.Events.Where(e => e.Actor is null || !named.Contains(e.Actor)).Select(e => Located(text, e))], digest.Model);
    }

    // Every place a form is written, longest form first, none overlapping another, a place another model entity took
    // (skipped) or a rule person's place; none at all when any of them overlaps a rule person's place (the person the rules read there).
    private static List<(int At, int Length)> Occurrences(string text, IEnumerable<string> forms, List<(int At, int Length)> taken, List<(int At, int Length)> skip)
    {
        static bool Overlaps((int At, int Length) a, (int At, int Length) b) => a.At < b.At + b.Length && b.At < a.At + a.Length;
        var found = new List<(int At, int Length)>();
        foreach (var form in forms.Where(f => f.Length > 0).Distinct(StringComparer.Ordinal).OrderByDescending(f => f.Length))
        {
            var at = text.IndexOf(form, StringComparison.Ordinal);
            for (var k = 0; at >= 0 && k < MaxOccurrences; k++, at = text.IndexOf(form, at + form.Length, StringComparison.Ordinal))
            {
                if (taken.Exists(t => Overlaps(t, (at, form.Length)))) return [];
                if (skip.Exists(t => Overlaps(t, (at, form.Length)))) continue;
                if (!found.Exists(f => Overlaps(f, (at, form.Length)))) found.Add((at, form.Length));
            }
        }
        return [.. found.OrderBy(f => f.At)];
    }

    /// <summary>
    /// The publication date a fetch read, kept only when it is not after the fetch nor before <see cref="EarliestPublication"/>:
    /// either is invalid, so MISSING (a misread old date would shift every stated age). The fetch time only bounds it; it is
    /// never the date. The bound is the latest calendar date anywhere at the fetch instant (UTC+14), so a page dated in its
    /// own time zone ahead of UTC is not refused.
    /// </summary>
    public static (PartialDate? On, PublicationSource Source) Published(PageFetch fetched, DateTimeOffset fetchedAt) =>
        fetched is { PublishedOn: { } on, PublishedOnSource: not PublicationSource.Missing }
        && on.Year >= EarliestPublication
        && new DateOnly(on.Year, on.Month ?? 1, on.Day ?? 1) <= DateOnly.FromDateTime(fetchedAt.UtcDateTime.AddHours(14))
            ? (on, fetched.PublishedOnSource) : (null, PublicationSource.Missing);

    /// <summary>A configured stage model's answer is missing for some person (STAGE_READER_NO_ANSWER): a job retries before writing.</summary>
    public static bool StageAnswerMissing(PageReading reading) => reading.Persons.Any(p => p.StageReasonCodes.Contains(StageVote.ReaderNoAnswer));

    /// <summary>The summary view: who the page names and their stage, without the text, identifiers, stated values, events or quotes.</summary>
    public static PageRecord Summary(PageRecord page) => page with
    {
        Text = null,
        Model = null,
        OtherEvents = page.OtherEvents is null ? null : [],
        Persons = page.Persons?.Select(p => p with { Identifiers = [], StatedAge = null, StatedBirthDates = [], StatedGender = null, StageReaders = null, Events = [] }).ToList(),
    };

    // The same reading the source condition gives attached news (SourceConditions.AdverseMedia), with the person's forms as the names.
    private static (LegalStage, string?, IReadOnlyList<StageReaderResult>?, IReadOnlyList<string>) Stage(string text, IReadOnlyList<string> forms, IReadOnlyList<IStageReader> models)
    {
        var rules = LegalStageReading.FromNews(text, forms);
        if (models.Count == 0) return (rules.Stage, rules.Basis, null, []);
        var ((stage, basis), disagree, noAnswer, readers) = StageVote.Read(rules, text, forms, models);
        return (stage, basis, readers, [.. new[] { disagree ? StageVote.ReadersDisagree : null, noAnswer ? StageVote.ReaderNoAnswer : null }.OfType<string>()]);
    }

    private static IEnumerable<string> Stated(IReadOnlyList<NewsIdentifier> identifiers, NewsIdentifierKind kind) =>
        identifiers.Where(i => i.Kind == kind && !string.IsNullOrEmpty(i.Text)).Select(i => i.Text!);

    // The occurrence of a value nearest a mention of the person: the digest reads it next to the name.
    // ponytail: the first MaxOccurrences occurrences only (a gender 男 in a long text); an offset from the reader would be exact.
    private static int? Locate(string text, string? value, IReadOnlyList<(int At, int Length)> spans)
    {
        if (string.IsNullOrEmpty(value)) return null;
        (int? At, int Distance) best = (null, int.MaxValue);
        var at = text.IndexOf(value, StringComparison.Ordinal);
        for (var n = 0; at >= 0 && n < MaxOccurrences; n++, at = text.IndexOf(value, at + 1, StringComparison.Ordinal))
            foreach (var m in spans)
                if (Math.Abs(at - m.At) < best.Distance) best = (at, Math.Abs(at - m.At));
        return best.At;
    }

    private static PageEvent Located(string text, NewsEvent e) =>
        new(e, text.IndexOf(e.Quote.TrimEnd('…'), StringComparison.Ordinal) is var at and >= 0 ? at : null);
}
