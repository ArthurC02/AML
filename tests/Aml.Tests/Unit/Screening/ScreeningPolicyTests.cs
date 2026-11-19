using System.Text.Json;
using Aml.Core.Cases;
using Aml.Core.Explanation;
using Aml.Core.Identity;
using Aml.Core.Screening;
using Aml.Core.Shared;
using Aml.Core.Subjects;
using Aml.Infra.Txt;
using Aml.Services;

namespace Aml.Tests.Unit.Screening;

/// <summary>Flags and categories, legal stage and ② relevance from news, Article 22 ①, review priority, risk scoring, former PEP and RCA scope.</summary>
public class ScreeningPolicyTests
{
    private static ScreenedSubject Subject(params string[] nationality) =>
        new(SubjectRole.Customer, null, ["陳志明"], Gender.Male, new PartialDate(1980, 5, 20), nationality.Length == 0 ? ["TW"] : nationality,
            null, null, null, null, [], []);

    private static SourceProfile Profile(string category, string? sub = null, OfficialListEntry? list = null,
        IReadOnlyList<ProfileRole>? roles = null, params string[] citizenship) =>
        new("DJ:1", Gender.Male, ["陳志明"], [], citizenship.Length == 0 ? ["TW"] : citizenship, [], [new SourceCategory(category, sub)], [],
            roles ?? [], [], list, [], []);

    private static HitResult Analyze(SourceProfile p, ListFlags? flags = null, bool isRca = false, ScreenedSubject? subject = null, DateOnly? asOf = null) =>
        CaseAnalyzer.Analyze(new CaseInput(subject ?? Subject(), [new CandidateRow("陳志明", "1", null, isRca, flags)],
            new Dictionary<string, SourceProfile> { ["1"] = p }, new Dictionary<string, NewsItem>(), []), asOf: asOf).Hits[0];

    private static ListFlags Flags(FlagValue pep = FlagValue.Missing, FlagValue blacklist = FlagValue.Missing, FlagValue adverse = FlagValue.Missing) =>
        new(pep, blacklist, adverse);

    private static IdentityResult Identity(IdentityDecision decision = IdentityDecision.Review) =>
        new(new Dictionary<EvidenceGroup, Evidence>(), null, 6, decision, []);

    private static SourceCondition Condition(ListType type, ConditionStatus status = ConditionStatus.Hit, LegalStage stage = LegalStage.Unknown,
        AmlRelevance? relevance = null, ConditionBasis basis = ConditionBasis.CategoryMapping, params string[] codes) =>
        new(type, status, basis, [], stage, relevance, SourceConditions.PolicyVersion, codes);

    // a mapped category whose flag is blank keeps its condition; 黑名單 = NO never clears a Sanction category.

    [Fact]
    public void sanction_category_with_pep_no_and_other_flags_blank_keeps_the_sanction_condition()
    {
        var hit = Analyze(Profile("Sanctions Lists", list: new OfficialListEntry("OFAC", "SDN List", "Active", null)), Flags(pep: FlagValue.No));
        var sanction = hit.Conditions.Single(c => c.Type == ListType.Sanction);
        Assert.Equal((ConditionStatus.Hit, ConditionBasis.CategoryMapping), (sanction.Status, sanction.Basis));
        Assert.Equal((ScreeningAssessment.Review, ReviewPriority.High), (hit.PairResult, hit.ReviewPriority));
    }

    [Fact]
    public void sanction_category_with_blacklist_no_is_unknown_with_a_conflict()
    {
        var hit = Analyze(Profile("Sanctions Lists"), Flags(FlagValue.No, FlagValue.No, FlagValue.No));
        var sanction = hit.Conditions.Single(c => c.Type == ListType.Sanction);
        Assert.Equal(ConditionStatus.Unknown, sanction.Status);
        Assert.Contains(SourceConditions.FlagCategoryConflict, sanction.ReasonCodes);
        Assert.Equal((ScreeningAssessment.Review, ReviewPriority.High), (hit.PairResult, hit.ReviewPriority));
    }

    [Fact]
    public void rca_category_with_is_rca_blank_and_other_flags_set_keeps_the_rca_condition()
    {
        var hit = Analyze(Profile("Relative or Close Associate (RCA)", "Spouse"), Flags(FlagValue.No, FlagValue.No, FlagValue.No));
        var rca = hit.Conditions.Single(c => c.Type == ListType.Rca);
        Assert.Equal((ConditionStatus.Hit, ConditionBasis.CategoryMapping), (rca.Status, rca.Basis));
        Assert.Equal(ScreeningAssessment.Review, hit.PairResult);
    }

    // Decision table: PEP, 黑名單 and Is RCA? flags × category × an official-list entry naming a Sanction (or none).
    // A 黑名單 YES takes its type from the official-list entry before the category; with neither, the type is undecided.
    [Theory]
    [InlineData(FlagValue.Missing, FlagValue.Yes, false, "Sanctions Lists", null, "SDN List", "Sanction:Hit")]
    [InlineData(FlagValue.Missing, FlagValue.Yes, false, "Special Interest Person (SIP)", "Fraud", "UN Sanctions List", "Sanction:Hit,AdverseMedia:Hit")]
    [InlineData(FlagValue.Missing, FlagValue.Yes, false, "Politically Exposed Person", null, "UN Sanctions List", "Sanction:Hit,Pep:Hit")]
    [InlineData(FlagValue.Missing, FlagValue.Yes, false, "Relative or Close Associate (RCA)", "Spouse", "EU Sanctions Regime", "Sanction:Hit,Rca:Hit")]
    [InlineData(FlagValue.Missing, FlagValue.Yes, false, "Politically Exposed Person", null, null, "Unknown:Unknown,Pep:Hit")]
    [InlineData(FlagValue.Missing, FlagValue.No, false, "Sanctions Lists", null, null, "Sanction:Unknown")]
    [InlineData(FlagValue.Missing, FlagValue.No, false, "Special Interest Person (SIP)", "Fraud", "UN Sanctions List", "Sanction:NotHit,AdverseMedia:Hit")]
    [InlineData(FlagValue.Missing, FlagValue.No, false, "Politically Exposed Person", null, null, "Unknown:NotHit,Pep:Hit")]
    [InlineData(FlagValue.Missing, FlagValue.Unparseable, false, "Sanctions Lists", null, "SDN List", "Sanction:Unknown")]
    [InlineData(FlagValue.Missing, FlagValue.Unparseable, false, "Relative or Close Associate (RCA)", "Spouse", null, "Unknown:Unknown,Rca:Hit")]
    [InlineData(FlagValue.Yes, FlagValue.Missing, false, "Politically Exposed Person", null, null, "Pep:Hit")]
    [InlineData(FlagValue.Yes, FlagValue.Missing, false, "Special Interest Person (SIP)", "Fraud", null, "Pep:Hit,AdverseMedia:Hit")]
    [InlineData(FlagValue.Missing, FlagValue.Missing, true, "Relative or Close Associate (RCA)", "Spouse", null, "Rca:Hit")]
    [InlineData(FlagValue.Missing, FlagValue.Missing, true, "Politically Exposed Person", null, null, "Pep:Hit,Rca:Hit")]
    [InlineData(FlagValue.No, FlagValue.Yes, true, "Sanctions Lists", null, "SDN List", "Pep:NotHit,Sanction:Hit,Rca:Hit")]
    public void source_conditions_decision_table(FlagValue pep, FlagValue blacklist, bool isRca, string category, string? sub, string? list, string expected)
    {
        var entry = list is null ? null : new OfficialListEntry("UN", list, "Active", null);
        var conditions = SourceConditions.Determine(Profile(category, sub, entry), isRca, null, Flags(pep, blacklist));
        Assert.Equal(expected, string.Join(",", conditions.Select(c => $"{c.Type}:{c.Status}")));
    }

