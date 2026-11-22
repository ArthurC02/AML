namespace Aml.Core.Screening.Stage;

/// <summary>
/// The words and limits the stage readers and the news digest work with: what changes when a new wording turns up. Every
/// word list is plain words separated by | with no regex syntax, so it can be kept as a glossary outside the code;
/// <see cref="StagePatterns"/> composes its patterns from these lists, and the rules and the form readers test with them.
/// Where an entry says that a pattern uses it, adding a word changes that pattern for every rule that uses the pattern.
/// </summary>
internal static class StageGlossary
{
    /// <summary>The version of this glossary; change it with any entry.</summary>
    internal const string Version = "stage-glossary-5";

    // ---- Stages and penalties ----

    /// <summary>
    /// Words of a prosecutor's dismissal (pattern Dismissal: DISMISSED), a withdrawn indictment among them; they are read
    /// before an indictment, so 不起訴 is never INDICTED. A word that is not a dismissal (起訴) would read every indictment as
    /// DISMISSED.
    /// </summary>
    internal const string DismissalWords = "不起訴|緩起訴|撤回起訴|撤回公訴|不予起訴|免予起訴";

    /// <summary>
    /// What may not stand right before 提起公訴 for it to be an indictment (pattern PublicProsecution): 不提起公訴, 未經提起公訴.
    /// A word that is no negation would lose real indictments.
    /// </summary>
    internal const string NotProsecutedBefore = "不|不予|未|未經";

    /// <summary>
    /// What may not stand right before 起訴 for it to be an indictment (pattern Indictment): those are dismissals
    /// (不起訴, 緩起訴, 撤回起訴, 不予起訴). A word that is no dismissal would lose real indictments.
    /// </summary>
    internal const string NotIndictedBefore = "不|緩|撤回|予";

    /// <summary>What after 起訴 makes it a noun, no step (pattern Indictment): 起訴罪名, 起訴法條.</summary>
    internal const string IndictmentNouns = "罪名|法條";

    /// <summary>
    /// Words that are a conviction by themselves (pattern Conviction: CONVICTED). A word that may also stand for a request or a
    /// penalty range would read those as convictions.
    /// </summary>
    internal const string ConvictionWords = "判處|判刑|判決有罪|有罪確定";

    /// <summary>
    /// The penalties a court imposes (patterns Conviction, OrderSentence). A word that is no penalty a court imposes would read
    /// its clause as a conviction. No word may begin another: some patterns use the list in a different order than it was
    /// first written in.
    /// </summary>
    internal const string Penalties = "有期徒刑|無期徒刑|死刑|拘役|罰金";

    /// <summary>The penalties an order combines after 應執行 (pattern OrderSentence: 應執行有期徒刑二年).</summary>
    internal const string CombinedPenalties = "有期徒刑|拘役|罰金";

    /// <summary>The units of a term after its number (pattern Conviction: 判1年6月, 重判十五年).</summary>
    internal const string TermUnits = "年|個月|月";

    /// <summary>
    /// Verbs of ruling that may stand up to ten characters before 無罪 for it to be an acquittal (pattern Acquittal: 判決無罪,
    /// 宣判陳志明等三人均無罪). A verb of claiming (堅稱) added here would read a claim of innocence as ACQUITTED.
    /// </summary>
    internal const string AcquittalRulingVerbs = "判決|改判|宣判|獲判";

    /// <summary>What after 無罪 makes it no acquittal (pattern Acquittal): 無罪推定 a presumption, 無罪判決 a noun.</summary>
    internal const string NotAcquittalAfter = "推定|判決";

    /// <summary>What after 無罪 settles the acquittal by itself (pattern Acquittal: 無罪定讞, 無罪確定).</summary>
    internal const string Settled = "定讞|確定";

    /// <summary>The word of an acquittal, as the rules look for it in what an order says of the person.</summary>
    internal const string Acquitted = "無罪";

