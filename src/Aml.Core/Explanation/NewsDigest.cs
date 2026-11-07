using System.ComponentModel;
using System.Text.RegularExpressions;
using Aml.Core.Normalization;
using Aml.Core.Screening;
using Aml.Core.Shared;
using Aml.Core.Subjects;
using static Aml.Core.Cases.CaseJson;

namespace Aml.Core.Explanation;

public enum NewsEventType { Investigation, Search, Questioning, Arrest, Detention, Bail, Indictment, NonIndictment, Conviction, Acquittal, Fine, WantedNotice }

public enum NewsCertainty { Stated, Negated, Hedged }

public enum NewsMatch { ProfileName, ProfileSurname, OtherPerson }

public sealed record NewsPerson(
    [property: Description("報導中的姓名或匿稱（如 王姓男子、王男）")][property: Sensitivity(Sensitivity.Personal)] string Name,
    [property: Description("PROFILE_NAME：與名單姓名相同；PROFILE_SURNAME：與名單人物同姓的匿稱；OTHER_PERSON：其他人")] NewsMatch Match,
    [property: Description("報導中緊接在姓名前的身分或職稱，依出現順序")] IReadOnlyList<string> Roles,
    [property: Description("出現次數")] int Mentions);

public sealed record NewsEvent(
    [property: Description("事件類型，見 /api/codes")] NewsEventType Type,
    [property: Description("STATED：報導陳述；NEGATED：報導否定（尚未、沒有）；HEDGED：推測、請求或條件（恐、擬、聲請）")] NewsCertainty Certainty,
    [property: Description("事件當事人；報導未指明時為 null")][property: Sensitivity(Sensitivity.Personal)] string? Actor,
    [property: Description("當事人與名單人物的關係；未指明當事人時為 null")] NewsMatch? ActorMatch,
    [property: Description("日期原文（相對日期不換算）")] string? Date,
    [property: Description("機關：地檢署、法院、調查局等")] string? Agency,
    [property: Description("同句提到的罪名或違法類型")] IReadOnlyList<string> Offences,
    [property: Description("金額原文")] string? Amount,
    [property: Description("刑度原文，只用於判決有罪")] string? Penalty,
    [property: Description("事件所在句子的原文，最多 200 字")][property: Sensitivity(Sensitivity.Personal)] string Quote);

// Restates what the attached news says, by fixed rules; it adds no finding and decides nothing (ADR-0028).
public sealed record NewsDigest(
    [property: Description("新聞在來源中的引用編號（S#）")] string SourceRef,
    [property: Description("報導中的人物；名單人物在前")] IReadOnlyList<NewsPerson> People,
    [property: Description("報導中的事件，依出現順序")] IReadOnlyList<NewsEvent> Events,
    [property: Description("由人物與事件依固定句型組成的摘要；每句都來自上列人物與事件")][property: Sensitivity(Sensitivity.Personal)] IReadOnlyList<string> Summary);

/// <summary>
/// Reads people, their roles and the legal events of one attached news item by rules (watchlist-and-adverse-information.md#news-digest).
/// Linear in the text: names, triggers and slots are found by fixed patterns, sentence by sentence and clause by clause.
/// </summary>
public static partial class NewsDigests
{
    private const string ProfileKey = "#profile";
    private const int MaxMentions = 2000, MaxPerName = 500, MaxFound = 50_000, MaxPeople = 50, MaxEvents = 100, MaxTriggers = 1000,
        MaxQuote = 200, MaxPhrases = 8, MaxOffences = 5;

    private sealed record Mention(int At, int Length, string Key, string Name, NewsMatch Match, string? Role);

