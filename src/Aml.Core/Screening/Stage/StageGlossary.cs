namespace Aml.Core.Screening.Stage;

/// <summary>
/// The words and limits the stage readers work with: what changes when a new wording turns up. Every word list is plain
/// words separated by | with no regex syntax, so it can be kept as a glossary outside the code; <see cref="StagePatterns"/>
/// composes its patterns from these lists, and the rules and the form readers test with them. Where an entry says that a
/// pattern uses it, adding a word changes that pattern for every rule that uses the pattern.
/// </summary>
internal static class StageGlossary
{
    /// <summary>The version of this glossary; change it with any entry.</summary>
    internal const string Version = "stage-glossary-2";

    // ---- Saying and quoting ----

    /// <summary>
    /// Verbs of a false statement: 佯稱, 謊稱 … (what a fraudster told the victim). The pattern Pretence cuts the clause at
    /// them and skips the next clause unless it opens with a passive; they are verbs of saying too. A verb of honest
    /// saying added here (聲稱) would drop the stage that follows a person's own statement.
    /// </summary>
    internal const string PretenceVerbs = "佯稱|謊稱|誆稱|訛稱|詐稱";

    /// <summary>
    /// The verbs of saying that open reported speech ending in <see cref="ReportedSpeechEnd"/> (pattern Reported), besides
    /// a bare 稱. A word that is no verb of saying would blank out a reported stage before 云云.
    /// </summary>
    internal const string SayingVerbs = "辯稱|" + PretenceVerbs + "|聲稱|宣稱|供稱|陳稱|堅稱|表示|主張";

    /// <summary>
    /// The word that closes reported speech (辯稱遭人陷害云云): from the nearest verb of saying to it is what someone said,
    /// blanked out (pattern Reported). One word, at the pattern's end.
    /// </summary>
    internal const string ReportedSpeechEnd = "云云";

    // ---- Stages and penalties ----

    /// <summary>
    /// Words of a prosecutor's dismissal (pattern Dismissal: DISMISSED); they are read before an indictment, so 不起訴 is never
    /// INDICTED. A word that is not a dismissal (起訴) would read every indictment as DISMISSED.
    /// </summary>
    internal const string DismissalWords = "不起訴|緩起訴|" + WithdrawalWords + "|不予起訴|免予起訴";

    /// <summary>
    /// Words of a withdrawn indictment (in <see cref="DismissalWords"/>): said in a sentence that does not name the person but
    /// points at nobody else, they still replace the person's own indictment (陳志明遭起訴。檢方嗣後撤回起訴). A word that is
    /// no withdrawal would let another's dismissal replace it.
    /// </summary>
    internal const string WithdrawalWords = "撤回起訴|撤回公訴";

    /// <inheritdoc cref="WithdrawalWords"/>
    internal static readonly string[] Withdrawal = WithdrawalWords.Split('|');

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
    /// Words that, up to eight characters before 通緝, lift the wanted notice (pattern WantedNotice): 撤銷通緝, 解除通緝.
    /// A word that does not lift it would lose real wanted notices.
    /// </summary>
    internal const string WantedLifted = "撤銷|解除|撤回";

    /// <summary>
    /// Words that are a conviction by themselves (pattern Conviction: CONVICTED). A word that may also stand for a request or a
    /// penalty range would read those as convictions (modals and negations are checked apart).
    /// </summary>
    internal const string ConvictionWords = "判處|判刑|判決有罪|有罪確定";

    /// <summary>
    /// The custodial penalties and death: a clause opening 處 and one of them is a sentence (pattern Sentenced); with
    /// 罰金 they are <see cref="Penalties"/>.
    /// </summary>
    internal const string CustodialPenalties = "有期徒刑|無期徒刑|死刑|拘役";

    /// <summary>
    /// The penalties a court imposes (patterns Conviction, OrderSentence, TablePenalty, OtherPersonJudged). A word that is no
    /// penalty a court imposes would read its clause as a conviction. No word may begin another: some patterns use the list
    /// in a different order than it was first written in.
    /// </summary>
    internal const string Penalties = CustodialPenalties + "|罰金";

    /// <summary>The penalties an order combines after 應執行 (pattern OrderSentence: 應執行有期徒刑二年).</summary>
    internal const string CombinedPenalties = "有期徒刑|拘役|罰金";

