using System.Text.RegularExpressions;
using Aml.Core.Normalization;

namespace Aml.Core.Screening.Stage;

/// <summary>
/// The form of a news report: what is blanked out before stages are looked for (quoted speech, another person's bracketed
/// asides, reported speech), where a name stands for a relative or a bystander, and whose a sentence or a clause is. It
/// says what the text holds, never what that means for the stage (<see cref="ReportRules"/>).
/// </summary>
internal static class ReportForm
{
    // Marks a name that stands for someone else in ReportText.Named, keeping every position.
    private const char RelationMark = '￼';

    // ---- The text, read once ----

    /// <summary>The text with what is not a report of the person blanked out, keeping every position; the names marked out where they stand for someone else.</summary>
    internal static ReportText Prepare(string news, IReadOnlyList<string> names)
    {
        var surnames = names.Where(n => n.Length > 1 && CjkText.IsCjk(n[0])).Select(n => n[0]).ToHashSet();
        var kept = BlankQuotes(news).ToCharArray();
        BlankOthersAsides(kept, news, names, surnames);
        BlankReportedSpeech(kept, news);
        return new(names, surnames, new string(kept), WithoutRelations(news, names));
    }

    // Quoted speech is blanked out, keeping every position: stages are looked for in the blanked text, names in the
    // original (綽號「阿明」的王大明 still names the person).
    private static string BlankQuotes(string news) => StageText.Blank(StagePatterns.Quoted(), news);

    // An aside in brackets is about whoever stands right before it: 李小四（另案起訴）、陳志明 says nothing of 陳志明.
    // The brackets are paired in the text as written (a quote blanked out may have held one of a pair).
    private static void BlankOthersAsides(char[] kept, string news, IReadOnlyList<string> names, HashSet<char> surnames)
    {
        foreach (Match m in StagePatterns.Aside().Matches(news))
            if (!StagePatterns.GroupAside().IsMatch(m.Value) && AfterAnotherName(news, m.Index, names, surnames)) kept.AsSpan(m.Index, m.Length).Fill('　');
    }

    // 辯稱遭人陷害云云, 稱其帳戶已遭通緝云云: from the verb of saying to 云云 is what someone said.
    private static void BlankReportedSpeech(char[] kept, string news) => StageText.Blank(kept, StagePatterns.Reported(), news);

    // The two or three characters before the bracket read as another person's name: a common surname other than the
    // Profile person's own, then ideographs or ○ (李小四, 李○○, 李四). After one of the Profile's names it is theirs.
    // ponytail: a noun opening with a surname character (高層（已起訴）) is taken for a name too.
    private static bool AfterAnotherName(string news, int bracket, IReadOnlyList<string> names, HashSet<char> ownSurnames)
    {
        var before = news.AsSpan(0, bracket);
        if (names.Take(StageGlossary.MaxNames).Any(n => n.Length > 1 && news.AsSpan(0, bracket).EndsWith(n, StringComparison.OrdinalIgnoreCase))) return false;
        foreach (var length in new[] { 3, 2 })
            if (before.Length >= length && before[^length..] is var name && StageGlossary.CommonSurnames.Contains(name[0], StringComparison.Ordinal)
                && !ownSurnames.Contains(name[0]) && name[1..].IndexOfAnyExcept(NameCharacters) < 0) return true;
        return false;
    }

    private static readonly System.Buffers.SearchValues<char> NameCharacters = System.Buffers.SearchValues.Create(
        "○" + string.Concat(Enumerable.Range(0x4E00, 0x9FFF - 0x4E00 + 1).Select(c => (char)c)));

    // 王大明的哥哥遭起訴, 王大明之妻李四遭判刑: the one spoken of is the relative, aide or lawyer, so the Profile name right
    // before 的 / 之 and such a word is marked out, keeping every position. A sentence that names the person only this way
    // is someone else's, and so is such a clause in a sentence that also names the person outright.
    // ponytail: a fixed word list, and the relation must follow 的 or 之 (王大明胞弟 still reads as the person).
    private static string WithoutRelations(string news, IReadOnlyList<string> names)
    {
        var chars = news.ToCharArray();
        foreach (Match m in StagePatterns.Relation().Matches(news))
            if (names.Take(StageGlossary.MaxNames).FirstOrDefault(n => n.Length > 1 && news.AsSpan(0, m.Index).EndsWith(n, StringComparison.OrdinalIgnoreCase)) is { } name)
                chars.AsSpan(m.Index - name.Length, name.Length).Fill(RelationMark);
        // 被害人王大明報案, 證人王大明指出: named in that role, the sentence's steps are someone else's.
        foreach (Match m in StagePatterns.Bystander().Matches(news))
            if (names.Take(StageGlossary.MaxNames).FirstOrDefault(n => n.Length > 1 && news.AsSpan(m.Index + m.Length).StartsWith(n, StringComparison.OrdinalIgnoreCase)) is { } name)
                chars.AsSpan(m.Index + m.Length, name.Length).Fill(RelationMark);
        return new string(chars);
    }

    // ---- Whose a sentence is ----

