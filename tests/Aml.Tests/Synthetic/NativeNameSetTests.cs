using Aml.Core.Identity;
using Aml.Core.Shared;
using Aml.Core.Subjects;

namespace Aml.Tests.Synthetic;

/// <summary>
/// Native-script name probes (docs/plan/poc-delivery-plan.md#native-script-name-probes): invented pairs whose romanised
/// names agree, built as row 4 shapes (gender, nationality and the birth year agree), through the identity policy. A logic
/// check on chosen forms, not a rate. Measurement only: the signal concludes nothing; the what-if counts what a NO_MATCH on
/// the signal would decide, split by whether the comparison's folding covers the forms of a same-person pair.
/// </summary>
public class NativeNameSetTests
{
    /// <param name="Folded">False for a same-person pair whose two forms neither the fold table nor the possibly-same list joins (a known wrong signal).</param>
    private sealed record Probe(string Kind, bool Same, bool Folded, string Customer, string Profile);

    private static readonly Probe[] Probes =
    [
        // Different persons: the same romanisation, other characters.
        new("surname, same reading", false, true, "張文華/Chang Wen-Hua", "Chang Wen-Hua/章文華"),
        new("surname, same Wade-Giles", false, true, "徐家豪/Hsu Chia-Hao", "Hsu Chia-Hao/許家豪"),
        new("surname, same reading", false, true, "林志明/Lin Chih-Ming", "Lin Chih-Ming/藺志明"),
        new("one given character", false, true, "張文華/Chang Wen-Hua", "Chang Wen-Hua/張文樺"),
        new("one given character", false, true, "王小明/Wang Hsiao-Ming", "Wang Hsiao-Ming/王曉明"),
        new("one given character", false, true, "陳美玲/Chen Mei-Ling", "Chen Mei-Ling/陳美齡"),
        new("all given characters", false, true, "張文華/Chang Wen-Hua", "Chang Wen-Hua/張雯樺"),
        new("all given characters", false, true, "林志明/Lin Chih-Ming", "Lin Chih-Ming/林智銘"),
        new("Wade-Giles against Pinyin", false, true, "張志宏/Chang Chih-Hung", "Zhang Zhihong/章志鴻"),
        new("Wade-Giles against Pinyin", false, true, "徐家豪/Hsu Chia-Hao", "Xu Jiahao/許家豪"),
        // The same person: forms the comparison joins, or that are not comparable.
        new("traditional against simplified", true, true, "張文華/Chang Wen-Hua", "Chang Wen-Hua/张文华"),
        new("controlled variant", true, true, "鍾文華/Chung Wen-Hua", "Chung Wen-Hua/鐘文華"),
        new("table variant 峰/峯", true, true, "林志峰/Lin Chih-Feng", "Lin Chih-Feng/林志峯"),
        new("table variant 恆/恒", true, true, "陳恆毅/Chen Heng-Yi", "Chen Heng-Yi/陈恒毅"),
        new("several native forms, one equal", true, true, "張文華/Chang Wen-Hua", "Chang Wen-Hua/章文華/張文華"),
        new("space in the local name", true, true, "張 文華/Chang Wen-Hua", "Chang Wen-Hua/張文華"),
        new("middle dot in the local name", true, true, "張·文華/Chang Wen-Hua", "Chang Wen-Hua/張文華"),
        new("name order reversed", true, true, "文華 張/Chang Wen-Hua", "Chang Wen-Hua/張文華"),
        new("masked list form", true, true, "張文華/Chang Wen-Hua", "Chang Wen-Hua/張○華"),
        new("masked customer form", true, true, "張○華/Chang Wen-Hua", "Chang Wen-Hua/張文華"),
        new("list form in kana", true, true, "山田花子/Yamada Hanako", "Yamada Hanako/ヤマダハナコ"),
        new("customer alias equals the list form", true, true, "張文華/Chang Wen-Hua/章文華", "Chang Wen-Hua/章文華"),
        new("two against three characters", true, true, "張文華/Chang Wen-Hua", "Chang Wen-Hua/張華"),
        // The same person, forms equality does not join but the possibly-same list does: silent.
        new("variant 杰/傑", true, true, "張文傑/Chang Wen-Chieh", "Chang Wen-Chieh/張文杰"),
        new("simplified 启 folds to 啓, not 啟", true, true, "李啟明/Li Chi-Ming", "Li Chi-Ming/李启明"),
        new("Japanese shinjitai 沢/澤", true, true, "林澤民/Lin Tse-Min", "Lin Tse-Min/林沢民"),
        new("Japanese shinjitai 広/廣", true, true, "廣田一郎/Hirota Ichiro", "Hirota Ichiro/広田一郎"),
        // The same person, variants in neither table: the signal is wrong (the residual risk).
        new("variant 喆/哲 in neither table", true, false, "張文哲/Chang Wen-Che", "Chang Wen-Che/張文喆"),
        new("variant 堃/坤 in neither table", true, false, "李坤宏/Li Kun-Hung", "Li Kun-Hung/李堃宏"),
        new("variant 珎/珍 in neither table", true, false, "王美珍/Wang Mei-Chen", "Wang Mei-Chen/王美珎"),
        new("variant 敍/敘 in neither table", true, false, "林敘德/Lin Hsu-Te", "Lin Hsu-Te/林敍德"),
    ];