    public static NewsDigest? Build(NewsItem? news, IReadOnlyList<string> profileNames, string sourceRef)
    {
        if (news is null) return null;
        var text = news.Text;
        var names = profileNames.Take(100).Where(n => n.Length > 1).ToList();
        var surnames = names.Where(n => CjkText.IsCjk(n[0])).Select(n => n[0]).ToHashSet();
        var mentions = Mentions(text, names, surnames);
        // Quoted speech is a claim: no event is read inside it (as for the legal stage), but names are.
        var blanked = SourceConditions.Quoted().Replace(text, m => new string('　', m.Length));
        var people = People(mentions);
        // An event names its person as the people list does (王男 and 王姓男子 are one person, shown by the first).
        var shownAs = mentions.GroupBy(m => m.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Name, StringComparer.Ordinal);
        var events = Events(text, blanked, mentions, shownAs);
        return new NewsDigest(sourceRef, people, events, Summary(news, sourceRef, people, events));
    }

    // ---- People ------------------------------------------------------------------------------------------------

    // Who the news names: the Profile's own names; a name right after a role (被告王大明, 董事長李四); a 2 or 3 character
    // name of a common surname opening a clause before 遭, 被, 涉嫌 …; and the anonymised forms 王姓男子, 王男, 王嫌.
    // Every other occurrence of a name found so is a mention too.
    private static List<Mention> Mentions(string text, List<string> names, HashSet<char> surnames)
    {
        var found = new List<Mention>();
        var shown = names.FirstOrDefault(n => text.Contains(n, StringComparison.OrdinalIgnoreCase));
        foreach (var name in names.OrderByDescending(n => n.Length))
            AddAll(found, text, name, ProfileKey, shown ?? name, NewsMatch.ProfileName);

        var others = new List<string>();
        foreach (Match m in RoleThenName().Matches(text).Take(MaxMentions))
            if (TrimName(m.Groups["name"].Value) is { } name) others.Add(name);
        foreach (Match m in ClauseOpeningName().Matches(text).Take(MaxMentions))
            if (TrimName(m.Groups["name"].Value) is { } name) others.Add(name);
        foreach (var name in others.Distinct(StringComparer.Ordinal).Take(MaxPeople))
        {
            // Only the Profile's own name is theirs: 王明 is not 王明華.
            var profile = names.Contains(name, StringComparer.OrdinalIgnoreCase);
            AddAll(found, text, name, profile ? ProfileKey : name, profile ? shown ?? name : name, profile ? NewsMatch.ProfileName : NewsMatch.OtherPerson);
        }

        foreach (Match m in Anonymised().Matches(text))
        {
            if (found.Count >= MaxFound) break;
            var surname = m.Groups["surname"].Value[0];
            if (!SourceConditions.CommonSurnames.Contains(surname, StringComparison.Ordinal) || !m.Groups["named"].Success && !StandsAlone(text, m)) continue;
            found.Add(new(m.Index, m.Length, "~" + surname, m.Value, surnames.Contains(surname) ? NewsMatch.ProfileSurname : NewsMatch.OtherPerson, null));
        }

        // One mention per place, the longest; then the role written just before it.
        var kept = new List<Mention>();
        var end = 0;
        foreach (var m in found.OrderBy(m => m.At).ThenByDescending(m => m.Length))
        {
            if (m.At < end) continue;
            kept.Add(m with { Role = RoleBefore(text, m.At) });
            end = m.At + m.Length;
            if (kept.Count == MaxMentions) break;
        }
        return kept;
    }

    // Each name at most MaxPerName times, so one name cannot crowd out the others. A 2 character name followed by a
    // character that does not follow a name is part of a longer one (王明 in 王明華) and is not a mention.
    private static void AddAll(List<Mention> found, string text, string name, string key, string shown, NewsMatch match)
    {
        var added = 0;
        for (var at = text.IndexOf(name, StringComparison.OrdinalIgnoreCase); at >= 0 && added < MaxPerName;
             at = text.IndexOf(name, at + name.Length, StringComparison.OrdinalIgnoreCase))
        {
            var next = at + name.Length;
            if (name.Length == 2 && next < text.Length && CjkText.IsCjk(text[next]) && !AfterName.Contains(text[next], StringComparison.Ordinal)
                && !Roles.Any(r => text.AsSpan(next).StartsWith(r, StringComparison.Ordinal))) continue;
            found.Add(new(at, name.Length, key, shown, match, null));
            added++;
        }
    }

