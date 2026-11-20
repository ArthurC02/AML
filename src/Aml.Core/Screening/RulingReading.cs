using System.Text.RegularExpressions;
using Aml.Core.Normalization;

namespace Aml.Core.Screening;

/// <summary>
/// What a judgment, or a court's press release of its ruling, says of one person. A cascade of three steps:
/// <see cref="RulingText.Parse"/> reads the text once into what every rule needs (the kind of text, the order's lines,
/// the defendants); <see cref="Readers"/> are the rules, each one way a ruling tells a person's outcome; the first
/// reader that speaks gives the stage. A new form of ruling is a new reader and its place in that list, or a new
/// pattern of an existing one.
/// </summary>
/// <remarks>
/// A judgment's order (the lines from 主文 to the 事實 / 理由 heading) is written to a fixed form: who is sentenced
/// (處有期徒刑, 拘役, 罰金, 無期徒刑, 死刑, 免刑) and who is 無罪. Only what the order says of the Profile person counts:
/// where they are the subject of a clause (the name opens it, alone or in a list of names: 林美玲、王大明均無罪; never
/// 王大明對林美玲犯詐欺罪 or 原判決關於林美玲部分撤銷), up to a clause that opens with another person's name (林美玲處
/// 有期徒刑一年，王大明無罪). A sentence in any part of the order makes CONVICTED, else an acquittal ACQUITTED; an order
/// that says neither of them (免訴, 公訴不受理) gives nothing, save what the later readers tell. Whether the person is a
/// listed defendant (a 被告 line before 主文) is told as well: such a person stands indicted even when the order is not read.
/// ponytail: pending a judgment Golden Case. Not read: a judgment's order sentence that goes on about the person without
/// naming them again (又犯…罪，處…; a release's is read), a co-defendant of the same surname, and anything after the order
/// that overturns it (the order wins over the rest of the text). An order with no 事實 / 理由 heading is read for 40 lines at most.
/// </remarks>
internal static partial class RulingReading
{
    /// <summary>Whether the text is a judgment or release; the person's stage by it (null: it does not say); whether they are a defendant of it, and its only one.</summary>
    internal readonly record struct Reading(bool Judgment, LegalStage? Stage, bool Defendant, bool Sole);

    internal static Reading Read(string news, IReadOnlyList<string> names) =>
        RulingText.Parse(news, names) is { } text
            ? new(true, StageWords.FirstOf(Readers, text), text.Defendant, text.Sole)
            : default;

    // The order of this list is the precedence: what stands under the order's heading first, then the whole release,
    // then what a dismissed appeal leaves standing, then the forms that name the person outside a sentence of the order.
    private static readonly StageRule<RulingText>[] Readers =
    [
        OrderUnderHeading, WholeRelease, AppealOfListedDefendant, AppealOfOnlyDefendant, AppealWithRecalledSentence,
        ProceedsConfiscated, SentencedByRowNumber, TriedElsewhereNote,
    ];

    /// <summary>The text is a court's press release of its ruling: it has a release's heading for the order, or opens in the court's own voice.</summary>
    internal static bool IsCourtRelease(string news) =>
        news.Split('\n').Take(MaxOrderLines).Select(Compact).ToList() is var lines && !lines.Contains("主文")
        && (lines.Exists(l => OrderHeading().IsMatch(l)) || CourtRelease().IsMatch(string.Concat(lines.Take(MaxOpeningLines))));

    private const int MaxOrderLines = 40, MaxReleaseLines = 400, MaxOpeningLines = 8, MaxRowLines = 12;

    private static string Compact(string s) => string.Concat(s.Where(c => !char.IsWhiteSpace(c)));

    private static IEnumerable<string> Sentences(string text) =>
        StageWords.Spans(text, 0, text.Length, StageWords.SentenceEnds).Select(s => text.Substring(s.Start, s.Length));

    // ---- The text, read once ----

    private sealed class RulingText
    {
        /// <summary>A court's press release (no party lines, a heading of its own or none); else a judgment with 主文.</summary>
        public bool Release { get; private init; }

        /// <summary>The text has a heading for the order.</summary>
        public bool HasHeading { get; private init; }

        /// <summary>The lines under the heading, to the order's end; of a release with no heading, all its lines.</summary>
        public string[] Block { get; private init; } = [];