    /// <summary>Words of taking proceeds of crime (a release's order: 陳志明犯罪所得…沒收): proceeds are taken from the person convicted.</summary>
    internal const string ConfiscationWords = "沒收|追徵";

    /// <inheritdoc cref="ConfiscationWords"/>
    internal static readonly string[] Confiscation = ConfiscationWords.Split('|');

    /// <summary>What follows the person's name for the proceeds to be theirs (陳志明犯罪所得, 陳志明之犯罪所得).</summary>
    internal const string ProceedsAfterNameWords = "犯罪所得|之犯罪所得";

    /// <inheritdoc cref="ProceedsAfterNameWords"/>
    internal static readonly string[] ProceedsAfterName = ProceedsAfterNameWords.Split('|');

    /// <summary>
    /// The bracketed notes after a name saying the person is tried in another proceeding (pattern TriedElsewhere: 王大明（本院審理中）),
    /// besides 另行審理 / 另行審結: that name is no subject of this release's order.
    /// </summary>
    internal const string TriedElsewhereNotes = "審理中|另案審理";

    /// <summary>
    /// Words of an investigation: with no stage read, they make the person ALLEGED (when a sentence naming them says they are
    /// suspected, or the text never names them). A word of something other than an investigation would make bystanders ALLEGED.
    /// </summary>
    internal const string InvestigationWords = "偵辦|調查";

    /// <inheritdoc cref="InvestigationWords"/>
    internal static readonly string[] Investigation = InvestigationWords.Split('|');

    /// <summary>
    /// Words that put the person under suspicion in a sentence naming them (pattern Suspicion), so the text's investigation is
    /// theirs. A word that does not would make counterparts in the facts ALLEGED.
    /// </summary>
    internal const string SuspicionWords = "涉|嫌|偵辦|調查|逮捕|拘提|約談|羈押|收押|移送|函送|交保|搜索|查獲|到案|被告|共犯|首腦|主謀|成員|車手|起訴|通緝";

    /// <summary>What counts people after 等 and a number (pattern Group: 李小四等3人, 等9名, 等共5被告): the suspects of a list.</summary>
    internal const string GroupCounters = "人|名|嫌|被告";

    /// <summary>Words of a confiscation sentence that make the proceeds not the person's, or not taken (pattern NotTheirProceeds).</summary>
    internal const string NotTheirProceedsWords = "參與人|第三人|無罪|不予沒收|不另為沒收|已發還|駁回|聲請";

    /// <summary>Words that make a sentence of the order speak of an acquittal, the court below or an appeal, not this sentence by row number (pattern NotThisOrder).</summary>
    internal const string NotThisOrderWords = "無罪|原審|第一審|上訴|前經";

    // ---- Negation and modality (the news digest) ----

    /// <summary>
    /// In the news digest only (the stage reading no longer applies it), a negation or question right before a keyword (past
    /// a passive) makes it no event: 尚未起訴, 沒有被起訴, 是否起訴. A word that is no negation would lose real events.
    /// </summary>
    internal const string NegationWords = "未|沒有|沒|不會|無|是否";

    /// <inheritdoc cref="NegationWords"/>
    internal static readonly string[] Negations = NegationWords.Split('|');

    /// <summary>
    /// Passive markers that may stand between a negation and the keyword (沒有被起訴). The first that ends the text before the
    /// keyword is taken, so a longer word stands before the word it ends with (遭到 before 遭).
    /// </summary>
    internal const string PassiveWords = "遭到|遭|被|受";

    /// <inheritdoc cref="PassiveWords"/>
    internal static readonly string[] Passives = PassiveWords.Split('|');

