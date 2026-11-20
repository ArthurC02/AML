using System.Text.RegularExpressions;

namespace Aml.Core.Screening.Stage;

/// <summary>
/// Every pattern the stage readers match, grouped by what it recognises. A pattern is the structure of a wording (what
/// stands before and after, lookarounds, numbers); its word lists come from <see cref="StageGlossary"/>.
/// </summary>
internal static partial class StagePatterns
{
    // ---- Quotes ----

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

    [GeneratedRegex("(?<!(?:" + StageGlossary.WantedLifted + ")[^，,]{0,8})通緝")]
    internal static partial Regex WantedNotice();

    // A report also words a sentence as 判 with the penalty itself: 判陳志明有期徒刑三年, 遭判無期徒刑, 重判十五年, 判1年6月.
    // Between 判 and the penalty only 決 / 處 and a name (他, 她, or two to four characters) fit; 審判 (but 一審判他五年,
    // 原審判…), 裁判, 談判, 誤判, 判斷, 判決前, 宣判時間 … are other words, and 判賠 names no penalty.
    [GeneratedRegex(StageGlossary.ConvictionWords + @"|(?<!(?<![一二三更初原])審|[裁談批評誤研預])判(?>[決處]?)(?![前後時斷期])(?:[他她]|\p{IsCJKUnifiedIdeographs}{2,4}?)??(?:" + StageGlossary.Penalties + "|[0-9０-９一二三四五六七八九十兩]+(?:" + StageGlossary.TermUnits + "))")]
    internal static partial Regex Conviction();

    [GeneratedRegex("(?<!無罪)定讞(?!前)")]
    internal static partial Regex Final();

    // 判決無罪, 獲判無罪, 判陳志明無罪, 二審逆轉無罪, 無罪定讞; 堅稱無罪 is a claim and 無罪推定 a presumption.
    // After 判決, 改判, 宣判 or 獲判 anything short may stand before 無罪 (宣判陳志明等三人均無罪); after a bare 判 only a
    // name (判陳志明無罪; never 判陳志明罰金、李四無罪, 難以判斷是否無罪 or 應判無罪).
    [GeneratedRegex("(?:" + StageGlossary.AcquittalRulingVerbs + ")[^，,、]{0,10}?無罪(?!" + StageGlossary.NotAcquittalAfter + @")|(?<!(?<![一二三更初原])審|[裁談批評誤研預應])判(?![斷決])\p{IsCJKUnifiedIdeographs}{0,4}?無罪(?!" + StageGlossary.NotAcquittalAfter + ")|(?:[一二三]審|更[一二三]?審|逆轉)[^，,、判]{0,6}?無罪(?!" + StageGlossary.NotAcquittalAfter + ")|無罪(?:" + StageGlossary.Settled + ")")]
    internal static partial Regex Acquittal();

    // 認被告犯…罪，處有期徒刑二年: a judgment's own wording, with no 判. Whose sentence it is the clause does not say, so it
    // is read only where the text has one defendant.
    [GeneratedRegex(@"^\s*(?:" + StageGlossary.EachOf + ")?處(?:" + StageGlossary.CustodialPenalties + ")")]
    internal static partial Regex Sentenced();

    // ---- Relations, bystanders and groups ----

    [GeneratedRegex("[的之](?:[妻夫女父母兄弟姊姐妹哥岳媳婿姪甥胞兒孫叔伯舅姑嬸]|子(?!公司)|" + StageGlossary.RelationWords + ")")]
    internal static partial Regex Relation();

    [GeneratedRegex(StageGlossary.BystanderRoles)]
    internal static partial Regex Bystander();

    [GeneratedRegex("(?<![平同相對])等共?[0-9０-９一二兩三四五六七八九十百餘多數]*(?:" + StageGlossary.GroupCounters + ")")]
    internal static partial Regex Group();

    [GeneratedRegex(StageGlossary.SuspicionWords)]
    internal static partial Regex Suspicion();

    // ---- A report's masking ----

    // 李小四、陳志明（均遭起訴）: an aside that opens with a word for all of them is the whole list's.
    [GeneratedRegex("^[（(](?:" + StageGlossary.GroupAsideOpeners + "|[兩二三四五]人)")]
    internal static partial Regex GroupAside();

    [GeneratedRegex("[（(][^（）()。\n]{1,200}[）)]", RegexOptions.NonBacktracking)]
    internal static partial Regex Aside();