    [Fact]
    public void a_flag_with_a_value_still_wins_over_its_category()
    {
        var pep = Analyze(Profile("Politically Exposed Person"), Flags(pep: FlagValue.No)).Conditions.Single(c => c.Type == ListType.Pep);
        Assert.Equal(ConditionStatus.NotHit, pep.Status);
        Assert.Contains(SourceConditions.FlagCategoryConflict, pep.ReasonCodes);
    }

    // legal stage.

    [Theory]
    [InlineData("檢察官對陳志明為不起訴處分。", LegalStage.Dismissed)]
    [InlineData("陳志明獲緩起訴處分。", LegalStage.Dismissed)]
    [InlineData("陳志明遭起訴後，檢方撤回起訴。", LegalStage.Dismissed)]                // the last stage wins
    [InlineData("陳志明涉嫌詐欺，最重可判處十年有期徒刑。", LegalStage.Unknown)]       // possibility, not a judgment
    [InlineData("檢方請求從重判處陳志明。", LegalStage.Unknown)]                      // request, not a judgment
    [InlineData("陳志明堅稱無罪。", LegalStage.Unknown)]                              // claim, not an acquittal
    [InlineData("陳志明涉嫌詐欺。同案其他被告已遭起訴。", LegalStage.Unknown)]          // co-defendant's indictment
    [InlineData("陳志明一審判處有期徒刑二年，二審改判無罪。", LegalStage.Acquitted)]   // the last stage wins
    [InlineData("法院判決陳志明無罪定讞。", LegalStage.Acquitted)]
    [InlineData("陳志明遭起訴，一審判處有期徒刑三年。", LegalStage.Convicted)]
    [InlineData("檢方已起訴。", LegalStage.Indicted)]                                 // subjectless: the person's
    [InlineData("陳志明因恐嚇取財罪被判處有期徒刑一年。", LegalStage.Convicted)]       // 恐嚇 is an offence, not 恐 the modal
    [InlineData("陳志明涉及恐怖活動遭判處有期徒刑五年。", LegalStage.Convicted)]
    [InlineData("陳志明要求和解未果，遭判處有期徒刑一年。", LegalStage.Convicted)]     // 求 in another clause
    [InlineData("陳志明因可疑交易遭檢方起訴。", LegalStage.Indicted)]                  // 可疑 is no modal
    [InlineData("陳志明要求和解遭判處有期徒刑。", LegalStage.Convicted)]               // nor 要求, 訴求 or 許可 in the same clause
    [InlineData("陳志明因訴求勞資爭議被起訴。", LegalStage.Indicted)]
    [InlineData("陳志明未經許可吸金遭起訴。", LegalStage.Indicted)]
    [InlineData("檢方請求法院依法從重判處陳志明。", LegalStage.Unknown)]               // a request, however far before
    [InlineData("陳志明尚未起訴。", LegalStage.Unknown)]                              // negations and questions are no stage
    [InlineData("陳志明並未被起訴。", LegalStage.Unknown)]
    [InlineData("陳志明沒有遭到起訴。", LegalStage.Unknown)]
    [InlineData("檢方尚在評估是否起訴陳志明。", LegalStage.Unknown)]
    [InlineData("陳志明未遭判刑。", LegalStage.Unknown)]
    [InlineData("檢方對陳志明不予起訴。", LegalStage.Dismissed)]
    [InlineData("陳志明不可能被起訴。", LegalStage.Unknown)]
    [InlineData("檢方擬起訴陳志明。", LegalStage.Unknown)]                             // a plan, a condition, hearsay
    [InlineData("陳志明若遭起訴將辭職。", LegalStage.Unknown)]
    [InlineData("據傳陳志明已遭起訴。", LegalStage.Unknown)]
    [InlineData("陳志明稱「我早就被判刑了」。", LegalStage.Unknown)]                   // quoted speech is a claim
    [InlineData("陳志明說：「李四已被判刑」。", LegalStage.Unknown)]
    [InlineData("陳志明與李四涉詐欺，李四遭起訴。", LegalStage.Unknown)]                // another person's clause
    [InlineData("陳志明涉詐欺；李四遭起訴。", LegalStage.Unknown)]
    [InlineData("陳志明涉詐欺，林姓男子被判處有期徒刑一年。", LegalStage.Unknown)]
    [InlineData("李小四涉嫌詐騙，檢方已起訴李小四。被害人陳志明表示遭騙。", LegalStage.Unknown)] // another person is the object
    [InlineData("李小四、王小五涉嫌詐騙。檢方已起訴李小四、王小五。陳志明為本案被害人。", LegalStage.Unknown)]
    [InlineData("李小四涉嫌詐騙。檢察官對李小四為不起訴處分。陳志明為本案告訴人。", LegalStage.Unknown)]
    [InlineData("李小四涉嫌詐騙。檢察官對李小四緩起訴。陳志明為本案告訴人。", LegalStage.Unknown)]
    [InlineData("李小四涉嫌詐騙，檢方已起訴李小四等七人。陳志明亦在其中。", LegalStage.Indicted)] // a group stays collective
    [InlineData("李小四涉嫌詐騙，檢方已起訴李小四及陳志明。", LegalStage.Indicted)]       // the clause names the person
    [InlineData("李小四涉嫌詐騙，檢方已起訴李小四、陳男。陳志明到案說明。", LegalStage.Indicted)] // the person's surname is in the clause
    [InlineData("陳大明涉嫌詐騙，檢方已起訴陳大明。陳志明到案說明。", LegalStage.Indicted)] // the person's own surname: not told apart
    [InlineData("檢方已起訴李小四。陳志明到案說明。", LegalStage.Indicted)]             // written once: not taken for a name
    [InlineData("陳志明涉背信。檢方已起訴高層。", LegalStage.Indicted)]                 // 高層, 官員, 金額 are no names
    [InlineData("陳志明涉貪。檢方已起訴官員。", LegalStage.Indicted)]
    [InlineData("李四涉嫌詐騙，檢方已起訴李四。陳志明為被害人。", LegalStage.Indicted)]    // a two-character name is not told apart
    [InlineData("陳志明涉詐欺，陳男遭起訴。", LegalStage.Indicted)]                    // the person's own surname: the person
    [InlineData("陳志明涉詐欺，隨後李四也遭起訴。", LegalStage.Unknown)]
    [InlineData("陳志明涉詐欺，施用毒品遭起訴。", LegalStage.Indicted)]                // a clause opening with a word, not a name
    [InlineData("陳志明涉詐欺，高額詐貸遭起訴。", LegalStage.Indicted)]
    [InlineData("陳志明涉貪，任內涉貪遭起訴。", LegalStage.Indicted)]
    [InlineData("陳志明涉詐欺，他曾涉案遭起訴。", LegalStage.Indicted)]
    [InlineData("陳志明遭起訴，地方法院一審被判處有期徒刑一年。", LegalStage.Convicted)]
    [InlineData("綽號「阿明」的陳志明遭判處有期徒刑一年。", LegalStage.Convicted)]   // a quote does not hide the name around it
    [InlineData("陳志明遭起訴，一審遭判處有期徒刑一年。", LegalStage.Convicted)]          // 一審 is no person
    [InlineData("恐龍法官判處陳志明有期徒刑一年。", LegalStage.Convicted)]              // 恐龍 and 可是 are no modals
    [InlineData("陳志明可是仍遭起訴。", LegalStage.Indicted)]
    [InlineData("陳志明恐遭起訴。", LegalStage.Unknown)]                              // still a possibility…
    [InlineData("陳志明可能遭到起訴。", LegalStage.Unknown)]
    [InlineData("檢方偵辦後，陳志明可能遭到起訴。", LegalStage.Alleged)]               // …so only the investigation counts
    // A judgment: 提起公訴 is an indictment, and the order (主文 up to 事實 / 理由) says the outcome.
    [InlineData("陳志明因詐欺案件，經檢察官提起公訴。", LegalStage.Indicted)]
    [InlineData("被告 陳志明\n上列被告因詐欺案件，經檢察官提起公訴，本院判決如下：\n主 文\n陳志明犯詐欺取財罪，處有期徒刑一年。\n理 由\n被告陳志明坦承犯行。", LegalStage.Convicted)]
    [InlineData("被告 陳志明\n主　文\n陳志明無罪。\n理　由\n不能證明被告犯罪。", LegalStage.Acquitted)]
    [InlineData("主文\n陳志明犯詐欺取財罪，處有期徒刑一年。\n李小四無罪。\n理由\n略。", LegalStage.Convicted)]   // the co-defendant's acquittal is not theirs
    [InlineData("主文\n李小四犯詐欺取財罪，處有期徒刑一年。\n陳志明無罪。\n理由\n略。", LegalStage.Acquitted)]   // nor the co-defendant's sentence
    [InlineData("主文\n李小四處有期徒刑一年，陳志明無罪。\n理由\n略。", LegalStage.Acquitted)]                 // one sentence, two defendants
    [InlineData("主文\n陳志明處有期徒刑一年，李小四無罪。\n理由\n略。", LegalStage.Convicted)]
    [InlineData("主文\n陳志明、李小四均無罪。\n理由\n略。", LegalStage.Acquitted)]
    [InlineData("主文\n陳志明犯詐欺取財罪，處有期徒刑一年。其餘被訴部分無罪。\n理由\n略。", LegalStage.Convicted)] // a sentence in any part is a conviction
    [InlineData("主文\n陳志明犯詐欺取財罪，處拘役五十日，如易科罰金，以新臺幣一千元折算一日。\n事實及理由\n略。", LegalStage.Convicted)]
    [InlineData("陳志明經檢察官提起公訴。\n主文\n上訴駁回。\n理由\n略。", LegalStage.Indicted)]                // the order says nothing of them: the keywords stand
    [InlineData("被告 李小四\n上列被告因詐欺案件，經檢察官提起公訴，本院判決如下：\n主文\n李小四犯詐欺取財罪，處有期徒刑一年。\n理由\n證人陳志明到庭作證。", LegalStage.Unknown)] // a witness: not a defendant, not in the order
    [InlineData("被告 陳志明\n上列被告因詐欺案件，經檢察官提起公訴，本院判決如下：\n主文\n本件公訴不受理。\n理由\n略。", LegalStage.Indicted)] // a listed defendant stands indicted
    [InlineData("上訴人即被告 陳志明\n主文\n上訴駁回。\n理由\n略。", LegalStage.Convicted)]                   // a defendant appeals a conviction only
    [InlineData("上訴人 臺灣臺北地方檢察署檢察官\n被告 陳志明\n主文\n上訴駁回。\n理由\n略。", LegalStage.Indicted)] // the prosecutor's appeal: the outcome below is not told
    [InlineData("被告 陳志明\n主文\n陳志明犯如附表一所示之罪，各處如附表一所示之刑。應執行有期徒刑三年。\n理由\n略。", LegalStage.Convicted)]
    [InlineData("被告 李小四\n被告 陳志明\n主文\n一、李小四犯詐欺取財罪，處有期徒刑一年。\n二、陳志明被訴部分無罪。\n理由\n略。", LegalStage.Acquitted)] // numbered items
    [InlineData("被告 李小四\n陳志明\n選任辯護人 王律師\n上列被告因詐欺案件，經檢察官提起公訴，本院判決如下：\n主文\n本件公訴不受理。\n理由\n略。", LegalStage.Indicted)] // listed on the line after 被告
    [InlineData("被告 李小四\n選任辯護人 陳志明律師\n上列被告因詐欺案件，經檢察官起訴，本院調查後判決如下：\n主文\n李小四無罪。\n理由\n略。", LegalStage.Unknown)]   // the defence lawyer is no defendant
    // A judgment names many people who are not its defendants: its subjectless 起訴 and its papers are not theirs.
    [InlineData("被告 李小四\n上列被告因詐欺案件，經檢察官起訴，本院判決如下：\n主文\n李小四犯詐欺取財罪，處有期徒刑一年。\n理由\n李小四將款項匯入陳志明之帳戶。\n本件起訴程序合法。", LegalStage.Unknown)]
    [InlineData("被告 李小四\n主文\n李小四犯詐欺取財罪，處有期徒刑一年。\n理由\n陳志明所涉部分非本案起訴範圍。\n起訴書誤載陳志明之帳號，應予更正。", LegalStage.Unknown)]
    [InlineData("被告 李小四\n主文\n李小四犯詐欺取財罪，處有期徒刑一年。\n理由\n共犯陳志明業經檢察官另案起訴。", LegalStage.Indicted)]       // said of them by name
    [InlineData("被告 陳志明\n主文\n本件公訴不受理。\n理由\n起訴書記載之事實，業經判決確定。", LegalStage.Indicted)]                    // a listed defendant keeps the papers
    [InlineData("主文\n李小四對陳志明犯詐欺取財罪，處有期徒刑一年。\n理由\n略。", LegalStage.Unknown)]          // the victim of the offence
    [InlineData("主文\n李小四偽造陳志明之署押，處有期徒刑一年。\n理由\n略。", LegalStage.Unknown)]
    [InlineData("被告 陳志明\n主文\n原判決關於陳志明處有期徒刑部分撤銷。\n陳志明無罪。\n理由\n略。", LegalStage.Acquitted)] // the revoked sentence is not the outcome
    [InlineData("主文\n陳志明無罪，李四處有期徒刑一年。\n理由\n略。", LegalStage.Acquitted)]                   // a two-character co-defendant
    [InlineData("主文\n陳志明處有期徒刑一年，李小四被訴部分無罪。\n理由\n略。", LegalStage.Convicted)]
    [InlineData("主文\n李小四、陳志明均無罪。\n理由\n略。", LegalStage.Acquitted)]                           // later in the list of names
    [InlineData("主文\n陳志明無罪。\n一、犯罪事實\n檢察官求處陳志明有期徒刑二年。", LegalStage.Acquitted)]      // a numbered heading ends the order
    [InlineData("檢察官對陳志明不提起公訴。", LegalStage.Unknown)]                                         // 不提起公訴 is no indictment
    [InlineData("陳志明到案說明，檢察官另對李小四提起公訴。李小四否認犯行。", LegalStage.Unknown)]             // another person's
    [InlineData("本案經檢察官提起公訴。證人陳志明到庭。", LegalStage.Unknown)]                               // 提起公訴 counts only with the person's name
    [InlineData("主文\n陳志明無罪。\n理由\n檢察官認被告應判處有期徒刑，尚無可採。", LegalStage.Acquitted)]      // the reasons do not undo the order
    // The ways a report words a judgment: 判 with the sentence itself, 獲判, 無罪定讞.
    [InlineData("法院今判陳志明有期徒刑三年。", LegalStage.Convicted)]
    [InlineData("陳志明一審遭判無期徒刑。", LegalStage.Convicted)]
    [InlineData("陳志明被依詐欺罪判1年6月。", LegalStage.Convicted)]
    [InlineData("陳志明遭重判十五年。", LegalStage.Convicted)]
    [InlineData("二審撤銷陳志明無罪判決，改判有期徒刑二年。", LegalStage.Convicted)]
    [InlineData("陳志明遭判賠三百萬元。", LegalStage.Unknown)]                         // a civil award is no conviction
    [InlineData("陳志明最重可判七年。", LegalStage.Unknown)]
    [InlineData("陳志明獲判無罪。", LegalStage.Acquitted)]
    [InlineData("法院判陳志明無罪。", LegalStage.Acquitted)]
    [InlineData("陳志明無罪定讞。", LegalStage.Acquitted)]
    [InlineData("陳志明無罪確定。", LegalStage.Acquitted)]
    [InlineData("陳志明強調自己無罪。", LegalStage.Unknown)]
    [InlineData("陳志明遭檢方聲請簡易判決處刑。", LegalStage.Indicted)]                 // the summary form of an indictment
    [InlineData("陳志明棄保潛逃，檢方發布通緝。", LegalStage.Wanted)]
    [InlineData("陳志明遭通緝多年後落網，檢方依詐欺罪起訴。", LegalStage.Indicted)]       // the last stage wins
    [InlineData("陳志明並未遭通緝。", LegalStage.Unknown)]
    // Someone close to the person is not the person.
    [InlineData("陳志明的哥哥遭起訴。", LegalStage.Unknown)]
    [InlineData("陳志明的丈夫李小四涉嫌詐欺，遭檢方起訴。", LegalStage.Unknown)]         // the next clause is still the husband's
    [InlineData("陳志明的子公司遭搜索，陳志明遭起訴。", LegalStage.Indicted)]            // 子公司 is no son
    [InlineData("陳志明之妻林美玲遭判處有期徒刑一年。", LegalStage.Unknown)]
    [InlineData("陳志明的助理被依貪污罪起訴，陳志明未遭起訴。", LegalStage.Unknown)]
    [InlineData("陳志明與其妻均遭起訴。", LegalStage.Indicted)]
    [InlineData("陳志明檢舉李小四詐欺，檢方起訴李小四。李小四否認犯行。", LegalStage.Unknown)]
    // 判 inside another word, and what a judgment says of someone else in the person's sentence.
    [InlineData("陳志明審判期間長達三年。", LegalStage.Unknown)]
    [InlineData("陳志明誤判三年內景氣。", LegalStage.Unknown)]
    [InlineData("法官判陳志明須賠償十萬元。", LegalStage.Unknown)]
    [InlineData("陳志明去年遭起訴，今年3月一審判他5年6月。", LegalStage.Convicted)]
    [InlineData("陳志明被判三個月拘役。", LegalStage.Convicted)]
    [InlineData("陳志明獲判無罪，法院另判處李小四有期徒刑一年。", LegalStage.Acquitted)]
    [InlineData("法院判李小四有期徒刑一年，陳志明則獲判無罪。", LegalStage.Acquitted)]
    [InlineData("陳志明遭判處有期徒刑一年，法院另判李小四無罪。", LegalStage.Convicted)]
    [InlineData("陳志明一審無罪，二審逆轉判刑。", LegalStage.Convicted)]
    [InlineData("陳志明一審判刑，二審逆轉無罪。", LegalStage.Acquitted)]
    [InlineData("陳志明獲不起訴處分，全案定讞。", LegalStage.Dismissed)]              // 定讞 alone settles what came before
    [InlineData("法院判決陳志明無罪，全案定讞。", LegalStage.Acquitted)]
    [InlineData("陳志明遭起訴，三審定讞。", LegalStage.Convicted)]
    [InlineData("警方通緝的李小四落網，陳志明到案說明。李小四否認犯行。", LegalStage.Unknown)]
    [InlineData("通緝犯陳志明落網。", LegalStage.Wanted)]
    [InlineData("法院撤銷陳志明通緝。", LegalStage.Unknown)]
    [InlineData("陳志明遭起訴後棄保潛逃，法院發布通緝。", LegalStage.Indicted)]          // wanted is no step past the indictment
    // Found in review: the report wordings must not reach past what they name.
    [InlineData("法院判處陳志明有期徒刑二年，另判李四無罪。", LegalStage.Convicted)]
    [InlineData("一審判陳志明罰金、李四無罪。", LegalStage.Convicted)]
    [InlineData("陳志明遭起訴，判決前三年的資金流向待查。", LegalStage.Indicted)]
    [InlineData("陳志明遭起訴，判決前十三年的資金流向待查。", LegalStage.Indicted)]
    [InlineData("陳志明遭起訴，法官判斷5年內不會再犯。", LegalStage.Indicted)]
    [InlineData("陳志明遭起訴，宣判前三年他已離職。", LegalStage.Indicted)]
    [InlineData("陳志明遭起訴，法官宣判時間訂於三年後。", LegalStage.Indicted)]
    [InlineData("陳志明遭起訴，法官難以判斷是否無罪。", LegalStage.Indicted)]
    [InlineData("陳志明遭起訴，他主張應判無罪。", LegalStage.Indicted)]
    [InlineData("法院宣判陳志明等三人均無罪。", LegalStage.Acquitted)]
    [InlineData("陳志明遭起訴，共犯李小四日前定讞。", LegalStage.Indicted)]
    [InlineData("陳志明遭起訴，同案被告已定讞。", LegalStage.Indicted)]
    [InlineData("陳志明遭起訴，全案定讞前仍在逃。", LegalStage.Indicted)]
    [InlineData("陳志明為某公司負責人，法院判處董事長有期徒刑二年。", LegalStage.Convicted)] // a title, not another person
    [InlineData("陳志明獲解除通緝。", LegalStage.Unknown)]
    [InlineData("法院撤回對陳志明的通緝。", LegalStage.Unknown)]
    [InlineData("陳志明遭起訴，原審判陳志明有期徒刑二年。", LegalStage.Convicted)]
    // An aside in brackets is about whoever stands right before it; a fraudster's story is no report; a heading is no step.
    [InlineData("李小四（另案起訴）、陳志明等人共組詐欺集團。", LegalStage.Unknown)]
    [InlineData("陳志明夥同李○○（業經提起公訴）、王小五（已判刑確定）詐騙被害人。", LegalStage.Unknown)]
    [InlineData("陳志明（業經檢察官另案起訴）夥同李小四詐騙被害人。", LegalStage.Indicted)]
    [InlineData("李小四與陳志明（58歲，已遭起訴）共組詐欺集團。", LegalStage.Indicted)]
    [InlineData("陳志明所屬集團成員佯稱被害人涉及洗錢，已遭通緝，須交付存款。", LegalStage.Unknown)]
    [InlineData("陳志明假冒檢察官謊稱被害人遭起訴。陳志明遭警方逮捕後移送偵辦。", LegalStage.Alleged)]
    [InlineData("陳志明佯稱可代辦貸款，騙取被害人帳戶，得手後轉匯一空，全案偵結後將陳志明等9人依詐欺罪嫌起訴。", LegalStage.Indicted)] // the story ends, the report goes on
    [InlineData("陳志明涉嫌詐欺。\n起訴罪名\n詐欺取財罪。", LegalStage.Unknown)]
    [InlineData("陳志明、李小四（均遭起訴）涉詐欺。", LegalStage.Indicted)]             // an aside for all of the list
    [InlineData("陳志明與李小四（兩人均遭檢方起訴）涉詐欺。", LegalStage.Indicted)]
    [InlineData("陳志明被控佯稱投資可獲利，遭起訴。", LegalStage.Indicted)]             // the next clause opens with the passive
    [InlineData("被告 陳志明\n主文\n陳志明無罪部分，檢察官上訴駁回。\n理由\n略。", LegalStage.Acquitted)] // the acquittal is upheld
    // A wanted notice said of a group in a report is everyone's; 提起公訴 stays the named person's (a known limit).
    [InlineData("陳志明、李小四、王小五涉嫌吸金，檢調偵辦。檢察官偵查終結，對李小四等3人提起公訴。", LegalStage.Alleged)]
    [InlineData("陳志明、李小四涉嫌吸金。李小四等2人潛逃出境，地檢署發布通緝。", LegalStage.Wanted)]
    [InlineData("陳志明涉嫌吸金，檢調偵辦中。檢察官另對李小四提起公訴。李小四否認犯行。", LegalStage.Alleged)]
    // Someone the text names only as a counterpart is not under investigation with the suspects.
    [InlineData("李小四涉嫌掏空公司，檢調持續偵辦。李小四曾將股票售予陳志明。", LegalStage.Unknown)]
    [InlineData("陳志明與李小四涉嫌掏空公司。檢調持續偵辦。", LegalStage.Alleged)]
    [InlineData("調查局約談陳志明到案。全案由檢方偵辦中。", LegalStage.Alleged)]
    // What stands before 云云 is what someone said.
    [InlineData("被害人接獲陳志明來電，稱其帳戶涉及洗錢、已遭通緝云云，因而匯款。", LegalStage.Unknown)]
    [InlineData("陳志明辯稱已獲不起訴處分云云，惟查陳志明業經檢察官起訴。", LegalStage.Indicted)]
    [InlineData("陳志明遭判刑，辯稱冤枉云云，不服上訴。", LegalStage.Convicted)]            // only what was said is left out
    [InlineData("陳志明遭起訴並辯稱遭人陷害云云。", LegalStage.Indicted)]
    // Named as the victim or a witness, the sentence's steps are someone else's.
    [InlineData("李小四等3人涉詐欺，被害人陳志明報案，警方發布通緝。", LegalStage.Unknown)]
    [InlineData("證人陳志明指出，李小四等3人潛逃，法院發布通緝。", LegalStage.Unknown)]
    [InlineData("被害人陳志明遭詐騙後，反遭檢方起訴。陳志明遭起訴後喊冤。", LegalStage.Indicted)] // a sentence naming them outright still counts
    // One defendant: 被告 about another case, an earlier one or the law is not this case's; the order may say 被告.
    [InlineData("被告 陳志明\n主文\n本件公訴不受理。\n理由\n另案被告李小四經法院判處有期徒刑二年。", LegalStage.Indicted)]
    [InlineData("被告 陳志明\n主文\n本件公訴不受理。\n理由\n刑法規定被告犯本罪者，處有期徒刑二年。", LegalStage.Indicted)]
    [InlineData("被告 陳志明\n主文\n本件公訴不受理。\n理由\n被告前因他案經判處有期徒刑六月確定。", LegalStage.Indicted)]
    [InlineData("被告 陳志明\n主文\n被告無罪。\n理由\n略。", LegalStage.Acquitted)]
    [InlineData("被告 陳志明\n主文\n原判決撤銷。\n被告無罪。\n理由\n原審判處被告有期徒刑一年，尚有未洽。", LegalStage.Acquitted)]
    // A ruling with one defendant goes on about 被告 without the name.
    [InlineData("聲請人即被告 陳志明\n主文\n聲請駁回。\n理由\n本案業經本院判處有期徒刑三年在案。\n被告聲請具保停止羈押，為無理由。", LegalStage.Indicted)] // names nobody: not read
    [InlineData("聲請人即被告 陳志明\n主文\n聲請駁回。\n理由\n被告因詐欺案件，業經本院判處有期徒刑三年在案。", LegalStage.Convicted)]
    [InlineData("聲請人即被告 陳志明\n主文\n聲請駁回。\n理由\n被告經原審判決，認被告犯詐欺取財罪，處有期徒刑二年。", LegalStage.Convicted)]
    [InlineData("被告 陳志明\n被告 李小四\n主文\n陳志明公訴不受理。\n理由\n被告李小四經原審判處有期徒刑三年。\n被告經本院判處有期徒刑一年。", LegalStage.Indicted)] // two defendants: 被告 is not told apart
    // More forms of a judgment's header and order.
    [InlineData("上 訴 人\n即 被 告 陳志明\n主文\n上訴駁回。\n理由\n略。", LegalStage.Convicted)]
    [InlineData("被告 陳志明\n主文\n陳志明犯如本院附表編號1所示之罪，處如本院附表編號1所示之刑。\n理由\n略。", LegalStage.Convicted)]
    [InlineData("被告 陳志明\n被告 李小四\n主文\n原判決關於陳志明沒收部分、李小四無罪部分均撤銷。\n陳志明犯詐欺取財罪，處有期徒刑一年。\n理由\n略。", LegalStage.Convicted)]
    public void legal_stage_from_news(string news, LegalStage expected) =>
        Assert.Equal(expected, Assert.Single(SourceConditions.Determine(Profile("Adverse Media"), false, news)).LegalStage);