        /// <summary>The text's lines, 400 at most.</summary>
        public string[] All { get; private init; } = [];

        /// <summary>The person's names; with 被告 when they are the only defendant.</summary>
        public List<string> Own { get; private init; } = [];

        public HashSet<char> Surnames { get; private init; } = [];

        /// <summary>A judgment's 被告 lines that hold the person.</summary>
        public List<string> Listed { get; private init; } = [];

        public bool Defendant { get; private init; }

        public bool Sole { get; private init; }

        /// <summary>A release's opening says the defendant appealed.</summary>
        public bool Appealed { get; private init; }

        private string? orderText, wholeText;

        public string OrderText => orderText ??= Text(Block);

        public string WholeText => wholeText ??= Text(All);

        /// <summary>Where a dismissed appeal is looked for: a release's whole text, a judgment's order.</summary>
        public string RulingWords => Release ? WholeText : OrderText;

        // A heading (a short numbered line with no comma, or the order's heading) ends there; it does not run on.
        // A release's page may break its lines inside a sentence, even inside a name: a line that ends no sentence runs
        // on. Its items join outcomes with ； (王大明部分公訴不受理；其餘被訴部分無罪), read here as a comma.
        public string Text(string[] rows) => !Release ? string.Join('\n', rows)
            : WrappedLine().Replace(string.Join('\n', rows.Select(l => Compact(l) is var c && (OrderHeading().IsMatch(c) || ReleaseHeading().IsMatch(c) || TableCell().IsMatch(c)) ? l + "。" : l)), "").Replace('；', '，').Replace(';', '，');

        /// <summary>Where the text goes on after each writing of one of the person's names.</summary>
        public List<int> AfterNames() => [.. Own.Where(n => n != "被告").SelectMany(After)];

        private IEnumerable<int> After(string n)
        {
            for (var i = WholeText.IndexOf(n, StringComparison.OrdinalIgnoreCase); i >= 0; i = WholeText.IndexOf(n, i + n.Length, StringComparison.OrdinalIgnoreCase)) yield return i + n.Length;
        }

