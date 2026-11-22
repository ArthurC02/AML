using System.Text.RegularExpressions;

namespace Aml.Core.Screening.Stage;

/// <summary>
/// Every pattern the stage readers and the news digest match, grouped by what it recognises. A pattern is the structure of
/// a wording (what stands before and after, lookarounds, numbers); its word lists come from <see cref="StageGlossary"/>.
/// </summary>
internal static partial class StagePatterns
{
    // ---- Quotes (the news digest) ----

    // Non-backtracking: a backtracking engine rescans to the end from every unclosed opening quote (1M 「 is quadratic).
    [GeneratedRegex("「[^」]*」|『[^』]*』|“[^”]*”", RegexOptions.NonBacktracking)]
    internal static partial Regex Quoted();

    // ---- Stage keywords ----

    [GeneratedRegex(StageGlossary.DismissalWords)]
    internal static partial Regex Dismissal();

    [GeneratedRegex("(?<!" + StageGlossary.NotProsecutedBefore + ")提起公訴")]
    internal static partial Regex PublicProsecution();

    // 聲請簡易判決處刑 is the summary form of an indictment.
    [GeneratedRegex("(?<!" + StageGlossary.NotIndictedBefore + ")起訴(?!" + StageGlossary.IndictmentNouns + ")|聲請簡易判決處刑")]
    internal static partial Regex Indictment();

    // A report also words a sentence as 判 with the penalty itself: 判陳志明有期徒刑三年, 遭判無期徒刑, 重判十五年, 判1年6月.
    // Between 判 and the penalty only 決 / 處 and a name (他, 她, or two to four characters) fit; 審判 (but 一審判他五年,
    // 原審判…), 裁判, 談判, 誤判, 判斷, 判決前, 宣判時間 … are other words, and 判賠 names no penalty.
    [GeneratedRegex(StageGlossary.ConvictionWords + @"|(?<!(?<![一二三更初原])審|[裁談批評誤研預])判(?>[決處]?)(?![前後時斷期])(?:[他她]|\p{IsCJKUnifiedIdeographs}{2,4}?)??(?:" + StageGlossary.Penalties + "|[0-9０-９一二三四五六七八九十兩]+(?:" + StageGlossary.TermUnits + "))")]
    internal static partial Regex Conviction();

    // 定讞 (the news digest).
    [GeneratedRegex("(?<!無罪)定讞(?!前)")]
    internal static partial Regex Final();

    // 判決無罪, 獲判無罪, 判陳志明無罪, 二審逆轉無罪, 無罪定讞; 堅稱無罪 is a claim and 無罪推定 a presumption.
    // After 判決, 改判, 宣判 or 獲判 anything short may stand before 無罪 (宣判陳志明等三人均無罪); after a bare 判 only a
    // name (判陳志明無罪; never 判陳志明罰金、李四無罪, 難以判斷是否無罪 or 應判無罪).
    [GeneratedRegex("(?:" + StageGlossary.AcquittalRulingVerbs + ")[^，,、]{0,10}?無罪(?!" + StageGlossary.NotAcquittalAfter + @")|(?<!(?<![一二三更初原])審|[裁談批評誤研預應])判(?![斷決])\p{IsCJKUnifiedIdeographs}{0,4}?無罪(?!" + StageGlossary.NotAcquittalAfter + ")|(?:[一二三]審|更[一二三]?審|逆轉)[^，,、判]{0,6}?無罪(?!" + StageGlossary.NotAcquittalAfter + ")|無罪(?:" + StageGlossary.Settled + ")")]
    internal static partial Regex Acquittal();

    // ---- Relations and groups ----

    // 王大明的哥哥, 王大明之妻 (StageText.OnlyAsRelation, the news models' reading).
    [GeneratedRegex("[的之](?:[妻夫女父母兄弟姊姐妹哥岳媳婿姪甥胞兒孫叔伯舅姑嬸]|子(?!公司)|" + StageGlossary.RelationWords + ")")]
    internal static partial Regex Relation();

    [GeneratedRegex("(?<![平同相對])等共?[0-9０-９一二兩三四五六七八九十百餘多數]*(?:" + StageGlossary.GroupCounters + ")")]
    internal static partial Regex Group();

    [GeneratedRegex(StageGlossary.SuspicionWords)]
    internal static partial Regex Suspicion();

    // ---- What kind of ruling text, and where its order is ----

    // 主文 alone on its line (a judgment), or a court press release's heading for it: 壹、判決主文：, 一、主文要旨,
    // 本院判決主文:, 壹、主文摘要說明:, 壹、判決結論:.
    // 判決結論 / 判決結果 is a release's heading only with its item number: a news report may set 判決結果 on a line too.
    [GeneratedRegex("^(?:(?:[壹貳參一二三]、)?(?:本院)?(?:判決)?主文|[壹貳參一二三]、判決結[論果])(?:" + StageGlossary.OrderHeadingQualifiers + ")?(?:說明)?(?:[（(][^（）()]*[）)])?[:：]?(?:[（(][^（）()]*[）)])?$")]
    internal static partial Regex OrderHeading();

