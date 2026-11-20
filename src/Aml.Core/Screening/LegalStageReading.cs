using System.Text.RegularExpressions;
using Aml.Core.Normalization;

namespace Aml.Core.Screening;

/// <summary>
/// The legal stage that attached news tells of the Profile person. <see cref="ReportText.Prepare"/> reads the text once
/// (quoted speech, another person's bracketed asides and reported speech blanked out; the names marked out where they
/// stand for a relative or a bystander); the text is then read sentence by sentence and clause by clause, and in a clause
/// that is the person's the <see cref="Transitions"/> are the rules, each one way a clause moves the stage; the first
/// that speaks gives the new stage. A judgment's or court release's order (<see cref="RulingReading"/>) wins over what
/// the clauses tell. A new way a report tells a stage is a new transition and its place in that list, or a new pattern.
/// </summary>
/// <remarks>
/// ponytail: keyword heuristic on attached news. Replace with approved legal-stage extraction when judgment wording
/// appears in a Golden Case.
/// - A judgment (conviction, acquittal) counts only in a sentence naming the Profile person: co-defendants' sentences
///   are not theirs (GC-12).
/// - A dismissal or indictment counts in such a sentence, or in one that points at nobody else: a subjectless 檢方已起訴
///   or a collective 起訴七人 (GC-12) is the person's; 同案, 另一, 其他 … mark someone else. Negation goes first:
///   不起訴, 緩起訴 and 撤回起訴 are DISMISSED, never INDICTED.
/// - A modal, request, plan, condition or hearsay before the keyword in its clause (可, 恐, 求, 最重, 推定, 擬, 預計, 即將,
///   將會, 若, 如果, 一旦, 據傳) is no stage: 最重可判處, 請求從重判處, 恐遭起訴, 擬起訴. Words that only hold those
///   characters (恐嚇, 恐怖, 恐龍, 可疑, 可是, 許可, 求償, 要求, 模擬, 若干) are not modals.
/// - A negation or question just before the keyword (未, 沒有, 並未, 尚未, 從未, 不會, 無, 是否, with 被/遭/受 between)
///   is no stage: 尚未起訴, 沒有被起訴, 是否起訴. 不予起訴 and 免予起訴 are DISMISSED.
/// - Quoted speech (「…」, 『…』, “…”) is a claim, not a report: no stage is read inside it, but names still are.
/// - A clause whose own subject is another person is not the Profile person's, even in a sentence that names them:
///   王大明與李四涉詐欺，李四遭起訴. The clause must open (past 而, 但, 隨後 …) with a 2 or 3 character name of a common
///   surname other than the person's own, or "X姓男子", right before 遭, 被 or 獲 (李四遭, 林姓男子被); 王男遭 for 王大明
///   is the person, and 施用毒品遭 or 他曾涉案遭 open with no name.
///   無罪 is an acquittal only as a judgment's word (see Acquittal; 堅稱無罪 is a claim).
/// - The last stage in the text wins; 定讞 alone settles what came before, and 通緝 is WANTED only while no judicial
///   stage is told. 偵辦 or 調查 give ALLEGED only when there is no stage.
/// The full rules, with examples, are in docs/feature/watchlist-and-adverse-information.md (Stage from attached news).
/// </remarks>
internal static partial class LegalStageReading
{
    /// <summary>The stage, and the code of the rule that gave it (null exactly when the stage is UNKNOWN).</summary>
    internal readonly record struct StageReading(LegalStage Stage, string? Basis);