    [Fact]
    public void gc_12_stays_indicted()
    {
        var a = CaseAnalyzer.Analyze(TxtCaseReader.Read(GoldenCases.GoldenCaseTests.Folder("GC-12")));
        Assert.Equal(LegalStage.Indicted, Assert.Single(a.Hits).Conditions.Single(c => c.Type == ListType.AdverseMedia).LegalStage);
    }

    // ② never sets aside a predicate offence.

    [Theory]
    [InlineData("〔記者丙／台北報導〕陳志明酒駕肇事逃逸致人於死。", AmlRelevance.Uncertain)]
    [InlineData("〔記者丙／台北報導〕陳志明酒駕撞人致重傷。", AmlRelevance.Uncertain)]
    [InlineData("〔記者丙／台北報導〕陳志明酒駕，警方依公共危險罪移送。", AmlRelevance.Unrelated)]
    [InlineData("〔記者丙／台北報導〕陳志明遭警方約談。同車友人王大同酒駕被捕。", AmlRelevance.Uncertain)] // someone else's conduct
    [InlineData("〔記者丙／台北報導〕陳志明酒駕，並涉竊盜。", AmlRelevance.Uncertain)]
    [InlineData("〔記者丙／台北報導〕陳志明酒駕，另涉違反證券交易法。", AmlRelevance.Uncertain)]
    [InlineData("〔記者丙／台北報導〕陳志明肇事。", AmlRelevance.Uncertain)]                       // 肇事 alone states no conduct
    public void relevance_from_news(string news, AmlRelevance expected) =>
        Assert.Equal(expected, Assert.Single(SourceConditions.Determine(Profile("Adverse Media"), false, news)).AmlRelevance);