    /// <summary>
    /// A prosecutor's or counsel's requests to the court for a penalty (檢察官建請法院判處, 請法院從重量刑), for the news digest: modals
    /// (<see cref="Modals"/>, with the same guards). 聲請法院, 申請法院, 提請法院, 呈請法院, 報請法院, 函請法院 and 移請法院 ask
    /// for a step or pass the case on (聲請法院羈押獲准，今依詐欺罪起訴; 提請法院，同日起訴): they are not counted. A word that is
    /// no request would lose the stage in its clause.
    /// </summary>
    internal static readonly (string Modal, string NotBefore, string NotAfter)[] CourtRequests = [("建請", "", ""), ("請法院", "聲申提呈報函移", "")];

    /// <summary>
    /// Modals, requests, plans, conditions and hearsay, in the news digest only: one ending at or before a keyword in its
    /// clause makes it no event (最重可判處, 請求從重判處, 建請法院判處, 恐遭起訴, 擬起訴). A modal does not count after a character of NotBefore or before
    /// one of NotAfter: words that only hold its characters (許可, 可疑, 恐嚇, 恐怖, 恐龍, 要求, 求償, 模擬, 若干).
    /// ponytail: a name holding 可 or 求 (陳可欣遭起訴) still reads as a modal, so no event: the safe side.
    /// </summary>
    internal static readonly (string Modal, string NotBefore, string NotAfter)[] Modals =
    [
        ("可", "許認核", "疑是"), ("恐", "", "嚇怖慌懼龍"), ("求", "要訴需追", "償援助職"), ("最重", "", ""), ("推定", "", ""),
        ("擬", "模虛", ""), ("預計", "", ""), ("即將", "", ""), ("將會", "", ""), ("若", "", "干"), ("如果", "", ""), ("一旦", "", ""), ("據傳", "", ""),
        .. CourtRequests,
    ];

    // ---- Parties and relations ----

    /// <summary>
    /// The word for the defendant. The only defendant's names carry it, as an order of a one-defendant ruling may say
    /// 被告無罪 without the name.
    /// </summary>
    internal const string Defendant = "被告";

    /// <summary>
    /// The commonest Taiwanese surnames, one character each (the news digest and the news models' reading tell names by them).
    /// ponytail: a person with a rarer surname is not told apart.
    /// </summary>
    internal const string CommonSurnames =
        "陳林黃張李王吳劉蔡楊許鄭謝郭洪曾邱廖賴周徐蘇葉莊呂江何蕭羅高潘簡朱鍾鐘游彭詹胡施沈余盧梁趙顏柯翁魏孫戴范方宋鄧杜傅侯曹薛丁卓阮馬董溫唐藍蔣石古紀姚連馮歐程湯田康姜白汪鄒尤巫黎涂龔嚴韓袁金童陸夏柳邵錢伍倪于譚駱熊任甘秦顧毛章史官萬俞雷粘饒";

    /// <summary>
    /// Relation words of more than one character after 的 / 之 and a name (pattern Relation: 王大明的哥哥, 王大明之妻): the one
    /// spoken of is the relative, aide or lawyer (the news models' reading). ponytail: the relation must follow 的 or 之. A
    /// word that is no relation would take a person's own steps away from them.
    /// </summary>
    internal const string RelationWords = "丈夫|先生|太太|老公|老婆|配偶|前妻|前夫|前女友|前男友|男友|友人|助理|秘書|司機|特助|部屬|下屬|員工|同事|合夥人|律師";

    /// <summary>
    /// What may stand before 被告 on a judgment's defendant line (pattern DefendantLine). A longer word stands before the word it
    /// begins with (上訴人即 before 上訴人).
    /// </summary>
    internal const string DefendantLinePrefixes = "上訴人即|上訴人|聲請人即|抗告人即|即";

    /// <summary>
    /// Kinds of counsel before 辯護人 on a party line (pattern OtherPartyLine). A kind left out lets the counsel's names run on
    /// as defendants (義務辯護人 林美玲).
    /// </summary>
    internal const string CounselKinds = "選任|指定|公設|義務|共同選任|共同指定";

    /// <summary>Kinds of agent before 代理人 on a party line (pattern OtherPartyLine): 告訴代理人, the complainant's.</summary>
    internal const string AgentKinds = "訴訟|法定|告訴";