    internal static StageReading FromNews(string? news, IReadOnlyList<string> names)
    {
        if (news is null) return new(LegalStage.Unknown, null);
        var stage = LegalStage.Unknown;
        string? basis = null; // the code of the transition that last set the stage
        var text = ReportText.Prepare(news, names);
        var (judgment, ordered, defendant, sole, orderBasis) = RulingReading.Read(news, names);
        var suspected = false;
        // A judgment names many people besides its defendants (witnesses, account holders, accomplices tried elsewhere):
        // for them its subjectless 起訴 is the defendants', and 起訴書, 起訴範圍 and 起訴意旨 are the case's papers.
        var bystander = judgment && !defendant;
        foreach (var (start, length) in StageWords.Spans(text.Blanked, 0, text.Blanked.Length, StageWords.SentenceEnds))
        {
            var sentence = SentenceScope.Of(text, start, length, judgment, bystander, sole);
            suspected |= sentence.Own && (Suspicion().IsMatch(sentence.Text) || Group().IsMatch(sentence.Text)); // a list of names ending 等9名 is the suspects'
            // 佯稱被害人涉案，已遭通緝: what a fraudster told the victim, to the end of that clause and through the next one.
            // No further: the sentence may go on to the fraudster's own arrest and indictment.
            var claimed = false;
            var theirPart = false; // the clause before this one named the person and what they are charged with (…所涉犯嫌)
            foreach (var (at, size) in StageWords.Spans(text.Blanked, start, length, StageWords.ClauseEnds))
            {
                var clause = text.Blanked.Substring(at, size);
                // …unless the next clause opens with the passive: 陳志明佯稱可獲利，遭起訴 is the fraudster's own indictment.
                if (claimed) { claimed = false; if (!OpensPassive().IsMatch(clause)) continue; }
                if (Pretence().Match(clause) is { Success: true } pretence) (claimed, clause) = (true, clause[..pretence.Index]);
                if (bystander) clause = IndictmentPaper().Replace(clause, m => new string('　', m.Length));
                // 王大明、李小四本件所涉犯嫌，業經提起公訴: the indictment is of those the clause before names.
                var mine = StageWords.MentionsAnyName(text.Named.Substring(at, size), names) || (theirPart && ChargedPart().IsMatch(clause));
                theirPart = StageWords.MentionsAnyName(text.Named.Substring(at, size), names) && TheirPart().IsMatch(clause);
                if (!mine && (text.Named.AsSpan(at, size).Contains(RelationMark) || NamesAnotherPerson(clause, text.Surnames) || IndictsAnotherPerson(clause, text.Surnames, news))) continue;
                if (StageWords.FirstOf(Transitions, new ClauseScope(clause, mine, sentence, sole, bystander, names, stage)) is { } next) (stage, basis) = next;
            }
        }
        if (ordered is { } outcome) return new(outcome, orderBasis); // a judgment's order is its outcome
        if (defendant && stage == LegalStage.Unknown) return new(LegalStage.Indicted, StageBasis.ListedDefendant); // a listed defendant stands indicted, whatever the order goes on to say
        // 偵辦 or 調查 somewhere in the text is the person's when a sentence naming them says they are under suspicion or
        // names a step (涉嫌, 約談, 移送 …); someone the text names only as a counterpart in the facts is not ALLEGED by it.
        // A text that never writes the Profile's names is about them as a whole.
        var underInvestigation = !bystander && (suspected || !StageWords.MentionsAnyName(text.Named, names))
            && (text.Blanked.Contains("偵辦", StringComparison.Ordinal) || text.Blanked.Contains("調查", StringComparison.Ordinal));
        return stage == LegalStage.Unknown && underInvestigation ? new(LegalStage.Alleged, StageBasis.UnderInvestigation) : new(stage, basis);
    }

    // ---- The text, read once ----

    private sealed record ReportText(IReadOnlyList<string> Names, HashSet<char> Surnames, string Blanked, string Named)
    {
        /// <summary>The text with what is not a report of the person blanked out, keeping every position; the names marked out where they stand for someone else.</summary>
        public static ReportText Prepare(string news, IReadOnlyList<string> names)
        {
            var surnames = names.Where(n => n.Length > 1 && CjkText.IsCjk(n[0])).Select(n => n[0]).ToHashSet();
            var kept = BlankQuotes(news).ToCharArray();
            BlankOthersAsides(kept, news, names, surnames);
            BlankReportedSpeech(kept, news);
            return new(names, surnames, new string(kept), WithoutRelations(news, names));
        }

        // Quoted speech is blanked out, keeping every position: stages are looked for in the blanked text, names in the
        // original (綽號「阿明」的王大明 still names the person).
        private static string BlankQuotes(string news) => StageWords.Quoted().Replace(news, m => new string('　', m.Length));