    // ① counts an UNKNOWN only for an unmapped category with no PEP flag NO.

    [Fact]
    public void pep_no_and_an_undecided_blacklist_type_give_no_foreign_pep_presumption()
    {
        var hit = Analyze(Profile("Special Interest Person (SIP)", citizenship: "VN"), Flags(FlagValue.No, FlagValue.Yes, FlagValue.No), subject: Subject("VN"));
        Assert.Contains(hit.Conditions, c => c.Type == ListType.Unknown && c.ReasonCodes.Contains(SourceConditions.BlacklistTypeUndecided));
        Assert.DoesNotContain(DispositionPolicy.ForeignPepPresumption, hit.Disposition.ReasonCodes);
    }

    [Fact]
    public void unmapped_category_with_pep_flag_no_gives_no_presumption_and_without_flags_still_does()
    {
        var unmapped = Condition(ListType.Unknown, ConditionStatus.Unknown, basis: ConditionBasis.Unmapped, codes: SourceConditions.UnmappedCategory);
        var pepNo = Condition(ListType.Pep, ConditionStatus.NotHit, basis: ConditionBasis.Flag);
        var s = Subject("VN");
        var p = Profile("Special Interest Person (SIP)", citizenship: "VN");
        Assert.DoesNotContain(DispositionPolicy.ForeignPepPresumption, DispositionPolicy.Recommend(s, p, [unmapped, pepNo], Identity(), false).ReasonCodes);
        Assert.Equal(Recommendation.OnList, DispositionPolicy.Recommend(s, p, [unmapped], Identity(), false).Recommendation);
    }

