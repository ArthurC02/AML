# Watchlist and Adverse Information Screening

> Module: Source Screening and Risk Assessment.

## Scope

Covers structured official lists, structured adverse-information Profiles and the list-type determination shared by all source policies. Fraud is one risk category among others, and list types follow the bank's vocabulary ([ADR-0005](../adr/0005-list-types-and-risk-categories.md)).

## List types

```text
SANCTION
PEP
RCA
SIE                 # state-owned or state-invested enterprise
OFFICIAL_WANTED_LIST
OTHER_OFFICIAL_LIST
ADVERSE_MEDIA
BANK_INTERNAL_LIST
```

A Profile can carry several.

## List-type determination

### Flags and category

Each candidate row carries four source flags: PEP, 黑名單 (blacklist), 負面新聞 (adverse news) and `Is RCA?`. A flag with a value (`NO` counts as a value) decides the list types it speaks for; a mapped category whose flag is `MISSING` keeps its [category](#category-mapping) condition.

| List type | Flag that speaks for it |
|---|---|
| PEP | PEP |
| SANCTION, OFFICIAL_WANTED_LIST, OTHER_OFFICIAL_LIST | 黑名單 |
| ADVERSE_MEDIA | 負面新聞 |
| RCA | `Is RCA?` |
| SIE, unmapped category | none; the category decides |

- PEP `YES` → PEP HIT; `NO` → PEP NOT_HIT.
- 負面新聞 `YES` → ADVERSE_MEDIA HIT; `NO` → ADVERSE_MEDIA NOT_HIT.
- 黑名單 `YES` → SANCTION, OFFICIAL_WANTED_LIST or OTHER_OFFICIAL_LIST, decided from approved source fields: an official-list entry naming a sanction or a Sanction category gives SANCTION, an active wanted list gives OFFICIAL_WANTED_LIST, any other official-list entry or category gives OTHER_OFFICIAL_LIST. If they do not decide it, the type is UNKNOWN with `BLACKLIST_TYPE_UNDECIDED` and goes to review.
- 黑名單 `NO` → NOT_HIT, except that it never clears a Sanction category: that SANCTION condition is UNKNOWN with `FLAG_CATEGORY_CONFLICT`.
- `Is RCA? = YES` → RCA HIT with `IS_RCA_FLAG`, whatever the other flags or the category say.
- `Is RCA? = NO` never clears an RCA category: that RCA condition is UNKNOWN with `FLAG_CATEGORY_CONFLICT`. Without an RCA category it adds nothing.
- Otherwise a flag that contradicts the category wins. The flag is never overwritten, and the conflict is recorded: when any of the PEP, 黑名單 or 負面新聞 flags has a value and a mapped category is left without a HIT condition, every condition of the Profile carries `FLAG_CATEGORY_CONFLICT`.

The flags are read per candidate row (`SourceValues.Flag`, [source flags](extraction-and-normalization.md#source-flags)) and merged per Profile: YES wins over UNPARSEABLE, which wins over NO. UNPARSEABLE gives an UNKNOWN condition with `FLAG_UNPARSEABLE`. `Is RCA?` is not one of the three list flags (PEP, 黑名單, 負面新聞).

The column order is assumed until the bank confirms it ([open questions](../plan/open-questions.md#data-and-source)): … 理由, Is RCA?, Name Property, PEP, 黑名單, 負面新聞. The samples stop at Is RCA?, so their PEP, 黑名單 and 負面新聞 flags are MISSING and the category mapping below applies.

### Category mapping

Applies to every mapped category whose flag is `MISSING`, and so to all of them when the PEP, 黑名單 and 負面新聞 flags are all `MISSING`:

| Source category | List type |
|---|---|
| Politically Exposed Person | PEP |
| Relative or Close Associate | RCA |
| Sanction (any subcategory) | SANCTION |
| State Owned / State Invested | SIE |
| Adverse Media | ADVERSE_MEDIA |
| Other Official List, Other Exclusion List | OTHER_OFFICIAL_LIST |
| SIP – Other Official List / Other Exclusion List | OTHER_OFFICIAL_LIST |
| SIP – Financial Crime, Tax Crime, Trafficking, Human Trafficking, Narcotics, Drug Trafficking, Organised Crime, Terror / Terrorism, Corruption, Bribery & Corruption, Money Laundering, Fraud, War Crimes, Cybercrime, Environmental Crime, Corporate Crime | ADVERSE_MEDIA, with the subcategory as risk category |

- Subcategories do not change non-SIP types.
- An SIP subcategory not in the table, or no subcategory (e.g. Regulatory Enforcement List), is unmapped: `UNKNOWN` with `UNMAPPED_CATEGORY`, never assumed to be adverse media.
- `BANK_INTERNAL_LIST` is in the bank's vocabulary ([ADR-0005](../adr/0005-list-types-and-risk-categories.md)) but no mapping produces it: no export field marks a bank-internal list yet ([open questions](../plan/open-questions.md#data-and-source)). A bank-internal category is therefore unmapped, `UNKNOWN` with `UNMAPPED_CATEGORY`, and goes to review.
- SIP (Special Interest Person) is not SIE; Dow Jones Special Interest Entity is not SIE either.
- Unmapped categories are copied verbatim into the explanation and left as UNKNOWN for review.

### RCA

The list type RCA (a PEP's relative or close associate) is not the Case's related party. Its relationship scope is described in [PEP and RCA screening](pep-rca-screening.md#rca-relationship-scope).

### Reason codes

| Code | Meaning |
|---|---|
| `FLAG_UNPARSEABLE` | a flag value could not be read; the condition is UNKNOWN |
| `FLAG_CATEGORY_CONFLICT` | a flag contradicts the category |
| `BLACKLIST_TYPE_UNDECIDED` | 黑名單 = YES, but the source fields do not decide the list type |
| `IS_RCA_FLAG` | the RCA condition comes from `Is RCA? = YES` |
| `UNMAPPED_CATEGORY` | the category has no list type; UNKNOWN for review |
| `SANCTION_MEASURE_NOT_CONFIRMED` | see [sanctions screening](sanctions-screening.md#sanction-condition) |
| `RCA_OUTSIDE_STATUTORY_SCOPE` | see [PEP and RCA screening](pep-rca-screening.md#rca-relationship-scope) |
| `STAGE_READERS_DISAGREE` | the stage models and the rules read different stages in the attached news; a reviewer reads it ([three readers](#three-readers)) |

## Risk categories

```text
FRAUD
BRIBERY
CORRUPTION
FORGERY
BREACH_OF_TRUST
TAX_EVASION
TAX_CRIME
FINANCIAL_CRIME
MONEY_LAUNDERING
TERRORISM
TRAFFICKING
HUMAN_TRAFFICKING
DRUG_TRAFFICKING
ORGANISED_CRIME
WAR_CRIMES
CYBERCRIME
ENVIRONMENTAL_CRIME
CORPORATE_CRIME
MARKET_ABUSE
SMUGGLING
ARMS_TRAFFICKING
VIOLENT_CRIME
THEFT
EXTORTION
COUNTERFEITING
SEXUAL_OFFENCE
OTHER
```

A source Profile can carry multiple categories. MARKET_ABUSE through SEXUAL_OFFENCE are given only to offences read by the [news digest](#news-digest); they follow the FATF designated categories of offences and the predicate offences of 洗錢防制法 第 3 條, and no source condition or risk score reads them.

## Legal/procedural stage

```text
ALLEGED
WANTED
INDICTED
CONVICTED
ACQUITTED
DISMISSED
UNKNOWN
```

The system preserves the source wording. `INDICTED` is not rewritten as convicted, and `WANTED` is not a court judgment.

- A sentencing request is not a sentence; an indictment is not a conviction; a co-defendant's conviction is not the subject's.
- A judgment counts as delivered once any instance has ruled; it need not be final. Finality is recorded only when the source states it; otherwise it is "not stated".
- A stage describes the source as of its date. "No later indictment or judgment recorded" is never rewritten as "not indicted"; an old or undated item does not describe the status on the review date.
- Explicit source denials are kept with quotation, time point, person and scope.

### Stage from attached news

**How the stage reading is built.** The code is in `Core/Screening/Stage/`, and each file holds one of five concerns, so that a change of one kind touches one place:

| Concern | File | Holds |
|---|---|---|
| Volatile parameters | `StageGlossary` | The words and limits that change when a new wording turns up: penalty names, dismissal words, words of suspicion and investigation, a ruling's party lines and headings, the line and name limits, and the negations, modals, relations and common surnames that the news digest and the news models' reading use. Every word list is plain words separated by `|` with no pattern syntax (a test keeps it so), and every entry says what the words mean, which pattern or rule uses them, and what a wrong word would do. It has its own version (`stage-glossary-4`). It stays in the code, where a pattern built from it is checked when the code is built and one commit fixes the words a decision was made with; it is shaped so that it could be maintained outside the code as a glossary. |
| Patterns | `StagePatterns` | Every regular expression, named and grouped by what it recognises, composed from the glossary's lists. A pattern with structure of its own (判, a name, then a penalty; what may not stand before 稱) is a pattern, not a glossary entry. |
| Technical logic | `StageText`, `RulingForm`, `ReportForm` | Reading the form of a text, never what it means: cutting sentences and clauses, finding names, and the modal, negation and relation mechanics the news digest uses (`StageText`); a ruling's kind, order block, defendants, wrapped lines, the clauses that have the person as subject (`RulingForm`); whose a report's sentence and clause are (`ReportForm`). |
| Structure | `StageTypes` | Data only: the parsed ruling, the sentence and clause scopes, a reading, a rule (`StageRule<T>`: a code and a function to a stage or nothing), the codes (`StageBasis`). |
| Business logic | `RulingRules`, `ReportRules` | What a found form means: the four readers of a ruling, the two transitions of a report, the closing decision. A rule is written as named patterns and form readers giving a stage, with its legal reason in its comment. |
| Orchestration | `RulingReading`, `LegalStageReading` | The entry points, the two ordered lists of rules and the sentence and clause loop; no pattern and no word. `DecisionList.FirstOf` gives the first rule that speaks, with its code. |

Both readings share one shape: a text read once into a context value, then an ordered list of rules of which the first that speaks wins (a decision list). `LegalStageReading.FromNews` reads a report and asks `RulingReading.Read` for a judgment's or release's order, which wins. The news digest uses the same glossary, patterns and text mechanics. `SourceConditions` holds only the list types and source conditions. A new wording is, in rising cost: a word in a glossary list; a pattern or a guard; a new rule and its place in its list.

*A report* (`LegalStageReading`) is read as written, sentence by sentence (`SentenceScope`: does it name the person, may its steps be theirs) and clause by clause. Between clauses the loop carries only whether a clause of the sentence so far named the person; in every clause the `Transitions` are the rules, each a named function from the clause and the stage so far to a new stage or nothing: a stage keyword, a wanted notice while no stage is told. The last stage read stands; the closing decision puts the ruling's order first, then ALLEGED.

*A ruling* (`RulingReading`) is a cascade of three steps, as rule-based extraction from fixed-form text is usually built (stages that each do one thing and hand a structure on):

1. **Parse once.** `RulingForm.Parse` turns the text into what every rule needs and nothing decides twice: whether it is a judgment or a court's release, the lines under the order's heading, the whole text, the person's names (with 被告 when they are the only defendant), and whether they are a defendant. A text that is neither gives no reading.
2. **Readers in a fixed order.** Each reader is one way a ruling tells a person's outcome, a named function from the parsed text to a stage or nothing: the order under its heading, the whole release, proceeds confiscated, sentenced by row number. The list `Readers` is the precedence, and the first reader that speaks gives the stage (a decision list). The readers do not know of each other.
3. **Patterns and guards** are named regular expressions grouped by what they recognise (the kind of text and its order, the parties, a sentence of the order and whose it is, guards for what looks like an outcome and is not, row numbers).

**Which rule gave the stage.** Every rule has a code (kind `LEGAL_STAGE_BASIS` in `/api/codes`, constants in `StageBasis`), and so do the closing decisions. The condition carries the code of the rule that gave its stage as `legalStageBasis` (null exactly when the stage is UNKNOWN, save a vote of the [three readers](#three-readers)): the ruling reader that spoke first when the order wins, else the transition that last set the stage, else the closing decision. The explanation shows it beside the stage: `法律階段 CONVICTED（依據：主文沒收犯罪所得（STAGE_PROCEEDS_CONFISCATED））`. For measuring rules one by one on the labelled sets and listing the texts where two of them disagree, `DecisionList.AllOf` gives what every rule of a list says, and `RulingReading.Trace` does so for the ruling readers; decisions use only `FirstOf`.

| Code | The stage comes from |
|---|---|
| `STAGE_ORDER_UNDER_HEADING` | A sentence of the order, under its heading, whose subject is the person (CONVICTED or ACQUITTED) |
| `STAGE_RELEASE_WHOLE_TEXT` | A release whose order says nothing of the person, read whole in the order's form |
| `STAGE_PROCEEDS_CONFISCATED` | The order confiscates the person's proceeds of crime (CONVICTED) |
| `STAGE_SENTENCED_BY_ROW_NUMBER` | A sentence of the order covering the person's row number (CONVICTED) |
| `STAGE_KEYWORD` | A stage keyword in a clause of a report, the last in the clause |
| `STAGE_WANTED_NOTICE` | 通緝 while no judicial stage is told (WANTED) |
| `STAGE_UNDER_INVESTIGATION` | 偵辦 / 調查 with the person under suspicion, or in a text that never writes their name, and no stage read (ALLEGED) |
| `STAGE_OFFICIAL_WANTED_LIST` | Not news: an active official wanted list (WANTED) |
| `STAGE_VOTE_UNANIMOUS`, `STAGE_VOTE_MAJORITY`, `STAGE_VOTE_SPLIT` | Not a rule: the vote of the rules and the two stage models ([three readers](#three-readers)) |

A new form, of a ruling or of a report, is therefore one of four changes, in rising cost: a word of the glossary, a pattern of an existing rule, a guard, or a new rule with its place in its list and its code in `StageBasis` and the catalog. Each comes with rows of the `legal_stage_from_news` theory (a new rule also with one of `legal_stage_basis_from_news`), counter-examples included, and is measured on the labelled sets before and after.

The ADVERSE_MEDIA stage is read from the attached news by a keyword heuristic, and from a judgment's order by its fixed form. Both await a judgment Golden Case; until then they are checked on augmented cases (AUG-02-22) and made-up reports:

- **A judgment's order.** When the text has a 主文 line, the lines from it to the 事實 / 理由 heading are the order, and what it says of the Profile person is the stage: a sentence (處有期徒刑, 拘役, 罰金, 無期徒刑, 死刑, 免刑) in any part is CONVICTED, else 無罪 is ACQUITTED. Only a sentence where the person is the subject counts: from the clause their name opens (alone or in a list of names: 林美玲、王大明均無罪) to the end of that sentence. So a co-defendant's sentence or acquittal in a sentence of its own is never theirs (林美玲處有期徒刑一年。王大明無罪。), nor is an offence against them (王大明對林美玲犯詐欺取財罪，處…) or a part of an earlier judgment being revoked (原判決關於林美玲處有期徒刑部分撤銷); a co-defendant's clause after the person's in the same sentence is read as theirs (王大明無罪，林美玲處有期徒刑一年 is CONVICTED for 王大明, a known limit). A numbered heading (一、犯罪事實) ends the order too, and without any heading 40 lines are read at most.
  - The order's forms that are read as well: an item number before the name (一、林美玲犯…), a penalty given by reference (處如附表一所示之刑, 處如本院附表編號1所示之刑), and names written in full-width letters or digits where the Profile writes half-width ones (or the reverse).
  - An order that says neither of them (免訴, 公訴不受理, 上訴駁回) gives no outcome, and the report rules below go on. Whether the person is a listed defendant is read too: named before 主文 on a 被告 line (上訴人即被告, 聲請人即被告 …), or on the lines after it up to the next party's line (any counsel's or agent's line: 選任, 指定, 公設, 義務, 共同選任 or 共同指定 辯護人; 訴訟, 法定 or 告訴 代理人; 上列, 檢察官 …); a defence lawyer named there is no defendant. An anonymised judgment's codes for its defendants (A03犯…) are not read as anyone's name.
  - 無罪部分 that the order revokes names a part of the judgment below (原判決關於林美玲沒收部分、王大明無罪部分均撤銷), not an outcome; 林美玲無罪部分，檢察官上訴駁回 upholds the acquittal and is ACQUITTED.
  - **A court's press release of its ruling** has no party lines, and gives the outcome under a heading of its own, in its opening or in its reasons, in the order's own form. It is known by that heading, or by an opening (its first 8 lines) in the court's own voice: 本院, a case number and 宣判 or 判決 (本院114年度訴字第1號被告…案件，於今日宣判). A news report may cite a court and its case number (臺灣高等法院113年度上訴字第5號) but never says 本院, and is read as a report. The heading is a line of its own: 主文, optionally with an item number 壹、 or 一、, 本院 and 判決 before it (壹、判決主文：, 主文要旨, 本院判決主文:), or 判決結論 / 判決結果 with its item number (壹、判決結論:; without the number it may be a news report's own line and is not a heading). 要旨, 摘要 or 摘要說明, a colon and a bracketed note may follow (壹、主文摘要:(一審判處有期徒刑4年)). The item number of a heading may be 壹、 貳、 參、 or 一、 二、 三、. What stands under the heading is read first, to the next heading of its series (a line opening 壹、 貳、 …; under a heading numbered 一、, the next short line numbered that way), and what it says of the person is the outcome. A release with no heading, or one that says nothing of the person under it, is read whole (400 lines at most). Either is read as a judgment's order is, with these differences:
    - A line that ends no sentence runs on into the next, since such a page may break a line inside a sentence or a name; not into a line that opens a new item (一、, ㈠, (一), 1.), and not from a heading (the order's heading, or an item number and at most 24 characters with no comma) or a table cell (a line of at most six characters with no punctuation). ； joins outcomes inside an item (陳志明被訴…部分，公訴不受理；其餘被訴部分無罪) and is read as a comma.
    - The ruling may stand in running text: before the name a clause may say 判決, 改判, 判決主文：「 or 認, after 經, 本院, 合議庭 or 國民法官法庭 or not, with 被告 or not (判決主文：「陳志明犯傷害致死罪，處有期徒刑九年」, 判決被告陳志明、李小四均犯…, 經本院認被告陳志明犯殺人罪，處有期徒刑14年); never 原判決 or 一審判決, which recall the ruling below.
    - An order item may be numbered ㈠, (一) or 1. and a list of names may go on after 與 and hold spaces (㈠、李小四等負責人，與王大同、 陳志明等共同犯…罪，各處如附表所示之刑期); a penalty by reference may be 處如該欄所示之刑, and 應執行有期徒刑, 拘役 or 罰金 is a sentence too (陳志明犯…等罪，應執行有期徒刑7月). These forms are read in a judgment's order as well.
    - Its defendants are the people its opening writes as 被告 and the name; the opening runs to where the release says 宣判 (within its first 8 lines), else to the heading. The person is the only defendant when every 被告 of the opening is followed by one of their names (下稱被告 aside), and the name by no 等, 、, 及 or 與.
    - The forms of a large case. A line closed by a bracketed note after a full stop does not run on into the next (…處有期徒刑2年。（刑度與原審相同）). Rulings may be numbered ①, ② after 判決, with 及 or a date before (於114年6月9日判決①被告李小四…；②被告陳志明…), and a clause may open 另 (另陳志明部分改判無罪). Defendants sentenced by row number: when the text writes the person with a number (陳志明（編號3）) and a sentence of the order's form under the heading covers that number in brackets (各處有期徒刑7年（編號1）、5年（編號2至3）), the person is CONVICTED; not by an unbracketed number (附表編號2 of an exhibit), and not in a sentence that also says 無罪, 原審, 第一審, 上訴 or 前經. A name followed by a bracket saying the person is tried elsewhere (（本院審理中）, （由本院另案審理）, （另行審結）) is not this ruling's subject. 被告提起上訴 in the opening does not name a further defendant.
    - 陳志明犯罪所得新臺幣五萬元…沒收 (or 追徵) under the heading is CONVICTED: proceeds of crime are taken from the person convicted of it, also where the order rules on nothing else of theirs (an appeal on the confiscation alone). Not in a sentence with 參與人, 第三人, 無罪, 不予沒收, 不另為沒收, 已發還, 駁回 or 聲請.
    - A text that opens with the court's name, the case number and 宣判 (臺灣士林地方法院113年度訴字第2號…案件，於…宣判) is a release too. A foreign name in Latin letters may stand in the list before the person's name (JOHN A. SMITH、陳志明共同犯…).
    - Not read: an appeal (上訴駁回 says nothing of the outcome below), a table of defendants one cell a line, an item that goes on without a subject (應執行…), probation, what is asked for (檢察官請求處有期徒刑五年 under the heading is read as a sentence), outcomes joined inside one clause, a name with a bracketed alias inside a list, defendants written only as masked forms that several of them share (王○○), and a release with no heading whose text goes on to recall the indictment after the outcome (the last stage wins, below).
  - **Anyone else the judgment names** (a witness, a victim, an account holder, an accomplice tried elsewhere) gets any stage keyword only from the clause naming them, a wanted notice from that clause or from a later clause of the sentence that has no subject of its own: it opens with 經, 由, 遭, 被, 並, 且, 而, 另, 現, 仍 or 通緝 itself and holds no 被告 (陳志明經傳喚未到，由本院通緝中; not 陳志明到庭，李小四則由本院通緝中, nor 陳志明提告後，檢察官起訴被告，被告遭通緝): the judgment's subjectless 起訴 is its defendants' (檢察官起訴被告之上開犯行，其中陳志明之陳述足以佐證 gives 陳志明 nothing; 李小四由本院通緝中，陳志明…另行審結 makes only 李小四 WANTED), 調查 or 偵辦 somewhere in the text does not make them ALLEGED, and 被告依陳志明指示取款，量處被告有期徒刑七年 sentences the defendant. 共犯陳志明業經另案起訴 and 共犯陳志明業經本院另案判處有期徒刑二年 are still theirs.
  - **The only defendant.** When the 被告 blocks before 主文 hold one line in all and it is the person's (被告, 上訴人即被告, 聲請人即被告 or 抗告人即被告), the order's bare 被告 is that person: the order may say 被告無罪 without the name. Outside the order a bare 被告 names nobody.
  - Not read: a co-defendant of the same surname, and text after the order that overturns it (the order wins over the rest of the text).

- **A report** is split into sentences (。！？； or a line break) and clauses (，). A sentence names the Profile person when it contains one of the Profile's names of two or more characters. Nothing is blanked out or set aside: quoted and reported speech, a fraudster's story, bracketed asides, a relative, a victim or witness and another person's clause are read as the rest of the text (known limits the vote covers, below).
- DISMISSED (不起訴, 緩起訴, 撤回起訴, 撤回公訴, 不予起訴, 免予起訴; never INDICTED) and INDICTED (起訴, not right after 不, 緩, 撤回 or 予, and not 起訴罪名 / 起訴法條; 聲請簡易判決處刑, the summary form) count in any clause outside a judgment's bystander: a subjectless 檢方已起訴 or a collective 起訴七人 (GC-12) is the person's.
- 提起公訴 (not after 不, 不予, 未 or 未經), a conviction and an acquittal count only in a sentence naming the Profile person: a co-defendant's sentence is not the subject's (GC-12).
  - CONVICTED: 判處, 判刑, 判決有罪, 有罪確定, and 判 with the penalty itself, as reports write it: 判陳志明有期徒刑三年, 遭判無期徒刑, 重判十五年, 判1年6月, 改判有期徒刑二年. Between 判 and the penalty (有期徒刑, 無期徒刑, 死刑, 拘役, 罰金, or a number of years or months) only 決 / 處 and a name (他, 她, or two to four characters) fit. 審判, 裁判, 談判, 誤判, 研判, 判斷, 判決前三年 and 宣判時間 are other words (一審判他五年 and 原審判陳志明有期徒刑 are sentences), and 判賠 names no penalty.
  - ACQUITTED: 無罪 shortly after 判決, 改判, 宣判 or 獲判 in its clause (宣判陳志明等三人均無罪), after a bare 判 and a name (判陳志明無罪), after an instance or a reversal (二審逆轉無罪), or as 無罪定讞 / 無罪確定. 堅稱無罪 is a claim, 應判無罪 a plea, 無罪推定 a presumption, 撤銷無罪判決 no acquittal.
- The last stage keyword in a clause is its stage, and the last stage in the text wins. A modal, request, negation or question before a keyword is not read: 尚未起訴 and 恐遭起訴 read INDICTED, 最重可判處 CONVICTED. Nor is 定讞 a stage of its own. Since a dismissal or an indictment counts in a sentence not naming the person, such a sentence can replace a stage a sentence naming them told: 王大明遭判刑。檢方另起訴李小四。 reads INDICTED for 王大明 (a known limit the vote covers).
- 通緝 in a sentence naming the person is WANTED, but only until a judicial stage is told, before or after it: 起訴後棄保潛逃遭通緝 stays INDICTED. A judicial stage that only a sentence not naming the person told does not stop it: in 檢察官依法起訴被告等十人。王大同因偵查中逃匿，另發布通緝 王大同 is WANTED. 撤銷通緝, 解除通緝 and 撤回…通緝 are none.
- With no stage, 偵辦 or 調查 anywhere in the text give ALLEGED when a sentence naming the person says they are under suspicion or names a step or a role (涉, 嫌, 偵辦, 調查, 逮捕, 拘提, 約談, 羈押, 收押, 移送, 函送, 交保, 搜索, 查獲, 到案, 被告, 共犯, 首腦, 主謀, 成員, 車手, 起訴, 通緝) or lists them in a group (…等9名). Someone the text names only as a counterpart in the facts (李小四曾將股票售予陳志明) stays UNKNOWN. A text that never writes one of the Profile's names is about them as a whole and keeps ALLEGED. Otherwise the stage is UNKNOWN.

**Why the rules are few.** The rules were once a fuller reading of reports and releases (quotes, reported speech, a fraudster's story, asides, relatives and bystander roles, another person's clause or object, modals and negations, 定讞, a clause opening 處有期徒刑, an anonymised judgment's codes, dismissed appeals, tables, probation, a listed defendant as INDICTED, and more), and alone gave the reference stage for 178, 122, 352, 87, 145 and 248 persons of the sets dev (185), dev2 (126), v9 (390), accept (93), accept3 (172) and accept4 (257). With the two stage models voting ([three readers](#three-readers)) most of that reading no longer changes the outcome. Each of 42 rule units was put behind a switch and measured outside the repository: one by one, then removed greedily while the vote (at least two of rules, classifier and Gemma 4 12B giving the reference stage) stayed above 0.90 on every set, and, on accept3, which was at 0.849 with all rules, not below its 146. 34 units could go; in that switch-off copy (the system's own counts follow in the next paragraph) the vote went from 182, 119, 360, 88, 146 and 250 (1,145) to 181, 120, 354, 85, 147 and 244 (1,131), the rules alone from 1,132 to 932 persons, and on the two held-back sets, computed once at the end, the vote from 335 to 332 of 362 (v11) and 137 to 135 of 148 (accept5). The 8 kept are those whose removal broke the condition: the subjectless step being the person's, a judgment's bystander, the stage keywords, the wanted notice, the investigation (ALLEGED), the whole release, proceeds confiscated and sentenced by row number. The order under its heading was among the 34 (the vote does as well without it) but stays, because a Golden Case needs it: AUG-02-22, a judgment's order giving the Profile person's own outcome.

Through the system's entry (`SourceConditions.Determine` on an adverse-media Profile with the news attached, the classifier run here and the language model's answers replayed), the rules now give the reference stage for 171, 120, 331, 77, 129 and 212 persons (1,040 of 1,223, before 1,132), and the vote 181, 119, 356, 84, 147 and 244 (1,131, before 1,150 the same way). With the language model asked as the reader asks it, one Profile person per call (it alone gives 1,574 of the 1,733 persons of the eight sets, against 1,589 when asked for every person of a document at once), the vote is 181, 117, 358, 84, 149 and 243, and on the held-back sets, computed once, 336 of 362 (v11) and 139 of 148 (accept5): above 0.90 on every set but accept3, which is below it with all rules too. Run again with these answers, the removal would have kept one rule more, that a stage told in a sentence not naming the person does not replace one told in theirs (the known limit above): without it the switch-off copy reads v9 351 of 390 (0.900), but the system 358, so it stays deleted. **So without stage models the rules alone are now weaker**, most of all on court releases (accept 77 of 93, accept4 212 of 257 where they were 87 and 248): a deployment that configures neither model reads the stage less well than before this reduction, and one with a single model keeps the rules' stage.

**Cost.** Over the 1,130 person-document pairs of the annotated sets (5,110 characters on average), one reading takes 1.4 ms on average and 13 ms at worst, 2.7 ms per 10,000 characters, on a busy developer machine (measured outside the repository through `SourceConditions.Determine`). These were measured with the rules before they were reduced to those the vote needs (below); fewer rules read less. A new rule adds little because the ruling is parsed once and shared by every rule, each decision list stops at the first rule that speaks, and the patterns are generated at compile time. A document is read once for each Profile person it is attached to, so the cost of a Case grows with the number of its candidates. One test guards it: on made-up reports, judgments and releases of about 5,000 characters the reading of a doubled body takes at most 3 times as long, and less than 25 ms per 10,000 characters, a tripwire far above the measure ([LegalStageCostTests](../../tests/Aml.Tests/Unit/Screening/LegalStageCostTests.cs)). It catches a reading that has become mostly quadratic or several times slower, not a small quadratic part, and no test reads hostile shapes (a million repeated names or keywords) through the stage reading: the bound of that kind in [NewsDigestTests](../../tests/Aml.Tests/Unit/Screening/NewsDigestTests.cs) covers the news digest only.

#### Three readers

The rules above are one of up to three readers of the stage. Two models may read it too, each optional and each behind the port `IStageReader` (the news text and the Profile's names in, a stage and an optional quote out, or nothing when it cannot answer); `StageVote` combines what they give.

| Reader | Name on the condition | What it is | Configuration |
|---|---|---|---|
| The rules | `rules` | `LegalStageReading.FromNews`, as above | always |
| Person-stage classifier | `classifier` | `Infra/Models/OnnxStageClassifier`: bert-base-chinese fine-tuned on (document, person) pairs, a 7-way head on [CLS], run with ONNX Runtime | `StageModels:ClassifierDirectory`: a folder with `model.onnx`, `vocab.txt` and `labels.json` (label order, `max_length`) |
| Large language model | `llm` | `Infra/Models/OllamaStageReader`: Gemma 4 12B (q4_K_M) on an Ollama server inside the bank, one `/api/chat` call per document | `StageModels:LlmUrl` (the server's root), `StageModels:LlmModel` (required with it), `StageModels:LlmTimeoutSeconds` (60) |

**The vote.** The readers present are the rules and every configured model that answered:

| Readers present | Stage | `legalStageBasis` | Condition reason |
|---|---|---|---|
| The rules alone | the rules' | the rules' | none |
| The rules and one model, the same stage | the rules' | the rules' | none |
| The rules and one model, different stages | the rules' (a model alone never changes the stage) | the rules' | `STAGE_READERS_DISAGREE` |
| All three the same | that stage | `STAGE_VOTE_UNANIMOUS` | none |
| Two of the three the same | UNKNOWN (two alike are not enough to decide) | `STAGE_VOTE_MAJORITY` | `STAGE_READERS_DISAGREE` |
| All three different | UNKNOWN | `STAGE_VOTE_SPLIT` | `STAGE_READERS_DISAGREE` |

**Why only unanimity decides.** The stage is advisory to the reviewer (the system identifies; humans decide), so what counts is how often an automatic stage is wrong and how much is left to read. Through `SourceConditions.Determine` with the classifier and the language model's one-person answers (as below), all three alike gave the reference stage for 141 of 141 persons (dev), 98 of 100 (dev2), 251 of 255 (v9), 69 of 69 (accept), 87 of 90 (accept3) and 193 of 193 (accept4), and on the held-back sets, computed once, 219 of 222 (v11) and 111 of 113 (accept5): 0.967 or more on every set. Two alike were right far less often (40 of 42, 18 of 24, 101 of 122, 13 of 19, 60 of 71, 50 of 61). So a stage is given only when the three agree, for 52 % (accept3) to 79 % (dev2) of the persons; every other person's stage is UNKNOWN with each reader's stage shown. No reader reaches 0.90 on court releases of accept3's kind on its own or by a two-to-one vote (measured with more court-release training data, a larger language model and a revised prompt); the counts of "the vote" elsewhere in this section are of the earlier two-of-three vote.

`STAGE_READERS_DISAGREE` tells the reviewer to read the stage in the news themselves; it does not change the condition's status or the pair's result (an ADVERSE_MEDIA condition is a HIT whatever its stage, so its pair is in review unless identity says NO_MATCH). With a vote basis, the stage may be UNKNOWN and still carry a basis. Whenever a model is configured, the condition carries `stageReaders`: each reader that answered, in the order rules, classifier, llm, with its stage, the rules' own basis, and the language model's quote with `quoteInText` (whether the quote, all whitespace removed, is in the news text, whitespace removed). A model that was not configured or did not answer is not listed. The explanation shows it beside the stage: `法律階段 UNKNOWN（依據：兩方相同（STAGE_VOTE_MAJORITY））；各讀取者：rules INDICTED、classifier CONVICTED、llm CONVICTED（引文「…」，原文可查到）`. With no model configured nothing of this appears and every output is as the rules alone make it. Without attached news no model is asked.

**A missing model.** A model that is not configured is not registered, and the vote has one reader fewer. One that fails (the ONNX run throws; the language model's server answers with an error, times out, or gives no stage for person 1 in the asked JSON) answers nothing for that Case, and the vote goes on without it: a failure never blocks the analysis, it moves the row of the table up. With only the classifier the rules decide and the classifier can only order review; the same with only the language model.

**The classifier's input** is built as the training code built it (`prep_stage.py`, `train_stage.py`), so that the model sees at run time what it saw in training: the text NFKC-normalised per character; the name is the Profile's first name of two characters or more; the evidence is, in text order, the first 60 characters, every sentence holding one of the Profile's names (sentences end on 。！？；!?; and line breaks), and after each the next sentence when it names no person; each occurrence of a name, longest first and not overlapping, is wrapped in `[unused1]` `[unused2]`. Pieces are joined directly when adjacent, with one space across whitespace and with "…" where text was skipped; over the budget of 512 − 3 − the name's tokens, the head, then the pieces in order up to 60 % of the budget, then the last pieces that fit. The tokens are `[CLS] name [SEP] evidence [SEP]` with token types 0 to the first [SEP] and 1 after, the markers as ids 1 and 2, no lower-casing (the tokenizer was saved with `do_lower_case` false and a BertNormalizer without lower-casing), the name at most 32 tokens. The stage is the label of the highest logit. One part differs from training, because at run time nobody is annotated: there, the next sentence was kept when it held no form of any annotated person; here, when it holds none of the Profile's names and no 被告, 嫌犯, 犯嫌, 嫌疑人 or 嫌疑犯 before a common surname (被告王大同). On the 1,063 rows of the four report sets the builder gives the training evidence exactly when given the annotated persons, and the run-time rule changes the evidence of 563 of them (mostly a following sentence kept that names a victim or witness); read with the run-time evidence the classifier is right for 894 persons where the training evidence gives 901 (dev 171 and 170, dev2 106 and 107, v9 300 and 305, v11 317 and 319). Keeping no following sentence at all gives 899, so the rule matters little either way. Given the training rows' own name and evidence, the .NET tokenizer and ONNX graph give the final epoch's label on all 185 dev rows (an opt-in test, `AML_STAGE_MODEL`, `AML_STAGE_PARITY_ROWS`, `AML_STAGE_PARITY_PRED`).

**The language model's input** is the measured run's: the fixed prompt (`Infra/Models/stage-prompt.txt`, which holds no name) with `{{PERSONS}}` the line `1：` and the Profile's names joined by `、`, and `{{TEXT}}` the news (over 16,000 characters: the first 10,000, a line `（中間省略）`, the last 6,000); `stream` false, `think` false, `format` the JSON schema `{persons: [{id, stage (the seven codes), quote}]}`, options temperature 0, seed 1, `num_ctx` 24576, `num_predict` 4096. The first answer for id 1 with one of the seven codes counts. One part differs from the measured run: there, the prompt listed every annotated suspect of the document with the forms the document writes them by; here it lists the Profile person alone, by the Profile's names. That difference has not been measured. Only the news text and the Profile's names are sent ([security and privacy](security-and-privacy.md#personal-data-boundary)).

**Measured.** Per person, each annotated suspect taken as the Profile person, persons whose stage is the reference's, on the sets measured for the rules above (v11 and accept5 held back: the rules were not written looking at them, and no reader was tuned on them). The classifier is the final epoch of one training run; the language model and the vote were run outside the repository, with the inputs of training and of the measured run (above), not through this code.

| Set | Persons | Rules | Classifier | Gemma 4 12B | Vote (two of three) |
|---|---|---|---|---|---|
| dev | 185 | 178 | 170 | 163 | 182 |
| dev2 | 126 | 122 | 107 | 115 | 119 |
| v9 | 390 | 352 | 305 | 364 | 360 |
| v11 (held back) | 362 | 295 | 319 | 319 | 335 |
| accept (court releases) | 93 | 87 | 79 | 89 | 88 |
| accept3 | 172 | 145 | 112 | 154 | 146 |
| accept4 | 257 | 248 | 230 | 251 | 250 |
| accept5 (held back) | 148 | 140 | 117 | 134 | 137 |
| All | 1,733 | 1,567 | 1,439 | 1,589 | 1,617 |

That table is of the fuller rules, before they were reduced to those the vote needs. Measured since through this code (`SourceConditions.Determine` with the classifier run here and Gemma's answers replayed), with the rules as they are now:

| Set | Persons | Rules | Classifier | Gemma 4 12B | Vote (two of three) |
|---|---|---|---|---|---|
| dev | 185 | 171 | 171 | 163 | 181 |
| dev2 | 126 | 120 | 106 | 115 | 119 |
| v9 | 390 | 331 | 300 | 364 | 356 |
| accept | 93 | 77 | 77 | 89 | 84 |
| accept3 | 172 | 129 | 116 | 154 | 147 |
| accept4 | 257 | 212 | 230 | 251 | 244 |

Where all three agree they are right for 0.967 or more of the persons on every set ([why only unanimity decides](#three-readers)). The vote gains most where the rules were not written against the text (v11: 335 against 295) and costs a little where they were (dev2, accept5): on a court release of fixed form the rules are near exact and the models are what is outvoted. The tables measure the earlier two-of-three vote (column "Vote"); they show that no reader alone is best on every set. That a stage only two readers give alike is too often wrong to stand, and so only unanimity decides, is measured in [why only unanimity decides](#three-readers) above.

**Cost.** The classifier runs one ONNX pass of at most 512 tokens per Profile person and document. The language model's call takes seconds to minutes on the bank's server and runs inside the synchronous analysis; `StageModels:LlmTimeoutSeconds` bounds it and is to be set below `Batch:Timeout` (2 min), else a slow answer makes the batch job time out instead of dropping the reader. Neither model's version is recorded in the revision, only the reader names on the condition (a known limit).

### News digest

Each hit with attached news carries a `newsDigest` in its explanation ([ADR-0012](../adr/0012-sources-and-web-information.md)): the people and legal events the news names, and a summary in fixed sentence patterns. It is read by rules (with the [news models](#news-models) beside it when configured), restates the news and decides nothing: no condition, stage, relevance, identity or disposition reads it.

- **People.**
  - Which mentions count:
    - the Profile's own names (of two or more characters)
    - a name right after a role word (被告王大明, 董事長李四, 前立委林大同)
    - a 2 or 3 character name of a common Taiwanese surname, or a 3 or 4 character name of a compound one (歐陽, 張簡, 范姜 …), that opens a clause before 遭, 遭到, 被, 獲, 涉嫌, 涉及, 坦承, 供稱 or 否認
    - a dotted foreign or indigenous name (約翰·史密斯, 瓦歷斯·諾幹), its first part starting at a transliteration character
    - a name marked 化名 (小美（化名）) and a lettered party (少年A, 證人甲)
    - the anonymised forms 王姓男子, 王姓少女, 王姓業者, 王男, 王嫌, 王董, 王某, also with a compound surname (歐陽姓男子)
  - A name that runs on into the next word (被告王明涉嫌) is cut a character earlier.
  - Words that only start like a name are not names: a second character that makes a common word (許多, 高層, 林務), a third that names an organisation (局, 會, 司, 署, 院 …), or an organisation's word starting inside or right after it (王品集團, 林口分局, 國泰銀行, 媽祖廟).
  - Every other occurrence of a found name is a mention, except a 2 character name running into a longer one (王明 in 王明華).
  - An anonymised form is the person named in full with that surname when the news names exactly one (王大明 … 王男); with two or more (王大明 and 王小華) it stays a person of its own.
  - `match` is `PROFILE_NAME` for the Profile's own names, and for an anonymised form linked so to one; a name that only starts like one (王明 for 王明華) is `OTHER_PERSON`.
  - `match` is `PROFILE_SURNAME` for an unlinked anonymised form sharing the person's surname, which is left to the reviewer. Anyone else is `OTHER_PERSON`.
- **Identifiers** (`identifiers`, `Core/Explanation/NewsIdentifiers`): what the news writes about a person right next to one of their forms, so the reviewer can set it against the customer. They are shown for every person; for the Profile person the birth date and gender are also compared with the customer's as identity evidence ([birth date and gender stated in a source](identity-adjudication.md#birth-date-and-gender-stated-in-a-source)). Nationality, residence, occupation and employer are shown only, and the stated age keeps its own reading.
  - Kinds: `GENDER`, `AGE`, `BIRTH_DATE` (a date or 年次 with 生), `ID_NUMBER` (in `number`, masked as everywhere else, never in `text`), `NATIONALITY`, `RESIDENCE` (a city or county, or an address opening with one, after 住 or 設籍 or alone; not 住院治療 or 前往臺北市), `OCCUPATION` and `EMPLOYER` (a company, bank, office or association, alone or with the job there: 某公司負責人; not a clause about one). An item holding an ID number is read only as one, so the number never reaches `text`. Each keeps the text as written.
  - Where they are read:
    - every item of a bracket right after the name, up to its end or the sentence's, within 200 characters and 12 items: 王大明（男，民國70年1月1日生，身分證統一編號：…，住…）
    - after a comma or a space right after the name, the items that are identifiers by themselves, until the first that is not, an occupation or employer, or a bracket: 王大明，男，45歲，涉嫌… and a judgment's 被告 王大明 男 45歲 (王大明，男，司機 gives the gender only: a bare job word after a comma may open the next clause)
    - a judgment's party lines: the lines right after the name's own line (at most three, the name's line at most 60 characters long), as long as each opens with an ID number or with a residence after 住, 居, 設籍 or 戶籍 (身分證統一編號：…, then 住屏東縣○○鄉○○路1號; a published judgment masks parts of an address with ○). A bare place on its own line is a heading and is not read; an empty line or another party's line ends the reading
    - an age right after the name (王大明今年45歲), an occupation word (王大明檢察官), or the place of a home (王大明位於臺中市西區民生路1號之住處, also 住所, 居所, 住居所, 租屋處, 戶籍地; not when the place names a company, office, factory, bank or shop, or two places joined by 及, 與 or 和)
    - what ends the clause before the name, peeled off one by one: 臺中市45歲男子王大明 gives the gender, age and city; 越南籍阮某 the nationality; 司機王大明 the occupation. Spaces there are dropped, since judgments space out titles (法　官).
    - later in the clause naming the person (up to a comma, since one may open another person's clause), an occupation after 從事, 擔任, 現職, 任職 or 受僱, when followed by 工作 or ending in an occupation word or in 業, 工, 員 or 師 (自陳高職畢業、從事水電工作). Conduct (從事虛擬貨幣買賣), roles in the offence (車手, 收水, 取簿手) and a relative's job (之妻擔任教師) are not occupations.
    - the gender of an anonymised form: 王男, 甲女, 王姓男子
  - Not read: an approximate age or a threshold (約40歲, 未滿18歲), as for the stated age; an occupation that is one of the person's role words already listed in `roles` is not repeated.
  - The model's entities carry the same reading over all their forms.
  - Measured against the descriptors of the annotated judgments and press releases (age, gender word, place, job or title, company and nationality, typed by rule), for the annotated people's own forms. For suspects, precision is 0.59 on the 58 dev documents and 0.77 on the 56 test documents, and recall 0.30 and 0.44 (stopping the 從事 / 擔任 reading at the clause's comma raised precision from 0.55 and 0.72 for 0.03 and 0.02 of recall). Many of the items counted wrong are occupations or genders the annotation left out. Residences are rare in this material: 65 place-like descriptors of suspects in the 1,716 training documents, of which the party lines and the home wording raised the number read from 11 to 21 (the rest are mostly places of an office held or of the conduct, or a home written away from the name); the 12 of the current dev documents were unchanged at 2. Published judgments are de-identified: no ID number and few birth dates are written in them, so those kinds are untested on real text.
  - Known limits: a place before a title is read as the person's (高雄市長 gives 高雄); an employer is read only inside a bracket or after 任職 and the like, not before the name; a name that is a prefix of a longer one (王明 in 王明華) is read at the longer one's places too.
  - The role is the word written just before the name, and `party` follows from it: `SUSPECT` (被告, 嫌犯, 嫌疑人), `VICTIM` (被害人, 告訴人), `WITNESS` (證人) or `OFFICIAL` (檢察官, 法官, 律師, 員警). Without such a word the party is null: nobody is a suspect by default.
- **Events.**
  - The event types (`/api/codes`, kind `NEWS_EVENT`) follow the ACE / Rich ERE Justice events, narrowed to Taiwanese procedure: investigation, search, questioning, arrest, referral (移送, 函送), detention, bail or release (交保, 停止羈押, 釋放), asset restraint (扣押, 凍結, 警示帳戶), indictment (起訴, 提起公訴, 聲請簡易判決處刑), non-indictment, conviction, acquittal, appeal or retrial (上訴, 抗告, 更一審), confiscation (沒收, 追徵), the court's fine (罰金, not 易科罰金), a regulator's penalty (罰鍰, 裁罰, 停止買賣), wanted notice, and extradition or repatriation (引渡, 遣返).
  - They are read clause by clause, outside quoted speech, with the legal stage's own keywords for indictment, dismissal, conviction and acquittal. 起訴書 is a document, not an indictment.
  - `certainty` (after NegEx / ConText and FactBank) is:
    - `NEGATED` after a negation or denial (尚未被起訴, 不予羈押, 不得上訴, 免予), or for a request the court refused (駁回, 未獲准, 無保請回)
    - `HEDGED` after a modal or plan (恐遭起訴, 擬起訴), a question or open possibility (是否遭起訴, 未必, 不排除), or under a condition: a clause opening with 若, 如果, 倘若, 假如 or 一旦 hedges itself and the clauses after it in its sentence, until one opens with 但, 然而, 不過, 卻, 惟 or 只是
    - `REQUESTED` for a request to the court not yet decided (聲請羈押, 羈押王大明的聲請, 聲押); granted (獲准, 裁准, 准予) it is `STATED`. These words are looked for within 200 characters of the keyword in its clause.
    - `STATED` otherwise. 涉嫌 hedges nothing: it is how every report names a charge.
  - `source` is whose claim the event is: the nearest attribution cue before the keyword in its sentence, within 200 characters, unless a contrast (，但 …) follows the cue; a defence cue counts only in its own clause (王大明否認犯行，法院仍判處 … is the report's). `INVESTIGATORS` (檢方指控, 起訴書指出, 警方調查 …), `COURT` (判決指出, 法院認定, 合議庭), `DEFENCE` (否認, 辯稱, 律師表示), `UNNAMED_SOURCE` (據了解, 據悉, 消息指出), else `MEDIA`.
- **Who the event is about.**
  - The actor is, in order of preference:
    1. the name nearest before the keyword in its clause
    2. the first name after it (檢方起訴王大明)
    3. for a pronoun (他, 她, 其, 該男), the person named last before the clause
    4. the person named last earlier in the sentence
  - A person introduced as a prosecutor, judge, lawyer, police officer, witness or victim is the actor only when nobody else is.
  - Otherwise the actor is null, and the summary says 未指明對象.
- **Slots.**
  - The date comes from the event's own clause, as written, and `dateValue` resolves it against the publication date (TimeML's document time, as HeidelTime does for news):
    - 2026年5月6日 as written; 民國 and two or three digit years add 1911 (113年 → 2024)
    - 5月6日, 6日 and 昨（6）日 take the latest such day that exists, not after publication (6日 published on 5 May is 6 April; 31日 published on 1 March is 31 January; 2月29日 is the last leap year's)
    - a term is not a date: 有期徒刑3年6月, 拘役30日, 羈押2月 and 求處10年6月 give none
    - 昨天, 今天, 前天, 去年, 今年, 上月 and 本月 count from the publication date
    - 日前 and 上週 name no day and stay null, as does every relative date when the publication date is unknown
  - The agency, amount and requested penalty come from the clause, else from the sentence. The offences, the penalty and finality come from the sentence.
  - The penalty (徒刑, 拘役, 緩刑, 併科罰金, 易科罰金, 褫奪公權) is read for convictions only, the requested penalty (求刑, 求處) for indictments only; a sentencing request is never a sentence, so 求處有期徒刑10年 is left out of the penalty.
  - 可上訴, 得上訴, 可提起上訴 and 得抗告 say a ruling can still be appealed and give no appeal event; 不得上訴 stays a `NEGATED` appeal.
  - `final` is set for a stated judgment or non-indictment whose sentence says 判決確定, 定讞, 全案確定 or 確定在案, and not 可上訴, 未定讞 or 未確定.
  - The offences found are mapped to risk categories (`offenceCategories`), after the FATF designated categories and 洗錢防制法 第 3 條: 內線交易 → MARKET_ABUSE, 地下匯兌 → FINANCIAL_CRIME, 背信 → BREACH_OF_TRUST, and so on; 公共危險, 賭博 and 傷害 have none.
  - Each event keeps its sentence as the quote, at most 200 characters.
- **Summary.** The lines are:
  1. the report (publisher, date, title)
  2. the people with their roles
  3. one line per person listing their events, one per event type and certainty (a person past the people cap included), in date order when every one is dated, else as reported; each written as date (resolved when it can be), agency, event, then offences, amount, penalty, requested penalty, finality and a source other than the report itself; non-stated events are marked, and a request reads 聲請…（尚未裁定）
  4. a last line for events with no person, or 「報導中未讀到偵查、起訴、判決等事件」 when there are none
  The report line and every event line cite the news source `[S#]`. Every phrase is a slot of a cited sentence; nothing is reworded or added.
- **Limits** (known, kept on the safe side of reading less):
  - a rarer surname, or a name not near a role word, clause opening, dot or 化名, is not found; a dotted name's first part is cut at the first transliteration character of a seed set
  - a name ending in a character that usually follows a name (林志和, 陳建一) is cut a character short
  - asset words opening a clause (金融帳戶, 田產) are kept from being read as names by a fixed list
  - an anonymised form is linked to a full name only when the news names one person in full with that surname
  - a pronoun points at the last person named
  - two people sentenced in one sentence share its penalties
  - several cases in one article are not told apart
- **Caps.** At most 100 Profile names are searched, 500 occurrences of each name and 2,000 mentions kept, 1,000 event keywords looked at, 50 people and 100 events listed; slots are read from the first 2,000 characters of a sentence or clause. The reading is linear in the text: slots are read once per clause and sentence, and patterns holding an open run (dates, amounts, penalties) are non-backtracking.

### News models

When `NewsModels:Directory` is configured, the digest also carries `model`: what the two small models read from the same news ([ADR-0012](../adr/0012-sources-and-web-information.md), [ADR-0015](../adr/0015-licences.md)). It sits beside the rule results, never mixes with them, and like them feeds no decision. Without the setting `model` is absent and the digest is read by rules alone.

- **Models.** Both are `bert-base-chinese` fine-tuned on the bank's annotated judgments and press releases and exported to ONNX; the directory holds `ner/` and `ev/`, each with `model.onnx`, `config.json` and `vocab.txt`, outside the repository. `Infra/Models/OnnxNewsModel` runs them with ONNX Runtime. Its tokenizer reproduces the training one (Hugging Face BertNormalizer without lower-casing, BertPreTokenizer, WordPiece) and keeps each token's place in the text; each character is first NFKC-normalised when that keeps it one character. The tagger reads the text in windows of 510 tokens every 384, averages each token's label probabilities over the windows holding it, and decodes the most probable valid BIO sequence (Viterbi: an `I-` only after a `B-` or `I-` of its role) into spans with a role (`PER-` or `ORG-` with `Suspect`, `Victim`, `Official` or `Other`). A NER config may explicitly set `aml_ner_ascii_lower: true` to lowercase ASCII letters after NFKC and before tokenization, preserving original offsets while keeping uppercase party codes distinguishable. Enable this only for a checkpoint trained with the same feature mode. The default remains the original cased tokenizer, and event inputs keep their existing normalization. The model lineage adds `:ascii-lower` for that input mode so identical weights with different features have different versions.
- **Optional document roles.** A NER config may enable aml_ner_document_roles=true with document-roles.onnx. Its aml_ner_document_role_labels list has eight entries: O followed by the seven role names, in the NER tag order. That encoder graph also emits its last hidden states. A first greedy pass groups predicted spans by the exact case-preserved, normalized source form within a document. Each mention contributes its mean encoder hidden state and eight mean O/role probabilities. Learned masked attention pools all mentions of a form, including other windows, and predicts eight additive logit biases; each role bias applies equally to its B and I tags, and tokens outside first-pass spans keep their probabilities. The normal runtime BIO decoder then reads the adjusted probabilities. Standalone acceptance still uses the final greedy exact-span-with-role result. The role graph hash joins the model version. Missing flags retain the original behavior. This component is experimental and is not enabled in deployed models; candidate selection is documented in [news model training](../plan/news-model-training.md#current-candidate-choice).
- **Optional event document context.** A model config may enable aml_event_document_context with version1, version2 or version3. NewsModelReading supplies the full source document in addition to the original entity/sentence pairs. A frozen matching NER encoder supplies predicted role probabilities and hidden states for all exact entity-form occurrences and the document prefix. Its ONNX graph and the paired NER graph/config/vocab hashes are required. Calls without a document fail for this mode; legacy models use the original pair-only behavior. Context stays local to the call. Version1 learns additive event-logit biases from these observations. Version2 also appends three source-only flags to each observation: inside the queried sentence, nearest preceding mention, and nearest following mention. It keeps every verbatim occurrence of repeated query sentences, leaves the prefix flags zero and derives no event labels from these positions. Per-query flags are added after the document/form feature cache, so one sentence cannot reuse another sentence's flags. Version3 keeps these flags and query-conditioned attention, but classifies only the pooled document observations, without adding legacy event logits or concatenating the query into the output classifier. Its lineage adds `:context-only` so caches cannot mix with additive heads. These are experimental model contracts, not enabled in deployed models; their acceptance and diagnostic evidence are described in [news model training](../plan/news-model-training.md#diagnose-an-error-at-the-boundary-where-it-arises).
- **Optional caption observation.** Event configs may set aml_event_caption_version=1. The normalized source before the first own-line disposition heading supplies a literal-form present/absent/unknown observation; one reserved token follows the unchanged marked query when its 512-token budget has room. This observation is input to the learned classifier, never an automatic prior or identity decision. Missing captions and empty forms are unknown; full inputs retain every original token. This mode requires the source document, has a distinct event version/cache identity and cannot be combined with the separate event context head. Unsupported versions fail. Models without the setting retain their original inputs and overload behavior. The feature must match the checkpoint's trained input mode. It remains experimental; deployed models are unchanged.
- **Choosing.** Each setting below was chosen on the 58 dev documents and reported on the held-out test documents, except the carry-over, which was kept on both sets together (below).
- **Corrections** of the tagger's known errors (`Core/Explanation/NewsModelReading`), each kept only because it helped:
  - quote marks and brackets at either end of a span are not the name (「王大明」), nor is a role word before a person's name (被告王大明), when two characters or more remain
  - a one-character span is dropped, and so is an organisation ending in 戶 (帳戶, 人頭戶: an account, not an organisation)
  - a well-formed name is two to four ideographs opening with a common surname; one holding exactly one other well-formed name of three or more characters is its form (王大明等 → 王大明), while a two-character name inside a three-character one stays apart (王明, 王明華)
  - any other personal span inside well-formed names, or around one of three or more characters, that all map to one person is that person's form (大明 → 王大明); one related to two people (大明 with 王大明 and 陳大明) is dropped; one related to none stays itself
  - a name of another shape (a transliterated or indigenous name, a lettered party, a Latin name) anchors itself: a shorter span inside exactly one such longer name is its form (莫伊 → 阿莫伊斯達, not the reverse). Measured end to end: dev 0.576 → 0.579, test 0.598 → 0.604, with fewer events on names no annotation covers.
  - a bare role word the tagger took for a name (被告, 告訴人, 上訴人 …) is the one person of that party when the text has exactly one, else dropped. A blind check of the dev end-to-end errors found 48 of the 61 events on names no annotation covers were on such words. Measured end to end: dev 0.579 → 0.601, test 0.604 → 0.621 (dropping them always: 0.588 and 0.611).
  - a short organisation name inside exactly one longer one is its abbreviation
  - the role is the majority over all mentions of all forms; party `OTHER` becomes null
  - tried and left out: joining touching spans (it glued neighbouring names), the rule digest's role word over the tagger's role, the rule events added to the model's, 前科 / 累犯 / 曾因 patterns for prior records (each made the results worse or found nothing more), and a role vote weighted by probability (one more role right in 443, not worth the code)
- **The Profile person.** The news is read for one known person, so the Profile's names are not left to the tagger alone: when no tagged person has one of them as a form, but exactly one has a form that holds or is held by a Profile name written in the text (the tagger cut the name short or took a word too many), that person is the Profile person, and the Profile's names of two or more characters written in the text become their forms, asked before the tagger's. A two-character name inside a three-character one is another person (李明 in 李明華); an organisation is never taken; with no such person or several, nothing changes, so a Profile name the tagger missed altogether is still not read. Measured the way the reading is used, with each labelled suspect in turn as the Profile person and only that person's result counted: the legal stage their events imply (convicted, acquitted, not indicted, indicted, wanted, alleged, none) agrees with the labels for 0.886 to 0.892 of 176 persons on the dev documents and 0.952 to 0.976 of 125 on the second set; their events F1 0.831 to 0.832 and 0.861 to 0.876. Counting whole-case steps for the Profile person without an event of their own was measured and left out: it raised the first set to 0.903 and lowered the second to 0.864 (steps given to persons the text only mentions).
- **Entities.** Each has its name (the full name when it has several forms), its other forms, whether it is an organisation, its party, whether a form equals a Profile name, its mentions, and for a person the [identifiers](#news-digest) written next to any of its forms; the Profile person first, then by mentions, at most 50.
- **Events.**
  - Each sentence (up to 。！？；!?; or a line break) holding a form of an entity is classified for that entity, with the form as written there, as `[CLS] form [SEP] sentence [SEP]`: the sentence cut to 460 characters around the form, every occurrence of the form in it wrapped in two marker tokens (`[unused1]`, `[unused2]`) so the model knows which of the people named the question is about, the whole cut to 512 tokens, as in training. So is the one sentence after a sentence naming a person, when it names no person (an organisation may be named), for the person named last, and only when that person is a suspect: a judgment goes on about the defendant without the name (原審判處…), while after a lawyer, an official or a victim the next sentence is not about them.
  - A step of the whole case is written without the names (案經…移送偵辦, 全案偵查終結), or with one name for a group (王○○等5人, 等人, 等被告). So each sentence that names no suspect, and each that names one for a group, is also classified for every suspect person it does not name, by the person's name. Three limits keep such a sentence from giving its step to the wrong person:
    - in a judgment that lists its defendants before 主文, only for those defendants; in a court's press release of its ruling (known as for the stage, above), only for the suspects it writes somewhere as 被告 and the name, not 同案被告 or 另案被告: the principal offender or co-actor it names without 被告 is not who its 被告 sentences are about;
    - in any other text, only for a suspect who already has an event from a sentence about them; a suspect the text only mentions (a contact, an accomplice tried elsewhere) gets none. A listed defendant needs no such event (0.807 to 0.815 on the dev documents, the second set unchanged); dropping the condition everywhere cost the second set 0.02;
    - when more than five suspects are read this way, only investigation and referral count: an unnamed search, arrest or seizure in a large case is about some of them, and the text does not say which.
  - A sentence that names a suspect without group wording (警方將王○○移送偵辦) is classified for the other suspects as well, under the first two limits, but gives them only investigation and referral: in a text about several suspects the referral is often everyone's, while the arrest or seizure in the same sentence is the named person's. These questions do not count towards the five suspects of the third limit and come last under the classification limit below. Measured with the rules above in place and one dev label corrected: 0.849 to 0.851 on the dev documents (four more events found, three wrong ones added), 0.877 to 0.881 on the second set (four found, one wrong).
  - A judgment's order (主文 up to the 事實 / 理由 heading) is written to a fixed form and is read by rule, not by the model: which listed defendants it sentences (處有期徒刑, 拘役, 罰金 …, 免刑) or acquits (無罪), and whom it fines (處 / 科 / 併科 … 罰金, not 易科罰金) or orders confiscation against (沒收 or 追徵, not 不予 / 不 / 毋庸 / 無庸沒收); the listed defendants are the suspects named before 主文, and a sentence of the order naming no defendant goes on about those of the sentence before only (a confiscation written once for all after the last defendant's sentence is read as that defendant's). A defendant the order sentences or acquits keeps a fine or a confiscation from the model only when the order states it for them: the reasons discuss the co-defendants' fines and confiscations and the law in general, and a whole-case question gives them to every defendant. An order that does not decide the defendant (上訴駁回), a text without 主文 or without the heading after it, and every other step are left to the model. Against the training labels, of 383 defendants an order sentences or acquits the labels hold one fine and nine confiscations (from earlier cases) that the order does not state, and the order states none the labels lack. Measured with the rules above in place: 0.851 to 0.854 on the dev documents (five wrong events removed), 0.881 to 0.884 on the second set (three removed), no event lost on either.
  - An appeal filed and dismissed is one event, the dismissal (the annotation guideline's rule). The filing sentence (被告不服提起上訴) does not say how the appeal ended, so the model answers both; the filed appeal is dropped when the dismissal is at least 0.95 and no less likely than it. Measured end to end: on 40 court press releases 0.612 to 0.681 together with the release's defendants above (extra events 65 to 38, found 124 to 131); on the dev documents three wrong events removed for one right one lost (0.863 to 0.864), the second dev set unchanged. Dropping the filed appeal whenever a dismissal is found would lose six right ones on the dev documents.
  - A sentence that writes an entity's forms only before 的 / 之 and a relation word (林美玲的丈夫王大明遭起訴) is not classified for that entity: it names them to say whose relative the other is (the same word list as the stage rule). Found on a made-up Case, where the event model gave the Profile person her husband's indictment at 0.98; on the two dev sets the rule changes no counted event (0.854 and 0.884 as before).
  - At most 1,200 classifications per article (about 80 s at most, beside the tagging): the sentences about an entity first, in text order, then the whole-case ones, then those of sentences naming another suspect. Raising the limit to 2,000 changed no result on either dev set.
  - Measured against every annotated event of the 161 dev documents and of a second dev set of 29 group-case documents never used in training, with the event model trained on whole-case rows: reading whole-case sentences in large cases under the third limit moved F1 from 0.785 to 0.794 and from 0.786 to 0.830 (without that limit the second set fell to 0.773); group sentences moved it to 0.807 and 0.844. These rules, the one for sentences naming another suspect and the one for the order were chosen on these two sets; the held-out test documents have not confirmed them.
  - The labels are the `NEWS_EVENT` types with `NEGATED` or `REQUESTED` where the label says so, and `PRIOR_RECORD` for a prior case (前科, 累犯), which only the model reads.
  - An organisation gets only the steps an organisation can undergo: investigation, search, indictment, conviction, fine, regulatory penalty, asset restraint and confiscation; never arrest, questioning, detention or bail.
  - Over an entity's sentences a label's probability is that at least one says it (noisy-or, 1 − Π(1 − p)). A label is listed at 0.3 or more, with that probability rounded to two places and the sentence most likely to say it, at most 200 characters and never cut inside a surrogate pair; ordered by probability.
  - Tried and left out: a threshold per label (tuned on dev it overfitted: end to end dev F1 0.599 against 0.576 but test 0.597 against 0.598), one certainty per type and sentence (no change), carrying over two or three sentences, carrying further only into sentences with a referring word (被告, 其, 他, 該男: dev 0.578 at two to five sentences against 0.579, worse at eight; most missed events sit in sentences that name another co-defendant or none after someone else was named), and listing events only for suspects, for anyone but officials, or for entities mentioned twice (all within noise).
- **Time.** On the PoC machine (12 cores, under other load) tagging takes about 1 s per 1,000 characters and each event classification about 65 ms, so a 2,000-character article takes 3 to 5 s and a long judgment up to about 30 s (a text with many suspects can reach the 1,200-classification limit, about 80 s more, which with a long text comes close to the default timeout), within a batch job's `Batch:Timeout`; the models rerun on every re-analysis. Measured and left out: int8 quantization (about 1.2× faster on this CPU, which has no VNNI instructions, and fewer people found), the BERT graph fusion of ONNX Runtime's optimizer (no faster), padded batches of event classifications (slower on a CPU), and one model that tags and reads events from a single encoder pass (dev end to end F1 0.572 against 0.576, 97% → 96% of people found, only 23% faster).
- **Lineage.** `newsModelVersion` in the revision's lineage names both model files by the first 12 hex digits of their SHA-256, each followed by the input options in force (such as `:ascii-lower`), when the models read an article of a hit, else it is null.
- **On a Case.** What the system owes is the decision on one known Profile person and its explanation, so the models are judged there first ([NewsModelCaseTests](../../tests/Aml.Tests/Integration/NewsModelCaseTests.cs), through the service and the store): the same Case analysed with and without the models is equal in every stored value (results, conditions, legal stage, evidence, explanation, rule digest, quality, lineage) except the readout itself and `newsModelVersion`, even when the model says something the text does not. With the trained models (run when `AML_NEWS_MODELS` names their directory; skipped otherwise, since they live outside the repository) the Profile person is the first entity, flagged with the Profile's name and read as a suspect, the step the news states for them is listed, no conviction or acquittal is, and the rules' legal stage says the same. Four more made-up reports are read the same way: a group arrest and referral (林○○等3人) and a decision not to indict, where models and rules agree; the Profile person as the victim of another person's indicted fraud, where the models read them as a victim with no step and the rules give no stage; and a two-defendant judgment, where the models give the Profile person the conviction and the co-defendant the acquittal and the rules read CONVICTED from the [order](#stage-from-attached-news). These reports found two faults, both in the rules and both fixed: another person's indictment read as the Profile person's, and a judgment's order not read at all. Five further kinds are read the same way: a sentence in a report's own words (判林○○有期徒刑二年), an acquittal (判林○○無罪), a reversal on appeal, a wanted notice, and the Profile person's husband indicted, where no step is the Profile person's. They found one fault in the pairing of the model's questions (the relative's indictment asked of the Profile person; fixed by the relation rule above) and several wordings the stage rules did not read (fixed, see [Stage from attached news](#stage-from-attached-news)).
- **Measured** for the first shipped pair, on its split. Candidate selection, the fixed 161-document dev split and the reserved final-test protocol are described in [news model training](../plan/news-model-training.md#acceptance-has-three-gates); this section is refreshed when those models ship. Measured on 49 annotated documents never used in training or tuning (people per document; events per document, person and label). Two event measures:
  - *given sentences* scores the event model on the annotated (person, sentence) rows. These include sentences that do not name the person, which the system cannot know to read.
  - *end to end* runs the whole readout on the text and matches its entities to the annotated people by their forms. An event on an entity matching no annotated person counts as wrong. This is the number that matters.

| | Found | Role right | Extra names | Events, given sentences: P / R / F1 | Events end to end: P / R / F1 |
|---|---|---|---|---|---|
| tagger (most probable tag per token, one-character spans dropped) | 382 / 394 (97%) | 371 / 382 (97%) | 111 | 0.82 / 0.83 / 0.83 (highest sentence) | |
| with valid-sequence decoding and the corrections | 384 / 394 (97%) | 371 / 384 (97%) | 49 | 0.82 / 0.85 / 0.84 (noisy-or) | |
| and the sentence after carried over | | | | | 0.65 / 0.65 / 0.65 |
| rule digest alone, for comparison | 82 / 394 (21%) | 80 / 82 (98%) | 14 | 0.34 / 0.22 / 0.27 | |

  - On the 58 dev documents, carrying one sentence over raised the end-to-end F1 from 0.545 to 0.576; on test it moved from 0.611 to 0.600, more recall for less precision. Over both sets together it gains (0.571 to 0.586), so it is kept. With the corrections above, on all 56 test documents the end-to-end F1 is 0.62 (P 0.63, R 0.61), and on dev 0.60 (P 0.68, R 0.54).
  - Of the events the readout misses on dev, almost half (75 of 166) sit in sentences it never asks about (most name no one or another co-defendant), and the rest in sentences it asks about where the event model says no.
  - Of the end-to-end false positives, about a quarter are events on names no annotation covers (21 of 92 on dev, 27 of 97 on test).
  - The rules were written for news; the test documents are judgments and press releases, so the rule row says little about news.
  - Remaining gaps: organisations missed, indictments and referrals stated without the name, prior records, and people tagged as `Other` read as suspects. Closing them needs retraining on rows chosen as the system chooses sentences, not more rules.

## Policy responsibilities

### OfficialListPolicy

Reads authority, list type, list status, decision number/date and alleged offence from approved fields. An official list whose approved fields state an active wanted or arrest list becomes `OFFICIAL_WANTED_LIST` with `legalStage: WANTED` (GC-03); any other official list stays `OTHER_OFFICIAL_LIST`.

### AdverseInformationPolicy

Reads approved source classifications and keywords. `Financial Crime` does not automatically mean `Fraud`.

Sets `amlRelevance` to `UNCERTAIN`, or to `UNRELATED`, which drives Article 22 ② ([disposition recommendation](review-and-disposition.md#article-22-2-adverse-news-unrelated-to-mltfpf)). RELATED is left to the reviewer. UNRELATED is a heuristic on controlled conduct lists, pending an approved extraction validated by a Golden Case; it requires all of:

- attached news that states a jurisdiction: a dateline (〔記者…／台北報導〕, 中央社台北6日電) or `Country of Reported Allegation`;
- a sentence naming the Profile person that states a non-predicate conduct (酒駕, 酒後駕車, 交通事故, 過失傷害, 傷害罪, 妨害名譽, 誹謗, 公然侮辱, 性騷擾, 家暴, drunk driving, defamation, traffic accident); another party's conduct does not count;
- no predicate conduct and no serious-consequence marker anywhere in the news;
- no SIP crime category or risk keyword on the Profile, since each of them is a predicate offence.

Predicate conduct follows 洗錢防制法 (Money Laundering Control Act) Article 3: offences with a minimum sentence of six months or more, plus the listed statutes. The controlled list covers fraud, money laundering, tax evasion, corruption and bribery, breach of trust, embezzlement, drugs, smuggling, human trafficking, organised crime, terrorism, insider trading and market manipulation, underground remittance, illegal deposit-taking and the Banking Act, false invoices, forgery, homicide, robbery, snatching, kidnapping for ransom, extortion, theft, firearms, sexual offences, the Securities and Exchange Act and intellectual-property offences (copyright, trademark). The serious-consequence markers (致死, 致人於死, 致重傷, 逃逸, hit-and-run, death) cover drunk driving or an accident that kills, gravely injures or is followed by flight (刑法 185-3 II, 185-4): those carry a minimum sentence of six months or more and so are predicate offences whatever the conduct word.

## Subject attribution

Profile-level conditions apply to the Profile subject only when source structure supports that attribution. People or companies merely mentioned in notes are not automatically assigned the Profile's risk category.

A news item attached to a Profile counts as attached news; the Profile's own description does not. Bank-internal list notes are not read ([open questions](../plan/open-questions.md#data-and-source)).

## Source versus customer result

- Profile category HIT remains a source fact, recorded as `riskCategories` on the [SourceConditionResult](data-contracts.md#sourceconditionresult).
- Pair results follow the canonical [combination matrix](data-contracts.md#combination-matrix), including UNKNOWN and NOT_EVALUATED.
- Only identity MATCH plus source HIT produces pair-level HIT.

## Risk scoring

`RiskScoring` runs downstream of identity and never feeds back into it ([ADR-0008](../adr/0008-validation-and-deferred-capabilities.md)). It needs a bank-approved `RiskPolicy`; none ships, so by default no pair is scored. A pair whose identity is NO_MATCH is not scored. Otherwise:

- HIT conditions are scored; an UNKNOWN Sanction or PEP condition scores as a HIT, other UNKNOWN conditions score nothing;
- an ADVERSE_MEDIA condition set aside by Article 22 ② (`amlRelevance: UNRELATED`) scores nothing;
- each scored condition adds its list weight (`list:<type>`) and the weight of each of its risk categories; the legal-stage weight (`stage:<stage>`) applies to ADVERSE_MEDIA conditions only;
- any scored Sanction condition makes the level High whatever the weights; otherwise the policy thresholds decide High, Medium or Low.

## Phase 1 constraint

General free-news event understanding is out of scope. Structured Profile fields, bounded Notes sections and explicit citations are used; no LLM is involved, and no model takes part in any decision ([ADR-0001](../adr/0001-identity-adjudication.md)). The [news digest](#news-digest) reads people and events from the news by fixed rules, and the [news models](#news-models) beside it, for the reviewer only ([ADR-0012](../adr/0012-sources-and-web-information.md)).

When news content is used:

- every attached article is listed; a fact stated by one article is never generalised to "all reports";
- rewording never swaps actor and object, instructor and instructed, payer and payee;
- the event date is not the publication date and not a later court date;
- several reports of the same event (same party, event time and charge) are one event and one piece of evidence.