        // An aside in brackets is about whoever stands right before it: 李小四（另案起訴）、陳志明 says nothing of 陳志明.
        // The brackets are paired in the text as written (a quote blanked out may have held one of a pair).
        private static void BlankOthersAsides(char[] kept, string news, IReadOnlyList<string> names, HashSet<char> surnames)
        {
            foreach (Match m in Aside().Matches(news))
                if (!GroupAside().IsMatch(m.Value) && AfterAnotherName(news, m.Index, names, surnames)) kept.AsSpan(m.Index, m.Length).Fill('　');
        }

        // 辯稱遭人陷害云云, 稱其帳戶已遭通緝云云: from the verb of saying to 云云 is what someone said.
        private static void BlankReportedSpeech(char[] kept, string news)
        {
            foreach (Match m in Reported().Matches(news)) kept.AsSpan(m.Index, m.Length).Fill('　');
        }
    }

    /// <summary>What a sentence of the report says of the person, and whose steps it may tell.</summary>
    private readonly record struct SentenceScope(string Text, bool Own, bool Attributable, bool Collective)
    {
        public static SentenceScope Of(ReportText text, int start, int length, bool judgment, bool bystander, bool sole)
        {
            var sentence = text.Blanked.Substring(start, length);
            var others = OtherParty.Any(m => sentence.Contains(m, StringComparison.Ordinal));
            // A judgment or ruling with one defendant goes on about 被告 without the name: that is the person.
            var own = StageWords.MentionsAnyName(text.Named.Substring(start, length), text.Names) || (sole && !others && sentence.Contains("被告", StringComparison.Ordinal) && !NotThisCase().IsMatch(sentence));
            var relation = text.Named.AsSpan(start, length).Contains(RelationMark);
            var attributable = own || (!bystander && !relation && !others);
            // 李小四等3人潛逃出境，發布通緝: outside a judgment a wanted notice said of a group is everyone's.
            var collective = attributable && !judgment && Group().IsMatch(sentence);
            return new(sentence, own, attributable, collective);
        }
    }

    /// <summary>A clause that is the person's (or may be), after the pretence cut and the indictment papers blanked out; <see cref="Stage"/> is the stage read so far.</summary>
    private readonly record struct ClauseScope(string Clause, bool Mine, SentenceScope Sentence, bool Sole, bool Bystander, IReadOnlyList<string> Names, LegalStage Stage);

    // ---- The transitions, in the order of Transitions ----

    // The order of this list is the precedence: a stage keyword of the clause first; the others only where it has none.
    private static readonly StageRule<ClauseScope>[] Transitions =
    [
        new(StageBasis.Keyword, KeywordStage), new(StageBasis.SentenceOpeningClause, SentencedOpening),
        new(StageBasis.FinalSettles, FinalSettles), new(StageBasis.WantedNotice, WantedWhileNoStage),
    ];

    // Someone a judgment names who is not its defendant gets a conviction or acquittal only from the clause
    // naming them: 被告依王大明指示…，量處被告有期徒刑七年 sentences the defendant.
    private static LegalStage? KeywordStage(ClauseScope c) =>
        LastStageIn(c.Clause, c.Sentence.Attributable, c.Sentence.Own && (!c.Bystander || c.Mine));

    // 處有期徒刑二年 opening a clause is the person's when the judgment has no other defendant, or when the
    // sentence says whom the court ruled on: 判決被告王大明犯詐欺罪，處有期徒刑二年.
    private static LegalStage? SentencedOpening(ClauseScope c) =>
        c.Sentence.Own && Says(c.Clause, Sentenced()) && (c.Sole || c.Names.Take(100).Any(n => n.Length > 1
            && (c.Sentence.Text.Contains("判決被告" + n, StringComparison.OrdinalIgnoreCase) || c.Sentence.Text.Contains("判決" + n, StringComparison.OrdinalIgnoreCase)))) ? LegalStage.Convicted : null;