    // ② sets aside the unrelated adverse media only: NOT_ON_LIST needs every remaining condition NOT_HIT.

    [Fact]
    public void article_22_2_gives_no_recommendation_while_an_unknown_sanction_remains()
    {
        var unrelated = Condition(ListType.AdverseMedia, relevance: AmlRelevance.Unrelated);
        var unknown = DispositionPolicy.Recommend(Subject(), Profile("Sanctions Lists"), [unrelated, Condition(ListType.Sanction, ConditionStatus.Unknown)], Identity(), false);
        Assert.Null(unknown.Recommendation);
        Assert.Equal([DispositionPolicy.AdverseUnrelated, DispositionPolicy.NotDetermined], unknown.ReasonCodes);

        var notHit = DispositionPolicy.Recommend(Subject(), Profile("Sanctions Lists"), [unrelated, Condition(ListType.Sanction, ConditionStatus.NotHit)], Identity(), false);
        Assert.Equal(Recommendation.NotOnList, notHit.Recommendation);
        Assert.Equal([DispositionPolicy.AdverseUnrelated], notHit.ReasonCodes);
    }

    // pending checks: IDENTITY_CHECK for a PEP without DOB that is no RCA; JUDGMENT_PARTY_IDENTITY for a person with news or adverse media.
    [Theory]
    [InlineData("Pep", false, false, "Person", "EMPLOYMENT_RELEVANCE,IDENTITY_CHECK")]
    [InlineData("Pep", true, false, "Person", "EMPLOYMENT_RELEVANCE")]
    [InlineData("Pep,Rca", false, false, "Person", "EMPLOYMENT_RELEVANCE")]
    [InlineData("AdverseMedia", false, true, "Person", "REGION_RELEVANCE,JUDGMENT_PARTY_IDENTITY")]
    [InlineData("AdverseMedia", false, false, "Entity", "REGION_RELEVANCE")]
    [InlineData("Sanction", false, true, "Person", "JUDGMENT_PARTY_IDENTITY")]
    [InlineData("Sanction", false, false, "Person", "")]
    public void pending_checks_table(string types, bool dob, bool news, string recordType, string expected)
    {
        var profile = Profile("Politically Exposed Person") with { DatesOfBirth = dob ? [new PartialDate(1980, 5, 20)] : [], RecordType = recordType };
        var conditions = types.Split(',').Select(t => Condition(Enum.Parse<ListType>(t))).ToList();
        Assert.Equal(expected, string.Join(",", DispositionPolicy.Recommend(Subject(), profile, conditions, Identity(), news).PendingChecks));
    }