    /// <summary>The units of a term after its number (patterns Conviction, OtherPersonJudged: 判1年6月, 重判十五年).</summary>
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

    /// <summary>The word of an acquittal, as the rules look for it in what an order or a table cell says of the person.</summary>
    internal const string Acquitted = "無罪";

    /// <summary>A dismissed appeal as an order writes it: with the defendant's appeal it leaves the conviction standing.</summary>
    internal const string DismissedAppeal = "上訴駁回";

    /// <summary>The word a release uses when it recalls a sentence of the court below (判處王大明有期徒刑).</summary>
    internal const string SentencedWord = "判處";

    /// <summary>Words of taking proceeds of crime (a release's order: 陳志明犯罪所得…沒收): proceeds are taken from the person convicted.</summary>
    internal const string ConfiscationWords = "沒收|追徵";

    /// <inheritdoc cref="ConfiscationWords"/>
    internal static readonly string[] Confiscation = ConfiscationWords.Split('|');

    /// <summary>What follows the person's name for the proceeds to be theirs (陳志明犯罪所得, 陳志明之犯罪所得).</summary>
    internal const string ProceedsAfterNameWords = "犯罪所得|之犯罪所得";

    /// <inheritdoc cref="ProceedsAfterNameWords"/>
    internal static readonly string[] ProceedsAfterName = ProceedsAfterNameWords.Split('|');

    /// <summary>
    /// Outcomes other than a sentence or an acquittal that end a table row (pattern TableOtherOutcome, in a short cell). A word
    /// that is part of a penalty cell would end the row before its penalty.
    /// </summary>
    internal const string TableOtherOutcomes = "免訴|不受理|駁回|撤銷";

    /// <summary>
    /// Outcomes after which a release's item is over: the sentence that follows with no subject is someone else's. A word that
    /// also stands in a conviction would lose 應執行 sentences of the person.
    /// </summary>
    internal const string ItemEndingWords = "無罪|不受理|免訴";

    /// <inheritdoc cref="ItemEndingWords"/>
    internal static readonly string[] ItemEnding = ItemEndingWords.Split('|');

    /// <summary>
    /// How a release's item goes on about the person in a sentence with no subject (王大明犯…罪，共2罪。應執行有期徒刑二年。).
    /// A word that may open someone else's sentence would give the person that sentence.
    /// </summary>
    internal const string ContinuationWords = "應執行|又犯|又共同犯";

    /// <inheritdoc cref="ContinuationWords"/>
    internal static readonly string[] ContinuationOpeners = ContinuationWords.Split('|');

    /// <summary>
    /// The bracketed notes after a name saying the person is tried in another proceeding (pattern TriedElsewhere: 王大明（本院審理中）),
    /// besides 另行審理 / 另行審結: charged, not ruled on here.
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

    /// <summary>What counts people after 等 and a number (pattern Group: 李小四等3人, 等9名, 等共5被告): a group, everyone's.</summary>
    internal const string GroupCounters = "人|名|嫌|被告";

    /// <summary>What after 起訴 names the case's papers, no step (pattern IndictmentPaper: 起訴書, 起訴範圍, 起訴意旨).</summary>
    internal const string IndictmentPapers = "書|範圍|意旨";

    /// <summary>
    /// What a report writes right before the person's name when it tells whom the court ruled on (判決被告王大明犯…, 判決王大明…):
    /// then a clause opening 處有期徒刑 is the person's sentence.
    /// </summary>
    internal const string RuledOnWords = "判決被告|判決";

    /// <inheritdoc cref="RuledOnWords"/>
    internal static readonly string[] RuledOn = RuledOnWords.Split('|');

    /// <summary>
    /// What is asked for, or what the law allows, is no outcome (pattern Asked: 檢察官請求處有期徒刑五年, 應處有期徒刑); 前經
    /// recalls what the court below ruled. A release's clause holding one of these is skipped. A word that stands in a real
    /// outcome would lose that outcome.
    /// </summary>
    internal const string AskedWords = "請求|求處|求刑|聲請|應處|得處|可處|最重|法定刑|前經";

    /// <summary>Words of a confiscation sentence that make the proceeds not the person's, or not taken (pattern NotTheirProceeds).</summary>
    internal const string NotTheirProceedsWords = "參與人|第三人|無罪|不予沒收|不另為沒收|已發還|駁回|聲請";