    // 定讞 alone settles what came before: an indictment (or nothing said) ends in a conviction, while
    // 不起訴處分，全案定讞 and 判決無罪，全案定讞 stay what they are.
    // 共犯李小四日前定讞 or 另案定讞 settles someone or something else.
    private static LegalStage? FinalSettles(ClauseScope c) =>
        c.Sentence.Own && c.Stage is LegalStage.Unknown or LegalStage.Indicted && Says(c.Clause, StageWords.Final())
            && !OtherParty.Any(m => c.Clause.Contains(m, StringComparison.Ordinal)) && !c.Clause.Contains("另案", StringComparison.Ordinal) ? LegalStage.Convicted : null;

    // A wanted notice is a stage only until a judicial one is told, before or after it (起訴後棄保遭通緝 stays INDICTED).
    private static LegalStage? WantedWhileNoStage(ClauseScope c) =>
        (c.Sentence.Own || c.Sentence.Collective) && c.Stage == LegalStage.Unknown && Says(c.Clause, StageWords.WantedNotice()) ? LegalStage.Wanted : null;

    private static LegalStage? LastStageIn(string clause, bool attributable, bool own)
    {
        var found = new List<(int End, LegalStage Stage)>();
        var modalEnd = StageWords.FirstModalEnd(clause); // once per clause: a modal ending at or before a keyword qualifies it
        if (attributable)
        {
            Find(found, clause, StageWords.Dismissal(), LegalStage.Dismissed, modalEnd);
            Find(found, clause, StageWords.Indictment(), LegalStage.Indicted, modalEnd);
        }
        if (own)
        {
            // 提起公訴 is a judgment's word for the indictment, and a judgment names many people: only with the person's name.
            // Tried and left out: reading 對李小四等4人提起公訴 in a report for everyone the text names. On the second dev
            // set it gave the indictment to five people the group did not hold and to three it did.
            Find(found, clause, StageWords.PublicProsecution(), LegalStage.Indicted, modalEnd);
            Find(found, clause, StageWords.Conviction(), LegalStage.Convicted, modalEnd);
            Find(found, clause, StageWords.Acquittal(), LegalStage.Acquitted, modalEnd);
        }
        return found.Count > 0 ? found.MaxBy(f => f.End).Stage : null;
    }

    private static bool Says(string clause, Regex pattern)
    {
        var found = new List<(int, LegalStage)>();
        Find(found, clause, pattern, LegalStage.Unknown, StageWords.FirstModalEnd(clause));
        return found.Count > 0;
    }

    private static void Find(List<(int, LegalStage)> found, string clause, Regex pattern, LegalStage stage, int modalEnd)
    {
        foreach (Match m in pattern.Matches(clause))
            if (modalEnd > m.Index && !StageWords.Negated(clause, m.Index)) found.Add((m.Index + m.Length, stage));
    }

    // ---- Patterns: masking ----

    // 李小四、陳志明（均遭起訴）: an aside that opens with a word for all of them is the whole list's.
    [GeneratedRegex("^[（(](?:均|皆|同|等|[兩二三四五]人)")]
    private static partial Regex GroupAside();

    [GeneratedRegex("[（(][^（）()。\n]{1,200}[）)]", RegexOptions.NonBacktracking)]
    private static partial Regex Aside();

    // The two or three characters before the bracket read as another person's name: a common surname other than the
    // Profile person's own, then ideographs or ○ (李小四, 李○○, 李四). After one of the Profile's names it is theirs.
    // ponytail: a noun opening with a surname character (高層（已起訴）) is taken for a name too.
    private static bool AfterAnotherName(string news, int bracket, IReadOnlyList<string> names, HashSet<char> ownSurnames)
    {
        var before = news.AsSpan(0, bracket);
        if (names.Take(100).Any(n => n.Length > 1 && news.AsSpan(0, bracket).EndsWith(n, StringComparison.OrdinalIgnoreCase))) return false;
        foreach (var length in new[] { 3, 2 })
            if (before.Length >= length && before[^length..] is var name && StageWords.CommonSurnames.Contains(name[0], StringComparison.Ordinal)
                && !ownSurnames.Contains(name[0]) && name[1..].IndexOfAnyExcept(NameCharacters) < 0) return true;
        return false;
    }

    private static readonly System.Buffers.SearchValues<char> NameCharacters = System.Buffers.SearchValues.Create(
        "○" + string.Concat(Enumerable.Range(0x4E00, 0x9FFF - 0x4E00 + 1).Select(c => (char)c)));