    // a NOT_HIT controlled condition is no release control; a NOT_HIT Sanction does not raise the review priority.

    [Fact]
    public void not_hit_pep_beside_a_closed_adverse_media_pair_stays_no_hit()
    {
        var pair = AssessmentPolicy.Assess([Condition(ListType.Pep, ConditionStatus.NotHit), Condition(ListType.AdverseMedia)], Identity(IdentityDecision.NoMatch));
        Assert.Equal((ScreeningAssessment.NoHit, (ReviewPriority?)null), (pair.Result, pair.Priority));
        Assert.Empty(pair.ReasonCodes);
    }

    [Fact]
    public void not_hit_sanction_does_not_raise_priority()
    {
        var pair = AssessmentPolicy.Assess([Condition(ListType.Sanction, ConditionStatus.NotHit), Condition(ListType.AdverseMedia)], Identity());
        Assert.Equal((ScreeningAssessment.Review, ReviewPriority.Low), (pair.Result, pair.Priority));
        var unknown = AssessmentPolicy.Assess([Condition(ListType.Sanction, ConditionStatus.Unknown)], Identity());
        Assert.Equal(ReviewPriority.High, unknown.Priority);
    }

    // monotone risk scoring.

    private static readonly RiskPolicy Policy = new("risk-test", Approved: true,
        new Dictionary<ListType, int> { [ListType.AdverseMedia] = 10, [ListType.Sanction] = 50, [ListType.Pep] = 20 },
        new Dictionary<string, int>(),
        new Dictionary<LegalStage, int> { [LegalStage.Indicted] = 10, [LegalStage.Wanted] = 10 },
        MediumFrom: 15, HighFrom: 100);