    /// <summary>What a sentence of the report says of the person, and whose steps it may tell.</summary>
    internal static SentenceScope Scope(ReportText text, int start, int length, bool judgment, bool bystander, bool sole)
    {
        var sentence = text.Blanked.Substring(start, length);
        var others = StageText.ContainsAny(sentence, StageGlossary.OtherParty);
        // A judgment or ruling with one defendant goes on about 被告 without the name: that is the person.
        var own = StageText.MentionsAnyName(text.Named.Substring(start, length), text.Names) || (sole && !others && sentence.Contains(StageGlossary.Defendant, StringComparison.Ordinal) && !StagePatterns.NotThisCase().IsMatch(sentence));
        var relation = text.Named.AsSpan(start, length).Contains(RelationMark);
        var attributable = own || (!bystander && !relation && !others);
        // 李小四等3人潛逃出境，發布通緝: outside a judgment a wanted notice said of a group is everyone's.
        var collective = attributable && !judgment && StagePatterns.Group().IsMatch(sentence);
        return new(sentence, own, attributable, collective);
    }

    // ---- Whose a clause is ----

    // …unless the next clause opens with the passive: 陳志明佯稱可獲利，遭起訴 is the fraudster's own indictment.
    internal static bool OpensPassive(string clause) => StagePatterns.OpensPassive().IsMatch(clause);

    /// <summary>Where a pretence (佯稱, 謊稱 …) starts in the clause; -1 when it holds none.</summary>
    internal static int PretenceStart(string clause) => StagePatterns.Pretence().Match(clause) is { Success: true } pretence ? pretence.Index : -1;

    // 起訴書, 起訴範圍, 起訴意旨 are the case's papers, blanked out keeping every position.
    internal static string BlankIndictmentPapers(string clause) => StageText.Blank(StagePatterns.IndictmentPaper(), clause);

    /// <summary>
    /// The clause at [at, at+size) names the person, or (with <paramref name="theirPart"/> from the clause before) goes on
    /// with their charge: 王大明、李小四本件所涉犯嫌，業經提起公訴. Sets <paramref name="theirPart"/> for the next clause.
    /// </summary>
    internal static bool IsMine(ReportText text, int at, int size, string clause, IReadOnlyList<string> names, ref bool theirPart)
    {
        var mine = StageText.MentionsAnyName(text.Named.Substring(at, size), names) || (theirPart && StagePatterns.ChargedPart().IsMatch(clause));
        theirPart = StageText.MentionsAnyName(text.Named.Substring(at, size), names) && StagePatterns.TheirPart().IsMatch(clause);
        return mine;
    }

    /// <summary>The clause speaks of someone else: a name marked as a relation or bystander, another person as its subject, or as the object of its indictment.</summary>
    internal static bool SpeaksOfAnother(ReportText text, int at, int size, string clause, string news) =>
        text.Named.AsSpan(at, size).Contains(RelationMark) || NamesAnotherPerson(clause, text.Surnames) || IndictsAnotherPerson(clause, text.Surnames, news);

    // The clause's own subject is another person: it opens (past a connective) with a name of a common surname other
    // than the person's own, 2 or 3 characters, or "X姓男子", right before 遭, 被 or 獲. A clause that opens with
    // anything else (施用毒品遭, 高院二審被, 他曾涉案遭) is not taken as naming someone.
    private static bool NamesAnotherPerson(string clause, HashSet<char> ownSurnames) =>
        StagePatterns.OtherPersonOpening().Match(clause) is { Success: true } m && StageGlossary.CommonSurnames.Contains(m.Groups["surname"].Value[0], StringComparison.Ordinal)
        && !ownSurnames.Contains(m.Groups["surname"].Value[0]);

    // The clause's indictment or dismissal has another person for its object: 檢方已起訴李小四, 對李小四為不起訴處分.
    // Losing the Profile person's own indictment is the worse error, so the object must be a three-character name of a
    // common surname that the text writes at least twice (起訴高層, 起訴官員, 起訴金額 are no names), and the clause must
    // hold no character of the person's own surname (起訴李小四、王男 may include 王大明). 起訴李小四等七人 stays collective.
    // The same for a judgment or a wanted notice (StagePatterns.OtherPersonJudged), written once or not.
    // ponytail: a two-character name (李四) or one of a rare surname is not told apart; widen when a real case needs it.
    private static bool IndictsAnotherPerson(string clause, HashSet<char> ownSurnames, string news) =>
        !clause.Any(ownSurnames.Contains) && (StagePatterns.OtherPersonObject().Matches(clause).Any(m =>
            StageGlossary.CommonSurnames.Contains(m.Groups["name"].Value[0], StringComparison.Ordinal)
            && news.IndexOf(m.Groups["name"].Value, StringComparison.Ordinal) != news.LastIndexOf(m.Groups["name"].Value, StringComparison.Ordinal))
            || StagePatterns.OtherPersonJudged().Matches(clause).Any(m => StageGlossary.CommonSurnames.Contains(m.Groups["name"].Value[0], StringComparison.Ordinal)));
}