    // From the verb nearest to 云云, over one comma at most: 號稱投資達人，遭判處有期徒刑二年，他辯稱無辜云云 leaves the
    // sentence in place, and so does 陳志明稱其無辜，法院仍判處有期徒刑二年，不服上訴云云 (too far to be the same saying).
    [GeneratedRegex(@"(?:辯稱|佯稱|謊稱|誆稱|訛稱|詐稱|聲稱|宣稱|供稱|陳稱|堅稱|表示|主張|(?<![下簡名俗職號堪又暱人對])稱)(?:(?!辯稱|佯稱|謊稱|誆稱|訛稱|詐稱|聲稱|宣稱|供稱|陳稱|堅稱|表示|主張|稱)[^。！？；\n，,]){0,80}(?:[，,](?:(?!辯稱|佯稱|謊稱|誆稱|訛稱|詐稱|聲稱|宣稱|供稱|陳稱|堅稱|表示|主張|稱)[^。！？；\n，,]){0,80})?云云")]
    private static partial Regex Reported();

    [GeneratedRegex("起訴(?:書|範圍|意旨)")]
    private static partial Regex IndictmentPaper();

    // ---- Patterns: whose sentence or clause it is ----

    // 王大明的哥哥遭起訴, 王大明之妻李四遭判刑: the one spoken of is the relative, aide or lawyer, so the Profile name right
    // before 的 / 之 and such a word is marked out, keeping every position. A sentence that names the person only this way
    // is someone else's, and so is such a clause in a sentence that also names the person outright.
    // ponytail: a fixed word list, and the relation must follow 的 or 之 (王大明胞弟 still reads as the person).
    private static string WithoutRelations(string news, IReadOnlyList<string> names)
    {
        var chars = news.ToCharArray();
        foreach (Match m in StageWords.Relation().Matches(news))
            if (names.Take(100).FirstOrDefault(n => n.Length > 1 && news.AsSpan(0, m.Index).EndsWith(n, StringComparison.OrdinalIgnoreCase)) is { } name)
                chars.AsSpan(m.Index - name.Length, name.Length).Fill(RelationMark);
        // 被害人王大明報案, 證人王大明指出: named in that role, the sentence's steps are someone else's.
        foreach (Match m in Bystander().Matches(news))
            if (names.Take(100).FirstOrDefault(n => n.Length > 1 && news.AsSpan(m.Index + m.Length).StartsWith(n, StringComparison.OrdinalIgnoreCase)) is { } name)
                chars.AsSpan(m.Index + m.Length, name.Length).Fill(RelationMark);
        return new string(chars);
    }

    private const char RelationMark = '￼';

    [GeneratedRegex("被害人|受害人|證人|告訴人|告發人|檢舉人")]
    private static partial Regex Bystander();

    // A sole defendant's 被告 sentence about another case, an earlier one, or the law in general is not this case's step.
    [GeneratedRegex("另案|該案|他案|前案|前因|前科|規定|條第")]
    private static partial Regex NotThisCase();

    [GeneratedRegex(@"所涉[^，,。]{0,8}$")]
    private static partial Regex TheirPart();

    // Not 經檢察官對李小四提起公訴 (another's) or 撤回.
    [GeneratedRegex(@"^\s*[均業已亦]{0,3}經[^，,。對撤]{0,12}提起公訴")]
    private static partial Regex ChargedPart();

    [GeneratedRegex(@"^\s*(?:遭|被|經)")]
    private static partial Regex OpensPassive();

    [GeneratedRegex("佯稱|謊稱|誆稱|訛稱|詐稱")]
    private static partial Regex Pretence();

    // ---- Patterns: another person ----

    private static readonly string[] OtherParty = ["同案", "另一", "另名", "其他", "其餘", "共犯", "共同被告"];

    // The clause's own subject is another person: it opens (past a connective) with a name of a common surname other
    // than the person's own, 2 or 3 characters, or "X姓男子", right before 遭, 被 or 獲. A clause that opens with
    // anything else (施用毒品遭, 高院二審被, 他曾涉案遭) is not taken as naming someone.
    private static bool NamesAnotherPerson(string clause, HashSet<char> ownSurnames) =>
        OtherPersonOpening().Match(clause) is { Success: true } m && StageWords.CommonSurnames.Contains(m.Groups["surname"].Value[0], StringComparison.Ordinal)
        && !ownSurnames.Contains(m.Groups["surname"].Value[0]);

