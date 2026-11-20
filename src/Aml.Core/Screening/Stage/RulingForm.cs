using Aml.Core.Normalization;

namespace Aml.Core.Screening.Stage;

/// <summary>
/// The form of a judgment or a court's release of its ruling: which kind of text it is, where its order is and where it
/// ends, who its defendants are, how its wrapped lines join, which clauses of a sentence have the person as subject, and
/// how a table of defendants is walked. It says what the text holds, never what that means for the stage
/// (<see cref="RulingRules"/>).
/// </summary>
internal static class RulingForm
{
    // The item numbers of a release's headings: 一、 under a heading numbered so, 壹、 for the headings of the series.
    private const string ItemNumerals = "一二三四五六七八九十", SeriesNumerals = "壹貳參叁肆伍陸柒捌玖拾";

    /// <summary>The text is a court's press release of its ruling: it has a release's heading for the order, or opens in the court's own voice.</summary>
    internal static bool IsCourtRelease(string news) =>
        news.Split('\n').Take(StageGlossary.MaxOrderLines).Select(StageText.Compact).ToList() is var lines && !lines.Contains(StageGlossary.OrderHeadingWord)
        && (lines.Exists(l => StagePatterns.OrderHeading().IsMatch(l)) || StagePatterns.CourtRelease().IsMatch(string.Concat(lines.Take(StageGlossary.MaxOpeningLines))));

    // ---- The text, read once ----

    internal static RulingText? Parse(string news, IReadOnlyList<string> names)
    {
        var lines = StageText.HalfWidth(news).Replace("\r", "", StringComparison.Ordinal).Split('\n');
        var start = Array.FindIndex(lines, l => StagePatterns.OrderHeading().IsMatch(StageText.Compact(l)));
        // A court's press release of its ruling has no party lines and gives the outcome under a heading of its own
        // (壹、判決主文：, 主文要旨, 壹、判決結論:) or in running text. It is known by such a heading, or by an opening in the
        // court's own voice: the court, the case number, and that it ruled (本院114年度訴字第1號被告…案件，於今日宣判).
        var release = start < 0 ? StagePatterns.CourtRelease().IsMatch(StageText.Compact(string.Join('\n', lines.Take(StageGlossary.MaxOpeningLines)))) : StageText.Compact(lines[start]) != StageGlossary.OrderHeadingWord;
        if (start < 0 && !release) return null;
        var own = names.Take(StageGlossary.MaxNames).Where(n => n.Length > 1).Select(StageText.HalfWidth).ToList();
        // The header lists defendants on a 被告 line and on the lines after it, up to the next party's line (辯護人, 上列 …).
        var listed = new List<string>();
        var defendantLines = 0;
        string? head = null;
        foreach (var l in lines.Take(release ? 0 : start).Select(StageText.Compact))
        {
            if (StagePatterns.DefendantLine().IsMatch(l)) head = l;
            else if (StagePatterns.OtherPartyLine().IsMatch(l)) head = null;
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
            var ruled = Array.FindIndex(lines, 0, Math.Min(lines.Length, StageGlossary.MaxOpeningLines), l => l.Contains(StageGlossary.Pronounced, StringComparison.Ordinal));
            var opening = StageText.Compact(string.Join('\n', lines.Take(ruled >= 0 ? ruled + 1 : Math.Max(start, 1)))).Replace(StageGlossary.NamedAsDefendant, "", StringComparison.Ordinal);
            if (opening.IndexOf(StageGlossary.Pronounced, StringComparison.Ordinal) is >= 0 and var at) opening = opening[..at]; // the line may go on past the ruling
            // an opening names a handful; 被告提起上訴, 被告不服 go on about the defendants already named
            var after = opening.Split(StageGlossary.Defendant, StageGlossary.MaxOpeningDefendants + 2).Skip(1).Take(StageGlossary.MaxOpeningDefendants).Where(a => !StageText.StartsWithAny(a, StageGlossary.OpeningGoesOn)).ToList();
            appealed = StagePatterns.DefendantAppealed().IsMatch(opening);
            var compact = own.Select(StageText.Compact).ToList();
            var theirs = after.Select(a => compact.FirstOrDefault(n => a.StartsWith(n, StringComparison.OrdinalIgnoreCase))).ToList();
            defendant = theirs.Exists(n => n is not null);
            sole = defendant && theirs.TrueForAll(n => n is not null) && !after.Zip(theirs).Any(x => x.First[x.Second!.Length..] is var rest && StageText.StartsWithAny(rest, StageGlossary.ListGoesOn));
        }
        var surnames = own.Where(n => CjkText.IsCjk(n[0])).Select(n => n[0]).ToHashSet();
        if (sole) own.Add(StageGlossary.Defendant); // the order of a one-defendant judgment may say 被告無罪 without the name
        // A judgment's order ends at the 事實 / 理由 heading. A release's order, under its heading, ends at the next heading
        // of the series: a line opening 壹、 貳、 … (the order's own items are never numbered so), or under a heading
        // numbered 一、 the next short line numbered that way.
        var small = start >= 0 && ItemNumerals.Contains(StageText.Compact(lines[start])[0], StringComparison.Ordinal);
        var end = start < 0 ? -1 : Array.FindIndex(lines, start + 1, l => StageText.Compact(l) is var c && (!release ? StagePatterns.OrderEnd().IsMatch(c)
            : small ? ItemNumerals.Contains(c.FirstOrDefault(), StringComparison.Ordinal) && StagePatterns.ReleaseHeading().IsMatch(c)
            : c.Length > 1 && c[1] == '、' && SeriesNumerals.Contains(c[0], StringComparison.Ordinal)));
        return new()
        {
            Release = release,
            HasHeading = start >= 0,
            Block = lines[(start + 1)..Math.Min(end < 0 ? lines.Length : end, start + 1 + (release ? StageGlossary.MaxReleaseLines : StageGlossary.MaxOrderLines))],
            All = lines[..Math.Min(lines.Length, StageGlossary.MaxReleaseLines)],
            Own = own,
            Surnames = surnames,
            Listed = listed,
            Defendant = defendant,
            Sole = sole,
            Appealed = appealed,
        };
    }