    // 被告王明涉嫌 reads 王明涉 as a name: a third character that usually follows a name ends it at two. The first must be a
    // common surname, the second not one that makes a word of it (許多, 王男, 高層), and the third not an organisation's
    // (林務局).
    // ponytail: fixed character lists; a 3 character name ending in one of them (陳志於) is cut to two, one ending in an
    // organisation character (林志局) is dropped.
    private static string? TrimName(string name)
    {
        if (name.Length == 3 && AfterName.Contains(name[2], StringComparison.Ordinal)) name = name[..2];
        if (name.Length == 3 && Organisation.Contains(name[2], StringComparison.Ordinal)) return null;
        return SourceConditions.CommonSurnames.Contains(name[0], StringComparison.Ordinal) && !NotGivenName.Contains(name[1], StringComparison.Ordinal) ? name : null;
    }

    private const string AfterName = "涉遭被於在與和及等因已則也亦向將稱表說為以今昨日前後是並經對到把從坦供否辯指曾仍再又均都一";
    private const string NotGivenName = "多姓男女嫌翁婦某家氏方達等們層務官司局會署院校部處所隊組系廠業界商";
    private const string Organisation = "局會司署院校部處所隊組廠行社團";

    // 王男 and 王嫌 are names only standing alone: not inside a word (主張男女), followed by what follows a name.
    private static bool StandsAlone(string text, Match m)
    {
        var before = m.Index > 0 ? text[m.Index - 1] : ' ';
        var after = m.Index + m.Length < text.Length ? text[m.Index + m.Length] : ' ';
        return (!CjkText.IsCjk(before) || "與和及將由對向讓名的".Contains(before, StringComparison.Ordinal))
            && (!CjkText.IsCjk(after) || AfterName.Contains(after, StringComparison.Ordinal));
    }

    private static string? RoleBefore(string text, int at)
    {
        var before = text.AsSpan(0, at);
        foreach (var role in Roles)
            if (before.EndsWith(role, StringComparison.Ordinal))
                return before.Length > role.Length && before[^(role.Length + 1)] == '前' ? "前" + role : role;
        return null;
    }

    // Longest first: 主任檢察官 before 檢察官, 共同被告 before 被告.
    private static readonly string[] Roles =
    [
        "主任檢察官", "共同被告", "立法委員", "副董事長", "嫌疑人", "檢察官", "審判長", "辯護人", "董事長", "總經理", "負責人", "執行長",
        "監察人", "被害人", "告訴人", "業務員", "被告", "嫌犯", "犯嫌", "法官", "律師", "董事", "會計", "立委", "議員", "市長", "縣長",
        "鄉長", "里長", "部長", "局長", "處長", "署長", "院長", "主任", "警員", "員警", "警官", "證人", "理專", "經理", "主管",
    ];

    private static readonly Regex RoleThenNamePattern = new($"(?:{string.Join('|', Roles)})(?<name>\\p{{IsCJKUnifiedIdeographs}}{{2,3}})");

    private static Regex RoleThenName() => RoleThenNamePattern;

    [GeneratedRegex(@"(?<=^|[，,。！？；\n、「」『』]|而|但|則|另外|隨後|其後|後來|至於)(?<name>\p{IsCJKUnifiedIdeographs}{2,3}?)(?:也|亦|則|已|均)?(?=遭到|遭|被|獲|涉嫌|涉及|坦承|供稱|否認)", RegexOptions.Multiline)]
    private static partial Regex ClauseOpeningName();

    [GeneratedRegex(@"(?<surname>\p{IsCJKUnifiedIdeographs})(?:(?<named>姓)(?:男子|女子|嫌犯|被告|婦人|老翁|少年|負責人|商人|男|女)|男|女|嫌|翁|婦|某)")]
    private static partial Regex Anonymised();

