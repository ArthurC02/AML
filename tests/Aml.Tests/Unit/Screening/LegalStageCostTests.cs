using System.Diagnostics;
using Aml.Core.Screening.Stage;

namespace Aml.Tests.Unit.Screening;

/// <summary>
/// The cost of the rule-based legal-stage reading on made-up documents of the three kinds its rules treat differently
/// (watchlist-and-adverse-information.md, Stage from attached news, Cost): it grows linearly with the text, and stays far
/// below a generous per-character budget. A guard against a new rule quietly making the reading much slower.
/// </summary>
/// <remarks>
/// Timed with a <see cref="Stopwatch"/>, as <c>NewsDigestTests.Hostile_text_is_read_in_bounded_time</c> is: this is a
/// performance bound, not business time, so <c>ManualTimeProvider</c> does not apply. Each reading's time is its minimum over
/// <see cref="Repetitions"/> passes after <see cref="WarmUps"/> warm-up passes: load can only add time, so the minimum is
/// the figure it inflates least, and the minimum of a reading of a few milliseconds is less exposed than that of a pass.
/// </remarks>
public class LegalStageCostTests
{
    // Measured outside the repository with SourceConditions.Determine on 1,130 (person, document) pairs of the annotated
    // sets, 5,110 characters on average, on a busy developer machine (2026-11).
    private const double MeasuredMsPer10kChars = 2.7;

    // A tripwire for a gross regression (about nine times the measure), not a target. The made-up corpus below is denser
    // in names and stage words than real text and read in a Debug build: about 4 to 5 ms per 10,000 characters on a busy machine.
    private const double BudgetMsPer10kChars = 25;

    // Linear code takes about 2 times as long on a body twice as long (a little less, as the head is read once);
    // quadratic code takes about 4 times (3.85 with a head of a few hundred characters; per-reading overhead brings it
    // nearer 3.5). 3 lies between them with room either side for what noise survives the minimum.
    // What this does not catch: a quadratic part under about half of the reading's time at this size (growth 2 + 2 × its
    // share), and growth in a release's ruling readers past StageGlossary.MaxReleaseLines, which the doubled release exceeds.
    private const double MaxGrowthOnDoubledBody = 3;

    private const int WarmUps = 3;
    private const int Repetitions = 15;
    private const int TargetChars = 5_000;

    // 林美玲 is named only in the bodies, so no order speaks of her and every ruling reader is tried; 周無名 is never named.
    private static readonly string[] Persons = ["張文華", "李小四", "林美玲", "周無名"];
    private static readonly string[] Named = ["張文華", "李小四", "王大同"];

    private static string N(int i) => Named[i % Named.Length];

    // A prosecutors' report about many suspects.
    private const string ReportHead = "臺灣臺北地方檢察署新聞稿\n本署偵辦跨境詐欺集團案，偵查終結，起訴張文華等12人。\n";

    private static string ReportSentence(int i) => (i % 8) switch
    {
        0 => $"被告{N(i)}涉嫌詐欺、洗錢，經本署於114年{i % 12 + 1}月{i % 28 + 1}日約談到案後，依詐欺等罪嫌提起公訴。",
        1 => $"{N(i + 1)}（另案通緝中）於第{i}次搜索時遭查獲，檢察官聲請羈押獲准。",
        2 => $"{N(i)}等{i % 9 + 2}人以電話佯稱被害人帳戶涉及洗錢、已遭通緝云云，騙取新臺幣{i * 3 + 10}萬元。",
        3 => $"{N(i + 2)}則因罪證不足，獲不起訴處分。",
        4 => $"被告{N(i)}到庭稱「我只是幫朋友領錢，不知道是詐騙」，檢方認其所辯不可採。",
        5 => $"另同案被告{N(i + 1)}所涉洗錢部分，檢察官為緩起訴處分，緩起訴期間{i % 3 + 1}年。",
        // 起訴 right before a name: whether it indicts someone else is looked up in the whole text.
        6 => $"警方於{i % 20 + 1}處據點查扣手機{i % 50 + 5}支，檢方已起訴林美玲，林美玲另涉洗錢部分依法偵辦。",
        _ => $"檢方指出，{N(i)}擔任車手，負責提領款項，嫌疑重大，現由法院審理中。",
    };

    // A judgment with a party header, the order and reasons, anonymised with court codes in the order and the reasons.
    private const string JudgmentHead = "臺灣臺北地方法院刑事判決\n公 訴 人 臺灣臺北地方檢察署檢察官\n被 告 張文華\n      李小四\n      王大同\n"
        + "上列被告因詐欺等案件，經檢察官提起公訴，本院判決如下：\n主 文\nA03犯三人以上共同詐欺取財罪，處有期徒刑一年。\nA07無罪。\nA12犯幫助洗錢罪，處有期徒刑六月。\n理 由\n";

    private static readonly string[] Codes = ["A03", "A07", "A12"];