    // A heading (a short numbered line with no comma, or the order's heading) ends there; it does not run on.
    // A release's page may break its lines inside a sentence, even inside a name: a line that ends no sentence runs
    // on. Its items join outcomes with ； (王大明部分公訴不受理；其餘被訴部分無罪), read here as a comma.
    internal static string Text(RulingText t, string[] rows) => !t.Release ? string.Join('\n', rows)
        : StagePatterns.WrappedLine().Replace(string.Join('\n', rows.Select(l => StageText.Compact(l) is var c && (StagePatterns.OrderHeading().IsMatch(c) || StagePatterns.ReleaseHeading().IsMatch(c) || StagePatterns.TableCell().IsMatch(c)) ? l + "。" : l)), "").Replace('；', '，').Replace(';', '，');

    /// <summary>Where the text goes on after each writing of one of the person's names.</summary>
    internal static List<int> AfterNames(RulingText t) => [.. t.Own.Where(n => n != StageGlossary.Defendant).SelectMany(n => After(t.WholeText, n))];

    private static IEnumerable<int> After(string text, string n)
    {
        for (var i = text.IndexOf(n, StringComparison.OrdinalIgnoreCase); i >= 0; i = text.IndexOf(n, i + n.Length, StringComparison.OrdinalIgnoreCase)) yield return i + n.Length;
    }

    // ---- Whose a sentence of the order's form is ----

    /// <summary>
    /// What the rows say of the person, sentence by sentence (an empty text where a sentence says nothing of them): the
    /// clauses whose subject they are, to a clause that is another's. What is asked for, or what the law allows, is no
    /// clause of theirs. Lazy: a reader that stops at a sentence reads no further.
    /// </summary>
    internal static IEnumerable<string> SaidOfPerson(RulingText t, string[] rows)
    {
        var (release, own) = (t.Release, t.Own);
        var text = Text(t, rows);
        var carried = false; // the sentence before this one was the person's
        foreach (var (at, length) in StageText.Spans(text, 0, text.Length, StageText.SentenceEnds))
        {
            var theirs = new System.Text.StringBuilder();
            foreach (var (from, size) in StageText.Spans(text, at, length, StageText.ClauseEnds))
            {
                var clause = text.Substring(from, size);
                // What is asked for, or what the law allows, is no outcome: 檢察官請求處有期徒刑五年, 應處有期徒刑.
                if (release && StagePatterns.Asked().IsMatch(clause)) continue;
                var another = release && Another(t, clause);
                // 王大明（本院審理中）、李小四犯…: the bracket takes the person out of this ruling.
                var named = own.Exists(n => !(n == StageGlossary.Defendant && another) && clause.IndexOf(n, StringComparison.OrdinalIgnoreCase) is >= 0 and var i && StagePatterns.OrderSubject().IsMatch(clause[..i])
                    && !(release && StagePatterns.TriedElsewhere().Match(clause, i + n.Length) is { Success: true } e && e.Index == i + n.Length));
                if (theirs.Length == 0 && !named) continue;
                if (!named && (another || (StagePatterns.OrderOpensWithName().Match(clause) is { Success: true } m
                    && StageGlossary.CommonSurnames.Contains(m.Value.TrimStart()[0], StringComparison.Ordinal) && !t.Surnames.Contains(m.Value.TrimStart()[0])))) break;
                theirs.Append(clause).Append('，');
            }
            var said = theirs.ToString();
            // A release's item goes on about the person in a sentence with no subject: 王大明犯…罪，共2罪。應執行有期徒刑二年。
            if (release && said.Length == 0 && carried && GoesOnWithoutSubject(text, at, length)) said = text.Substring(at, length);
            // Not after an acquittal or a dismissal: what follows it is another's.
            carried = said.Length > 0 && !StageText.ContainsAny(said, StageGlossary.ItemEnding);
            yield return said;
        }
    }