    /// <summary>Party-line openings besides counsel, agents and <see cref="OtherPartyRoles"/> that end the defendants' block (pattern OtherPartyLine).</summary>
    internal const string OtherPartyHeads = "上列|輔佐人";

    /// <summary>
    /// The roles of a judgment's header that end the lines of a defendant (pattern OtherPartyLine). A role that is the
    /// defendant's own would cut their block short.
    /// </summary>
    internal const string OtherPartyRoles = "公訴人|上訴人|告訴人|參與人|被害人|具保人|聲請人|受刑人|抗告人|檢察官";

    /// <summary>
    /// What after 被告 in a release's opening goes on about the defendants already named (被告提起上訴, 被告不服), so it names
    /// none. A word that may begin a name would drop that defendant.
    /// </summary>
    internal const string OpeningGoesOnWords = "提起|不服|上訴";

    /// <inheritdoc cref="OpeningGoesOnWords"/>
    internal static readonly string[] OpeningGoesOn = OpeningGoesOnWords.Split('|');

    /// <summary>A release's opening that writes 下稱被告 names nobody new: the words are taken out before the defendants are read.</summary>
    internal const string NamedAsDefendant = "下稱被告";

    /// <summary>What after a defendant's name in a release's opening adds another defendant (被告王大明等3人, 被告王大明、李小四): then the person is not the only one.</summary>
    internal const string ListGoesOnWords = "等|、|及|與";

    /// <inheritdoc cref="ListGoesOnWords"/>
    internal static readonly string[] ListGoesOn = ListGoesOnWords.Split('|');

    /// <summary>The benches that may rule before the person's name in an order's subject (pattern OrderSubject: 經合議庭判決王大明…).</summary>
    internal const string CourtBenches = "本院|合議庭|國民法官法庭";

    /// <summary>Verbs of ruling that may stand before the person's name in an order's subject (pattern OrderSubject: 判決被告王大明…, 改判…).</summary>
    internal const string OrderRulingVerbs = "判決|改判";

    // ---- Headings ----

    /// <summary>A judgment's heading of the order, alone on its line; a release's heading for it is something else (壹、判決主文：).</summary>
    internal const string OrderHeadingWord = "主文";

    /// <summary>What may follow 主文 in a release's heading for the order (pattern OrderHeading: 主文要旨, 主文摘要說明).</summary>
    internal const string OrderHeadingQualifiers = "要旨|摘要";

    /// <summary>The word of the court pronouncing its ruling: a release's opening runs to the line that says it.</summary>
    internal const string Pronounced = "宣判";

    /// <summary>What a court's release says of its ruling after its case number (pattern CourtRelease: 本院114年度訴字第1號…宣判).</summary>
    internal const string ReleaseRuledWords = "宣判|判決";

    /// <summary>What opens a court's name in a release that opens with it (pattern CourtRelease: 臺灣士林地方法院…).</summary>
    internal const string CourtRegions = "臺灣|福建";

    /// <summary>The headings that end a judgment's order (pattern OrderEnd: 事實, 犯罪事實, 理由).</summary>
    internal const string OrderEndHeadings = "事實|理由";

    // ---- Limits ----

    /// <summary>The names of a Profile that are searched for, as in StatedAgeCheck: every sentence and clause is searched for each.</summary>
    internal const int MaxNames = 100;

    /// <summary>The defendants of a release's opening that are read: an opening names a handful.</summary>
    internal const int MaxOpeningDefendants = 100;

    /// <summary>An order with no 事實 / 理由 heading is read for this many lines; the lines looked at for a release's heading.</summary>
    internal const int MaxOrderLines = 40;

    /// <summary>The lines of a release read, under its heading and whole.</summary>
    internal const int MaxReleaseLines = 400;

    /// <summary>The lines of a release's opening, where its court's voice and its defendants are looked for.</summary>
    internal const int MaxOpeningLines = 8;
}
