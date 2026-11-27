namespace Aml.Core.Identity;

/// <summary>
/// The closed word lists of the advisory implausible age at a dated document
/// (identity-adjudication.md#implausible-age-at-a-dated-document-advisory), kept apart from the logic in
/// <see cref="HardConflictGate"/> so they can be extended alone. They are known to be incomplete. Every list makes the hint
/// rarer, except <see cref="Qualifying"/> and <see cref="SafeFollowers"/>, which make it more frequent.
/// </summary>
internal static class DocumentAgeWords
{
    /// <summary>Directly before a whole full-name mention: a suspect or a company officer. Longest first; 實際負責人 and 常務董事 end in one.</summary>
    public static readonly string[] Qualifying =
        ["實際負責人", "共同被告", "副董事長", "副總經理", "獨立董事", "常務董事", "嫌疑人", "董事長", "總經理", "負責人", "執行長", "監察人", "被告", "嫌犯", "犯嫌", "董事"];

    /// <summary>Directly before a qualifying word, it is another word and does not qualify (原被告, 準被告, 代位被告); 前 is allowed.</summary>
    public static readonly string[] NotRolePrefixes = ["代位", "原", "準"];

    /// <summary>Directly before a full name or its qualifying word, the item is silent: a child of somebody (其子女, 兒子董事…).</summary>
    public static readonly string[] Kinship = ["兒子", "女兒", "孫子", "孫女", "子女", "幼子", "幼女"];

    /// <summary>The mentions of the Profile's names examined per item; over it the item is silent (no article names a person a thousand times).</summary>
    public const int MaxMentions = 1000;

    /// <summary>Directly before any full-name mention, the item is silent: victims, complainants, witnesses, children, officials.</summary>
    public static readonly string[] Disqualifying =
    [
        "主任檢察官", "被害人", "受害人", "被害者", "受害者", "告訴人", "告發人", "檢舉人", "證人", "少年", "少女", "男童", "女童", "兒童", "學童",
        "幼童", "學生", "檢察官", "審判長", "法官", "律師", "辯護人", "警員", "員警", "警官",
    ];

    /// <summary>
    /// After a whole name: particles that never start a name or an organisation (the digest's AfterName set without 於, 在, 向,
    /// 將, 為, 以, 經, 曾, 都, 一, 有, 無, 和, 是, 今, 昨, 日, 前, 後, 對, 到, 把, 從, 指, 仍, 再, 又, 表, which can), and the offence
    /// and penalty words of a court text (被告張文華詐欺案件, 判處張文華有期徒刑).
    /// </summary>
    public static readonly string[] SafeFollowers =
    [
        "涉", "遭", "被", "與", "及", "等", "因", "已", "則", "也", "亦", "稱", "說", "並", "坦", "供", "否", "辯", "均", "獲", "未", "不", "的",
        "詐欺", "貪污", "違反", "侵占", "背信", "洗錢", "竊盜", "偽造", "有期徒刑", "無期徒刑", "拘役", "罰金", "死刑",
    ];

    /// <summary>Anywhere in the item, the item is silent: a minor or a child context (a juvenile defendant may be the young customer).</summary>
    public static readonly string[] MinorCues =
    [
        // minors and schooling
        "少年", "少女", "未成年", "兒童", "男童", "女童", "幼童", "學童", "孩童", "國小", "國中", "小學", "中學生", "學生", "高中", "國一", "國二", "國三",
        "幼稚園", "幼兒園", "就讀", "年幼", "年紀尚幼", "幼兒", "嬰兒", "稚子", "小六生",
        // a guardian or limited capacity: a minor acting through others
        "法定代理人", "監護人", "監護宣告", "受監護", "輔助宣告", "無行為能力", "限制行為能力", "代為出席", "由父母",
        // civil and inheritance: a minor can be a civil defendant or an heir holding shares
        "民事", "原告", "繼承", "遺產", "損害賠償", "連帶給付",
    ];

    /// <summary>Anywhere in the item, the item is silent: a nominal officer or a name used by someone else (a minor so used is the same person).</summary>
    public static readonly string[] NominalCues = ["人頭", "掛名", "冒名", "冒用", "盜用", "借名", "名義負責人", "登記負責人"];

    /// <summary>In the sentence of a qualifying mention, that mention does not qualify: negation, mistaken identity, misuse of a name, the person harmed.</summary>
    public static readonly string[] SentenceCues = ["否認", "並非", "不是", "誤傳", "誤認", "誤植", "同名", "冒用", "冒名", "盜用", "人頭", "掛名", "名義", "被害", "受害", "遭詐", "受騙"];

    /// <summary>Directly before the qualifying word, that mention does not qualify (非董事張文華).</summary>
    public const char NegationBeforeRole = '非';

    /// <summary>A stated age under this anywhere in the item makes it silent (N 歲, 年僅 N), as does 未滿 N 歲.</summary>
    public const int MinorAge = 18;

    /// <summary>Sentence ends for the sentence cues.</summary>
    public static readonly System.Buffers.SearchValues<char> SentenceEnds = System.Buffers.SearchValues.Create("。！？；!?;\n");
}