    private static RiskAssessment Score(params SourceCondition[] c) => RiskScoring.Assess(c, Identity(), Policy)!;

    [Fact]
    public void unknown_sanction_or_pep_scores_no_lower_than_hit()
    {
        Assert.Equal(Score(Condition(ListType.Sanction)).Score, Score(Condition(ListType.Sanction, ConditionStatus.Unknown)).Score);
        Assert.Equal(Score(Condition(ListType.Pep)).Score, Score(Condition(ListType.Pep, ConditionStatus.Unknown)).Score);
    }

    [Fact]
    public void legal_stage_weighs_on_adverse_media_only()
    {
        Assert.Equal(0, Score(Condition(ListType.OfficialWantedList, stage: LegalStage.Wanted)).Score); // WANTED is weighted, but not here
        Assert.Equal(20, Score(Condition(ListType.AdverseMedia, stage: LegalStage.Indicted)).Score);
    }

    [Fact]
    public void condition_set_aside_by_article_22_2_scores_nothing()
    {
        Assert.Equal(0, Score(Condition(ListType.AdverseMedia, stage: LegalStage.Indicted, relevance: AmlRelevance.Unrelated)).Score);
    }

    [Fact]
    public void contributions_carry_list_and_stage_prefixes()
    {
        Assert.Equal(["list:AdverseMedia+10", "stage:Indicted+10"], Score(Condition(ListType.AdverseMedia, stage: LegalStage.Indicted)).Contributions);
    }

    [Fact]
    public void unknown_condition_other_than_sanction_or_pep_scores_nothing()
    {
        var risk = Score(Condition(ListType.AdverseMedia, ConditionStatus.Unknown, LegalStage.Indicted));
        Assert.Equal((0, RiskLevel.Low), (risk.Score, risk.Level));
        Assert.Empty(risk.Contributions);
    }

    // MediumFrom 15, HighFrom 40: each threshold is reached at its own value.
    [Theory]
    [InlineData(14, RiskLevel.Low)]
    [InlineData(15, RiskLevel.Medium)]
    [InlineData(16, RiskLevel.Medium)]
    [InlineData(39, RiskLevel.Medium)]
    [InlineData(40, RiskLevel.High)]
    [InlineData(41, RiskLevel.High)]
    public void risk_level_turns_at_medium_from_and_high_from(int points, RiskLevel expected)
    {
        var policy = Policy with { ListWeights = new Dictionary<ListType, int> { [ListType.AdverseMedia] = points }, HighFrom = 40 };
        var risk = RiskScoring.Assess([Condition(ListType.AdverseMedia)], Identity(), policy)!;
        Assert.Equal((points, expected), (risk.Score, risk.Level));
    }

    [Fact]
    public void any_raised_sanction_is_high()
    {
        Assert.Equal(RiskLevel.High, Score(Condition(ListType.Sanction)).Level);                         // 50 < HighFrom 100
        Assert.Equal(RiskLevel.High, Score(Condition(ListType.Sanction, ConditionStatus.Unknown)).Level);
        Assert.Equal(RiskLevel.Low, Score(Condition(ListType.Sanction, ConditionStatus.NotHit)).Level);
    }

    // former PEP.

    [Fact]
    public void inactive_pep_is_former_with_an_influence_check_and_still_hit()
    {
        var hit = Analyze(Profile("Politically Exposed Person", "Inactive PEP"));
        var pep = Assert.Single(hit.Conditions);
        Assert.Equal((ConditionStatus.Hit, "FORMER_PEP"), (pep.Status, pep.PepType));
        Assert.Contains(DispositionPolicy.FormerPepInfluence, hit.Disposition.PendingChecks);
    }

    [Fact]
    public void every_role_ended_is_former_and_the_rca_of_a_former_pep_gets_the_check()
    {
        var ended = Analyze(Profile("Politically Exposed Person", roles: [new ProfileRole("部長", "某部", new PartialDate(2010), new PartialDate(2014))]));
        Assert.Equal("FORMER_PEP", Assert.Single(ended.Conditions).PepType);
        Assert.Contains(DispositionPolicy.FormerPepInfluence, ended.Disposition.PendingChecks);

        var rca = Analyze(Profile("Relative or Close Associate (RCA)", "Spouse of Former PEP"));
        Assert.Equal("PEP_FAMILY", Assert.Single(rca.Conditions).PepType);
        Assert.Contains(DispositionPolicy.FormerPepInfluence, rca.Disposition.PendingChecks);

        Assert.DoesNotContain(DispositionPolicy.FormerPepInfluence, Analyze(Profile("Politically Exposed Person")).Disposition.PendingChecks);
    }

    // Analysis date 2026-11-04: a role ends only when the last day its end date stands for is before it. The date is the
    // CaseService clock's (UTC), passed in; a call that gives none uses today.
    [Theory]
    [InlineData(2025, null, null, true)]
    [InlineData(2026, null, null, false)]
    [InlineData(2026, 10, null, true)]
    [InlineData(2026, 11, null, false)]
    [InlineData(2026, 11, 3, true)]
    [InlineData(2026, 11, 4, false)]
    public void a_role_has_ended_only_when_its_end_date_lies_before_the_analysis_date(int year, int? month, int? day, bool former) =>
        Assert.Equal(former, SourceConditions.IsFormerPep(
            Profile("Politically Exposed Person", roles: [new ProfileRole("部長", "某部", new PartialDate(2010), new PartialDate(year, month, day))]),
            ownRoles: true, new DateOnly(2026, 11, 4)));