    private static string JudgmentSentence(int i) => (i % 6) switch
    {
        0 => $"一、{Codes[i % 3]}與真實姓名年籍不詳之人共同意圖為自己不法之所有，於113年{i % 12 + 1}月{i % 28 + 1}日以電話佯稱被害人帳戶遭冒用云云，致被害人匯款新臺幣{i * 7 + 20}萬元。",
        1 => $"{Codes[(i + 1) % 3]}（另行審結）提供帳戶，所涉犯嫌，業經檢察官提起公訴。\n",
        2 => $"被告{Codes[i % 3]}於本院審理時坦承犯行，核與證人林美玲於警詢之證述相符，並有匯款紀錄{i % 40 + 1}份在卷可稽。",
        3 => $"是被告{Codes[(i + 1) % 3]}所辯，尚非無據，此外查無其他積極證據足認其犯行，應為無罪之諭知。\n",
        4 => $"爰審酌被告{Codes[i % 3]}犯後態度、智識程度、家庭生活狀況及所生損害新臺幣{i + 50}萬元，量處如主文所示之刑。",
        _ => $"檢察官請求從重量刑，判處有期徒刑{i % 5 + 2}年，本院認尚屬過重。\n",
    };

    // A court's press release with its order heading and a table-like block of defendants.
    private const string ReleaseHead = "臺灣臺北地方法院新聞稿\n本院114年度訴字第1號被告張文華等3人詐欺案件，於今日宣判。\n壹、判決主文：\n"
        + "㈠張文華犯三人以上共同詐欺取財罪，處有期徒刑2年。\n㈡李小四無罪。\n㈢王大同處如附表所示之刑。\n貳、理由摘要：\n";

    private static string ReleaseSentence(int i) => (i % 6) switch
    {
        0 => $"被告張文華擔任詐欺集團首腦，指揮成員於113年{i % 12 + 1}月間向被害人{i % 30 + 2}人詐得新臺幣{i * 5 + 30}萬元。\n",
        1 => $"被告李小四雖提供帳戶，惟無證據證明其知情，應為無罪之諭知。\n",
        2 => $"{i % 40 + 1}\n王大同（車手）\n有期徒刑{i % 3 + 1}年{i % 11 + 1}月\n",
        3 => $"被告王大同（編號{i % 40 + 1}）負責提領款項，犯後坦承，量處有期徒刑{i % 4 + 1}年。\n",
        4 => $"檢察官請求從重量刑，判處張文華有期徒刑{i % 5 + 6}年，本院審酌後認不宜。\n",
        _ => $"告訴人林美玲表示將請求檢察官上訴，本件尚未確定，得於判決送達後{i % 10 + 10}日內提起上訴。\n",
    };

    private static readonly (string Head, Func<int, string> Sentence)[] Kinds =
        [(ReportHead, ReportSentence), (JudgmentHead, JudgmentSentence), (ReleaseHead, ReleaseSentence)];

    // The head, then as many sentences as bring each document of the base corpus to about TargetChars; doubled, twice as many.
    private static string[] Corpus(int bodies) => Kinds.Select(k =>
    {
        var count = 0;
        for (var length = k.Head.Length; length < TargetChars; count++) length += k.Sentence(count).Length;
        return k.Head + string.Concat(Enumerable.Range(0, count * bodies).Select(k.Sentence));
    }).ToArray();

    // Reads every (document, person) pair once, keeping in best the fastest time seen for each.
    private static void Read(string[] corpus, TimeSpan[] best)
    {
        var i = 0;
        foreach (var document in corpus)
            foreach (var person in Persons)
            {
                var started = Stopwatch.GetTimestamp();
                LegalStageReading.FromNews(document, [person]);
                var elapsed = Stopwatch.GetElapsedTime(started);
                if (elapsed < best[i]) best[i] = elapsed;
                i++;
            }
    }

    [Fact]
    public void The_reading_grows_linearly_and_stays_within_its_budget()
    {
        var (single, doubled) = (Corpus(1), Corpus(2));
        var readings = Kinds.Length * Persons.Length;
        var (bests, bestsDoubled) = (Enumerable.Repeat(TimeSpan.MaxValue, readings).ToArray(), Enumerable.Repeat(TimeSpan.MaxValue, readings).ToArray());
        for (var w = 0; w < WarmUps; w++) // past the JIT's first tier, which would favour whichever corpus is timed later
        {
            Read(single, new TimeSpan[readings]);
            Read(doubled, new TimeSpan[readings]);
        }

        for (var r = 0; r < Repetitions; r++) // interleaved, so a burst of load falls on both corpora alike
        {
            Read(single, bests);
            Read(doubled, bestsDoubled);
        }

        // The minimum is taken per reading: a few milliseconds each, so one of the repetitions nearly always runs undisturbed.
        var (best, bestDoubled) = (bests.Aggregate((a, b) => a + b), bestsDoubled.Aggregate((a, b) => a + b));
        var growth = bestDoubled / best;
        var characters = single.Sum(d => d.Length) * Persons.Length;
        var msPer10k = best.TotalMilliseconds * 10_000 / characters;
        var figures = $"{best.TotalMilliseconds:F1} ms for {characters} characters ({msPer10k:F2} ms per 10,000; measured {MeasuredMsPer10kChars}), "
            + $"{bestDoubled.TotalMilliseconds:F1} ms doubled (x{growth:F2})";
        Assert.True(growth <= MaxGrowthOnDoubledBody, figures);
        Assert.True(msPer10k < BudgetMsPer10kChars, figures);
    }
}