    private static IdentityResult Decide(IEnumerable<string> customer, IEnumerable<string> profile) =>
        IdentityDecisionPolicy.Decide(
            new(SubjectRole.Customer, null, [.. customer], Gender.Male, new PartialDate(1980, 5, 20), ["TW"], null, null, null, null, [], []),
            new("DJ:1", Gender.Male, [.. profile], [new PartialDate(1980)], ["TW"], [], [new SourceCategory("Special Interest Person (SIP)", "Fraud")], [], [], [], null, [], []));

    private static bool IsLatin(string name) => !name.Any(c => c > 'ɏ');

    private static (bool Signal, bool ProbableBefore, bool ProbableNow) Run(Probe x)
    {
        var (c, p) = (x.Customer.Split('/'), x.Profile.Split('/'));
        var now = Decide(c, p);
        var romanisedOnly = Decide(c.Where(IsLatin), p.Where(IsLatin));
        return (now.ReasonCodes.Contains(ReasonCodes.NativeNameConflict), romanisedOnly.Decision == IdentityDecision.ProbableMatch, now.Decision == IdentityDecision.ProbableMatch);
    }

    [Fact]
    public void Signals_per_probe()
    {
        // Every different person is signalled; of the same person only the four pairs neither table joins.
        Assert.Equal(Probes.Select(x => x.Same ? !x.Folded : true), Probes.Select(x => Run(x).Signal));
        Assert.Equal((10, 4, 27), (Probes.Count(x => !x.Same), Probes.Count(x => x.Same && !x.Folded), Probes.Count(x => x.Folded)));
    }

    [Fact]
    public void Probable_matches_lost_and_the_what_if_of_a_concluding_signal()
    {
        var runs = Probes.Select(x => (x, r: Run(x))).ToList();
        // Lost PROBABLE_MATCH (row 4 → row 5): rightly the 8 strangers whose romanised names are EXACT (the 2 Wade-Giles /
        // Pinyin pairs were PARTIAL, row 5, already), wrongly the 4 same-person pairs neither table joins.
        Assert.Equal((8, 4), (runs.Count(t => !t.x.Same && t.r.ProbableBefore && !t.r.ProbableNow), runs.Count(t => t.x.Same && t.r.ProbableBefore && !t.r.ProbableNow)));
        // Nothing else moves: no pair gains PROBABLE_MATCH, and nothing is NO_MATCH.
        Assert.Equal(0, runs.Count(t => !t.r.ProbableBefore && t.r.ProbableNow));
        // What-if the signal concluded NO_MATCH, right / wrong: where a table covers the forms 10 / 0, where neither does 0 / 4.
        int Count(bool folded, bool same) => runs.Count(t => t.r.Signal && t.x.Folded == folded && t.x.Same == same);
        Assert.Equal(((10, 0), (0, 4)), ((Count(true, false), Count(true, true)), (Count(false, false), Count(false, true))));
    }
}