    // From the verb nearest to 云云, over one comma at most: 號稱投資達人，遭判處有期徒刑二年，他辯稱無辜云云 leaves the
    // sentence in place, and so does 陳志明稱其無辜，法院仍判處有期徒刑二年，不服上訴云云 (too far to be the same saying).
    [GeneratedRegex("(?:" + StageGlossary.SayingVerbs + "|(?<![下簡名俗職號堪又暱人對])稱)(?:(?!" + StageGlossary.SayingVerbs + @"|稱)[^。！？；\n，,]){0,80}(?:[，,](?:(?!" + StageGlossary.SayingVerbs + @"|稱)[^。！？；\n，,]){0,80})?" + StageGlossary.ReportedSpeechEnd)]
    internal static partial Regex Reported();

    // 佯稱：帳戶遭凍結，涉及詐騙，且伊已遭通緝云云: after a colon the pretence is quoted whole, over any commas, to 云云.
    // Not past another verb of saying: …檢察官起訴陳志明，並於庭上辯稱無辜云云 ends a later saying.
    [GeneratedRegex("(?:" + StageGlossary.PretenceVerbs + @")[:：](?:(?!" + StageGlossary.SayingVerbs + @"|稱)[^。！？；\n]){0,300}?" + StageGlossary.ReportedSpeechEnd)]
    internal static partial Regex PretenceQuoted();

    // 請法院從重量刑，判處陳志明有期徒刑八年: the clause after a request opens with the penalty and no subject.
    [GeneratedRegex(@"^\s*(?:" + StageGlossary.AskedPenaltyLinks + ")?(?:" + StageGlossary.AskedPenaltyVerbs + ")")]
    internal static partial Regex AskedPenalty();

    [GeneratedRegex("起訴(?:" + StageGlossary.IndictmentPapers + ")")]
    internal static partial Regex IndictmentPaper();

    [GeneratedRegex(StageGlossary.PretenceVerbs)]
    internal static partial Regex Pretence();

    [GeneratedRegex(@"^\s*(?:" + StageGlossary.PassiveOpeners + ")")]
    internal static partial Regex OpensPassive();

    // ---- Whose a report's sentence or clause is ----

    // A sole defendant's 被告 sentence about another case, an earlier one, or the law in general is not this case's step.
    [GeneratedRegex(StageGlossary.OtherCaseWords)]
    internal static partial Regex NotThisCase();

    [GeneratedRegex(@"所涉[^，,。]{0,8}$")]
    internal static partial Regex TheirPart();

    // Not 經檢察官對李小四提起公訴 (another's) or 撤回.
    [GeneratedRegex(@"^\s*[均業已亦]{0,3}經[^，,。對撤]{0,12}提起公訴")]
    internal static partial Regex ChargedPart();

    // 陳志明本案所涉罪嫌，另案通緝中: the clause after the person's part opens with the notice. Not 對李小四發布通緝.
    [GeneratedRegex(@"^\s*[^，,。對]{0,6}通緝")]
    internal static partial Regex WantedPart();

    // 王大同經合法傳喚未到，由本院通緝中: a clause with no subject of its own, and no 被告 (…，被告遭通緝 is the defendant's).
    [GeneratedRegex(@"^(?![^，,]*被告)\s*(?:" + StageGlossary.SubjectlessOpeners + "|通緝)")]
    internal static partial Regex OpensWithoutSubject();

    // 檢方對李小四提起公訴: the indictment has a named object; whether it is another person the caller tells by the surname.
    [GeneratedRegex(@"對(?<name>\p{IsCJKUnifiedIdeographs}{2,3}?)(?:" + StageGlossary.DispositionVerbs + ")?提起公訴")]
    internal static partial Regex ProsecutionObject();

    // The same for a judgment or a wanted notice, where what follows the name tells it is one, written once or not:
    // 法院另判處李小四有期徒刑一年, 判李小四無罪, 警方通緝的李小四落網.
    // A two-character name counts here too (另判李四無罪); a title does not (判處董事長有期徒刑 may be the person).
    [GeneratedRegex(@"判[決處]?(?<name>\p{IsCJKUnifiedIdeographs}{1,2}[^\P{IsCJKUnifiedIdeographs}長員官人層嫌犯])(?=" + StageGlossary.Penalties + "|無罪|[0-9０-９一二三四五六七八九十兩]+(?:" + StageGlossary.TermUnits + @"))|通緝的?(?<name>\p{IsCJKUnifiedIdeographs}{3})(?=" + StageGlossary.CaughtWords + "|$)")]
    internal static partial Regex OtherPersonJudged();