    // The clause's indictment or dismissal has another person for its object: 檢方已起訴李小四, 對李小四為不起訴處分.
    // Losing the Profile person's own indictment is the worse error, so the object must be a three-character name of a
    // common surname that the text writes at least twice (起訴高層, 起訴官員, 起訴金額 are no names), and the clause must
    // hold no character of the person's own surname (起訴李小四、王男 may include 王大明). 起訴李小四等七人 stays collective.
    // ponytail: a two-character name (李四) or one of a rare surname is not told apart; widen when a real case needs it.
    private static bool IndictsAnotherPerson(string clause, HashSet<char> ownSurnames, string news) =>
        !clause.Any(ownSurnames.Contains) && (OtherPersonObject().Matches(clause).Any(m =>
            StageWords.CommonSurnames.Contains(m.Groups["name"].Value[0], StringComparison.Ordinal)
            && news.IndexOf(m.Groups["name"].Value, StringComparison.Ordinal) != news.LastIndexOf(m.Groups["name"].Value, StringComparison.Ordinal))
            || OtherPersonJudged().Matches(clause).Any(m => StageWords.CommonSurnames.Contains(m.Groups["name"].Value[0], StringComparison.Ordinal)));

    // The same for a judgment or a wanted notice, where what follows the name tells it is one, written once or not:
    // 法院另判處李小四有期徒刑一年, 判李小四無罪, 警方通緝的李小四落網.
    // A two-character name counts here too (另判李四無罪); a title does not (判處董事長有期徒刑 may be the person).
    [GeneratedRegex(@"判[決處]?(?<name>\p{IsCJKUnifiedIdeographs}{1,2}[^\P{IsCJKUnifiedIdeographs}長員官人層嫌犯])(?=有期徒刑|無期徒刑|死刑|拘役|罰金|無罪|[0-9０-９一二三四五六七八九十兩]+(?:年|個月|月))|通緝的?(?<name>\p{IsCJKUnifiedIdeographs}{3})(?=落網|到案|歸案|$)")]
    private static partial Regex OtherPersonJudged();

    [GeneratedRegex(@"^\s*(?:而|但|則|另外|另|隨後|其後|後來|至於)?(?<surname>\p{IsCJKUnifiedIdeographs})(?:\p{IsCJKUnifiedIdeographs}{1,2}|姓(?:男子|女子|男|女|嫌犯|被告))(?:也|亦|則|已|均)?(?:遭到|遭|被|獲)")]
    private static partial Regex OtherPersonOpening();

    // The name ends the clause or goes on in a list (、及與和); 等 after it makes a group, which is not matched.
    [GeneratedRegex(@"起訴(?<name>\p{IsCJKUnifiedIdeographs}{3})(?=$|[、及與和\s])|對(?<name>\p{IsCJKUnifiedIdeographs}{3})(?:為|作成|予以)?(?:(?:不|緩)起訴|提起公訴)")]
    private static partial Regex OtherPersonObject();

    // ---- Patterns: stage keywords only a report's clauses use ----

    // 認被告犯…罪，處有期徒刑二年: a judgment's own wording, with no 判. Whose sentence it is the clause does not say, so it
    // is read only where the text has one defendant.
    [GeneratedRegex(@"^\s*(?:各|均)?處(?:有期徒刑|拘役|無期徒刑|死刑)")]
    private static partial Regex Sentenced();

    // ---- Patterns: groups and suspicion ----

    [GeneratedRegex("(?<![平同相對])等共?[0-9０-９一二兩三四五六七八九十百餘多數]*(?:人|名|嫌|被告)")]
    private static partial Regex Group();

    [GeneratedRegex("涉|嫌|偵辦|調查|逮捕|拘提|約談|羈押|收押|移送|函送|交保|搜索|查獲|到案|被告|共犯|首腦|主謀|成員|車手|起訴|通緝")]
    private static partial Regex Suspicion();
}