    private static bool GoesOnWithoutSubject(string text, int at, int length) =>
        StageText.StartsWithAny(text.AsSpan(at, length).TrimStart(), StageGlossary.ContinuationOpeners);

    // 被告李小四犯…, 判決李小四無罪: the clause's subject is another person. The only defendant's 被告 is not theirs there.
    internal static bool Another(RulingText t, string clause) => StagePatterns.OtherSubject().Match(clause) is { Success: true } m && m.Groups["n"].Value is var n
        && StageGlossary.CommonSurnames.Contains(n[0], StringComparison.Ordinal) && !t.Own.Exists(o => o != StageGlossary.Defendant && clause.AsSpan(m.Groups["n"].Index).StartsWith(o, StringComparison.OrdinalIgnoreCase));

    // ---- A table of defendants ----

    /// <summary>A column of the table is headed 宣告刑, 刑度 or 有期徒刑: its cells are the term alone.</summary>
    internal static bool HasTermColumn(string[] rows) =>
        rows.Any(r => StageText.Compact(r) is { Length: <= 10 } c && StagePatterns.TableTermHeading().IsMatch(c));

    /// <summary>
    /// The person's rows of a table the page's text holds one cell a line: for each line that is the person's name cell
    /// (a row number before it, a bracketed title after it), the cells after it, to the row's end. The row ends at the
    /// next numbered row, a heading, or a line about someone else. Lazy: a reader may stop a row and go on to the next.
    /// </summary>
    /// <param name="own">The person's names, compacted, without 被告.</param>
    /// <param name="names">The person's names as the text is read with (with 被告 for the only defendant).</param>
    internal static IEnumerable<IEnumerable<string>> TableRows(string[] rows, List<string> own, List<string> names, HashSet<char> surnames)
    {
        for (var i = 0; i < rows.Length; i++)
        {
            // The name cell may hold the defendants who share the row: 陳志明、李小四、 (each a name, nothing more).
            var cellNames = StagePatterns.TableNameCell().Replace(StageText.Compact(rows[i]), "").Split('、', StringSplitOptions.RemoveEmptyEntries);
            if (cellNames.Length is 0 or > 12 || !cellNames.Any(c => own.Contains(c, StringComparer.OrdinalIgnoreCase))
                || !cellNames.All(c => c.Length is >= 2 and <= 4 || own.Contains(c, StringComparer.OrdinalIgnoreCase))) continue;
            yield return RowCells(rows, i, names, surnames);
        }
    }

    private static IEnumerable<string> RowCells(string[] rows, int i, List<string> names, HashSet<char> surnames)
    {
        foreach (var row in rows.Skip(i + 1).Take(StageGlossary.MaxRowLines))
        {
            var cell = StageText.Compact(row);
            if (StagePatterns.TableNextRow().IsMatch(row) || (cell.Length <= 14 && (cell.EndsWith('：') || cell.EndsWith(':')))) yield break; // (二)李小四： opens the next block
            if (cell.Length is 1 or 2 && cell.All(char.IsAsciiDigit)) yield break; // the next row's number on its own line
            // The row ends too at a heading, and at a line about someone else: 李小四處有期徒刑二年, 判決被告李小四….
            if (StagePatterns.ReleaseHeading().IsMatch(cell) || (cell.Contains(StageGlossary.Defendant, StringComparison.Ordinal) && !names.Exists(n => cell.Contains(n, StringComparison.OrdinalIgnoreCase)))
                || (StagePatterns.OrderOpensWithName().Match(cell) is { Success: true } m && StageGlossary.CommonSurnames.Contains(m.Value[0], StringComparison.Ordinal) && !surnames.Contains(m.Value[0]))) yield break;
            yield return cell;
        }
    }
}