    [GeneratedRegex(@"^\s*(?:" + StageGlossary.Connectives + @")?(?<surname>\p{IsCJKUnifiedIdeographs})(?:\p{IsCJKUnifiedIdeographs}{1,2}|姓(?:" + StageGlossary.PersonNouns + "))(?:(?:" + StageGlossary.ClauseAdverbs + ")?(?:" + StageGlossary.OtherPersonPassives + ")|(?:" + StageGlossary.BeforeDispositionAdverbs + ")為)")]
    internal static partial Regex OtherPersonOpening();

    // The name ends the clause or goes on in a list (、及與和); 等 after it makes a group, which is not matched.
    [GeneratedRegex(@"起訴(?<name>\p{IsCJKUnifiedIdeographs}{3})(?=$|[、及與和\s])|對(?<name>\p{IsCJKUnifiedIdeographs}{3})(?:" + StageGlossary.DispositionVerbs + ")?(?:(?:" + StageGlossary.UnindictedPrefixes + ")起訴|提起公訴)")]
    internal static partial Regex OtherPersonObject();

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

    [GeneratedRegex("被告(?:不服[^，,。]{0,8})?提起上訴")]
    internal static partial Regex DefendantAppealed();

    // A header line that is a defendant's name alone, past the 被告 label: 陳志明, 陳志明（另案在押）; not 住臺北市中正區….
    [GeneratedRegex(@"^(?<name>\p{IsCJKUnifiedIdeographs}{2,4})(?:[（(][^（）()]*[）)])?$")]
    internal static partial Regex BareName();

    // The code an anonymised judgment writes for a defendant from the order on: A05, B12, Ａ０５; not inside AB123, A073 or A0512.
    private const string Code = "[A-ZＡ-Ｚ][0-9０-９]{2}", NoLetterOrDigitAfter = "(?![A-Za-z0-9Ａ-Ｚａ-ｚ０-９])";

    [GeneratedRegex("(?<![A-Za-z0-9Ａ-Ｚａ-ｚ０-９])" + Code + NoLetterOrDigitAfter)]
    internal static partial Regex PartyCode();

    // A code that is the subject of a sentence of the order: it opens a line or follows 。；; (an item number 一、 before it),
    // alone or in a list joined by 、: A03犯…, A03、A07均無罪, 上開撤銷部分，A03處…; not A12 of A03與A12共同犯…, nor 如附表A07所示之物.
    [GeneratedRegex(@"(?<=(?:^|[。；;\n])\s*(?:[一二三四五六七八九十]+、)?(?:(?:" + StageGlossary.RevokedPartOpeners + ")[，,])?(?:" + Code + "、)*)" + Code + NoLetterOrDigitAfter)]
    internal static partial Regex SubjectCode();

    // ---- A sentence of the order and whose it is ----

    [GeneratedRegex("處(?:" + StageGlossary.Penalties + "|如?(?:本院)?附[表件]|如[^，,。\n]{0,20}所示之刑)|應執行(?:" + StageGlossary.CombinedPenalties + ")|免刑")]
    internal static partial Regex OrderSentence();

    [GeneratedRegex("緩刑[0-9一二三四五六七八九十壹貳參肆伍]+年")]
    internal static partial Regex Probation();

    [GeneratedRegex("無罪部分(?=[^。]{0,40}撤銷)")]
    internal static partial Regex RevokedAcquittal();

    // What may stand before the person's name when they are the clause's subject: an item number (一、), 被告, and other
    // names of a list (a published judgment may write them 甲○○ or A05).
    // A release also writes the ruling in running text: 判決主文：「王大明犯…, 判決被告王大明…, 經本院認被告王大明犯….
    // 於114年6月9日判決①被告王大明…；②被告李小四…, 另王大明部分改判無罪.
    [GeneratedRegex(@"^\s*[另及]?(?:[一二三四五六七八九十]+、|[㈠-㈩]、?|[①-⑳]|[（(][一二三四五六七八九十0-9]+[）)]|[0-9]{1,2}(?:[.．、]|(?=\s)))?\s*(?:(?:於[^，,。]{0,14}日)?經?(?:" + StageGlossary.CourtBenches + ")?(?:(?:" + StageGlossary.OrderRulingVerbs + @")(?:主文)?[:：]?[「『]?[①-⑳]?|認))?與?(?:(?:上訴人即)?被告)?(?:(?:[\p{IsCJKUnifiedIdeographs}○A-Za-z0-9]{2,4}|[A-Za-z][A-Za-z .\-]{1,40})[、及與]\s*)*$")]
    internal static partial Regex OrderSubject();