        public static RulingText? Parse(string news, IReadOnlyList<string> names)
        {
            // Full-width letters and digits are read as half-width ones: a name may be written either way (Ａ０５, A05).
            static string HalfWidth(string s) => string.Concat(s.Select(c => c is (>= 'Ａ' and <= 'Ｚ') or (>= 'ａ' and <= 'ｚ') or (>= '０' and <= '９') ? (char)(c - 0xFEE0) : c));
            var lines = HalfWidth(news).Replace("\r", "", StringComparison.Ordinal).Split('\n');
            var start = Array.FindIndex(lines, l => OrderHeading().IsMatch(Compact(l)));
            // A court's press release of its ruling has no party lines and gives the outcome under a heading of its own
            // (壹、判決主文：, 主文要旨, 壹、判決結論:) or in running text. It is known by such a heading, or by an opening in the
            // court's own voice: the court, the case number, and that it ruled (本院114年度訴字第1號被告…案件，於今日宣判).
            var release = start < 0 ? CourtRelease().IsMatch(Compact(string.Join('\n', lines.Take(MaxOpeningLines)))) : Compact(lines[start]) != "主文";
            if (start < 0 && !release) return null;
            var own = names.Take(100).Where(n => n.Length > 1).Select(HalfWidth).ToList();
            // The header lists defendants on a 被告 line and on the lines after it, up to the next party's line (辯護人, 上列 …).
            var listed = new List<string>();
            var defendantLines = 0;
            string? head = null;
            foreach (var l in lines.Take(release ? 0 : start).Select(Compact))
            {
                if (DefendantLine().IsMatch(l)) head = l;
                else if (OtherPartyLine().IsMatch(l)) head = null;
                if (head is not null && l.Length > 0) defendantLines++;
                if (head is not null && own.Exists(n => l.Contains(n, StringComparison.OrdinalIgnoreCase))) listed.Add(head);
            }
            // The only defendant: one line in all the 被告 blocks, and it is theirs. The text's bare 被告 is then this person.
            var sole = listed.Count == 1 && defendantLines == 1;
            var defendant = listed.Count > 0;
            var appealed = false;
            if (release)
            {
                // A release's defendants are the people its opening writes as 被告 and the name; the opening runs to the line
                // that says the court ruled (宣判), or to the heading. The only defendant: every 被告 of the opening is
                // followed by one of the person's names, and that name by no further one (被告王大明等3人, 被告王大明、李小四).
                var ruled = Array.FindIndex(lines, 0, Math.Min(lines.Length, MaxOpeningLines), l => l.Contains("宣判", StringComparison.Ordinal));
                var opening = Compact(string.Join('\n', lines.Take(ruled >= 0 ? ruled + 1 : Math.Max(start, 1)))).Replace("下稱被告", "", StringComparison.Ordinal);
                if (opening.IndexOf("宣判", StringComparison.Ordinal) is >= 0 and var at) opening = opening[..at]; // the line may go on past the ruling
                // an opening names a handful; 被告提起上訴, 被告不服 go on about the defendants already named
                var after = opening.Split("被告", 102).Skip(1).Take(100).Where(a => !a.StartsWith("提起", StringComparison.Ordinal) && !a.StartsWith("不服", StringComparison.Ordinal) && !a.StartsWith("上訴", StringComparison.Ordinal)).ToList();
                appealed = DefendantAppealed().IsMatch(opening);
                var compact = own.Select(Compact).ToList();
                var theirs = after.Select(a => compact.FirstOrDefault(n => a.StartsWith(n, StringComparison.OrdinalIgnoreCase))).ToList();
                defendant = theirs.Exists(n => n is not null);
                sole = defendant && theirs.TrueForAll(n => n is not null) && !after.Zip(theirs).Any(x => x.First[x.Second!.Length..] is var rest && (rest.StartsWith('等') || rest.StartsWith('、') || rest.StartsWith('及') || rest.StartsWith('與')));
            }
            var surnames = own.Where(n => CjkText.IsCjk(n[0])).Select(n => n[0]).ToHashSet();
            if (sole) own.Add("被告"); // the order of a one-defendant judgment may say 被告無罪 without the name
            // A judgment's order ends at the 事實 / 理由 heading. A release's order, under its heading, ends at the next heading
            // of the series: a line opening 壹、 貳、 … (the order's own items are never numbered so), or under a heading
            // numbered 一、 the next short line numbered that way.
            var small = start >= 0 && "一二三四五六七八九十".Contains(Compact(lines[start])[0], StringComparison.Ordinal);
            var end = start < 0 ? -1 : Array.FindIndex(lines, start + 1, l => Compact(l) is var c && (!release ? OrderEnd().IsMatch(c)
                : small ? "一二三四五六七八九十".Contains(c.FirstOrDefault(), StringComparison.Ordinal) && ReleaseHeading().IsMatch(c)
                : c.Length > 1 && c[1] == '、' && "壹貳參叁肆伍陸柒捌玖拾".Contains(c[0], StringComparison.Ordinal)));
            return new()
            {
                Release = release,
                HasHeading = start >= 0,
                Block = lines[(start + 1)..Math.Min(end < 0 ? lines.Length : end, start + 1 + (release ? MaxReleaseLines : MaxOrderLines))],
                All = lines[..Math.Min(lines.Length, MaxReleaseLines)],
                Own = own,
                Surnames = surnames,
                Listed = listed,
                Defendant = defendant,
                Sole = sole,
                Appealed = appealed,
            };
        }
    }

    // ---- The readers, in the order of Readers ----

    // What stands under the heading is the outcome.
    private static LegalStage? OrderUnderHeading(RulingText t) => Outcome(t, t.Block);

    // A release that says nothing of the person under its heading is read whole: it also gives the outcome in its
    // opening and its reasons, in the order's own form. (One with no heading was read whole already.)
    private static LegalStage? WholeRelease(RulingText t) => t.Release && t.HasHeading ? Outcome(t, t.All) : null;

    // A defendant appeals a conviction only: 上訴駁回 on the appeal of an 上訴人即被告 leaves that conviction standing.
    private static LegalStage? AppealOfListedDefendant(RulingText t) =>
        t.RulingWords.Contains("上訴駁回", StringComparison.Ordinal) && t.Listed.Exists(l => l.StartsWith("上訴人即被告", StringComparison.Ordinal) || l.StartsWith("即被告", StringComparison.Ordinal)) // 即被告 opens the line under 上訴人
            ? LegalStage.Convicted : null;