    private static List<NewsPerson> People(List<Mention> mentions) =>
        mentions.GroupBy(m => m.Key, StringComparer.Ordinal)
            .OrderBy(g => g.Key == ProfileKey ? 0 : g.First().Match == NewsMatch.ProfileSurname ? 1 : 2).ThenBy(g => g.First().At)
            .Take(MaxPeople)
            .Select(g => new NewsPerson(g.First().Name, g.First().Match, g.Select(m => m.Role).OfType<string>().Distinct(StringComparer.Ordinal).ToList(), g.Count()))
            .ToList();

    // ---- Events ------------------------------------------------------------------------------------------------

    // Linear in the text: each sentence's and clause's slots are read once, each trigger finds its actor by a binary search
    // over the mentions (sorted by place), and at most MaxTriggers keywords are looked at in all.
    private static List<NewsEvent> Events(string text, string blanked, List<Mention> mentions, Dictionary<string, string> shownAs)
    {
        var events = new List<NewsEvent>();
        var triggers = 0;
        foreach (var (s, sl) in SourceConditions.Spans(blanked, 0, blanked.Length, SourceConditions.SentenceEnds))
        {
            if (sl == 0) continue;
            var sentence = text.Substring(s, sl);
            Slots? sentenceSlots = null;
            var seen = new HashSet<(NewsEventType, string?, NewsCertainty)>();
            foreach (var (c, cl) in SourceConditions.Spans(blanked, s, sl, SourceConditions.ClauseEnds))
            {
                if (cl == 0) continue;
                var clause = blanked.Substring(c, cl);
                var found = Triggers.SelectMany(t => t.Pattern.Matches(clause).Take(MaxTriggers).Select(m => (t.Type, m))).OrderBy(x => x.m.Index).ToList();
                if (found.Count == 0) continue;
                var modalEnd = SourceConditions.FirstModalEnd(clause);
                var own = text.Substring(c, cl);
                var clauseSlots = Slots.Of(own, withOffences: false);
                sentenceSlots ??= Slots.Of(sentence, withOffences: true);
                var end = 0;
                foreach (var (type, m) in found)
                {
                    if (m.Index < end) continue; // 不起訴 is one event, not also 起訴
                    end = m.Index + m.Length;
                    if (++triggers > MaxTriggers) return events;
                    var actor = Actor(mentions, s, c, cl, c + m.Index, clause);
                    var certainty = SourceConditions.Negated(clause, m.Index) ? NewsCertainty.Negated
                        : modalEnd <= m.Index || Requested(clause, m) ? NewsCertainty.Hedged : NewsCertainty.Stated;
                    if (!seen.Add((type, actor?.Key, certainty))) continue;
                    // A date belongs to its own clause; the agency, amount and penalty come from the clause, else the
                    // sentence; the offences from the sentence.
                    events.Add(new NewsEvent(type, certainty, actor is null ? null : shownAs[actor.Key], actor?.Match,
                        clauseSlots.Date, clauseSlots.Agency ?? sentenceSlots.Agency, sentenceSlots.Offences,
                        clauseSlots.Amount ?? sentenceSlots.Amount,
                        type == NewsEventType.Conviction ? clauseSlots.Penalty ?? sentenceSlots.Penalty : null,
                        Quote(sentence)));
                    if (events.Count == MaxEvents) return events;
                }
            }
        }
        return events;
    }

    private sealed record Slots(string? Date, string? Agency, string? Amount, string? Penalty, IReadOnlyList<string> Offences)
    {
        public static Slots Of(string text, bool withOffences) => new(
            Value(DatePattern().Match(text)), Value(AgencyPattern().Match(text)), Value(AmountPattern().Match(text)),
            PenaltyPattern().Match(text) is { Success: true } p ? (p.Groups["years"].Success ? p.Groups["years"].Value : p.Value) : null,
            withOffences ? NewsDigests.Offences(text) : []);

