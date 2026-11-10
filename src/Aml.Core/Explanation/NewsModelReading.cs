using System.ComponentModel;
using Aml.Core.Normalization;
using Aml.Core.Ports;
using Aml.Core.Screening;
using Aml.Core.Shared;

namespace Aml.Core.Explanation;

public sealed record NewsModelEvent(
    [property: Description("事件類型，見 /api/codes")] NewsEventType Type,
    [property: Description("STATED、NEGATED 或 REQUESTED（模型不讀 HEDGED）")] NewsCertainty Certainty,
    [property: Description("至少一句說了此事件的模型機率（各句合併：1 − Π(1 − p)，取兩位小數）；列出的事件都至少 0.3")] double Probability,
    [property: Description("單句機率最高的句子原文，最多 200 字")][property: Sensitivity(Sensitivity.Personal)] string Quote);

public sealed record NewsModelEntity(
    [property: Description("姓名或機構名；同一對象有多種寫法時取完整姓名")][property: Sensitivity(Sensitivity.Personal)] string Name,
    [property: Description("同一對象在報導中的其他寫法（如只寫名、名字後接「等」）")][property: Sensitivity(Sensitivity.Personal)] IReadOnlyList<string> OtherForms,
    [property: Description("true：機構；false：個人")] bool Organisation,
    [property: Description("模型判斷的身分：SUSPECT、VICTIM、OFFICIAL；其他或看不出時為 null")] NewsParty? Party,
    [property: Description("姓名或其他寫法與名單人物的姓名相同")] bool ProfileName,
    [property: Description("出現次數")] int Mentions,
    [property: Description("模型讀出此對象涉及的事件，依機率由高到低")] IReadOnlyList<NewsModelEvent> Events);

// What the small models read (ADR-0012, ADR-0015). It restates the news for the reviewer and feeds no decision.
public sealed record NewsModelReadout(
    [property: Description("模型版本：人物模型與事件模型的檔案雜湊")] string Version,
    [property: Description("模型找到的人物與機構；名單人物在前，其餘依出現次數")] IReadOnlyList<NewsModelEntity> Entities);

/// <summary>
/// Reads one news text with the small models and corrects the tagger's known errors by fixed rules
/// (watchlist-and-adverse-information.md#news-models): one-character fragments and accounts are dropped; a fragment or an
/// over-long span holding exactly one well-formed name is that name's form, and so is a short organisation name inside
/// exactly one longer one. Events are classified per entity and sentence, as the event model was trained.
/// </summary>
public static class NewsModelReading
{
    public const double EventThreshold = 0.3;
    private const int MaxEntities = 50, MaxClassifications = 300, MaxQuote = 200;
    private static readonly char[] SentenceEnds = ['。', '！', '？', '；', '!', '?', ';', '\n'];

    internal sealed record Entity(string Name, IReadOnlyList<string> Forms, string Role, int Mentions);

    public static NewsModelReadout Read(string text, IReadOnlyList<string> profileNames, INewsModel model)
    {
        var entities = Entities(text, model.Tag(text))
            .Select(e => (e, Profile: e.Forms.Any(f => profileNames.Contains(f, StringComparer.OrdinalIgnoreCase))))
            .OrderByDescending(x => x.Profile).ThenByDescending(x => x.e.Mentions).Take(MaxEntities).ToList();
        var asked = Asked(Sentences(text), entities.Select(x => x.e).ToList()).Take(MaxClassifications).ToList();
        var answers = asked.Count == 0 ? [] : model.Events([.. asked.Select(q => (q.Form!, q.Sentence))]);
        // Over an entity's sentences a label's probability is that at least one says it (noisy-or: 1 − Π(1 − p)); the
        // quote is the sentence most likely to. Measured on the held-out documents: higher F1 than the highest sentence alone.
        var found = entities.Select(_ => new Dictionary<(NewsEventType, NewsCertainty), (double None, double Top, string Quote)>()).ToList();
        foreach (var (q, labels) in asked.Zip(answers))
            foreach (var (label, p) in labels)
                if (Label(label) is { } key)
                {
                    var (none, top, quote) = found[q.Entity].TryGetValue(key, out var f) ? f : (1.0, -1.0, "");
                    found[q.Entity][key] = (none * (1 - p), Math.Max(top, p), p > top ? Quote(q.Sentence) : quote);
                }
        var best = found.Select(d => d.Where(x => 1 - x.Value.None >= EventThreshold)
            .ToDictionary(x => x.Key, x => (P: 1 - x.Value.None, x.Value.Quote))).ToList();

        return new NewsModelReadout(model.Version, entities.Select((x, i) => new NewsModelEntity(x.e.Name, x.e.Forms.Where(f => f != x.e.Name).ToList(),
            IsOrganisation(x.e.Role), Party(x.e.Role), x.Profile, x.e.Mentions,
            best[i].OrderByDescending(v => v.Value.P).ThenBy(v => v.Key)
                .Select(v => new NewsModelEvent(v.Key.Item1, v.Key.Item2, Math.Round(v.Value.P, 2), v.Value.Quote)).ToList())).ToList());
    }

    // Sentences that name no person (an organisation may be named) after one that names a person are classified for that
    // person, up to this many. Chosen on the dev documents, end to end: 0 → 0.545 F1, 1 → 0.576, 2 → 0.569, 3 → 0.567.
    private const int CarryOver = 1;