    // A release has no such line: its opening says its only defendant appealed (被告提起上訴, 被告不服提起上訴), and a
    // line under its heading is 上訴駁回 and nothing more.
    private static LegalStage? AppealOfOnlyDefendant(RulingText t) =>
        t.Release && t.Sole && t.Appealed && t.HasHeading && t.Block.Any(l => Compact(l).TrimEnd('。') == "上訴駁回") ? LegalStage.Convicted : null;

    // A release's 上訴駁回 leaves standing the sentence the release recalls of the person
    // (經…法院判決「王大明犯殺人罪，處有期徒刑十年」, 判處王大明有期徒刑): a sentence of the text naming them with one.
    // ponytail: a prosecutor's dismissed appeal against an acquittal, in a release that also writes the penalty asked
    // for the person (請求判處王大明…), reads CONVICTED; tell whose appeal it was if that turns up.
    // 其他上訴駁回（即被告王大明、李小四部分） dismisses the appeals of those it names: their sentences stand.
    private static LegalStage? AppealWithRecalledSentence(RulingText t) =>
        t.Release && AppealDismissed().IsMatch(t.WholeText) && Sentences(t.WholeText)
            .Any(s => t.Own.Exists(n => s.Contains(n, StringComparison.OrdinalIgnoreCase))
                && (s.Contains("判處", StringComparison.Ordinal) || OrderSentence().IsMatch(s) || (DismissedPart().Match(s) is { Success: true } part && t.Own.Exists(n => part.Value.Contains(n, StringComparison.OrdinalIgnoreCase)))))
            ? LegalStage.Convicted : null;

    // 陳志明犯罪所得新臺幣五萬元…沒收 under the heading: proceeds of crime are taken from the person convicted of it,
    // also where the release's order rules on nothing else of theirs (an appeal on the confiscation alone).
    private static LegalStage? ProceedsConfiscated(RulingText t) =>
        t.Release && t.HasHeading && Sentences(t.OrderText)
            .Any(s => (s.Contains("沒收", StringComparison.Ordinal) || s.Contains("追徵", StringComparison.Ordinal)) && !NotTheirProceeds().IsMatch(s)
                && t.Own.Exists(n => n != "被告" && (s.Contains(n + "犯罪所得", StringComparison.OrdinalIgnoreCase) || s.Contains(n + "之犯罪所得", StringComparison.OrdinalIgnoreCase))))
            ? LegalStage.Convicted : null;