        private static string? Value(Match m) => m.Success ? m.Value : null;
    }

    private static readonly (NewsEventType Type, Regex Pattern)[] Triggers =
    [
        (NewsEventType.Investigation, InvestigationPattern()), (NewsEventType.Search, SearchPattern()), (NewsEventType.Questioning, QuestioningPattern()),
        (NewsEventType.Arrest, ArrestPattern()), (NewsEventType.Detention, DetentionPattern()), (NewsEventType.Bail, BailPattern()),
        (NewsEventType.NonIndictment, SourceConditions.Dismissal()), (NewsEventType.Indictment, SourceConditions.Indictment()),
        (NewsEventType.Conviction, SourceConditions.Conviction()), (NewsEventType.Acquittal, SourceConditions.Acquittal()),
        (NewsEventType.Fine, FinePattern()), (NewsEventType.WantedNotice, WantedPattern()),
    ];

    // Who the event is about: the name nearest before it in its clause, else the first after it (檢方起訴王大明), else, for
    // 他, 她, 其 or 該男, the last person named before the clause, else the last named earlier in the sentence
    // (王大明涉嫌詐欺，昨遭起訴). None of these: the news does not say. A person named as an official, witness or victim
    // (檢察官陳志強起訴王明) is the actor only when nobody else is.
    private static Mention? Actor(List<Mention> mentions, int sentence, int clause, int clauseLength, int trigger, string clauseText) =>
        Actor(mentions, sentence, clause, clauseLength, trigger, clauseText, skipBystanders: true)
        ?? Actor(mentions, sentence, clause, clauseLength, trigger, clauseText, skipBystanders: false);

    // The mentions are in text order: a binary search finds the first in the clause, and only the clause's own mentions and
    // the nearest earlier ones are looked at.
    private static Mention? Actor(List<Mention> mentions, int sentence, int clause, int clauseLength, int trigger, string clauseText, bool skipBystanders)
    {
        bool Counts(Mention m) => !(skipBystanders && m.Role is { } role && Bystanders.Contains(role.TrimStart('前')));
        var first = FirstAtOrAfter(mentions, clause);
        Mention? before = null, after = null;
        for (var i = first; i < mentions.Count && mentions[i].At < clause + clauseLength; i++)
        {
            var m = mentions[i];
            if (!Counts(m)) continue;
            if (m.At < trigger) before = m;
            else { after = m; break; }
        }
        if ((before ?? after) is { } inClause) return inClause;
        Mention? earlier = null;
        for (var i = first - 1; i >= 0; i--)
            if (Counts(mentions[i])) { earlier = mentions[i]; break; }
        return Pronoun().IsMatch(clauseText) ? earlier : earlier is { } e && e.At >= sentence ? e : null;
    }

