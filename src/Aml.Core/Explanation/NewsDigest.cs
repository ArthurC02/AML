using System.Buffers;
using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using Aml.Core.Normalization;
using Aml.Core.Screening;
using Aml.Core.Shared;
using Aml.Core.Subjects;
using static Aml.Core.Cases.CaseJson;

namespace Aml.Core.Explanation;

// The legal-event inventory follows the ACE / Rich ERE Justice events, narrowed to Taiwanese criminal procedure and the
// regulators' sanctions (watchlist-and-adverse-information.md#news-digest).
public enum NewsEventType
{
    Investigation, Search, Questioning, Arrest, Referral, Detention, Bail, AssetRestraint, Indictment, NonIndictment,
    Conviction, Acquittal, Appeal, Confiscation, Fine, RegulatoryPenalty, WantedNotice, Extradition,
}

public enum NewsCertainty { Stated, Negated, Hedged, Requested }

public enum NewsSource { Media, Investigators, Court, Defence, UnnamedSource }

public enum NewsParty { Suspect, Victim, Witness, Official }

public enum NewsMatch { ProfileName, ProfileSurname, OtherPerson }

public sealed record NewsPerson(
    [property: Description("報導中的姓名或匿稱（如 王姓男子、王男）")][property: Sensitivity(Sensitivity.Personal)] string Name,
    [property: Description("PROFILE_NAME：名單人物（姓名相同，或全文唯一同姓全名的匿稱）；PROFILE_SURNAME：與名單人物同姓的匿稱；OTHER_PERSON：其他人")] NewsMatch Match,
    [property: Description("報導中緊接在姓名前的身分或職稱，依出現順序")] IReadOnlyList<string> Roles,
    [property: Description("依身分或職稱歸類：SUSPECT、VICTIM、WITNESS、OFFICIAL；看不出時為 null")] NewsParty? Party,
    [property: Description("出現次數")] int Mentions);

public sealed record NewsEvent(
    [property: Description("事件類型，見 /api/codes")] NewsEventType Type,
    [property: Description("STATED：報導陳述；NEGATED：否定或遭駁回；HEDGED：推測、條件或疑問；REQUESTED：已聲請、尚未裁定")] NewsCertainty Certainty,
    [property: Description("說法來源：MEDIA（報導本身）、INVESTIGATORS（檢警調）、COURT（法院）、DEFENCE（被告方）、UNNAMED_SOURCE（不具名消息）")] NewsSource Source,
    [property: Description("事件當事人；報導未指明時為 null")][property: Sensitivity(Sensitivity.Personal)] string? Actor,
    [property: Description("當事人與名單人物的關係；未指明當事人時為 null")] NewsMatch? ActorMatch,
    [property: Description("日期原文")] string? Date,
    [property: Description("以發布日期換算的日期（民國年、相對日期）；無法換算時為 null")] PartialDate? DateValue,
    [property: Description("機關：地檢署、法院、調查局、金管會等")] string? Agency,
    [property: Description("同句提到的罪名或違法類型")] IReadOnlyList<string> Offences,
    [property: Description("罪名對應的風險類別（RISK_CATEGORY，依 FATF 指定犯罪類別與洗錢防制法前置犯罪）")] IReadOnlyList<string> OffenceCategories,
    [property: Description("金額原文")] string? Amount,
    [property: Description("刑度原文（徒刑、緩刑、併科罰金、易科罰金、褫奪公權），只用於判決有罪")] string? Penalty,
    [property: Description("檢方求刑原文，只用於起訴")] string? RequestedPenalty,
    [property: Description("報導寫明判決確定或定讞")] bool Final,
    [property: Description("事件所在句子的原文，最多 200 字")][property: Sensitivity(Sensitivity.Personal)] string Quote);