    // A large case numbers its defendants and sentences them by number: 王大明等15人各犯…罪，各處有期徒刑7年（編號1）、
    // 5年（編號2）… in the order, 李小四（編號2） in the facts. The person's number in a sentence of the order that
    // sentences is their conviction. Only a bracketed number under the heading counts (not 附表編號2 of an exhibit),
    // and not in a sentence that also acquits or speaks of the court below or an appeal.
    private static LegalStage? SentencedByRowNumber(RulingText t)
    {
        if (!t.Release) return null;
        var numbers = t.AfterNames().Select(i => RowNumber().Match(t.WholeText, i)).Where(m => m.Success).Select(m => Number(m.Groups[1])).ToHashSet();
        return numbers.Count > 0 && Sentences(t.HasHeading ? t.OrderText : t.WholeText)
            .Where(s => OrderSentence().IsMatch(s) && !NotThisOrder().IsMatch(s)).SelectMany(s => NumberedRows().Matches(s))
            .Any(r => Number(r.Groups[1]) is var low && (r.Groups[2].Success ? Number(r.Groups[2]) : low) is var high && numbers.Any(n => n >= low && n <= high))
            ? LegalStage.Convicted : null;

        static int Number(Group g) => int.Parse(g.Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    // 王大明（本院審理中）, 王大明（由本院另案審理）: charged, and not ruled on here.
    private static LegalStage? TriedElsewhereNote(RulingText t) =>
        t.Release && t.AfterNames().Exists(i => TriedElsewhere().IsMatch(t.WholeText, i)) ? LegalStage.Indicted : null;

    // ---- Reading sentences of the order's form ----

    // What the rows say of the person, sentence by sentence: the clauses whose subject they are, to a clause that is
    // another's. A release's rows that say nothing so are read as a table.
    private static LegalStage? Outcome(RulingText t, string[] rows)
    {
        var (release, own) = (t.Release, t.Own);
        var text = t.Text(rows);
        LegalStage? read = null;
        var carried = false; // the sentence before this one was the person's
        foreach (var (at, length) in StageWords.Spans(text, 0, text.Length, StageWords.SentenceEnds))
        {
            var theirs = new System.Text.StringBuilder();
            foreach (var (from, size) in StageWords.Spans(text, at, length, StageWords.ClauseEnds))
            {
                var clause = text.Substring(from, size);
                // What is asked for, or what the law allows, is no outcome: 檢察官請求處有期徒刑五年, 應處有期徒刑.
                if (release && Asked().IsMatch(clause)) continue;
                var another = release && Another(t, clause);
                // 王大明（本院審理中）、李小四犯…: the bracket takes the person out of this ruling.
                var named = own.Exists(n => !(n == "被告" && another) && clause.IndexOf(n, StringComparison.OrdinalIgnoreCase) is >= 0 and var i && OrderSubject().IsMatch(clause[..i])
                    && !(release && TriedElsewhere().Match(clause, i + n.Length) is { Success: true } e && e.Index == i + n.Length));
                if (theirs.Length == 0 && !named) continue;
                if (!named && (another || (OrderOpensWithName().Match(clause) is { Success: true } m
                    && StageWords.CommonSurnames.Contains(m.Value.TrimStart()[0], StringComparison.Ordinal) && !t.Surnames.Contains(m.Value.TrimStart()[0])))) break;
                theirs.Append(clause).Append('，');
            }
            var said = theirs.ToString();
            // A release's item goes on about the person in a sentence with no subject: 王大明犯…罪，共2罪。應執行有期徒刑二年。
            if (release && said.Length == 0 && carried && text.AsSpan(at, length).TrimStart() is var next && (next.StartsWith("應執行") || next.StartsWith("又犯") || next.StartsWith("又共同犯"))) said = text.Substring(at, length);
            // Not after an acquittal or a dismissal: what follows it is another's.
            carried = said.Length > 0 && !said.Contains("無罪", StringComparison.Ordinal) && !said.Contains("不受理", StringComparison.Ordinal) && !said.Contains("免訴", StringComparison.Ordinal);
            if (OrderSentence().IsMatch(said)) return LegalStage.Convicted;
            // 陳志明緩刑3年: probation is given on a conviction. Only right after the person's own name.
            if (release && Probation().Match(said) is { Success: true } p && own.Exists(n => n != "被告" && said.AsSpan(0, p.Index).EndsWith(n, StringComparison.OrdinalIgnoreCase))) return LegalStage.Convicted;
            // 原判決關於X沒收部分、Y無罪部分均撤銷 names a part of the judgment below, not an outcome.
            // 陳志明無罪部分，檢察官上訴駁回 upholds the acquittal and stays one.
            if (RevokedAcquittal().Replace(said, "").Contains("無罪", StringComparison.Ordinal)) read = LegalStage.Acquitted;
        }
        return read ?? (release ? TableRow(rows, [.. own.Where(n => n != "被告").Select(Compact)], own, t.Surnames) : null);
    }

    // 被告李小四犯…, 判決李小四無罪: the clause's subject is another person. The only defendant's 被告 is not theirs there.
    private static bool Another(RulingText t, string clause) => OtherSubject().Match(clause) is { Success: true } m && m.Groups["n"].Value is var n
        && StageWords.CommonSurnames.Contains(n[0], StringComparison.Ordinal) && !t.Own.Exists(o => o != "被告" && clause.AsSpan(m.Groups["n"].Index).StartsWith(o, StringComparison.OrdinalIgnoreCase));

    // A release may give its defendants as a table, which the page's text holds one cell a line: the person's name alone
    // on a line (a row number before it, a bracketed title after it), then the cells of their row. The first penalty
    // among the next lines is theirs (a cell opening 有期徒刑, 拘役 …, or a sentence of the order's form), unless 無罪
    // comes first; the row ends at the next numbered row. Lines that are only a short name are skipped: defendants
    // sharing one penalty cell stand one under the other.
    // ponytail: cells merged the other way round (one name, the next row's penalty) read as this row's; a table with a
    // column of names and a separate column of outcomes read column by column is not told apart.
    private static LegalStage? TableRow(string[] rows, List<string> own, List<string> names, HashSet<char> surnames)
    {
        var terms = rows.Any(r => Compact(r) is { Length: <= 10 } c && TableTermHeading().IsMatch(c));
        for (var i = 0; i < rows.Length; i++)
        {
            // The name cell may hold the defendants who share the row: 陳志明、李小四、 (each a name, nothing more).
            var cellNames = TableNameCell().Replace(Compact(rows[i]), "").Split('、', StringSplitOptions.RemoveEmptyEntries);
            if (cellNames.Length is 0 or > 12 || !cellNames.Any(c => own.Contains(c, StringComparer.OrdinalIgnoreCase))
                || !cellNames.All(c => c.Length is >= 2 and <= 4 || own.Contains(c, StringComparer.OrdinalIgnoreCase))) continue;
            foreach (var row in rows.Skip(i + 1).Take(MaxRowLines))
            {
                var cell = Compact(row);
                if (TableNextRow().IsMatch(row) || (cell.Length <= 14 && (cell.EndsWith('：') || cell.EndsWith(':')))) break; // (二)李小四： opens the next block
                if (cell.Length is 1 or 2 && cell.All(char.IsAsciiDigit)) break; // the next row's number on its own line
                // The row ends too at a heading, and at a line about someone else: 李小四處有期徒刑二年, 判決被告李小四….
                if (ReleaseHeading().IsMatch(cell) || (cell.Contains("被告", StringComparison.Ordinal) && !names.Exists(n => cell.Contains(n, StringComparison.OrdinalIgnoreCase)))
                    || (OrderOpensWithName().Match(cell) is { Success: true } m && StageWords.CommonSurnames.Contains(m.Value[0], StringComparison.Ordinal) && !surnames.Contains(m.Value[0]))) break;
                if (TablePenalty().IsMatch(cell) || OrderSentence().IsMatch(cell)) return LegalStage.Convicted;
                // A cell that says another outcome comes first: 無罪, 免訴, 不受理, 上訴駁回, 撤銷.
                if (cell.Contains("無罪", StringComparison.Ordinal)) return LegalStage.Acquitted;
                if (cell.Length <= 10 && TableOtherOutcome().IsMatch(cell)) break;
                // Under a column headed 宣告刑, 刑度 or 有期徒刑 the cell is the term alone: 1年8月、3月（得易科罰金）, 各10月, 緩刑5年.
                if (terms && TableTerm().IsMatch(cell)) return LegalStage.Convicted;
            }
        }
        return null;
    }

    // ---- Patterns: what kind of text, and where its order is ----

    // 主文 alone on its line (a judgment), or a court press release's heading for it: 壹、判決主文：, 一、主文要旨,
    // 本院判決主文:, 壹、主文摘要說明:, 壹、判決結論:.
    // 判決結論 / 判決結果 is a release's heading only with its item number: a news report may set 判決結果 on a line too.
    [GeneratedRegex("^(?:(?:[壹貳參一二三]、)?(?:本院)?(?:判決)?主文|[壹貳參一二三]、判決結[論果])(?:要旨|摘要)?(?:說明)?(?:[（(][^（）()]*[）)])?[:：]?(?:[（(][^（）()]*[）)])?$")]
    private static partial Regex OrderHeading();

    // 本院114年度訴字第1號…宣判: the court speaking of itself, its case number, its ruling. A news report may cite a
    // court and a case number (臺灣高等法院113年度上訴字第5號), but never says 本院.
    // Or the text opens with the court's name and the case number, and says 宣判: 臺灣士林地方法院113年度訴字第2號…案件，於…宣判.
    [GeneratedRegex(@"本院(?:審理)?[0-9]{2,3}年度?[^，,。]{1,12}字第[0-9]+號.{0,160}(?:宣判|判決)|^(?:臺灣|福建)[\p{IsCJKUnifiedIdeographs}]{2,12}法院[0-9]{2,3}年度[^，,。]{1,12}字第[0-9]+號[^。]{0,160}宣判")]
    private static partial Regex CourtRelease();

    [GeneratedRegex("^(?:[一二三四五壹貳參]、)?(?:犯罪)?(?:事實|理由).{0,12}$")]
    private static partial Regex OrderEnd();

    // 貳、事實及理由摘要, 一、犯罪事實：, (一)論罪科刑: an item number and at most 24 characters with no comma or full stop.
    [GeneratedRegex(@"^(?:[壹貳參叁肆伍陸柒捌玖拾一二三四五六七八九十]+、|[（(][一二三四五六七八九十]+[）)])[^，,。；]{1,24}$")]
    private static partial Regex ReleaseHeading();

    // Not before a line that opens a new item (一、 ㈠ (一) 1.): 一、陳志明無罪 / 二、李小四處有期徒刑二年 stay two.
    // Nor after a bracketed note that follows a full stop: …處有期徒刑二年。（刑度與原審相同）.
    [GeneratedRegex(@"(?<![。；：:;！？\n]|。[（(][^（）()\n]{0,40}[）)])\n(?!\s*(?:[一二三四五六七八九十]+、|[㈠-㈩]|[（(][一二三四五六七八九十]+[）)]|[0-9]{1,2}[.．、]))")]
    private static partial Regex WrappedLine();

    // A table cell on its own line (被告, 陳志明, 宣告刑): at most six characters and no punctuation. It does not run on.
    [GeneratedRegex(@"^[\p{IsCJKUnifiedIdeographs}○A-Za-z0-9]{1,6}$")]
    private static partial Regex TableCell();

    // ---- Patterns: the parties ----

    [GeneratedRegex("^(?:上訴人即|上訴人|聲請人即|抗告人即|即)*被告")]
    private static partial Regex DefendantLine();

    [GeneratedRegex("^(?:(?:選任|指定|公設)?辯護人|上列|輔佐人|(?:訴訟|法定)?代理人|公訴人|上訴人|告訴人|參與人|被害人|具保人|聲請人|受刑人|抗告人|檢察官)")]
    private static partial Regex OtherPartyLine();

    [GeneratedRegex("被告(?:不服[^，,。]{0,8})?提起上訴")]
    private static partial Regex DefendantAppealed();

    // ---- Patterns: a sentence of the order and whose it is ----

    [GeneratedRegex("處(?:有期徒刑|拘役|罰金|無期徒刑|死刑|如(?:本院)?附[表件]|如[^，,。\n]{0,20}所示之刑)|應執行(?:有期徒刑|拘役|罰金)|免刑")]
    private static partial Regex OrderSentence();

    [GeneratedRegex("緩刑[0-9一二三四五六七八九十壹貳參肆伍]+年")]
    private static partial Regex Probation();

    [GeneratedRegex("無罪部分(?=[^。]{0,40}撤銷)")]
    private static partial Regex RevokedAcquittal();

    // What may stand before the person's name when they are the clause's subject: an item number (一、), 被告, and other
    // names of a list (a published judgment may write them 甲○○ or A05).
    // A release also writes the ruling in running text: 判決主文：「王大明犯…, 判決被告王大明…, 經本院認被告王大明犯….
    // 於114年6月9日判決①被告王大明…；②被告李小四…, 另王大明部分改判無罪.
    [GeneratedRegex(@"^\s*[另及]?(?:[一二三四五六七八九十]+、|[㈠-㈩]、?|[①-⑳]|[（(][一二三四五六七八九十0-9]+[）)]|[0-9]{1,2}(?:[.．、]|(?=\s)))?\s*(?:(?:於[^，,。]{0,14}日)?經?(?:本院|合議庭|國民法官法庭)?(?:(?:判決|改判)(?:主文)?[:：]?[「『]?[①-⑳]?|認))?與?(?:(?:上訴人即)?被告)?(?:(?:[\p{IsCJKUnifiedIdeographs}○A-Za-z0-9]{2,4}|[A-Za-z][A-Za-z .\-]{1,40})[、及與]\s*)*$")]
    private static partial Regex OrderSubject();

    // Another defendant's part of an order sentence: a name of two to four characters right before what the order says of them.
    [GeneratedRegex(@"^\s*\p{IsCJKUnifiedIdeographs}{2,4}?(?=共同|幫助|犯|無罪|處|均|應|部分|被訴)")]
    private static partial Regex OrderOpensWithName();

    // The subject of a ruling's clause, to see whether it is someone else: 被告李小四犯…, 判決李小四無罪, 認李小四處….
    [GeneratedRegex(@"(?:被告|判決|改判|認)(?<n>[\p{IsCJKUnifiedIdeographs}○]{2,4}?)(?=共同|幫助|犯|無罪|處|均|應|部分|被訴|涉)")]
    private static partial Regex OtherSubject();

    // ---- Patterns: guards (what looks like an outcome and is not) ----

    // 前經…地方法院以其等共同犯…罪: what the court below ruled, recalled.
    [GeneratedRegex("請求|求處|求刑|聲請|應處|得處|可處|最重|法定刑|前經")]
    private static partial Regex Asked();

    [GeneratedRegex("參與人|第三人|無罪|不予沒收|不另為沒收|已發還|駁回|聲請")]
    private static partial Regex NotTheirProceeds();

    [GeneratedRegex("無罪|原審|第一審|上訴|前經")]
    private static partial Regex NotThisOrder();

    [GeneratedRegex(@"\G[（(][^（）()]{0,16}(?:審理中|另案審理|另行審[理結])[^（）()]{0,8}[）)]")]
    private static partial Regex TriedElsewhere();

    // ---- Patterns: appeals ----

    // 上訴駁回, 其他上訴駁回, 上訴（即…部分）駁回, 駁回其上訴.
    [GeneratedRegex(@"上訴(?:[（(][^（）()]*[）)])?(?:均)?駁回|駁回(?:其等?|被告之?)?上訴")]
    private static partial Regex AppealDismissed();

    // The part of a dismissed appeal, with the names it covers: 上訴（即被告王大明部分）駁回, 上訴駁回（即王大明、李小四部分）.
    [GeneratedRegex(@"上訴[（(]即[^（）()]*部分[）)]駁回|上訴駁回[（(]即[^（）()]*部分[）)]")]
    private static partial Regex DismissedPart();

    // ---- Patterns: row numbers and tables ----

    // （編號1）, （編號2至3）: the row numbers a sentence of the order covers.
    [GeneratedRegex("[（(]編號([0-9]{1,3})(?:至([0-9]{1,3}))?[）)]")]
    private static partial Regex NumberedRows();

    [GeneratedRegex(@"\G[（(]編號([0-9]{1,3})[）)]")]
    private static partial Regex RowNumber();

    [GeneratedRegex("免訴|不受理|駁回|撤銷")]
    private static partial Regex TableOtherOutcome();

    // The heading cell whole: never 檢察官求處有期徒刑, 法定刑度 or 原判決刑度.
    [GeneratedRegex(@"^(?:宣告刑|刑度|(?:應執行)?有期徒刑|[（(]有期徒刑[）)])$")]
    private static partial Regex TableTermHeading();

    // 2年, 1年8月、3月（得易科罰金）, 各10月, 緩刑5年; never a date (112年4月8日) or a range of terms (7月以上11月以下).
    [GeneratedRegex(@"^(?:[各均]?[0-9]{1,2}年(?:[0-9]{1,2}月)?|[各均]?[0-9]{1,2}月|緩刑[0-9]{1,2}年)(?:[、，,（(].*)?$")]
    private static partial Regex TableTerm();

    // What stands beside the name in its cell: a row or item number before it, a bracketed title or a colon after it
    // ((十一)王大明： heads the lines of that person's counts).
    [GeneratedRegex(@"^(?:[0-9]{1,2}[.．、]?|[（(][一二三四五六七八九十0-9]+[）)]|[一二三四五六七八九十]+、|[㈠-㈩]、?)|(?:[（(][^（）()]*[）)])?[:：]?$")]
    private static partial Regex TableNameCell();

    [GeneratedRegex(@"^(?:有期徒刑|無期徒刑|拘役|死刑|罰金)")]
    private static partial Regex TablePenalty();

    // 2 王能斌: a row number, a space and a name; never 1.犯詐欺罪 (a count of this row) or 7 年 of a wrapped penalty.
    [GeneratedRegex(@"^\s*[0-9]{1,2}\s+[\p{IsCJKUnifiedIdeographs}○]{2,4}(?:\s|$|[（(])")]
    private static partial Regex TableNextRow();
}