    private static int FirstAtOrAfter(List<Mention> mentions, int at)
    {
        int lo = 0, hi = mentions.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (mentions[mid].At < at) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    private static readonly HashSet<string> Bystanders = new(["主任檢察官", "檢察官", "審判長", "法官", "律師", "辯護人", "警員", "員警", "警官", "證人", "告訴人", "被害人"], StringComparer.Ordinal);

    // 聲請羈押 and 聲押 ask the court; 獲准 or 裁准 after it is the court's yes.
    private static bool Requested(string clause, Match m) =>
        (clause.AsSpan(0, m.Index).Contains("聲請", StringComparison.Ordinal) || m.Value == "聲押")
        && !clause.AsSpan(m.Index).ContainsAny(Granted);

    private static readonly System.Buffers.SearchValues<string> Granted = System.Buffers.SearchValues.Create(["獲准", "裁准", "准予"], StringComparison.Ordinal);

    // Longest first, and a shorter word inside a longer one found (吸金 in 違法吸金) is not counted again.
    private static List<string> Offences(string sentence)
    {
        var found = new List<(int At, string Word)>();
        var covered = new bool[sentence.Length];
        foreach (var word in OffenceWords)
            for (var at = sentence.IndexOf(word, StringComparison.Ordinal); at >= 0; at = sentence.IndexOf(word, at + word.Length, StringComparison.Ordinal))
            {
                if (covered.AsSpan(at, word.Length).Contains(true)) continue;
                covered.AsSpan(at, word.Length).Fill(true);
                if (!found.Exists(f => f.Word == word)) found.Add((at, word));
            }
        return found.OrderBy(f => f.At).Take(MaxOffences).Select(f => f.Word).ToList();
    }

    private static readonly string[] OffenceWords = new[]
    {
        "違反證券交易法", "違反稅捐稽徵法", "違反商業會計法", "違反銀行法", "違反公司法", "違反槍砲", "妨害性自主", "操縱股價", "內線交易",
        "地下匯兌", "地下通匯", "違法吸金", "偽造文書", "組織犯罪", "人口販運", "擄人勒贖", "公共危險", "逃漏稅", "洗錢", "詐欺", "詐騙",
        "背信", "侵占", "貪污", "貪汙", "收賄", "受賄", "行賄", "圖利", "炒股", "吸金", "販毒", "運毒", "毒品", "走私", "逃稅", "賭博",
        "資恐", "殺人", "傷害", "竊盜", "強盜", "恐嚇", "掏空",
    }.OrderByDescending(w => w.Length).ToArray();

    private static string Quote(string sentence)
    {
        var s = sentence.Trim();
        if (s.Length <= MaxQuote) return s;
        var cut = char.IsHighSurrogate(s[MaxQuote - 1]) ? MaxQuote - 1 : MaxQuote;
        return s[..cut] + "…";
    }

    [GeneratedRegex("偵辦|偵查|調查(?![局處站官員])")]
    private static partial Regex InvestigationPattern();

    [GeneratedRegex("搜索|搜查")]
    private static partial Regex SearchPattern();

    [GeneratedRegex("約談|傳喚|到案說明")]
    private static partial Regex QuestioningPattern();

    [GeneratedRegex("逮捕|拘提|拘捕|落網|移送")]
    private static partial Regex ArrestPattern();

    [GeneratedRegex("羈押|收押|聲押")]
    private static partial Regex DetentionPattern();

    [GeneratedRegex("交保|具保|責付|限制出境|限制出海|限制住居")]
    private static partial Regex BailPattern();

    [GeneratedRegex("罰鍰|裁罰|罰款")]
    private static partial Regex FinePattern();

    [GeneratedRegex("通緝")]
    private static partial Regex WantedPattern();

    [GeneratedRegex("他(?![們人])|她(?!們)|該男|該女|渠|其(?![他中餘實後次])")]
    private static partial Regex Pronoun();

    // The slot patterns that hold an open run are non-backtracking: a long digit run with no 元 or 年 is read once, not
    // once from every digit in it.
    [GeneratedRegex(@"(?:\d{2,4}年)?\d{1,2}月(?:\d{1,2}日)?|[昨今前][（(]\d{1,2}[）)]日|\d{1,2}日|昨天|今天|日前|去年|今年|上月|本月|上週", RegexOptions.NonBacktracking)]
    private static partial Regex DatePattern();

    [GeneratedRegex(@"\p{IsCJKUnifiedIdeographs}{2}(?:地方檢察署|地檢署|地方法院|地院|分局|地檢)|最高法院|最高檢察署|最高檢|高等法院|高院|高檢署|智慧財產及商業法院|調查局|調查處|調查站|刑事警察局|刑事局|廉政署|金管會|海巡署|移民署|國稅局|法務部|新北檢|(?:[北士桃竹苗中彰投雲嘉南雄橋屏宜花東基澎金]檢|北院)(?![方察舉查驗測討視])")]
    private static partial Regex AgencyPattern();

    [GeneratedRegex(@"(?:新[台臺]幣|台幣|美金|人民幣|港幣)?[\d,.一二三四五六七八九十兩百千]+(?:多|餘)?[萬億]*(?:多|餘)?(?:美元|元)", RegexOptions.NonBacktracking)]
    private static partial Regex AmountPattern();

    // 判處3年 keeps only 3年 (group years): non-backtracking takes no lookbehind.
    [GeneratedRegex(@"有期徒刑[\d一二三四五六七八九十兩]+年(?:[\d一二三四五六七八九十兩]+個?月)?|(?:判處|判刑)(?<years>[\d一二三四五六七八九十兩]+年(?:[\d一二三四五六七八九十兩]+個?月)?)|無期徒刑|死刑|拘役[\d一二三四五六七八九十兩]+日", RegexOptions.NonBacktracking)]
    private static partial Regex PenaltyPattern();

    // ---- Summary -----------------------------------------------------------------------------------------------

    // Fixed sentence patterns over the people and events above; nothing is reworded or added.
    private static List<string> Summary(NewsItem news, string sourceRef, List<NewsPerson> people, List<NewsEvent> events)
    {
        var cite = sourceRef.Length > 0 ? $"[{sourceRef}]" : "";
        var lines = new List<string> { $"{news.Publisher}{(news.PublishedOn is { } d ? $"（{d}）" : "")}報導「{news.Title}」{cite}。" };
        if (people.Count > 0) lines.Add($"報導人物：{string.Join("、", people.Select(PersonLabel))}。");
        // The people in their order, then any actor past the people cap, then events with nobody named.
        var actors = people.Select(p => p.Name).Concat(events.Select(e => e.Actor).OfType<string>()).Distinct(StringComparer.Ordinal).Select(a => (string?)a).Append(null);
        foreach (var actor in actors)
        {
            var phrases = events.Where(e => e.Actor == actor).DistinctBy(e => (e.Type, e.Certainty)).Take(MaxPhrases).Select(Phrase).ToList();
            if (phrases.Count > 0) lines.Add($"{actor ?? "未指明對象"}：{string.Join("；", phrases)}{cite}。");
        }
        if (events.Count == 0) lines.Add("報導中未讀到偵查、起訴、判決等事件。");
        return lines;
    }

    private static string PersonLabel(NewsPerson p)
    {
        var notes = new List<string>();
        if (p.Match == NewsMatch.ProfileName) notes.Add("名單人物");
        if (p.Match == NewsMatch.ProfileSurname) notes.Add("與名單人物同姓的匿稱");
        notes.AddRange(p.Roles);
        return notes.Count > 0 ? $"{p.Name}（{string.Join("；", notes)}）" : p.Name;
    }

    private static string Phrase(NewsEvent e)
    {
        var verb = e.Certainty switch
        {
            NewsCertainty.Negated => "未" + Label(e.Type),
            NewsCertainty.Hedged => Label(e.Type) + "（報導為推測、請求或條件）",
            _ => Label(e.Type),
        };
        var details = e.Offences.Count > 0 ? string.Join("、", e.Offences) : null;
        var extra = new[] { details, e.Amount, e.Penalty }.OfType<string>().ToList();
        return $"{(e.Date is null ? "" : e.Date + " ")}{e.Agency}{verb}{(extra.Count > 0 ? $"（{string.Join("；", extra)}）" : "")}";
    }

    private static string Label(NewsEventType type) => type switch
    {
        NewsEventType.Investigation => "偵辦或調查",
        NewsEventType.Search => "搜索",
        NewsEventType.Questioning => "約談",
        NewsEventType.Arrest => "逮捕或移送",
        NewsEventType.Detention => "羈押",
        NewsEventType.Bail => "交保或限制出境",
        NewsEventType.Indictment => "起訴",
        NewsEventType.NonIndictment => "不起訴",
        NewsEventType.Conviction => "判決有罪",
        NewsEventType.Acquittal => "判決無罪",
        NewsEventType.Fine => "裁罰",
        NewsEventType.WantedNotice => "通緝",
        _ => Code(type),
    };
}