    [Fact]
    public void a_clause_of_many_keywords_is_read_in_linear_time()
    {
        // The modal was looked for again before every keyword: 200,000 起訴 in one clause took 13 s.
        var started = System.Diagnostics.Stopwatch.StartNew();
        var news = "陳志明" + string.Concat(Enumerable.Repeat("起訴", 200_000));
        Assert.Equal(LegalStage.Indicted, Assert.Single(SourceConditions.Determine(Profile("Adverse Media"), false, news)).LegalStage);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(3), $"took {started.Elapsed}");
    }

    [Fact]
    public void unclosed_quotes_are_read_in_linear_time()
    {
        // A backtracking quote pattern rescanned to the end from every unclosed 「.
        var started = System.Diagnostics.Stopwatch.StartNew();
        var news = "陳志明遭起訴" + new string('「', 1_000_000);
        Assert.Equal(LegalStage.Indicted, Assert.Single(SourceConditions.Determine(Profile("Adverse Media"), false, news)).LegalStage);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(3), $"took {started.Elapsed}");
    }

    [Fact]
    public void the_analysis_takes_its_date_from_the_caller_not_the_machine_clock()
    {
        var profile = Profile("Politically Exposed Person", roles: [new ProfileRole("部長", "某部", new PartialDate(2010), new PartialDate(2026, 6, 30))]);
        var before = Analyze(profile, asOf: new DateOnly(2026, 6, 30));
        var after = Analyze(profile, asOf: new DateOnly(2026, 7, 1));
        Assert.Equal((null, "FORMER_PEP"), (Assert.Single(before.Conditions).PepType, Assert.Single(after.Conditions).PepType));
        Assert.DoesNotContain(DispositionPolicy.FormerPepInfluence, before.Disposition.PendingChecks);
        Assert.Contains(DispositionPolicy.FormerPepInfluence, after.Disposition.PendingChecks);
    }

    [Fact]
    public void a_pep_with_one_ended_and_one_current_role_is_not_former()
    {
        var asOf = new DateOnly(2026, 11, 4);
        bool Former(params PartialDate?[] ends) => SourceConditions.IsFormerPep(
            Profile("Politically Exposed Person", roles: ends.Select(to => new ProfileRole("部長", "某部", new PartialDate(2010), to)).ToList()), ownRoles: true, asOf);

        Assert.False(Former(new PartialDate(2014), null));
        Assert.False(Former(new PartialDate(2014), new PartialDate(2030)));
        Assert.True(Former(new PartialDate(2014), new PartialDate(2018)));
    }

    // RCA statutory scope (認定標準第 6 條).

    [Theory]
    [InlineData("Cousin")]
    [InlineData("堂兄弟")]
    public void relation_outside_the_statutory_family_is_flagged_and_still_hit(string sub)
    {
        var rca = Assert.Single(Analyze(Profile("Relative or Close Associate (RCA)", sub)).Conditions);
        Assert.Equal(ConditionStatus.Hit, rca.Status);
        Assert.NotEqual("PEP_FAMILY", rca.PepType);
        Assert.Contains(SourceConditions.RcaOutsideStatutoryScope, rca.ReasonCodes);
    }

    [Fact]
    public void spouse_stays_pep_family()
    {
        var rca = Assert.Single(Analyze(Profile("Relative or Close Associate (RCA)", "Spouse")).Conditions);
        Assert.Equal("PEP_FAMILY", rca.PepType);
        Assert.DoesNotContain(SourceConditions.RcaOutsideStatutoryScope, rca.ReasonCodes);
    }

    // every disposition in the analysis JSON is advisory.

    [Fact]
    public void every_disposition_in_the_analysis_json_is_advisory()
    {
        var input = TxtCaseReader.Read(GoldenCases.GoldenCaseTests.Folder("GC-01"));
        var analysis = CaseAnalyzer.Analyze(input);
        foreach (var json in new[] { JsonSerializer.Serialize(analysis, CaseJson.Options), JsonSerializer.Serialize(ExplanationBuilder.Build(input, analysis), CaseJson.Options) })
        {
            var dispositions = JsonDocument.Parse(json).RootElement.GetProperty("hits").EnumerateArray().Select(h => h.GetProperty("disposition")).ToList();
            Assert.NotEmpty(dispositions);
            Assert.All(dispositions, d => Assert.True(d.GetProperty("advisory").GetBoolean()));
        }
    }

    // Is RCA? = NO, wanted-list entries and unmapped nationalities.

    [Fact]
    public void An_rca_category_with_is_rca_no_stays_for_review_with_a_conflict()
    {
        var no = SourceConditions.Determine(Profile("Relative or Close Associate (RCA)", "Spouse"), false, null, ListFlags.None with { Rca = FlagValue.No });
        var rca = Assert.Single(no, c => c.Type == ListType.Rca);
        Assert.Equal(ConditionStatus.Unknown, rca.Status);
        Assert.Contains(SourceConditions.FlagCategoryConflict, rca.ReasonCodes);

        var blank = SourceConditions.Determine(Profile("Relative or Close Associate (RCA)", "Spouse"), false, null, ListFlags.None);
        Assert.Equal(ConditionStatus.Hit, Assert.Single(blank, c => c.Type == ListType.Rca).Status);
    }

    [Fact]
    public void An_active_wanted_list_entry_does_not_confirm_a_sanction_measure()
    {
        SourceCondition Sanction(string list) => SourceConditions.Determine(
            Profile("Sanctions Lists", list: new OfficialListEntry("MJIB", list, "Active", null)), false, null).Single(c => c.Type == ListType.Sanction);

        Assert.Equal(ConditionStatus.Hit, Sanction("SDN List").Status);
        var wanted = Sanction("Wanted List");
        Assert.Equal(ConditionStatus.Unknown, wanted.Status);
        Assert.Contains(SourceConditions.SanctionMeasureNotConfirmed, wanted.ReasonCodes);
    }

    [Fact]
    public void An_unmapped_nationality_keeps_the_foreign_pep_presumption_off()
    {
        var pep = Condition(ListType.Pep);
        var profile = Profile("Politically Exposed Person", citizenship: "VN");
        Assert.Equal(Recommendation.OnList, DispositionPolicy.Recommend(Subject("VN"), profile, [pep], Identity(), false).Recommendation);
        var ambiguous = DispositionPolicy.Recommend(Subject("VN") with { Findings = ["AMBIGUOUS:國籍 2"] }, profile, [pep], Identity(), false);
        Assert.DoesNotContain(DispositionPolicy.ForeignPepPresumption, ambiguous.ReasonCodes);
    }
}
