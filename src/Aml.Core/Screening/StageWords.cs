using System.Text.RegularExpressions;

namespace Aml.Core.Screening;

/// <summary>One way a text tells a person's stage: the stage, or null when this rule does not speak.</summary>
internal delegate LegalStage? StageRule<in T>(T text);

/// <summary>
/// The words of legal stages and the text helpers that the readers of news reports (<see cref="LegalStageReading"/>),
/// of rulings (<see cref="RulingReading"/>) and the news digest share: how text is cut into sentences and clauses, which
/// surnames are common, what is quoted, the stage keywords, and what qualifies a keyword (a modal, a negation, a relation).
/// </summary>
internal static partial class StageWords
{
    /// <summary>The first rule that speaks, in the list's order (a decision list).</summary>
    internal static LegalStage? FirstOf<T>(StageRule<T>[] rules, T text)
    {
        foreach (var rule in rules)
            if (rule(text) is { } stage) return stage;
        return null;
    }

    // ---- Cutting text ----

    internal static readonly char[] SentenceEnds = ['。', '！', '？', '\n', '；'], ClauseEnds = ['，', ','];

    // The (start, length) of each piece of text[from..from+length] between separators, as string.Split cuts it.
    internal static IEnumerable<(int Start, int Length)> Spans(string text, int from, int length, char[] separators)
    {
        var end = from + length;
        for (var start = from; start <= end;)
        {
            var next = text.IndexOfAny(separators, start, end - start);
            if (next < 0) next = end;
            yield return (start, next - start);
            start = next + 1;
        }
    }

    // ---- Names ----

    // ponytail: the commonest Taiwanese surnames, enough to tell 李四遭起訴 from 一審遭判處; a person with a rarer surname
    // still reads as the Profile person's clause.
    internal const string CommonSurnames =
        "陳林黃張李王吳劉蔡楊許鄭謝郭洪曾邱廖賴周徐蘇葉莊呂江何蕭羅高潘簡朱鍾鐘游彭詹胡施沈余盧梁趙顏柯翁魏孫戴范方宋鄧杜傅侯曹薛丁卓阮馬董溫唐藍蔣石古紀姚連馮歐程湯田康姜白汪鄒尤巫黎涂龔嚴韓袁金童陸夏柳邵錢伍倪于譚駱熊任甘秦顧毛章史官萬俞雷粘饒";

    // ponytail: the first 100 names, as in StatedAgeCheck: every sentence and clause is searched for each name.
    internal static bool MentionsAnyName(string sentence, IReadOnlyList<string> names) =>
        names.Take(100).Any(n => n.Length > 1 && sentence.Contains(n, StringComparison.OrdinalIgnoreCase));

    // Non-backtracking: a backtracking engine rescans to the end from every unclosed opening quote (1M 「 is quadratic).
    [GeneratedRegex("「[^」]*」|『[^』]*』|“[^”]*”", RegexOptions.NonBacktracking)]
    internal static partial Regex Quoted();

    // ---- Stage keywords ----

    [GeneratedRegex("不起訴|緩起訴|撤回起訴|不予起訴|免予起訴")]
    internal static partial Regex Dismissal();

    [GeneratedRegex("(?<!不|不予|未|未經)提起公訴")]
    internal static partial Regex PublicProsecution();

    // 聲請簡易判決處刑 is the summary form of an indictment.
    [GeneratedRegex("(?<!不|緩|撤回|予)起訴(?!罪名|法條)|聲請簡易判決處刑")]
    internal static partial Regex Indictment();

    [GeneratedRegex("(?<!(?:撤銷|解除|撤回)[^，,]{0,8})通緝")]
    internal static partial Regex WantedNotice();

    // A report also words a sentence as 判 with the penalty itself: 判陳志明有期徒刑三年, 遭判無期徒刑, 重判十五年, 判1年6月.
    // Between 判 and the penalty only 決 / 處 and a name (他, 她, or two to four characters) fit; 審判 (but 一審判他五年,
    // 原審判…), 裁判, 談判, 誤判, 判斷, 判決前, 宣判時間 … are other words, and 判賠 names no penalty.
    [GeneratedRegex(@"判處|判刑|判決有罪|有罪確定|(?<!(?<![一二三更初原])審|[裁談批評誤研預])判(?>[決處]?)(?![前後時斷期])(?:[他她]|\p{IsCJKUnifiedIdeographs}{2,4}?)??(?:有期徒刑|無期徒刑|死刑|拘役|罰金|[0-9０-９一二三四五六七八九十兩]+(?:年|個月|月))")]
    internal static partial Regex Conviction();