    // Another defendant's part of an order sentence: a name of two to four characters right before what the order says of them.
    [GeneratedRegex(@"^\s*\p{IsCJKUnifiedIdeographs}{2,4}?(?=" + StageGlossary.OrderPredicates + ")")]
    internal static partial Regex OrderOpensWithName();

    // The subject of a ruling's clause, to see whether it is someone else: 被告李小四犯…, 判決李小四無罪, 認李小四處….
    [GeneratedRegex("(?:" + StageGlossary.OtherSubjectOpeners + @")(?<n>[\p{IsCJKUnifiedIdeographs}○]{2,4}?)(?=" + StageGlossary.OtherSubjectPredicates + ")")]
    internal static partial Regex OtherSubject();

    // ---- Guards: what looks like a ruling's outcome and is not ----

    // 前經…地方法院以其等共同犯…罪: what the court below ruled, recalled.
    [GeneratedRegex(StageGlossary.AskedWords)]
    internal static partial Regex Asked();

    [GeneratedRegex(StageGlossary.NotTheirProceedsWords)]
    internal static partial Regex NotTheirProceeds();

    [GeneratedRegex(StageGlossary.NotThisOrderWords)]
    internal static partial Regex NotThisOrder();

    [GeneratedRegex(@"\G[（(][^（）()]{0,16}(?:" + StageGlossary.TriedElsewhereNotes + "|另行審[理結])[^（）()]{0,8}[）)]")]
    internal static partial Regex TriedElsewhere();

    // ---- Appeals ----

    // 上訴駁回, 其他上訴駁回, 上訴（即…部分）駁回, 駁回其上訴.
    [GeneratedRegex(@"上訴(?:[（(][^（）()]*[）)])?(?:均)?駁回|駁回(?:其等?|被告之?)?上訴")]
    internal static partial Regex AppealDismissed();

    // The part of a dismissed appeal, with the names it covers: 上訴（即被告王大明部分）駁回, 上訴駁回（即王大明、李小四部分）.
    [GeneratedRegex(@"上訴[（(]即[^（）()]*部分[）)]駁回|上訴駁回[（(]即[^（）()]*部分[）)]")]
    internal static partial Regex DismissedPart();

    // ---- Row numbers and tables ----

    // （編號1）, （編號2至3）: the row numbers a sentence of the order covers.
    [GeneratedRegex("[（(]編號([0-9]{1,3})(?:至([0-9]{1,3}))?[）)]")]
    internal static partial Regex NumberedRows();

    [GeneratedRegex(@"\G[（(]編號([0-9]{1,3})[）)]")]
    internal static partial Regex RowNumber();

    [GeneratedRegex(StageGlossary.TableOtherOutcomes)]
    internal static partial Regex TableOtherOutcome();

    // The heading cell whole: never 檢察官求處有期徒刑, 法定刑度 or 原判決刑度.
    [GeneratedRegex("^(?:" + StageGlossary.TermHeadings + "|(?:應執行)?有期徒刑|[（(]有期徒刑[）)])$")]
    internal static partial Regex TableTermHeading();

    // 2年, 1年8月、3月（得易科罰金）, 各10月, 緩刑5年; never a date (112年4月8日) or a range of terms (7月以上11月以下).
    [GeneratedRegex(@"^(?:[各均]?[0-9]{1,2}年(?:[0-9]{1,2}月)?|[各均]?[0-9]{1,2}月|緩刑[0-9]{1,2}年)(?:[、，,（(].*)?$")]
    internal static partial Regex TableTerm();

    // What stands beside the name in its cell: a row or item number before it, a bracketed title or a colon after it
    // ((十一)王大明： heads the lines of that person's counts).
    [GeneratedRegex(@"^(?:[0-9]{1,2}[.．、]?|[（(][一二三四五六七八九十0-9]+[）)]|[一二三四五六七八九十]+、|[㈠-㈩]、?)|(?:[（(][^（）()]*[）)])?[:：]?$")]
    internal static partial Regex TableNameCell();

    [GeneratedRegex("^(?:" + StageGlossary.Penalties + ")")]
    internal static partial Regex TablePenalty();

    // 2 李小四: a row number, a space and a name; never 1.犯詐欺罪 (a count of this row) or 7 年 of a wrapped penalty.
    [GeneratedRegex(@"^\s*[0-9]{1,2}\s+[\p{IsCJKUnifiedIdeographs}○]{2,4}(?:\s|$|[（(])")]
    internal static partial Regex TableNextRow();
}
