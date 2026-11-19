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
    [property: Description("模型讀出此對象涉及的事件，依機率由高到低")] IReadOnlyList<NewsModelEvent> Events,
    [property: Description("報導緊接此對象的姓名或其他寫法寫出的識別資訊，以規則讀取（同 people 的 identifiers）")] IReadOnlyList<NewsIdentifier> Identifiers);

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
    private const int MaxEntities = 50, MaxClassifications = 1200, MaxQuote = 200;
    private static readonly char[] SentenceEnds = ['。', '！', '？', '；', '!', '?', ';', '\n'];

    internal sealed record Entity(string Name, IReadOnlyList<string> Forms, string Role, int Mentions);

    public static NewsModelReadout Read(string text, IReadOnlyList<string> profileNames, INewsModel model)
    {
        var tagged = Entities(text, model.Tag(text)).ToList();
        // The Profile person is who the news is read for, and their names are known. When no tagged person has one of
        // them as a form, but exactly one has a form that holds or is held by a Profile name written in the text (the
        // tagger cut the name short or took a word too many), that person is the Profile person and the written names
        // become their forms, so the sentences with the full name are read for them too. A two-character name inside a
        // three-character one is another person (李明 in 李明華), as in Entities; with no such person, or several, nothing
        // changes. Measured with each labelled suspect in turn as the Profile person (see the feature doc).
        var written = profileNames.Where(n => n.Length > 1 && text.Contains(n, StringComparison.Ordinal)).Distinct().OrderByDescending(n => n.Length).ToList();
        if (written.Count > 0 && !tagged.Exists(e => e.Forms.Any(f => profileNames.Contains(f, StringComparer.OrdinalIgnoreCase))))
        {
            static bool Same(string a, string b) => a.Length <= b.Length ? b.Contains(a, StringComparison.Ordinal) && !(a.Length == 2 && b.Length == 3) : Same(b, a);
            var close = tagged.Where(e => !IsOrganisation(e.Role) && e.Forms.Any(f => written.Exists(n => Same(f, n)))).ToList();
            if (close.Count == 1)
                tagged[tagged.IndexOf(close[0])] = close[0] with { Name = written[0].Length > close[0].Name.Length ? written[0] : close[0].Name, Forms = [.. written.Union(close[0].Forms)] };
        }
        var entities = tagged
            .Select(e => (e, Profile: e.Forms.Any(f => profileNames.Contains(f, StringComparer.OrdinalIgnoreCase))))
            .OrderByDescending(x => x.Profile).ThenByDescending(x => x.e.Mentions).Take(MaxEntities).ToList();
        var sentences = Sentences(text);
        var asked = Asked(sentences, entities.Select(x => x.e).ToList()).Take(MaxClassifications).ToList();
        var answers = asked.Count == 0 ? [] : model.Events(text, [.. asked.Select(q => (q.Form!, q.Sentence))]);
        // Over an entity's sentences a label's probability is that at least one says it (noisy-or: 1 − Π(1 − p)); the
        // quote is the sentence most likely to. Measured on the held-out documents: higher F1 than the highest sentence alone.
        var found = entities.Select(_ => new Dictionary<(NewsEventType, NewsCertainty), (double None, double Top, string Quote)>()).ToList();
        // A whole-case sentence counts only for a suspect some sentence about them already gives an event: a suspect
        // without one (a contact known by a nickname, an accomplice tried elsewhere) is not who the case's steps are about.
        // A defendant a judgment lists needs none: the case is theirs. Measured against every annotated event: 0.807 →
        // 0.815 F1 on the dev documents, no change on the second dev set; dropping the condition for every text, or for
        // texts with few suspects, cost the second set 0.02 (events on people only mentioned).
        var defendants = Defendants(sentences, entities.Select(x => x.e).ToList());
        var anchored = new List<bool>();
        var many = asked.Where(q => q.Whole && !q.CaseOnly).Select(q => q.Entity).Distinct().Count() > ManySuspects;
        foreach (var whole in new[] { false, true })
        {
            foreach (var (q, labels) in asked.Zip(answers).Where(x => x.First.Whole == whole && (!whole || anchored[x.First.Entity])))
                foreach (var (label, p) in labels)
                    if (Label(label) is { } key && (!IsOrganisation(entities[q.Entity].e.Role) || OrganisationSteps.Contains(key.Item1))
                        && (!whole || !many || WholeCaseSteps.Contains(key.Item1))
                        && (!q.CaseOnly || WholeCaseSteps.Contains(key.Item1)))
                    {
                        var (none, top, quote) = found[q.Entity].TryGetValue(key, out var f) ? f : (1.0, -1.0, "");
                        found[q.Entity][key] = (none * (1 - p), Math.Max(top, p), p > top ? Quote(q.Sentence) : quote);
                    }
            if (!whole) anchored = found.Select((d, i) => defendants.Contains(i) || d.Values.Any(v => 1 - v.None >= EventThreshold)).ToList();
        }
        var best = found.Select(d => d.Where(x => 1 - x.Value.None >= EventThreshold)
            .ToDictionary(x => x.Key, x => (P: 1 - x.Value.None, x.Value.Quote))).ToList();
        foreach (var (i, steps) in Order(sentences, entities.Select(x => x.e).ToList(), defendants)
            .Where(x => x.Value.Contains(NewsEventType.Conviction) || x.Value.Contains(NewsEventType.Acquittal)))
            foreach (var step in new[] { NewsEventType.Fine, NewsEventType.Confiscation }.Where(s => !steps.Contains(s)))
                best[i].Remove((step, NewsCertainty.Stated));

        return new NewsModelReadout(model.Version, entities.Select((x, i) => new NewsModelEntity(x.e.Name, x.e.Forms.Where(f => f != x.e.Name).ToList(),
            IsOrganisation(x.e.Role), Party(x.e.Role), x.Profile, x.e.Mentions,
            best[i].OrderByDescending(v => v.Value.P).ThenBy(v => v.Key)
                .Select(v => new NewsModelEvent(v.Key.Item1, v.Key.Item2, Math.Round(v.Value.P, 2), v.Value.Quote)).ToList(),
            IsOrganisation(x.e.Role) ? [] : NewsIdentifiers.Read(text, x.e.Forms))).ToList());
    }

    // An organisation is investigated, searched, charged, convicted, fined, penalised or has assets restrained or confiscated;
    // it is not arrested, questioned, detained or bailed. Measured end to end on the dev documents: 0.763 → 0.772 F1.
    private static readonly HashSet<NewsEventType> OrganisationSteps =
    [
        NewsEventType.Investigation, NewsEventType.Search, NewsEventType.Indictment, NewsEventType.Conviction, NewsEventType.Fine,
        NewsEventType.RegulatoryPenalty, NewsEventType.AssetRestraint, NewsEventType.Confiscation,
    ];

    // Sentences that name no person (an organisation may be named) after one that names a person are classified for that
    // person, up to this many. Chosen on the dev documents, end to end: 0 → 0.545 F1, 1 → 0.576, 2 → 0.569, 3 → 0.567.
    private const int CarryOver = 1;

    // A text is also read, for each suspect person, in every sentence that names none of them: a whole-case step
    // (案經…移送偵辦, 警方循線查獲) is written without the name. With more suspects than this, such a sentence gives only
    // the steps of the case as a whole (investigation, referral): an unnamed search, arrest or seizure in a large case is
    // about some of them, and the text does not say which. Measured end to end against every annotated event, compared
    // with not reading such sentences in a large case: 0.785 → 0.794 F1 on the dev documents, 0.786 → 0.830 on the
    // second dev set (group cases); without the restriction to these steps the second set falls to 0.773.
    private const int ManySuspects = 5;
    private static readonly HashSet<NewsEventType> WholeCaseSteps = [NewsEventType.Investigation, NewsEventType.Referral];

    // A sentence that names one suspect for a group (王○○等5人, 等人, 等被告) states its step for the others too, so it is
    // read like a sentence naming no suspect for every suspect it does not name. Measured the same way with the rule
    // above in place: 0.794 → 0.807 F1 on the dev documents, 0.830 → 0.844 on the second dev set.
    // A sentence that names a suspect without such wording is read for the suspects it does not name too, but gives them
    // only the steps of the case as a whole: 警方將王○○移送偵辦 in a text about several suspects is often the referral of
    // all of them, while its arrest or seizure is the named person's. These questions do not count towards ManySuspects
    // and are asked last, so the limit cuts them before any other. Measured the same way on top of the rules above:
    // 0.849 → 0.851 F1 on the dev documents (four more events found, three wrong ones added), 0.877 → 0.881 on the
    // second dev set (four found, one wrong); it asks 8 % and 23 % more questions on the two sets.
    private static readonly System.Text.RegularExpressions.Regex Group = new("(?<![平同相對])等共?[0-9０-９一二兩三四五六七八九十百餘多數]*(?:人|名|位|嫌|被告)");

    // A judgment lists its defendants before 主文; the case's steps are theirs, not those of the other suspects it names
    // (accomplices tried elsewhere). A text without such a list (a press release) has no one to tell apart this way.
    private static List<int> Defendants(List<string> sentences, List<Entity> entities)
    {
        var order = sentences.FindIndex(s => string.Concat(s.Where(c => !char.IsWhiteSpace(c))) == "主文");
        return [.. Enumerable.Range(0, entities.Count).Where(i => entities[i].Role == "PER-Suspect"
            && sentences.Take(Math.Max(order, 0)).Any(s => entities[i].Forms.Any(f => s.Contains(f, StringComparison.Ordinal))))];
    }

    private static readonly (NewsEventType Step, System.Text.RegularExpressions.Regex Words)[] OrderSteps =
    [
        (NewsEventType.Conviction, new("處(?:有期徒刑|拘役|罰金|無期徒刑|死刑)|免刑")),
        (NewsEventType.Fine, new("(?:併科|處|(?<!易)科)[^，。；、]{0,20}?(?<!易科)罰金")),
        (NewsEventType.Confiscation, new("(?<!不予)(?<!不)(?<!毋庸)(?<!無庸)沒收|追徵")),
        (NewsEventType.Acquittal, new("無罪")),
    ];
    private static readonly System.Text.RegularExpressions.Regex OrderEnd = new("^(?:犯罪)?(?:事實|理由).{0,12}$");

    // A judgment's order (主文 up to the 事實 / 理由 heading) is written to a fixed form, so it is read by rule: the steps
    // it states, per listed defendant; a sentence of the order naming no defendant goes on about those of the sentence
    // before. A defendant the order sentences or acquits has the fine and the confiscation the order gives them and no
    // other: the reasons discuss fines and confiscation of the co-defendants and of the law in general, and the event
    // model, asked such a sentence for every defendant, gives them to all. Checked against the training labels: of 383
    // defendants an order sentences or acquits, the labels hold one fine and nine confiscations (earlier cases) the order
    // does not state, and none the other way round. Measured end to end: 0.851 → 0.854 F1 on the dev documents (five
    // wrong events removed), 0.881 → 0.884 on the second dev set (three removed); no event lost on either.
    private static Dictionary<int, HashSet<NewsEventType>> Order(List<string> sentences, List<Entity> entities, List<int> defendants)
    {
        var stated = new Dictionary<int, HashSet<NewsEventType>>();
        static string Compact(string s) => string.Concat(s.Where(c => !char.IsWhiteSpace(c)));
        var start = sentences.FindIndex(s => Compact(s) == "主文");
        var end = start < 0 ? -1 : sentences.FindIndex(start + 1, s => OrderEnd.IsMatch(Compact(s)));
        List<int> last = [];
        for (var k = start + 1; k < end; k++)
        {
            var s = sentences[k];
            var named = defendants.Where(i => entities[i].Forms.Any(f => s.Contains(f, StringComparison.Ordinal))).ToList();
            if (named.Count > 0) last = named;
            foreach (var (step, words) in OrderSteps)
                if (words.IsMatch(s))
                    foreach (var i in last)
                        (stated.TryGetValue(i, out var set) ? set : stated[i] = []).Add(step);
        }
        return stated;
    }

    // The classifications to run, in this order (the limit cuts the last): each sentence holding a form of an entity, with
    // the form written there, and each of the next CarryOver sentences naming nobody, for the person named last (a judgment
    // goes on about 被告 without the name); then each sentence, for every suspect it does not name, by name: for all its
    // steps when it names no suspect or names one for a group, and after all of those, otherwise (CaseOnly) for the
    // whole-case steps alone.
    private static IEnumerable<(int Entity, string Form, string Sentence, bool Whole, bool CaseOnly)> Asked(List<string> sentences, List<Entity> entities)
    {
        (int Entity, string Form)? last = null;
        var since = 0;
        var suspects = Enumerable.Range(0, entities.Count).Where(i => entities[i].Role == "PER-Suspect").ToList();
        var unnamed = new List<(int Entity, string Form, string Sentence, bool Whole, bool CaseOnly)>();
        if (Defendants(sentences, entities) is { Count: > 0 } defendants) suspects = defendants;
        foreach (var s in sentences)
        {
            int? carried = null;
            var named = entities.Select((e, i) => (i, Form: e.Forms.FirstOrDefault(f => s.Contains(f, StringComparison.Ordinal)), e.Role))
                .Where(x => x.Form is not null).ToList();
            foreach (var (i, form, _) in named) yield return (i, form!, s, false, false);
            // The person named last in the sentence's text, with the form written there.
            var person = named.Where(x => !IsOrganisation(x.Role))
                .SelectMany(x => entities[x.i].Forms.Where(f => s.Contains(f, StringComparison.Ordinal))
                    .Select(f => (x.i, Form: f, At: s.LastIndexOf(f, StringComparison.Ordinal) + f.Length)))
                .OrderByDescending(x => x.At).ThenByDescending(x => x.Form.Length).FirstOrDefault();
            // Only a suspect is carried over: after a lawyer, an official or a victim the next sentence is not about them.
            if (person.Form is not null) { last = entities[person.i].Role == "PER-Suspect" ? (person.i, person.Form) : null; since = 0; }
            else if (last is { } l && since++ < CarryOver) { carried = l.Entity; yield return (l.Entity, l.Form, s, false, false); }
            var caseOnly = named.Exists(x => x.Role == "PER-Suspect") && !Group.IsMatch(s);
            unnamed.AddRange(suspects.Where(i => i != carried && !named.Exists(x => x.i == i)).Select(i => (i, entities[i].Name, s, true, caseOnly)));
        }
        foreach (var q in unnamed.OrderBy(q => q.CaseOnly)) yield return q; // stable: the limit cuts the case-only questions first
    }

    internal static IReadOnlyList<Entity> Entities(string text, IReadOnlyList<TaggedSpan> spans)
    {
        var found = spans.Where(s => s.Length > 1 && s.Start >= 0 && s.Start + s.Length <= text.Length)
            .Select(s => (Name: Clean(text.Substring(s.Start, s.Length), IsOrganisation(s.Role)), s.Role))
            .Where(s => s.Name.Length > 1 && !(IsOrganisation(s.Role) && s.Name.EndsWith('戶')))
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
            // A longer span holds a name of three characters or more (王大明等); a two-character piece of it (莫伊 in 阿莫伊斯達)
            // is not its name.
            var related = wellFormed.Where(o => o.Contains(n, StringComparison.Ordinal) || (o.Length >= 3 && n.Contains(o, StringComparison.Ordinal)))
                .Select(o => canonical[o]!).Distinct(StringComparer.Ordinal).ToList();
            canonical[n] = related.Count switch { 0 => n, 1 => related[0], _ => null };
        }
        // A name of another shape (a transliterated or indigenous name, a lettered party, a Latin name) is anchored by
        // itself: a shorter span inside exactly one such longer name is its form, unless it is a well-formed name inside a
        // well-formed one (kept apart above).
        foreach (var n in people.Where(n => canonical[n] == n).OrderBy(n => n.Length))
        {
            var longer = people.Where(o => o.Length > n.Length && !wellFormed.Contains(o) && canonical[o] == o && o.Contains(n, StringComparison.Ordinal)).ToList();
            if (longer.Count == 1) canonical[n] = longer[0];
        }
        foreach (var n in names.Where(n => kind[n])) canonical[n] = Organisation(n, names.Where(o => kind[o]).ToList());
        // A bare role word the tagger took for a name (被告, 告訴人) is no one by itself: it is the one person of that party
        // when the text has exactly one, else dropped. Measured end to end: dev 0.579 → 0.601, test 0.604 → 0.621 (dropping
        // it always: 0.588, 0.611).
        foreach (var n in people.Where(n => BareRoles.TryGetValue(n, out _)))
        {
            var party = BareRoles[n];
            var only = people.Where(o => canonical[o] == o && !BareRoles.ContainsKey(o)
                    && found.Where(s => s.Name == o).GroupBy(s => s.Role).OrderByDescending(r => r.Count()).First().Key == party)
                .ToList();
            canonical[n] = only.Count == 1 ? only[0] : null;
        }

        // The role is the majority over all mentions of all forms.
        return found.Where(s => canonical[s.Name] is not null)
            .GroupBy(s => canonical[s.Name]!, StringComparer.Ordinal)
            .Select(g => new Entity(g.Key, g.Select(s => s.Name).Distinct(StringComparer.Ordinal).ToList(),
                g.GroupBy(s => s.Role, StringComparer.Ordinal).OrderByDescending(r => r.Count()).First().Key, g.Count()))
            .ToList();
    }

    // Quote marks and brackets the tagger took in at either end are not the name (「王大明」); nor is a role word before a
    // person's name (被告王大明), when two characters or more remain.
    private static string Clean(string name, bool organisation)
    {
        name = name.Trim().Trim(Enclosing).Trim();
        if (organisation) return name;
        foreach (var role in RoleWords)
            if (name.Length - role.Length >= 2 && name.StartsWith(role, StringComparison.Ordinal)) return name[role.Length..];
        return name;
    }

    private static readonly char[] Enclosing = ['「', '」', '『', '』', '“', '”', '"', '\'', '(', ')', '（', '）', '〈', '〉', '《', '》', '[', ']', '【', '】'];
    private static readonly Dictionary<string, string> BareRoles = new(StringComparer.Ordinal)
    {
        ["被告"] = "PER-Suspect",
        ["被告人"] = "PER-Suspect",
        ["共同被告"] = "PER-Suspect",
        ["同案被告"] = "PER-Suspect",
        ["嫌犯"] = "PER-Suspect",
        ["犯嫌"] = "PER-Suspect",
        ["嫌疑人"] = "PER-Suspect",
        ["上訴人"] = "PER-Suspect",
        ["受刑人"] = "PER-Suspect",
        ["抗告人"] = "PER-Suspect",
        ["再抗告人"] = "PER-Suspect",
        ["被上訴人"] = "PER-Suspect",
        ["告訴人"] = "PER-Victim",
        ["被害人"] = "PER-Victim",
    };

    private static readonly string[] RoleWords = ["共同被告", "被告人", "上訴人", "被害人", "告訴人", "嫌疑人", "被告", "嫌犯", "犯嫌", "證人"];

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