    // 本院114年度訴字第1號…宣判: the court speaking of itself, its case number, its ruling. A news report may cite a
    // court and a case number (臺灣高等法院113年度上訴字第5號), but never says 本院.
    // Or the text opens with the court's name and the case number, and says 宣判: 臺灣士林地方法院113年度訴字第2號…案件，於…宣判.
    [GeneratedRegex(@"本院(?:審理)?[0-9]{2,3}年度?[^，,。]{1,12}字第[0-9]+號.{0,160}(?:" + StageGlossary.ReleaseRuledWords + ")|^(?:" + StageGlossary.CourtRegions + @")[\p{IsCJKUnifiedIdeographs}]{2,12}法院[0-9]{2,3}年度[^，,。]{1,12}字第[0-9]+號[^。]{0,160}宣判")]
    internal static partial Regex CourtRelease();

    [GeneratedRegex("^(?:[一二三四五壹貳參]、)?(?:犯罪)?(?:" + StageGlossary.OrderEndHeadings + ").{0,12}$")]
    internal static partial Regex OrderEnd();

    // 貳、事實及理由摘要, 一、犯罪事實：, (一)論罪科刑: an item number and at most 24 characters with no comma or full stop.
    [GeneratedRegex(@"^(?:[壹貳參叁肆伍陸柒捌玖拾一二三四五六七八九十]+、|[（(][一二三四五六七八九十]+[）)])[^，,。；]{1,24}$")]
    internal static partial Regex ReleaseHeading();

    // Not before a line that opens a new item (一、 ㈠ (一) 1.): 一、陳志明無罪 / 二、李小四處有期徒刑二年 stay two.
    // Nor after a bracketed note that follows a full stop: …處有期徒刑二年。（刑度與原審相同）.
    [GeneratedRegex(@"(?<![。；：:;！？\n]|。[（(][^（）()\n]{0,40}[）)])\n(?!\s*(?:[一二三四五六七八九十]+、|[㈠-㈩]|[（(][一二三四五六七八九十]+[）)]|[0-9]{1,2}[.．、]))")]
    internal static partial Regex WrappedLine();

    // A table cell on its own line (被告, 陳志明, 宣告刑): at most six characters and no punctuation. It does not run on.
    [GeneratedRegex(@"^[\p{IsCJKUnifiedIdeographs}○A-Za-z0-9]{1,6}$")]
    internal static partial Regex TableCell();

    // ---- A ruling's parties ----

    [GeneratedRegex("^(?:" + StageGlossary.DefendantLinePrefixes + ")*被告")]
    internal static partial Regex DefendantLine();

    [GeneratedRegex("^(?:(?:" + StageGlossary.CounselKinds + ")?辯護人|" + StageGlossary.OtherPartyHeads + "|(?:" + StageGlossary.AgentKinds + ")?代理人|" + StageGlossary.OtherPartyRoles + ")")]
    internal static partial Regex OtherPartyLine();

    // ---- A sentence of the order and whose it is ----

    [GeneratedRegex("處(?:" + StageGlossary.Penalties + "|如?(?:本院)?附[表件]|如[^，,。\n]{0,20}所示之刑)|應執行(?:" + StageGlossary.CombinedPenalties + ")|免刑")]
    internal static partial Regex OrderSentence();

    [GeneratedRegex("無罪部分(?=[^。]{0,40}撤銷)")]
    internal static partial Regex RevokedAcquittal();

    // What may stand before the person's name when they are the clause's subject: an item number (一、), 被告, and other
    // names of a list (a published judgment may write them 甲○○ or A05).
    // A release also writes the ruling in running text: 判決主文：「王大明犯…, 判決被告王大明…, 經本院認被告王大明犯….
    // 於114年6月9日判決①被告王大明…；②被告李小四…, 另王大明部分改判無罪.
    [GeneratedRegex(@"^\s*[另及]?(?:[一二三四五六七八九十]+、|[㈠-㈩]、?|[①-⑳]|[（(][一二三四五六七八九十0-9]+[）)]|[0-9]{1,2}(?:[.．、]|(?=\s)))?\s*(?:(?:於[^，,。]{0,14}日)?經?(?:" + StageGlossary.CourtBenches + ")?(?:(?:" + StageGlossary.OrderRulingVerbs + @")(?:主文)?[:：]?[「『]?[①-⑳]?|認))?與?(?:(?:上訴人即)?被告)?(?:(?:[\p{IsCJKUnifiedIdeographs}○A-Za-z0-9]{2,4}|[A-Za-z][A-Za-z .\-]{1,40})[、及與]\s*)*$")]
    internal static partial Regex OrderSubject();

    // ---- Guards: what looks like a ruling's outcome and is not ----

    [GeneratedRegex(StageGlossary.NotTheirProceedsWords)]
    internal static partial Regex NotTheirProceeds();

    [GeneratedRegex(StageGlossary.NotThisOrderWords)]
    internal static partial Regex NotThisOrder();

    [GeneratedRegex(@"\G[（(][^（）()]{0,16}(?:" + StageGlossary.TriedElsewhereNotes + "|另行審[理結])[^（）()]{0,8}[）)]")]
    internal static partial Regex TriedElsewhere();

    // ---- Row numbers ----

    // （編號1）, （編號2至3）: the row numbers a sentence of the order covers.
    [GeneratedRegex("[（(]編號([0-9]{1,3})(?:至([0-9]{1,3}))?[）)]")]
    internal static partial Regex NumberedRows();

    [GeneratedRegex(@"\G[（(]編號([0-9]{1,3})[）)]")]
    internal static partial Regex RowNumber();
}