// Restates what the attached news says, by fixed rules; it adds no finding and decides nothing (ADR-0012).
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
        MaxQuote = 200, MaxPhrases = 8, MaxOffences = 5, CueWindow = 200, MaxSlotText = 2000, MaxDates = 10;

    private sealed record Mention(int At, int Length, string Key, string Name, NewsMatch Match, string? Role, bool Anonymous = false);

    public static NewsDigest? Build(NewsItem? news, IReadOnlyList<string> profileNames, string sourceRef)
    {
        if (news is null) return null;
        var text = news.Text;
        var names = profileNames.Take(100).Where(n => n.Length > 1).ToList();
        var surnames = names.Where(n => CjkText.IsCjk(n[0])).Select(Surname).ToHashSet(StringComparer.Ordinal);
        var mentions = Mentions(text, names, surnames);
        // Quoted speech is a claim: no event is read inside it (as for the legal stage), but names are.
        var blanked = SourceConditions.Quoted().Replace(text, m => new string('　', m.Length));
        var people = People(mentions);
        // An event names its person as the people list does: by the first full name, else the first form.
        var shownAs = mentions.GroupBy(m => m.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (g.FirstOrDefault(m => !m.Anonymous) ?? g.First()).Name, StringComparer.Ordinal);
        var events = Events(text, blanked, mentions, shownAs, news.PublishedOn);
        return new NewsDigest(sourceRef, people, events, Summary(news, sourceRef, people, events));
    }

    // ---- People ------------------------------------------------------------------------------------------------

    // Who the news names: the Profile's own names; a name right after a role (被告王大明, 董事長李四); a 2 to 4 character
    // name of a common or compound surname opening a clause before 遭, 被, 涉嫌 …; a dotted foreign or indigenous name
    // (約翰·史密斯); a name marked 化名; a lettered party (少年A); and the anonymised forms 王姓男子, 王男, 王嫌, 王董.
    // Every other occurrence of a name found so is a mention too.
    private static List<Mention> Mentions(string text, List<string> names, HashSet<string> surnames)
    {
        var found = new List<Mention>();
        var shown = names.FirstOrDefault(n => text.Contains(n, StringComparison.OrdinalIgnoreCase));
        foreach (var name in names.OrderByDescending(n => n.Length))
            AddAll(found, text, name, ProfileKey, shown ?? name, NewsMatch.ProfileName);

        var others = new List<string>();
        foreach (var pattern in new[] { RoleThenName(), ClauseOpeningName() })
            foreach (Match m in pattern.Matches(text).Take(MaxMentions))
                if (TrimName(m.Groups["name"].Value) is { } name && !HoldsOrganisation(text, m.Groups["name"].Index, name.Length)) others.Add(name);
        foreach (Match m in DottedName().Matches(text).Take(MaxMentions))
            if (Dotted(m) is { } name) others.Add(name);
        foreach (Match m in Pseudonym().Matches(text).Take(MaxMentions)) others.Add(m.Groups["name"].Value);
        foreach (Match m in LetteredParty().Matches(text).Take(MaxMentions)) others.Add(m.Value);
        foreach (var name in others.Distinct(StringComparer.Ordinal).Take(MaxPeople))
        {
            // Only the Profile's own name is theirs: 王明 is not 王明華.
            var profile = names.Contains(name, StringComparer.OrdinalIgnoreCase);
            AddAll(found, text, name, profile ? ProfileKey : name, profile ? shown ?? name : name, profile ? NewsMatch.ProfileName : NewsMatch.OtherPerson);
        }

        foreach (Match m in Anonymised().Matches(text))
        {
            if (found.Count >= MaxFound) break;
            var surname = m.Groups["surname"].Value;
            if (surname.Length == 1 && !SourceConditions.CommonSurnames.Contains(surname[0], StringComparison.Ordinal)) continue;
            if (!m.Groups["named"].Success && !StandsAlone(text, m)) continue;
            found.Add(new(m.Index, m.Length, "~" + surname, m.Value, surnames.Contains(surname) ? NewsMatch.ProfileSurname : NewsMatch.OtherPerson,
                AnonymousRole(m.Groups["kind"].Value), Anonymous: true));
        }

        // One mention per place, the longest; then the role written just before it.
        var kept = new List<Mention>();
        var end = 0;
        foreach (var m in found.OrderBy(m => m.At).ThenByDescending(m => m.Length))
        {
            if (m.At < end) continue;
            kept.Add(m with { Role = m.Role ?? RoleBefore(text, m.At) });
            end = m.At + m.Length;
            if (kept.Count == MaxMentions) break;
        }
        return LinkAliases(kept);
    }

    // 王男, 王嫌, 王董 are the person named in full with that surname, when exactly one is; with two or more (王大明 and
    // 王小華) they stay apart, as Taiwanese reporting itself splits them.
    private static List<Mention> LinkAliases(List<Mention> kept)
    {
        var fullBySurname = kept.Where(m => !m.Anonymous && CjkText.IsCjk(m.Name[0]) && !m.Name.AsSpan().ContainsAny(Dots))
            .GroupBy(m => Surname(m.Name), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.DistinctBy(m => m.Key, StringComparer.Ordinal).ToList(), StringComparer.Ordinal);
        return kept.Select(m => m.Anonymous && fullBySurname.TryGetValue(m.Key[1..], out var full) && full.Count == 1
            ? m with { Key = full[0].Key, Match = full[0].Match }
            : m).ToList();
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

    // A compound surname (張簡, 歐陽 …) takes two characters, any other one; ponytail: the compound list is the ones seen
    // in Taiwan (內政部 statistics) plus the classical ones.
    private static string Surname(string name) =>
        name.Length >= 3 && CompoundSurnames.Contains(name[..2]) ? name[..2] : name[..1];

    private static readonly HashSet<string> CompoundSurnames = new(
        ["張簡", "歐陽", "范姜", "周黃", "江謝", "張廖", "司馬", "諸葛", "上官", "司徒", "東方", "夏侯", "皇甫", "公孫", "令狐", "慕容"], StringComparer.Ordinal);

    // 被告王明涉嫌 reads 王明涉 as a name: a last character that usually follows a name ends it a character earlier. The
    // surname must be common or compound, the given name's first character not one that makes a word of it (許多, 王男,
    // 高層), and a two character given name not end in an organisation's character (林務局).
    // ponytail: fixed character lists; a name ending in one of them (陳志於) is cut, one ending in an organisation
    // character (林志局) is dropped.
    private static string? TrimName(string candidate)
    {
        var compound = candidate.Length >= 3 && CompoundSurnames.Contains(candidate[..2]);
        int min = compound ? 3 : 2, max = compound ? 4 : 3;
        var name = candidate.Length > max ? candidate[..max] : candidate;
        if (name.Length > min && AfterName.Contains(name[^1], StringComparison.Ordinal)) name = name[..^1];
        if (name.Length < min) return null;
        var given = name[(compound ? 2 : 1)..];
        if (!compound && !SourceConditions.CommonSurnames.Contains(name[0], StringComparison.Ordinal)) return null;
        if (NotGivenName.Contains(given[0], StringComparison.Ordinal)) return null;
        return given.Length == 2 && Organisation.Contains(given[1], StringComparison.Ordinal) ? null : name;
    }

    // 林口分局, 王品集團, 國泰銀行: an organisation's name word starts inside the candidate or right after it; 金融帳戶,
    // 田產, 金條: an asset word starts anywhere in it.
    private static bool HoldsOrganisation(string text, int at, int length)
    {
        for (var k = 0; k <= length && at + k < text.Length; k++)
        {
            var rest = text.AsSpan(at + k);
            if (k > 0)
                foreach (var suffix in OrganisationWords)
                    if (rest.StartsWith(suffix, StringComparison.Ordinal)) return true;
            if (k < length)
                foreach (var asset in AssetWords)
                    if (rest.StartsWith(asset, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static readonly string[] AssetWords =
        ["帳戶", "帳號", "金融", "資產", "財產", "田產", "房產", "地產", "金條", "金飾", "存款", "股票", "股份", "車輛", "名下"];

    private static readonly string[] OrganisationWords =
    [
        "公司", "集團", "控股", "企業", "實業", "銀行", "商銀", "證券", "投信", "投顧", "壽險", "產險", "基金會", "協會", "公會", "農會",
        "地檢", "法院", "地院", "分局", "派出所", "大學", "醫院", "國小", "國中", "高中", "宮", "廟", "寺",
    ];

    // ponytail: a real name ending in one of these (林志和, 陳建一) is cut a character short; tune on reporting.
    private const string AfterName = "涉遭被於在與和及等因已則也亦向將稱表說為以今昨日前後是並經對到把從坦供否辯指曾仍再又均都一的獲未不有無";
    private const string NotGivenName = "多姓男女嫌翁婦某家氏方達等們層務官司局會署院校部處所隊組系廠業界商";
    private const string Organisation = "局會司署院校部處所隊組廠行社團";

    // 約翰·史密斯, 瓦歷斯·諾幹: a dotted name. The part before the dot drops what precedes the name (被告約翰 → 約翰) by
    // keeping it from the first transliteration character; the part after ends where a name usually ends (遭, 涉, 說 …).
    // ponytail: a seed set of transliteration characters (after Chen & Lee 1996, Busemann & Zhang 2008); tune on reporting.
    private static string? Dotted(Match m)
    {
        var head = m.Groups["head"].Value;
        var start = head.AsSpan().IndexOfAny(Transliteration);
        if (start < 0) return null;
        var tail = m.Groups["tail"].Value;
        var stop = tail.AsSpan().IndexOfAny(AfterNameChars);
        if (stop == 0) return null;
        return head[start..] + m.Groups["dot"].Value + (stop > 0 ? tail[..stop] : tail);
    }

    private static readonly SearchValues<char> Transliteration = SearchValues.Create(
        "阿埃艾安奧巴拜班貝本比彼波伯布查達戴德迪丁杜多厄恩法菲費芬弗福富格戈哈海漢赫亨胡華霍吉加傑金卡凱坎科克庫拉萊蘭朗勞雷黎里利莉林琳盧魯倫羅洛瑪馬麥曼梅門蒙米密摩莫穆納奈尼妮諾努帕佩皮普奇喬切瑞薩塞桑森沙莎史斯松蘇索塔泰坦特提湯托瓦威維韋溫沃西希謝休雅亞楊伊因英尤約澤扎茲翰頓絲歐蒂黛茵爾娜娃");

    private static readonly SearchValues<char> AfterNameChars = SearchValues.Create(AfterName);

    private static readonly SearchValues<char> Dots = SearchValues.Create("·．・‧•");

    // 王嫌 is a suspect, 王董 a chairman: the anonymised form carries its role.
    private static string? AnonymousRole(string kind) => kind switch
    {
        "嫌" or "嫌犯" or "嫌疑人" => "嫌犯",
        "被告" => "被告",
        "董" or "董事長" => "董事長",
        "負責人" => "負責人",
        "員警" => "員警",
        _ => null,
    };

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

    private static readonly HashSet<string> SuspectRoles = new(["被告", "共同被告", "嫌犯", "犯嫌", "嫌疑人"], StringComparer.Ordinal);
    private static readonly HashSet<string> VictimRoles = new(["被害人", "告訴人"], StringComparer.Ordinal);
    private static readonly HashSet<string> OfficialRoles = new(["主任檢察官", "檢察官", "審判長", "法官", "律師", "辯護人", "警員", "員警", "警官"], StringComparer.Ordinal);

    // A role word says the party; none says nothing (no one is a suspect by default).
    private static NewsParty? Party(IReadOnlyList<string> roles)
    {
        var plain = roles.Select(r => r.TrimStart('前')).ToList();
        if (plain.Exists(SuspectRoles.Contains)) return NewsParty.Suspect;
        if (plain.Exists(VictimRoles.Contains)) return NewsParty.Victim;
        if (plain.Contains("證人")) return NewsParty.Witness;
        return plain.Exists(OfficialRoles.Contains) ? NewsParty.Official : null;
    }

    private static readonly Regex RoleThenNamePattern = new($"(?:{string.Join('|', Roles)})(?<name>\\p{{IsCJKUnifiedIdeographs}}{{2,4}})");

    private static Regex RoleThenName() => RoleThenNamePattern;

    [GeneratedRegex(@"(?<=^|[，,。！？；\n、「」『』]|而|但|則|另外|隨後|其後|後來|至於)(?<name>\p{IsCJKUnifiedIdeographs}{2,4}?)(?:也|亦|則|已|均)?(?=遭到|遭|被|獲|涉嫌|涉及|坦承|供稱|否認)", RegexOptions.Multiline)]
    private static partial Regex ClauseOpeningName();

    [GeneratedRegex(@"(?<surname>張簡|歐陽|范姜|周黃|江謝|張廖|\p{IsCJKUnifiedIdeographs})(?:(?<named>姓)(?<kind>男子|女子|男童|女童|少年|少女|嫌犯|嫌疑人|被告|婦人|老翁|負責人|董事長|業者|員警|移工|商人|男|女)|(?<kind>男|女|嫌|翁|婦|某|董|童))")]
    private static partial Regex Anonymised();

    [GeneratedRegex(@"(?<head>\p{IsCJKUnifiedIdeographs}{1,4})(?<dot>[·．・‧•])(?<tail>\p{IsCJKUnifiedIdeographs}{1,6})")]
    private static partial Regex DottedName();

    [GeneratedRegex(@"(?<name>\p{IsCJKUnifiedIdeographs}{2,3})[（(]化名[）)]")]
    private static partial Regex Pseudonym();

    [GeneratedRegex(@"(?:少年|少女|男童|女童|被害人|告訴人|證人|被告|嫌犯)[A-ZＡ-Ｚ甲乙丙丁]")]
    private static partial Regex LetteredParty();

    private static List<NewsPerson> People(List<Mention> mentions) =>
        mentions.GroupBy(m => m.Key, StringComparer.Ordinal)
            .OrderBy(g => g.Key == ProfileKey ? 0 : g.First().Match == NewsMatch.ProfileSurname ? 1 : 2).ThenBy(g => g.First().At)
            .Take(MaxPeople)
            .Select(g =>
            {
                var roles = g.Select(m => m.Role).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
                return new NewsPerson((g.FirstOrDefault(m => !m.Anonymous) ?? g.First()).Name, g.First().Match, roles, Party(roles), g.Count());
            })
            .ToList();

    // ---- Events ------------------------------------------------------------------------------------------------

    // Linear in the text: each sentence's and clause's slots are read once, each trigger finds its actor by a binary search
    // over the mentions (sorted by place), and at most MaxTriggers keywords are looked at in all.
    private static List<NewsEvent> Events(string text, string blanked, List<Mention> mentions, Dictionary<string, string> shownAs, PartialDate? published)
    {
        var events = new List<NewsEvent>();
        var triggers = 0;
        foreach (var (s, sl) in SourceConditions.Spans(blanked, 0, blanked.Length, SourceConditions.SentenceEnds))
        {
            if (sl == 0) continue;
            var sentence = text.Substring(s, sl);
            Slots? sentenceSlots = null;
            var seen = new HashSet<(NewsEventType, string?, NewsCertainty)>();
            var conditional = false;
            foreach (var (c, cl) in SourceConditions.Spans(blanked, s, sl, SourceConditions.ClauseEnds))
            {
                if (cl == 0) continue;
                var clause = blanked.Substring(c, cl);
                // A condition (若…，) hedges its own clause and the clauses after it until a contrast (但, 然而 …) ends it.
                var opening = clause.AsSpan().TrimStart();
                if (StartsWithAny(opening, Contrasts)) conditional = false;
                if (StartsWithAny(opening, Conditions)) conditional = true;
                var underCondition = conditional;

                var found = Triggers.SelectMany(t => t.Pattern.Matches(clause).Take(MaxTriggers).Select(m => (t.Type, m))).OrderBy(x => x.m.Index).ToList();
                if (found.Count == 0) continue;
                var modalEnd = SourceConditions.FirstModalEnd(clause);
                var pronoun = Pronoun().IsMatch(clause);
                var clauseSlots = Slots.Of(Head(text.Substring(c, cl)), published, withOffences: false);
                sentenceSlots ??= Slots.Of(Head(sentence), published, withOffences: true);
                var end = 0;
                foreach (var (type, m) in found)
                {
                    if (m.Index < end) continue; // 不起訴 is one event, not also 起訴
                    end = m.Index + m.Length;
                    if (type == NewsEventType.Indictment && m.Index + m.Length < clause.Length && clause[m.Index + m.Length] == '書') continue; // 起訴書
                    if (++triggers > MaxTriggers) return events;
                    var actor = Actor(mentions, s, c, cl, c + m.Index, pronoun);
                    var certainty = Certainty(clause, m, modalEnd, underCondition);
                    if (!seen.Add((type, actor?.Key, certainty))) continue;
                    // A date belongs to its own clause; the agency, amount and requested penalty come from the clause, else
                    // the sentence; the sentence, probation and finality run over clauses, so they come from the sentence.
                    // Finality is only for a judgment the report states.
                    // ponytail: two people sentenced in one sentence share its penalties; split by clause if that shows up.
                    events.Add(new NewsEvent(type, certainty, Source(blanked, s, c + m.Index), actor is null ? null : shownAs[actor.Key], actor?.Match,
                        clauseSlots.Date, clauseSlots.DateValue, clauseSlots.Agency ?? sentenceSlots.Agency,
                        sentenceSlots.Offences, sentenceSlots.Categories, clauseSlots.Amount ?? sentenceSlots.Amount,
                        type == NewsEventType.Conviction ? sentenceSlots.Penalty : null,
                        type == NewsEventType.Indictment ? clauseSlots.RequestedPenalty ?? sentenceSlots.RequestedPenalty : null,
                        type is NewsEventType.Conviction or NewsEventType.Acquittal or NewsEventType.NonIndictment
                            && certainty == NewsCertainty.Stated && sentenceSlots.Final,
                        Quote(sentence)));
                    if (events.Count == MaxEvents) return events;
                }
            }
        }
        return events;
    }

    // Slots are read from the first MaxSlotText characters of a sentence or clause: a real one is far shorter, and a
    // run-on text of millions of characters is then read once, not once per slot pattern.
    private static string Head(string s) => s.Length <= MaxSlotText ? s : s[..(char.IsHighSurrogate(s[MaxSlotText - 1]) ? MaxSlotText - 1 : MaxSlotText)];

    private static bool StartsWithAny(ReadOnlySpan<char> s, string[] words)
    {
        foreach (var w in words)
            if (s.StartsWith(w, StringComparison.Ordinal)) return true;
        return false;
    }

    private static bool EndsWithAny(ReadOnlySpan<char> s, string[] words)
    {
        foreach (var w in words)
            if (s.EndsWith(w, StringComparison.Ordinal)) return true;
        return false;
    }

    private static readonly string[] Conditions = ["若", "如果", "倘若", "假如", "一旦"];
    private static readonly string[] Contrasts = ["但", "然而", "不過", "卻", "惟", "只是"];

    // A modal or condition hedges; 是否, 未必 and 不排除 ask rather than deny; 不予, 不得 and 免予 deny; a request to the
    // court (聲請羈押, 羈押聲請, 聲押) is REQUESTED until granted (獲准, 裁准: STATED) or refused (駁回, 未獲准: NEGATED).
    // Each looks at most CueWindow characters either side of the keyword, so a clause is not rescanned per keyword.
    private static NewsCertainty Certainty(string clause, Match m, int modalEnd, bool underCondition)
    {
        var from = Math.Max(0, m.Index - CueWindow);
        var to = Math.Min(clause.Length, m.Index + m.Length + CueWindow);
        var before = clause.AsSpan(from, m.Index - from);
        var after = clause.AsSpan(m.Index + m.Length, to - m.Index - m.Length);
        var requested = !m.Value.StartsWith("聲請簡易", StringComparison.Ordinal)
            && (m.Value == "聲押" || before.Contains("聲請", StringComparison.Ordinal) || after.Contains("聲請", StringComparison.Ordinal));
        if (requested && clause.AsSpan(from, to - from).ContainsAny(Refused)) return NewsCertainty.Negated;
        var bare = WithoutPassive(before);
        if (bare.EndsWith("是否", StringComparison.Ordinal) || before.Contains("未必", StringComparison.Ordinal) || before.Contains("不排除", StringComparison.Ordinal))
            return NewsCertainty.Hedged;
        if (EndsWithAny(bare, Denials) || SourceConditions.Negated(clause, m.Index)) return NewsCertainty.Negated;
        if (modalEnd <= m.Index || underCondition) return NewsCertainty.Hedged;
        return requested && !after.ContainsAny(Granted) ? NewsCertainty.Requested : NewsCertainty.Stated;
    }

    private static ReadOnlySpan<char> WithoutPassive(ReadOnlySpan<char> before)
    {
        foreach (var passive in Passives)
            if (before.EndsWith(passive, StringComparison.Ordinal)) return before[..^passive.Length];
        return before;
    }

    private static readonly string[] Denials = ["不予", "不得", "免予", "無須", "不准"];

    private static readonly string[] Passives = ["遭到", "遭", "被", "受"];

    private static readonly SearchValues<string> Granted = SearchValues.Create(["獲准", "裁准", "准予", "裁定羈押"], StringComparison.Ordinal);
    private static readonly SearchValues<string> Refused = SearchValues.Create(["駁回", "未獲准", "不准", "無保請回"], StringComparison.Ordinal);

    // Who says it: the nearest attribution cue before the keyword in its sentence (within CueWindow characters), unless a
    // contrast (，但 …) comes after the cue. Without one the report itself says it.
    private static NewsSource Source(string blanked, int sentence, int trigger)
    {
        var from = Math.Max(sentence, trigger - CueWindow);
        var window = blanked.AsSpan(from, trigger - from);
        var (at, source) = (-1, NewsSource.Media);
        foreach (var (cue, kind) in AttributionCues)
            if (window.LastIndexOf(cue, StringComparison.Ordinal) is var i && i > at) (at, source) = (i, kind);
        if (at < 0) return NewsSource.Media;
        // A denial (否認犯行，…) is the defendant's own clause, not what the report goes on to say.
        if (source == NewsSource.Defence && window[at..].IndexOfAny('，', ',') >= 0) return NewsSource.Media;
        foreach (var contrast in Contrasts)
            if (window.LastIndexOf("，" + contrast, StringComparison.Ordinal) > at) return NewsSource.Media;
        return source;
    }

    // Source-introducing predicates (FactBank, PARC): whose claim the event is.
    private static readonly (string Cue, NewsSource Source)[] AttributionCues =
    [
        .. new[] { "檢方指控", "檢方認定", "檢方指出", "檢方調查", "檢方查出", "起訴書指出", "起訴書", "檢調", "檢警", "警方調查", "警方指出", "警方查出",
            "調查局指出", "檢察官認定", "檢察官指出", "據檢方", "據警方" }.Select(c => (c, NewsSource.Investigators)),
        .. new[] { "判決指出", "判決書", "判決認定", "法院認定", "法院指出", "法院審理", "合議庭", "法官認為" }.Select(c => (c, NewsSource.Court)),
        .. new[] { "否認", "堅稱", "辯稱", "喊冤", "律師表示", "律師指出" }.Select(c => (c, NewsSource.Defence)),
        .. new[] { "據了解", "據悉", "據報", "據傳", "傳出", "消息指出", "知情人士" }.Select(c => (c, NewsSource.UnnamedSource)),
    ];

    private static readonly (NewsEventType Type, Regex Pattern)[] Triggers =
    [
        (NewsEventType.Investigation, InvestigationPattern()), (NewsEventType.Search, SearchPattern()), (NewsEventType.Questioning, QuestioningPattern()),
        (NewsEventType.Arrest, ArrestPattern()), (NewsEventType.Referral, ReferralPattern()), (NewsEventType.Bail, BailPattern()),
        (NewsEventType.Detention, DetentionPattern()), (NewsEventType.AssetRestraint, AssetRestraintPattern()),
        (NewsEventType.NonIndictment, SourceConditions.Dismissal()), (NewsEventType.Indictment, IndictmentPattern()),
        (NewsEventType.Indictment, SourceConditions.Indictment()), (NewsEventType.Acquittal, AcquittalPattern()),
        (NewsEventType.Conviction, SourceConditions.Conviction()), (NewsEventType.Acquittal, SourceConditions.Acquittal()),
        (NewsEventType.Appeal, AppealPattern()), (NewsEventType.Confiscation, ConfiscationPattern()), (NewsEventType.Fine, FinePattern()),
        (NewsEventType.RegulatoryPenalty, RegulatoryPattern()), (NewsEventType.WantedNotice, WantedPattern()), (NewsEventType.Extradition, ExtraditionPattern()),
    ];

    // Who the event is about: the name nearest before it in its clause, else the first after it (檢方起訴王大明), else, for
    // 他, 她, 其 or 該男, the last person named before the clause, else the last named earlier in the sentence
    // (王大明涉嫌詐欺，昨遭起訴). None of these: the news does not say. A person named as an official, witness or victim
    // (檢察官陳志強起訴王明) is the actor only when nobody else is.
    private static Mention? Actor(List<Mention> mentions, int sentence, int clause, int clauseLength, int trigger, bool pronoun) =>
        Actor(mentions, sentence, clause, clauseLength, trigger, pronoun, skipBystanders: true)
        ?? Actor(mentions, sentence, clause, clauseLength, trigger, pronoun, skipBystanders: false);

    // The mentions are in text order: a binary search finds the first in the clause, and only the clause's own mentions and
    // the nearest earlier ones are looked at.
    private static Mention? Actor(List<Mention> mentions, int sentence, int clause, int clauseLength, int trigger, bool pronoun, bool skipBystanders)
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
        return pronoun ? earlier : earlier is { } e && e.At >= sentence ? e : null;
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

    // ---- Slots -------------------------------------------------------------------------------------------------

    private sealed record Slots(string? Date, PartialDate? DateValue, string? Agency, string? Amount, string? Penalty, string? RequestedPenalty,
        bool Final, IReadOnlyList<string> Offences, IReadOnlyList<string> Categories)
    {
        public static Slots Of(string text, PartialDate? published, bool withOffences)
        {
            var date = DatePattern().Matches(text).Take(MaxDates).FirstOrDefault(m => !IsTerm(text, m))?.Value;
            // 求處有期徒刑10年 is the prosecutor's request, not the sentence.
            var sentenced = RequestedPenaltyPattern().Replace(text, m => new string('　', m.Length));
            var penalties = PenaltyPattern().Matches(sentenced).Take(5).Select(Years).Distinct(StringComparer.Ordinal).ToList();
            var offences = withOffences ? NewsDigests.Offences(text) : [];
            return new(date, ResolveDate(date, published), Value(AgencyPattern().Match(text)), Value(AmountPattern().Match(text)),
                penalties.Count > 0 ? string.Join("，", penalties) : null,
                RequestedPenaltyPattern().Match(text) is { Success: true } r ? Years(r) : null,
                FinalPattern().IsMatch(text) && !NotFinalPattern().IsMatch(text),
                offences.Select(o => o.Word).ToList(), offences.Select(o => o.Category).OfType<string>().Distinct(StringComparer.Ordinal).ToList());
        }

        // 有期徒刑3年6月, 拘役30日, 羈押2月: a term, not a date.
        private static bool IsTerm(string text, Match m)
        {
            var before = text.AsSpan(0, m.Index);
            if (before.Length >= 2 && before[^1] == '年' && (char.IsAsciiDigit(before[^2]) || before[^2] is >= '０' and <= '９')) return true;
            foreach (var word in TermWords)
                if (before.EndsWith(word, StringComparison.Ordinal)) return true;
            return false;
        }

        private static readonly string[] TermWords =
            ["徒刑", "拘役", "羈押", "收押", "延押", "緩刑", "褫奪公權", "判處", "判刑", "求刑", "求處", "為期", "長達"];

        private static string? Value(Match m) => m.Success ? m.Value : null;

        private static string Years(Match m) => m.Groups["years"].Success ? m.Groups["years"].Value : m.Value;
    }

    // Longest first, and a shorter word inside a longer one found (吸金 in 違法吸金) is not counted again.
    private static List<(string Word, string? Category)> Offences(string sentence)
    {
        var found = new List<(int At, string Word, string? Category)>();
        var covered = new bool[sentence.Length];
        foreach (var (word, category) in OffenceWords)
            for (var at = sentence.IndexOf(word, StringComparison.Ordinal); at >= 0; at = sentence.IndexOf(word, at + word.Length, StringComparison.Ordinal))
            {
                if (covered.AsSpan(at, word.Length).Contains(true)) continue;
                covered.AsSpan(at, word.Length).Fill(true);
                if (!found.Exists(f => f.Word == word)) found.Add((at, word, category));
            }
        return found.OrderBy(f => f.At).Take(MaxOffences).Select(f => (f.Word, f.Category)).ToList();
    }

    // Offence words and the risk category each falls in: the FATF designated categories of offences and the predicate
    // offences of 洗錢防制法第 3 條, as RISK_CATEGORY codes; null for an offence that is no predicate by itself.
    private static readonly (string Word, string? Category)[] OffenceWords = new (string, string?)[]
    {
        ("違反證券交易法", "MARKET_ABUSE"), ("違反期貨交易法", "MARKET_ABUSE"), ("操縱股價", "MARKET_ABUSE"), ("內線交易", "MARKET_ABUSE"), ("炒股", "MARKET_ABUSE"),
        ("違反洗錢防制法", "MONEY_LAUNDERING"), ("洗錢", "MONEY_LAUNDERING"),
        ("詐欺", "FRAUD"), ("詐騙", "FRAUD"), ("詐貸", "FRAUD"), ("車手", "FRAUD"), ("人頭帳戶", "FRAUD"),
        ("違反銀行法", "FINANCIAL_CRIME"), ("地下匯兌", "FINANCIAL_CRIME"), ("地下通匯", "FINANCIAL_CRIME"), ("違法吸金", "FINANCIAL_CRIME"), ("吸金", "FINANCIAL_CRIME"),
        ("違反稅捐稽徵法", "TAX_EVASION"), ("逃漏稅", "TAX_EVASION"), ("逃稅", "TAX_EVASION"),
        ("違反商業會計法", "FORGERY"), ("偽造有價證券", "FORGERY"), ("偽造文書", "FORGERY"),
        ("偽造貨幣", "COUNTERFEITING"), ("偽鈔", "COUNTERFEITING"), ("仿冒", "COUNTERFEITING"), ("盜版", "COUNTERFEITING"), ("侵害商標", "COUNTERFEITING"),
        ("違反著作權法", "COUNTERFEITING"),
        ("背信", "BREACH_OF_TRUST"), ("侵占", "BREACH_OF_TRUST"), ("掏空", "BREACH_OF_TRUST"),
        ("貪污", "CORRUPTION"), ("貪汙", "CORRUPTION"), ("圖利", "CORRUPTION"), ("收賄", "BRIBERY"), ("受賄", "BRIBERY"), ("行賄", "BRIBERY"), ("賄賂", "BRIBERY"),
        ("販毒", "DRUG_TRAFFICKING"), ("運毒", "DRUG_TRAFFICKING"), ("毒品", "DRUG_TRAFFICKING"),
        ("走私", "SMUGGLING"), ("人口販運", "HUMAN_TRAFFICKING"), ("偷渡", "HUMAN_TRAFFICKING"), ("組織犯罪", "ORGANISED_CRIME"),
        ("資恐", "TERRORISM"), ("恐怖攻擊", "TERRORISM"), ("恐怖組織", "TERRORISM"),
        ("違反槍砲", "ARMS_TRAFFICKING"), ("槍械", "ARMS_TRAFFICKING"),
        ("擄人勒贖", "VIOLENT_CRIME"), ("妨害自由", "VIOLENT_CRIME"), ("綁架", "VIOLENT_CRIME"), ("殺人", "VIOLENT_CRIME"), ("強盜", "VIOLENT_CRIME"),
        ("竊盜", "THEFT"), ("搶奪", "THEFT"), ("贓物", "THEFT"), ("恐嚇", "EXTORTION"), ("勒索", "EXTORTION"),
        ("妨害性自主", "SEXUAL_OFFENCE"), ("性剝削", "SEXUAL_OFFENCE"),
        ("盜採", "ENVIRONMENTAL_CRIME"), ("非法傾倒", "ENVIRONMENTAL_CRIME"), ("違反公司法", "CORPORATE_CRIME"),
        ("公共危險", null), ("賭博", null), ("傷害", null),
    }.OrderByDescending(w => w.Item1.Length).ToArray();

    // The date written, resolved against the publication date (TimeML's document creation time; HeidelTime's news
    // strategy): 民國 and two or three digit years add 1911; 昨（6）日, 6日 and 5月6日 take the latest such day not after
    // publication; 昨天, 前天, 去年, 上月 count back from it. 日前 and 上週 name no day and stay unresolved.
    private static PartialDate? ResolveDate(string? raw, PartialDate? published)
    {
        if (raw is null) return null;
        var t = CjkText.HalfWidth(raw).Replace('（', '(').Replace('）', ')');
        if (AbsoluteDate().Match(t) is { Success: true } a)
        {
            var year = Number(a.Groups["y"]);
            var latest = published?.Year ?? DateTime.UtcNow.Year;
            if (a.Groups["roc"].Success || year < 1000) year += 1911;
            return year <= latest + 1 ? Day(year, Number(a.Groups["m"]), a.Groups["d"].Success ? Number(a.Groups["d"]) : null) : null;
        }
        if (published is not { } p) return null;
        if (MonthDay().Match(t) is { Success: true } md)
        {
            var month = Number(md.Groups["m"]);
            int? day = md.Groups["d"].Success ? Number(md.Groups["d"]) : null;
            if (md.Groups["rel"].Success) return Day(md.Groups["rel"].Value == "去" ? p.Year - 1 : p.Year, month, day);
            var year = p.Month is { } pm && (month > pm || month == pm && day > (p.Day ?? 31)) ? p.Year - 1 : p.Year;
            // 2月29日 is the latest such day that exists: back to the last leap year.
            for (var back = 0; back < 8; back++)
                if (Day(year - back, month, day) is { } found) return found;
            return null;
        }
        if (DayOfMonth().Match(t) is { Success: true } dm && p is { Month: { } m0, Day: { } d0 })
        {
            var day = Number(dm.Groups["d"]);
            var month = new DateOnly(p.Year, m0, 1);
            if (day > d0) month = month.AddMonths(-1);
            // 31日 published on 1 March is 31 January: February has no 31st.
            for (var back = 0; back < 3; back++, month = month.AddMonths(-1))
                if (Day(month.Year, month.Month, day) is { } found) return found;
            return null;
        }
        if (p is { Month: { } mm, Day: { } dd })
        {
            var today = new DateOnly(p.Year, mm, dd);
            DateOnly? back = t switch { "昨天" or "昨日" => today.AddDays(-1), "今天" or "今日" => today, "前天" or "前日" => today.AddDays(-2), _ => null };
            if (back is { } b) return new PartialDate(b.Year, b.Month, b.Day);
        }
        return t switch
        {
            "去年" => new PartialDate(p.Year - 1),
            "今年" => new PartialDate(p.Year),
            "上月" when p.Month is { } um => um == 1 ? new PartialDate(p.Year - 1, 12) : new PartialDate(p.Year, um - 1),
            "本月" when p.Month is { } bm => new PartialDate(p.Year, bm),
            _ => null,
        };

        static int Number(Group g) => int.Parse(g.Value, NumberStyles.None, CultureInfo.InvariantCulture);

        static PartialDate? Day(int year, int month, int? day) =>
            month is < 1 or > 12 || year < 1800 ? null
            : day is null ? new PartialDate(year, month)
            : day >= 1 && day <= DateTime.DaysInMonth(year, month) ? new PartialDate(year, month, day) : null;
    }

    [GeneratedRegex(@"^(?<roc>民國)?(?<y>\d{2,4})年(?<m>\d{1,2})月(?:(?<d>\d{1,2})日)?$")]
    private static partial Regex AbsoluteDate();

    [GeneratedRegex(@"^(?:(?<rel>去|今)年)?(?<m>\d{1,2})月(?:(?<d>\d{1,2})日)?$")]
    private static partial Regex MonthDay();

    [GeneratedRegex(@"^(?:[昨今前]\()?(?<d>\d{1,2})\)?日$")]
    private static partial Regex DayOfMonth();

    private static string Quote(string sentence)
    {
        var s = sentence.Trim();
        if (s.Length <= MaxQuote) return s;
        var cut = char.IsHighSurrogate(s[MaxQuote - 1]) ? MaxQuote - 1 : MaxQuote;
        return s[..cut] + "…";
    }

    // ---- Triggers and slot patterns ---------------------------------------------------------------------------

    [GeneratedRegex("偵辦|偵查(?![隊佐庭員])|調查(?![局處站官員])|立案")]
    private static partial Regex InvestigationPattern();

    [GeneratedRegex("搜索|搜查")]
    private static partial Regex SearchPattern();

    [GeneratedRegex("約談|傳喚|訊問|複訊|到案說明|通知到案")]
    private static partial Regex QuestioningPattern();

    [GeneratedRegex("逮捕|逕行拘提|拘提|拘捕|緝獲|緝捕|落網")]
    private static partial Regex ArrestPattern();

    [GeneratedRegex("移送|函送|送辦")]
    private static partial Regex ReferralPattern();

    // Listed before detention: 停止羈押 and 撤銷羈押 end a detention.
    [GeneratedRegex("交保|具保|責付|限制出境|限制出海|限制住居|停止羈押|撤銷羈押|獲釋|釋放|假釋|無保請回")]
    private static partial Regex BailPattern();

    [GeneratedRegex("羈押|收押|聲押|延押")]
    private static partial Regex DetentionPattern();

    [GeneratedRegex("扣押|查扣|凍結|查封|禁止處分|警示帳戶|圈存")]
    private static partial Regex AssetRestraintPattern();

    [GeneratedRegex("提起公訴|聲請簡易判決處刑")]
    private static partial Regex IndictmentPattern();

    [GeneratedRegex("獲判無罪|判無罪|無罪確定|無罪定讞")]
    private static partial Regex AcquittalPattern();

    [GeneratedRegex("非常上訴|提起上訴|上訴|抗告|聲請再審|發回更審|更[一二三四五]審")]
    private static partial Regex AppealPattern();

    [GeneratedRegex("沒收|追徵|追繳")]
    private static partial Regex ConfiscationPattern();

    // 罰金 is the criminal court's; 易科罰金 converts a prison term and is no fine of its own.
    [GeneratedRegex("(?<!易科)罰金|罰款")]
    private static partial Regex FinePattern();

    // 罰鍰 and the like are a regulator's or an exchange's, not a court's.
    [GeneratedRegex("罰鍰|裁罰|核處|糾正|違約金|過怠金|停止買賣|停業|解除[^，,]{0,6}職務|撤銷[^，,]{0,4}許可")]
    private static partial Regex RegulatoryPattern();

    [GeneratedRegex("通緝")]
    private static partial Regex WantedPattern();

    [GeneratedRegex("引渡|遣返|遣送|押解回[臺台]|解送回[臺台]")]
    private static partial Regex ExtraditionPattern();

    [GeneratedRegex("(?<!其)他(?![們人])|她(?!們)|該男|該女|渠(?!道)|(?<![尤極])其(?![他中餘實後次])")]
    private static partial Regex Pronoun();

    // The slot patterns that hold an open run are non-backtracking: a long digit run with no 元 or 年 is read once, not
    // once from every digit in it.
    [GeneratedRegex(@"(?:民國)?\d{2,4}年\d{1,2}月(?:\d{1,2}日)?|(?:去|今)年\d{1,2}月(?:\d{1,2}日)?|\d{1,2}月(?:\d{1,2}日)?|[昨今前][（(]\d{1,2}[）)]日|\d{1,2}日|昨天|昨日|今天|今日|前天|前日|日前|去年|今年|上月|本月|上週", RegexOptions.NonBacktracking)]
    private static partial Regex DatePattern();

    [GeneratedRegex(@"\p{IsCJKUnifiedIdeographs}{2}(?:地方檢察署|地檢署|地方法院|地院|分局|地檢)|法務部調查局|\p{IsCJKUnifiedIdeographs}{2}市?調查[處站]|洗錢防制處|最高法院|最高檢察署|最高檢|[臺台]灣高等(?:法院|檢察署)|高等法院|高院|高檢署|智慧財產及商業法院|調查局|刑事警察局|刑事局|廉政署|金融監督管理委員會|金管會|證期局|銀行局|保險局|檢查局|證交所|櫃買中心|海巡署|移民署|國稅局|法務部|新北檢|(?:[北士桃竹苗中彰投雲嘉南雄橋屏宜花東基澎金]檢|北院)(?![方察舉查驗測討視])")]
    private static partial Regex AgencyPattern();

    [GeneratedRegex(@"(?:逾|超過|近|約|高達|上看)?(?:新[台臺]幣|台幣|NT\$|美金|美元|人民幣|港幣|日圓)?[\d,.一二三四五六七八九十兩百千]+(?:多|餘)?[萬億]*(?:多|餘)?(?:美元|元)", RegexOptions.NonBacktracking)]
    private static partial Regex AmountPattern();

    // 判處3年 keeps only 3年 (group years): non-backtracking takes no lookbehind.
    [GeneratedRegex(@"有期徒刑[\d一二三四五六七八九十兩]+年(?:[\d一二三四五六七八九十兩]+個?月)?|(?:判處|判刑)(?<years>[\d一二三四五六七八九十兩]+年(?:[\d一二三四五六七八九十兩]+個?月)?)|無期徒刑|死刑|拘役[\d一二三四五六七八九十兩]+日|緩刑[\d一二三四五六七八九十兩]+年|併科罰金(?:新[台臺]幣)?[\d,.一二三四五六七八九十兩百千萬億]+元|得易科罰金|易科罰金|褫奪公權[\d一二三四五六七八九十兩]+年", RegexOptions.NonBacktracking)]
    private static partial Regex PenaltyPattern();

    // 求刑 is what the prosecutor asks, not a sentence.
    [GeneratedRegex(@"(?:具體)?求(?:刑|處)(?:有期徒刑)?(?<years>[\d一二三四五六七八九十兩]+年(?:[\d一二三四五六七八九十兩]+個?月)?)|求處無期徒刑|求處死刑", RegexOptions.NonBacktracking)]
    private static partial Regex RequestedPenaltyPattern();

    [GeneratedRegex("判決確定|定讞|全案確定|確定在案")]
    private static partial Regex FinalPattern();

    [GeneratedRegex("可上訴|得上訴|可提起上訴|未定讞|未確定")]
    private static partial Regex NotFinalPattern();

    // ---- Summary -----------------------------------------------------------------------------------------------

    // Fixed sentence patterns over the people and events above (Reiter & Dale: the events are the content, ordered by
    // date when every one has a date, else as reported); nothing is reworded or added.
    private static List<string> Summary(NewsItem news, string sourceRef, List<NewsPerson> people, List<NewsEvent> events)
    {
        var cite = sourceRef.Length > 0 ? $"[{sourceRef}]" : "";
        var lines = new List<string> { $"{news.Publisher}{(news.PublishedOn is { } d ? $"（{d}）" : "")}報導「{news.Title}」{cite}。" };
        if (people.Count > 0) lines.Add($"報導人物：{string.Join("、", people.Select(PersonLabel))}。");
        // The people in their order, then any actor past the people cap, then events with nobody named.
        var actors = people.Select(p => p.Name).Concat(events.Select(e => e.Actor).OfType<string>()).Distinct(StringComparer.Ordinal).Select(a => (string?)a).Append(null);
        foreach (var actor in actors)
        {
            var own = events.Where(e => e.Actor == actor).DistinctBy(e => (e.Type, e.Certainty)).Take(MaxPhrases).ToList();
            if (own.TrueForAll(e => e.DateValue is not null)) own = [.. own.OrderBy(e => e.DateValue!.Value.LatestDay)];
            if (own.Count > 0) lines.Add($"{actor ?? "未指明對象"}：{string.Join("；", own.Select(Phrase))}{cite}。");
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
            NewsCertainty.Hedged => Label(e.Type) + "（報導為推測、條件或疑問）",
            NewsCertainty.Requested => "聲請" + Label(e.Type) + "（尚未裁定）",
            _ => Label(e.Type),
        };
        var extra = new List<string>();
        if (e.Offences.Count > 0) extra.Add(string.Join("、", e.Offences));
        if (e.Amount is { } amount) extra.Add(amount);
        if (e.Penalty is { } penalty) extra.Add(penalty);
        if (e.RequestedPenalty is { } asked) extra.Add("求刑" + asked);
        if (e.Final) extra.Add("判決確定");
        if (SourceLabel(e.Source) is { } source) extra.Add(source);
        var date = e.DateValue?.ToString() ?? e.Date;
        return $"{(date is null ? "" : date + " ")}{e.Agency}{verb}{(extra.Count > 0 ? $"（{string.Join("；", extra)}）" : "")}";
    }

    private static string? SourceLabel(NewsSource source) => source switch
    {
        NewsSource.Investigators => "檢警調說法",
        NewsSource.Court => "法院認定",
        NewsSource.Defence => "被告方說法",
        NewsSource.UnnamedSource => "不具名消息",
        _ => null,
    };

    private static string Label(NewsEventType type) => type switch
    {
        NewsEventType.Investigation => "偵辦或調查",
        NewsEventType.Search => "搜索",
        NewsEventType.Questioning => "約談",
        NewsEventType.Arrest => "逮捕或拘提",
        NewsEventType.Referral => "移送",
        NewsEventType.Detention => "羈押",
        NewsEventType.Bail => "交保或釋放",
        NewsEventType.AssetRestraint => "扣押或凍結",
        NewsEventType.Indictment => "起訴",
        NewsEventType.NonIndictment => "不起訴",
        NewsEventType.Conviction => "判決有罪",
        NewsEventType.Acquittal => "判決無罪",
        NewsEventType.Appeal => "上訴或更審",
        NewsEventType.Confiscation => "沒收或追徵",
        NewsEventType.Fine => "罰金",
        NewsEventType.RegulatoryPenalty => "行政裁罰",
        NewsEventType.WantedNotice => "通緝",
        NewsEventType.Extradition => "遣返或引渡",
        _ => Code(type),
    };
}