    // The classifications to run: each sentence holding a form of an entity, with the form written there; and each of the
    // next CarryOver sentences naming nobody, for the person named last (a judgment goes on about 被告 without the name).
    private static IEnumerable<(int Entity, string Form, string Sentence)> Asked(List<string> sentences, List<Entity> entities)
    {
        (int Entity, string Form)? last = null;
        var since = 0;
        foreach (var s in sentences)
        {
            var named = entities.Select((e, i) => (i, Form: e.Forms.FirstOrDefault(f => s.Contains(f, StringComparison.Ordinal)), e.Role))
                .Where(x => x.Form is not null).ToList();
            foreach (var (i, form, _) in named) yield return (i, form!, s);
            // The person named last in the sentence's text, with the form written there.
            var person = named.Where(x => !IsOrganisation(x.Role))
                .SelectMany(x => entities[x.i].Forms.Where(f => s.Contains(f, StringComparison.Ordinal))
                    .Select(f => (x.i, Form: f, At: s.LastIndexOf(f, StringComparison.Ordinal) + f.Length)))
                .OrderByDescending(x => x.At).ThenByDescending(x => x.Form.Length).FirstOrDefault();
            if (person.Form is not null) { last = (person.i, person.Form); since = 0; }
            else if (last is { } l && since++ < CarryOver) yield return (l.Entity, l.Form, s);
        }
    }

    internal static IReadOnlyList<Entity> Entities(string text, IReadOnlyList<TaggedSpan> spans)
    {
        var found = spans.Where(s => s.Length > 1 && s.Start >= 0 && s.Start + s.Length <= text.Length)
            .Select(s => (Name: text.Substring(s.Start, s.Length), s.Role))
            .Where(s => !(IsOrganisation(s.Role) && s.Name.EndsWith('戶')))
            .ToList();
        var names = found.Select(s => s.Name).Distinct(StringComparer.Ordinal).ToList();
        var kind = names.ToDictionary(n => n, n => IsOrganisation(found.First(s => s.Name == n).Role), StringComparer.Ordinal);

        // Each name maps to the entity it is a form of; null drops it. A well-formed name holding exactly one other
        // (王大明等 holding 王大明) is its form; a fragment or longer span goes to the one entity its well-formed names map to.
        var people = names.Where(n => !kind[n]).ToList();
        var wellFormed = people.Where(WellFormed).ToList();
        var canonical = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var n in wellFormed)
        {
            // A two-character name inside a three-character one is usually a different reading (王明 in 王明華), not the name.
            var inside = wellFormed.Where(o => o.Length is >= 3 && o.Length < n.Length && n.Contains(o, StringComparison.Ordinal)).ToList();
            canonical[n] = inside.Count == 1 ? inside[0] : n;
        }
        foreach (var n in people.Except(wellFormed))
        {
            var related = wellFormed.Where(o => o.Contains(n, StringComparison.Ordinal) || n.Contains(o, StringComparison.Ordinal))
                .Select(o => canonical[o]!).Distinct(StringComparer.Ordinal).ToList();
            canonical[n] = related.Count switch { 0 => n, 1 => related[0], _ => null };
        }
        foreach (var n in names.Where(n => kind[n])) canonical[n] = Organisation(n, names.Where(o => kind[o]).ToList());

        // The role is the majority over all mentions of all forms.
        return found.Where(s => canonical[s.Name] is not null)
            .GroupBy(s => canonical[s.Name]!, StringComparer.Ordinal)
            .Select(g => new Entity(g.Key, g.Select(s => s.Name).Distinct(StringComparer.Ordinal).ToList(),
                g.GroupBy(s => s.Role, StringComparer.Ordinal).OrderByDescending(r => r.Count()).First().Key, g.Count()))
            .ToList();
    }

    // At most 200 UTF-16 units, never ending inside a surrogate pair.
    private static string Quote(string s) =>
        s.Length <= MaxQuote ? s : s[..(char.IsHighSurrogate(s[MaxQuote - 1]) ? MaxQuote - 1 : MaxQuote)];

    // As the training data was cut: up to and including 。！？；!?; or a line break.
    internal static List<string> Sentences(string text)
    {
        var list = new List<string>();
        for (var start = 0; start < text.Length;)
        {
            var end = text.IndexOfAny(SentenceEnds, start);
            end = end < 0 ? text.Length : end + 1;
            var s = text[start..end].Trim();
            if (s.Length > 0) list.Add(s);
            start = end;
        }
        return list;
    }

    // Model labels: a type, a type with -Negated or -Requested, or "prior" (a prior record).
    private static (NewsEventType, NewsCertainty)? Label(string label)
    {
        if (label == "prior") return (NewsEventType.PriorRecord, NewsCertainty.Stated);
        var dash = label.IndexOf('-', StringComparison.Ordinal);
        var certainty = dash < 0 ? NewsCertainty.Stated : Enum.TryParse<NewsCertainty>(label[(dash + 1)..], out var c) ? c : (NewsCertainty?)null;
        return certainty is { } k && Enum.TryParse<NewsEventType>(dash < 0 ? label : label[..dash], out var t) ? (t, k) : null;
    }

    private static bool IsOrganisation(string role) => role.StartsWith("ORG-", StringComparison.Ordinal);

    private static NewsParty? Party(string role) => role[(role.IndexOf('-', StringComparison.Ordinal) + 1)..] switch
    {
        "Suspect" => NewsParty.Suspect,
        "Victim" => NewsParty.Victim,
        "Official" => NewsParty.Official,
        _ => null,
    };

    // A short organisation name inside exactly one longer one is its abbreviation.
    private static string Organisation(string n, List<string> organisations)
    {
        var longer = organisations.Where(o => o.Length > n.Length && o.Contains(n, StringComparison.Ordinal)).ToList();
        return longer.Count == 1 ? longer[0] : n;
    }

    // A Chinese personal name: two to four CJK characters opening with a common surname.
    private static bool WellFormed(string n) =>
        n.Length is >= 2 and <= 4 && n.All(CjkText.IsCjk) && SourceConditions.CommonSurnames.Contains(n[0], StringComparison.Ordinal);
}