    /// <summary>Words that make a sentence of the order speak of an acquittal, the court below or an appeal, not this sentence by row number (pattern NotThisOrder).</summary>
    internal const string NotThisOrderWords = "無罪|原審|第一審|上訴|前經";

    // ---- Negation and modality ----

    /// <summary>
    /// A negation or question right before a keyword (past a passive) makes it no stage: 尚未起訴, 沒有被起訴, 是否起訴. A word
    /// that is no negation would lose real stages.
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
    /// A prosecutor's or counsel's requests to the court for a penalty (檢察官建請法院判處, 請法院從重量刑): modals
    /// (<see cref="Modals"/>, with the same guards), and what is asked runs into the next clause when it opens with the
    /// penalty (<see cref="AskedPenaltyVerbs"/>). 聲請法院, 申請法院, 提請法院, 呈請法院, 報請法院, 函請法院 and 移請法院 ask for a
    /// step or pass the case on (聲請法院羈押獲准，今依詐欺罪起訴; 提請法院，同日起訴): they are not counted. A word that is no
    /// request would lose the stage in its clause.
    /// </summary>
    internal static readonly (string Modal, string NotBefore, string NotAfter)[] CourtRequests = [("建請", "", ""), ("請法院", "聲申提呈報函移", "")];

    /// <summary>
    /// The penalty verbs that, opening the clause after a request to the court with no subject, are still what is asked
    /// (pattern AskedPenalty: 請法院從重量刑，判處陳志明有期徒刑八年). A clause with its own subject (法院今判處…, 陳志明獲判無罪)
    /// is the court's ruling and is read. A word that does not open an asked penalty would lose real rulings.
    /// </summary>
    internal const string AskedPenaltyVerbs = "判處|量處|處|科";

    /// <summary>What may stand before the asked penalty verb (pattern AskedPenalty: 並判處, 再處).</summary>
    internal const string AskedPenaltyLinks = "並|再";

    /// <summary>
    /// Modals, requests, plans, conditions and hearsay: one ending at or before a keyword in its clause makes it no stage
    /// (最重可判處, 請求從重判處, 建請法院判處, 恐遭起訴, 擬起訴). A modal does not count after a character of NotBefore or before
    /// one of NotAfter: words that only hold its characters (許可, 可疑, 恐嚇, 恐怖, 恐龍, 要求, 求償, 模擬, 若干).
    /// ponytail: a name holding 可 or 求 (陳可欣遭起訴) still reads as a modal, so no stage: the safe side.
    /// </summary>
    internal static readonly (string Modal, string NotBefore, string NotAfter)[] Modals =
    [
        ("可", "許認核", "疑是"), ("恐", "", "嚇怖慌懼龍"), ("求", "要訴需追", "償援助職"), ("最重", "", ""), ("推定", "", ""),
        ("擬", "模虛", ""), ("預計", "", ""), ("即將", "", ""), ("將會", "", ""), ("若", "", "干"), ("如果", "", ""), ("一旦", "", ""), ("據傳", "", ""),
        .. CourtRequests,
    ];

    // ---- Parties and relations ----

    /// <summary>
    /// The word for the defendant. The only defendant's names carry it, as an order of a one-defendant judgment may say
    /// 被告無罪 without the name.
    /// </summary>
    internal const string Defendant = "被告";

    /// <summary>
    /// The commonest Taiwanese surnames, one character each: a clause that opens with a name of one of them, other than the
    /// person's own surname, is another person's. ponytail: enough to tell 李四遭起訴 from 一審遭判處; a person with a
    /// rarer surname still reads as the Profile person's clause.
    /// </summary>
    internal const string CommonSurnames =
        "陳林黃張李王吳劉蔡楊許鄭謝郭洪曾邱廖賴周徐蘇葉莊呂江何蕭羅高潘簡朱鍾鐘游彭詹胡施沈余盧梁趙顏柯翁魏孫戴范方宋鄧杜傅侯曹薛丁卓阮馬董溫唐藍蔣石古紀姚連馮歐程湯田康姜白汪鄒尤巫黎涂龔嚴韓袁金童陸夏柳邵錢伍倪于譚駱熊任甘秦顧毛章史官萬俞雷粘饒";