    [GeneratedRegex("(?<!無罪)定讞(?!前)")]
    internal static partial Regex Final();

    // 判決無罪, 獲判無罪, 判陳志明無罪, 二審逆轉無罪, 無罪定讞; 堅稱無罪 is a claim and 無罪推定 a presumption.
    // After 判決, 改判, 宣判 or 獲判 anything short may stand before 無罪 (宣判陳志明等三人均無罪); after a bare 判 only a
    // name (判陳志明無罪; never 判陳志明罰金、李四無罪, 難以判斷是否無罪 or 應判無罪).
    [GeneratedRegex(@"(?:判決|改判|宣判|獲判)[^，,、]{0,10}?無罪(?!推定|判決)|(?<!(?<![一二三更初原])審|[裁談批評誤研預應])判(?![斷決])\p{IsCJKUnifiedIdeographs}{0,4}?無罪(?!推定|判決)|(?:[一二三]審|更[一二三]?審|逆轉)[^，,、判]{0,6}?無罪(?!推定|判決)|無罪(?:定讞|確定)")]
    internal static partial Regex Acquittal();

    // ---- What qualifies a keyword ----

    // Where the first modal of the clause ends (past the end when there is none). A modal anywhere before the keyword makes
    // it no stage (最重可判處, 檢方請求法院依法從重判處), but a word that only holds the modal's characters is not one.
    // ponytail: a name holding 可 or 求 (陳可欣遭起訴) still reads as a modal, so no stage: the safe side; exclude the matched
    // names when that costs real hits.
    internal static int FirstModalEnd(string clause)
    {
        var first = int.MaxValue;
        foreach (var (modal, notBefore, notAfter) in Modals)
            for (var at = clause.IndexOf(modal, StringComparison.Ordinal); at >= 0 && at + modal.Length < first;
                 at = clause.IndexOf(modal, at + 1, StringComparison.Ordinal))
            {
                var next = at + modal.Length;
                if (at > 0 && notBefore.Contains(clause[at - 1], StringComparison.Ordinal)) continue;
                if (next < clause.Length && notAfter.Contains(clause[next], StringComparison.Ordinal)) continue;
                first = next;
                break;
            }
        return first;
    }

    // 尚未起訴, 沒有被起訴, 是否遭到起訴: the words just before the keyword, past a passive marker.
    internal static bool Negated(string clause, int keyword)
    {
        var before = clause.AsSpan(0, keyword);
        foreach (var passive in Passives)
            if (before.EndsWith(passive, StringComparison.Ordinal)) { before = before[..^passive.Length]; break; }
        foreach (var negation in Negations)
            if (before.EndsWith(negation, StringComparison.Ordinal)) return true;
        return false;
    }

    private static readonly string[] Passives = ["遭到", "遭", "被", "受"];

    private static readonly string[] Negations = ["未", "沒有", "沒", "不會", "無", "是否"];

    private static readonly (string Modal, string NotBefore, string NotAfter)[] Modals =
    [
        ("可", "許認核", "疑是"), ("恐", "", "嚇怖慌懼龍"), ("求", "要訴需追", "償援助職"), ("最重", "", ""), ("推定", "", ""),
        ("擬", "模虛", ""), ("預計", "", ""), ("即將", "", ""), ("將會", "", ""), ("若", "", "干"), ("如果", "", ""), ("一旦", "", ""), ("據傳", "", ""),
    ];

    [GeneratedRegex("[的之](?:[妻夫女父母兄弟姊姐妹哥岳媳婿姪甥胞兒孫叔伯舅姑嬸]|子(?!公司)|丈夫|先生|太太|老公|老婆|配偶|前妻|前夫|前女友|前男友|男友|友人|助理|秘書|司機|特助|部屬|下屬|員工|同事|合夥人|律師)")]
    internal static partial Regex Relation();

    /// <summary>Every place the sentence writes one of the forms goes on with 的 / 之 and a relation word: it speaks of that relative, not of the person.</summary>
    internal static bool OnlyAsRelation(string sentence, IEnumerable<string> forms)
    {
        var any = false;
        foreach (var f in forms.Where(f => f.Length > 1))
            for (var at = sentence.IndexOf(f, StringComparison.Ordinal); at >= 0; at = sentence.IndexOf(f, at + 1, StringComparison.Ordinal))
            {
                if (Relation().Match(sentence, at + f.Length) is not { Success: true } m || m.Index != at + f.Length) return false;
                any = true;
            }
        return any;
    }
}