    /// <summary>
    /// Relation words of more than one character after 的 / 之 and the person's name (pattern Relation: 王大明的哥哥, 王大明之妻):
    /// the one spoken of is the relative, aide or lawyer. ponytail: the relation must follow 的 or 之. A word that is no
    /// relation would take the person's own steps away from them.
    /// </summary>
    internal const string RelationWords = "丈夫|先生|太太|老公|老婆|配偶|前妻|前夫|前女友|前男友|男友|友人|助理|秘書|司機|特助|部屬|下屬|員工|同事|合夥人|律師";

    /// <summary>
    /// Roles before a name that make it a bystander's (pattern Bystander: 被害人王大明報案, 證人王大明指出): the sentence's steps
    /// are someone else's. A role that may be the accused's would lose their stage.
    /// </summary>
    internal const string BystanderRoles = "被害人|受害人|證人|告訴人|告發人|檢舉人";

    /// <summary>
    /// Words that point at someone else in a sentence or clause (同案, 另一, 其他 …): a subjectless step there is not the
    /// person's, and 定讞 there settles someone else. A word that does not point at someone else would lose the person's steps.
    /// </summary>
    internal const string OtherPartyWords = "同案|另一|另名|其他|其餘|共犯|共同被告";

    /// <inheritdoc cref="OtherPartyWords"/>
    internal static readonly string[] OtherParty = OtherPartyWords.Split('|');

    /// <summary>
    /// The whole case: outside a judgment, a report's sentence saying it, and pointing at nobody else, gives its 提起公訴 to
    /// everyone the report names (全案移送地檢署偵辦，經檢察官偵結後提起公訴). A word that is no whole would give one
    /// person's indictment to all.
    /// </summary>
    internal const string WholeCase = "全案";

    /// <summary>Another case: 另案定讞 settles something else.</summary>
    internal const string AnotherCase = "另案";

    /// <summary>
    /// Words that make a sole defendant's 被告 sentence about another case, an earlier one, or the law in general (pattern
    /// NotThisCase), not this case's step.
    /// </summary>
    internal const string OtherCaseWords = "另案|該案|他案|前案|前因|前科|規定|條第";

    /// <summary>
    /// Connectives that may open a clause before another person's name (pattern OtherPersonOpening: 而李四遭起訴). A longer word
    /// stands before the word it begins with (另外 before 另).
    /// </summary>
    internal const string Connectives = "而|但|則|另外|另|隨後|其後|後來|至於";

    /// <summary>What follows 姓 for an unnamed person (pattern OtherPersonOpening: 林姓男子被). A longer word stands first (男子 before 男).</summary>
    internal const string PersonNouns = "男子|女子|男|女|嫌犯|被告";

    /// <summary>Adverbs that may stand between another person's name and the passive (pattern OtherPersonOpening: 李四也遭).</summary>
    internal const string ClauseAdverbs = "也|亦|則|已|均";

    /// <summary>
    /// The passive or 獲 right after another person's name that makes the clause theirs (pattern OtherPersonOpening: 李四遭,
    /// 李四獲). A longer word stands first (遭到 before 遭).
    /// </summary>
    internal const string OtherPersonPassives = "遭到|遭|被|獲";

    /// <summary>
    /// The adverbs that must stand between another person's name and 為 for the clause to be theirs (pattern
    /// OtherPersonOpening: 王大同則為緩起訴處分). Without one, 為 goes on a word (高院認為, 金額為, 方式為). A word that may
    /// end such a word would take the person's clause away from them.
    /// </summary>
    internal const string BeforeDispositionAdverbs = "則|另|均|亦";

    /// <summary>
    /// A clause that opens with one of these after a pretence is the fraudster's own step (pattern OpensPassive: 陳志明佯稱可獲利，遭起訴).
    /// </summary>
    internal const string PassiveOpeners = "遭|被|經";

    /// <summary>What may stand between 對 and a name and the dismissal or indictment (pattern OtherPersonObject: 對李小四為不起訴處分).</summary>
    internal const string DispositionVerbs = "為|作成|予以";

    /// <summary>What before 起訴 makes it a dismissal of another person in pattern OtherPersonObject (對李小四不起訴, 緩起訴).</summary>
    internal const string UnindictedPrefixes = "不|緩";

    /// <summary>What after a wanted person's name tells they are another person (pattern OtherPersonJudged: 警方通緝的李小四落網).</summary>
    internal const string CaughtWords = "落網|到案|歸案";

    /// <summary>
    /// Words that open a bracketed aside for the whole list before it (pattern GroupAside: 李小四、陳志明（均遭起訴）), besides
    /// 兩人, 三人 …: such an aside is not another person's alone.
    /// </summary>
    internal const string GroupAsideOpeners = "均|皆|同|等";

    /// <summary>
    /// Words that may open a bracketed aside before the names it is about (按李小四、王大同…部分，業經另案起訴). A longer word
    /// stands first. A word that may begin a name would hide that name.
    /// </summary>
    internal const string AsideNameOpenerWords = "按|即|其中";

    /// <summary>
    /// The characters that mask a name, besides ideographs (李○四, 李O四, 李Ｘ四): a bracket after such a name is another
    /// person's aside. One character each, not a word list. A character that stands in ordinary words would read them as names.
    /// </summary>
    internal const string NameMasks = "○OXＯＸ";

    /// <summary>
    /// Words that open a clause with no subject of its own (pattern OpensWithoutSubject: 王大同經合法傳喚未到，由本院通緝中):
    /// a wanted notice there goes on about the one a clause before it named, also for someone a judgment only names. A word
    /// that may open a subject (a surname) would give them another person's notice.
    /// </summary>
    internal const string SubjectlessOpeners = "經|由|遭|被|並|且|而|另|現|仍";

    /// <inheritdoc cref="AsideNameOpenerWords"/>
    internal static readonly string[] AsideNameOpeners = AsideNameOpenerWords.Split('|');

    /// <summary>
    /// What after a name opening an aside makes the aside that person's (（李小四、王大同…）, （李小四等人…）): it is blanked out
    /// when it holds none of the Profile's names. Only a list goes on so: a word that may go on a noun (金額經, 白手套所涉)
    /// would blank out asides about the Profile person that open with a surname character.
    /// </summary>
    internal const string AfterAsideNameWords = "、|等";

    /// <inheritdoc cref="AfterAsideNameWords"/>
    internal static readonly string[] AfterAsideName = AfterAsideNameWords.Split('|');

    /// <summary>Words that make one sentence each one's (pattern Sentenced: 各處有期徒刑, 均處有期徒刑).</summary>
    internal const string EachOf = "各|均";

    /// <summary>
    /// What may stand before 被告 on a judgment's defendant line (pattern DefendantLine). A longer word stands before the word it
    /// begins with (上訴人即 before 上訴人).
    /// </summary>
    internal const string DefendantLinePrefixes = "上訴人即|上訴人|聲請人即|抗告人即|即";

    /// <summary>
    /// A defendant line that opens so is an appellant defendant's (即被告 opens the line under 上訴人): with 上訴駁回 their conviction
    /// stands.
    /// </summary>
    internal const string AppellantDefendantWords = "上訴人即被告|即被告";

    /// <inheritdoc cref="AppellantDefendantWords"/>
    internal static readonly string[] AppellantDefendant = AppellantDefendantWords.Split('|');

    /// <summary>Kinds of counsel before 辯護人 on a party line (pattern OtherPartyLine).</summary>
    internal const string CounselKinds = "選任|指定|公設";

    /// <summary>Kinds of agent before 代理人 on a party line (pattern OtherPartyLine).</summary>
    internal const string AgentKinds = "訴訟|法定";

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

    /// <summary>
    /// What an order says right after a defendant's name (pattern OrderOpensWithName: 李小四犯…, 李小四無罪, 李小四處…). A word that
    /// may continue a name would cut it short.
    /// </summary>
    internal const string OrderPredicates = "共同|幫助|犯|無罪|處|均|應|部分|被訴";

    /// <summary>The same, and 涉, after the subject of a ruling's clause (pattern OtherSubject: 被告李小四涉…).</summary>
    internal const string OtherSubjectPredicates = OrderPredicates + "|涉";

    /// <summary>What may open the subject of a ruling's clause (pattern OtherSubject: 被告李小四犯…, 判決李小四無罪, 認李小四處…).</summary>
    internal const string OtherSubjectOpeners = "被告|判決|改判|認";

    // ---- Headings and tables ----

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

    /// <summary>
    /// Headings of a table column whose cells are the term alone (pattern TableTermHeading, besides 有期徒刑): 宣告刑, 刑度. A heading
    /// of a column of what was asked for would read those terms as sentences.
    /// </summary>
    internal const string TermHeadings = "宣告刑|刑度";

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

    /// <summary>The lines after a name cell read as that person's table row.</summary>
    internal const int MaxRowLines = 12;
}
