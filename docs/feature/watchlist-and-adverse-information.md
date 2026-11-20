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

The ADVERSE_MEDIA stage is read from the attached news by a keyword heuristic, and from a judgment's order by its fixed form. Both await a judgment Golden Case; until then they are checked on augmented cases (AUG-02-22) and made-up reports:

- **A judgment's order.** When the text has a 主文 line, the lines from it to the 事實 / 理由 heading are the order, and what it says of the Profile person is the stage: a sentence (處有期徒刑, 拘役, 罰金, 無期徒刑, 死刑, 免刑) in any part is CONVICTED, else 無罪 is ACQUITTED. Only a clause where the person is the subject counts (their name opens it, alone or in a list of names: 林美玲、王大明均無罪), and what follows up to a clause that opens with another person's name of two to four characters (林美玲處有期徒刑一年，王大明無罪). So a co-defendant's sentence or acquittal is never theirs, nor is an offence against them (王大明對林美玲犯詐欺取財罪，處…) or a part of an earlier judgment being revoked (原判決關於林美玲處有期徒刑部分撤銷). A numbered heading (一、犯罪事實) ends the order too, and without any heading 40 lines are read at most.
  - The order's forms that are read as well: an item number before the name (一、林美玲犯…), a penalty given by reference (處如附表一所示之刑, 處如本院附表編號1所示之刑), and names written in full-width letters or digits where the Profile writes half-width ones (or the reverse).
  - An order that says neither of them (免訴, 公訴不受理) gives no outcome. A listed defendant then stands INDICTED unless the keywords below say more. Listed means: named before 主文 on a 被告 or 上訴人即被告 line, or on the lines after it up to the next party's line (辯護人, 上列, 代理人, 檢察官 …); a defence lawyer named there is no defendant.
  - 無罪部分 that the order revokes names a part of the judgment below (原判決關於林美玲沒收部分、王大明無罪部分均撤銷), not an outcome; 林美玲無罪部分，檢察官上訴駁回 upholds the acquittal and is ACQUITTED.
  - 上訴駁回 on the appeal of an 上訴人即被告 (also written 上訴人 on one line and 即被告 with the name on the next) is CONVICTED: a defendant appeals a conviction only. When the prosecutor is the appellant and the person a plain 被告, the outcome below is not told and the person stands INDICTED.
  - **A court's press release of its ruling** has no party lines, and gives the outcome under a heading of its own, in its opening or in its reasons, in the order's own form. It is known by that heading, or by an opening (its first 8 lines) in the court's own voice: 本院, a case number and 宣判 or 判決 (本院114年度訴字第1號被告…案件，於今日宣判). A news report may cite a court and its case number (臺灣高等法院113年度上訴字第5號) but never says 本院, and is read as a report. The heading is a line of its own: 主文, optionally with an item number 壹、 or 一、, 本院 and 判決 before it (壹、判決主文：, 主文要旨, 本院判決主文:), or 判決結論 / 判決結果 with its item number (壹、判決結論:; without the number it may be a news report's own line and is not a heading). 要旨, 摘要 or 摘要說明, a colon and a bracketed note may follow (壹、主文摘要:(一審判處有期徒刑4年)). The item number of a heading may be 壹、 貳、 參、 or 一、 二、 三、. What stands under the heading is read first, to the next heading of its series (a line opening 壹、 貳、 …; under a heading numbered 一、, the next short line numbered that way), and what it says of the person is the outcome. A release with no heading, or one that says nothing of the person under it, is read whole (400 lines at most). Either is read as a judgment's order is, with these differences:
    - What is asked for or what the law allows is no outcome: a clause with 請求, 求處, 求刑, 聲請, 應處, 得處, 可處, 最重 or 法定刑 is passed over (檢察官請求處有期徒刑五年). A clause whose subject is someone else ends what is said of the person: after 被告, 判決, 改判 or 認, a name of a common surname that is not theirs (本院另判決被告李小四處有期徒刑).
    - A line that ends no sentence runs on into the next, since such a page may break a line inside a sentence or a name; not into a line that opens a new item (一、, ㈠, (一), 1.), and not from a heading (the order's heading, or an item number and at most 24 characters with no comma) or a table cell (a line of at most six characters with no punctuation). ； joins outcomes inside an item (陳志明被訴…部分，公訴不受理；其餘被訴部分無罪) and is read as a comma.
    - The ruling may stand in running text: before the name a clause may say 判決, 改判, 判決主文：「 or 認, after 經, 本院, 合議庭 or 國民法官法庭 or not, with 被告 or not (判決主文：「陳志明犯傷害致死罪，處有期徒刑九年」, 判決被告陳志明、李小四均犯…, 經本院認被告陳志明犯殺人罪，處有期徒刑14年); never 原判決 or 一審判決, which recall the ruling below.
    - A table of defendants, which the page's text holds one cell a line: the person's name alone on a line, with a row or item number before it and a bracketed title or a colon after it (2 陳志明, (十一)陳志明：), then the lines of their row. The first penalty among the next 12 lines is theirs (a cell opening 有期徒刑, 無期徒刑, 拘役, 死刑 or 罰金, or a sentence of the order's form) unless 無罪 comes first; a row number with a name, a short line ending in a colon, a heading, or a line about someone else (李小四處有期徒刑二年, a 被告 line without the person's name) ends the row. The table is consulted only when no sentence gives the person's outcome. Lines that are only another name are passed over, since defendants sharing a penalty cell stand one under the other. Cells merged the other way are not told apart.
    - An order item may be numbered ㈠, (一) or 1. and a list of names may go on after 與 and hold spaces (㈠、李小四等負責人，與王大同、 陳志明等共同犯…罪，各處如附表所示之刑期); a penalty by reference may be 處如該欄所示之刑, and 應執行有期徒刑, 拘役 or 罰金 is a sentence too (陳志明犯…等罪，應執行有期徒刑7月). These forms are read in a judgment's order as well.
    - Its defendants are the people its opening writes as 被告 and the name; the opening runs to where the release says 宣判 (within its first 8 lines), else to the heading. The person is the only defendant when every 被告 of the opening is followed by one of their names (下稱被告 aside), and the name by no 等, 、, 及 or 與.
    - A dismissed appeal (上訴駁回, 其他上訴駁回, 駁回其上訴) is CONVICTED when a sentence of the text names the person (or says 被告, for the only defendant) with 判處 or a sentence of the order's form, since the release recalls the ruling below (前經…法院判決「陳志明犯詐欺取財罪，處有期徒刑一年」), or names them as the part dismissed (其他上訴（即被告王大同、陳志明部分）駁回). With several defendants and no name in such a sentence the person stands INDICTED. Not told apart: a prosecutor's dismissed appeal against an acquittal.
    - The forms of a large case. An item goes on about the person in a sentence with no subject, opening 應執行 or 又 (陳志明共同犯交付賄賂罪，共4罪。應執行有期徒刑1年2月); a heading ends that. A line closed by a bracketed note after a full stop does not run on into the next (…處有期徒刑2年。（刑度與原審相同）). The carrying on stops after an acquittal, 不受理 or 免訴, and 又 carries only as 又犯 or 又共同犯. Rulings may be numbered ①, ② after 判決, with 及 or a date before (於114年6月9日判決①被告李小四…；②被告陳志明…), and a clause may open 另 (另陳志明部分改判無罪). 陳志明緩刑3年 is a conviction, since probation is given on one, only when 緩刑 and its years stand right after the person's own name; 緩刑部分撤銷 alone is not. A clause with 前經 recalls the ruling below and is passed over as a request is. Defendants sentenced by row number: when the text writes the person with a number (陳志明（編號3）) and a sentence of the order's form under the heading covers that number in brackets (各處有期徒刑7年（編號1）、5年（編號2至3）), the person is CONVICTED; not by an unbracketed number (附表編號2 of an exhibit), and not in a sentence that also says 無罪, 原審, 第一審, 上訴 or 前經. A name followed by a bracket saying the person is tried elsewhere (（本院審理中）, （由本院另案審理）, （另行審結）) is not this ruling's subject, and with no outcome read the person is INDICTED. The only defendant's own dismissed appeal (被告提起上訴 or 被告不服…提起上訴 in the opening, and a line under the heading that is 上訴駁回 and nothing more) is CONVICTED, as 上訴人即被告 is in a judgment; 被告提起上訴 in the opening does not name a further defendant.
    - Not read: a table whose rows the page's text breaks into fragments, a person the order names only for the confiscation of their proceeds, outcomes joined inside one clause (李小四處有期徒刑3年、陳志明處有期徒刑2年), a name with a bracketed alias inside a list, defendants written only as masked forms that several of them share (王○○), and a release with no heading whose text goes on to recall the indictment after the outcome (the last stage wins, below).
  - **Anyone else the judgment names** (a witness, a victim, an account holder, an accomplice tried elsewhere) gets only what a sentence naming them says, and a conviction, an acquittal or 提起公訴 only from the clause naming them: the judgment's subjectless 起訴 is its defendants', 起訴書, 起訴範圍 and 起訴意旨 are the case's papers, 調查 or 偵辦 somewhere in the text does not make them ALLEGED, and 被告依陳志明指示取款，量處被告有期徒刑七年 sentences the defendant. 共犯陳志明業經另案起訴 and 共犯陳志明業經本院另案判處有期徒刑二年 are still theirs.
  - **The only defendant.** When the 被告 blocks before 主文 hold one line in all and it is the person's (被告, 上訴人即被告, 聲請人即被告 or 抗告人即被告), the text's bare 被告 is that person: the order may say 被告無罪 without the name, and a sentence with 被告 and no word for another party (同案, 共同被告 …), another or an earlier case (另案, 該案, 他案, 前案, 前因, 前科) or the law in general (規定, 條第) is read as naming them, so a ruling's 被告…業經本院判處有期徒刑三年在案 is CONVICTED. There a clause opening with 處有期徒刑, 拘役, 無期徒刑 or 死刑 (認被告犯…罪，處有期徒刑二年) is a sentence too; with several defendants neither is read. In any text such a clause is the person's when its sentence says whom the court ruled on, 判決 then 被告 or not and the name (判決被告陳志明犯傷害罪，處有期徒刑一年六月).
  - Not read: an order sentence that goes on about the person without naming them again, a co-defendant of the same surname, and text after the order that overturns it (the order wins over the rest of the text).
- 提起公訴 is an indictment only in a sentence naming the Profile person: every judgment says 經檢察官提起公訴 of its defendants, whoever else it names. 不提起公訴, 不予提起公訴 and 未提起公訴 are no indictment, and 對李小四提起公訴 is another person's under the rule below.

- The news is split into sentences (。！？； or a line break) and clauses (，). A sentence names the Profile person when it contains one of the Profile's names of two or more characters.
- A judgment (CONVICTED, ACQUITTED) counts only in a sentence naming the Profile person: a co-defendant's sentence is not the subject's (GC-12).
  - CONVICTED: 判處, 判刑, 判決有罪, 有罪確定, and 判 with the penalty itself, as reports write it: 判陳志明有期徒刑三年, 遭判無期徒刑, 重判十五年, 判1年6月, 改判有期徒刑二年. Between 判 and the penalty (有期徒刑, 無期徒刑, 死刑, 拘役, 罰金, or a number of years or months) only 決 / 處 and a name (他, 她, or two to four characters) fit. 審判, 裁判, 談判, 誤判, 研判, 判斷, 判決前三年 and 宣判時間 are other words (一審判他五年 and 原審判陳志明有期徒刑 are sentences), and 判賠 names no penalty.
  - ACQUITTED: 無罪 shortly after 判決, 改判, 宣判 or 獲判 in its clause (宣判陳志明等三人均無罪), after a bare 判 and a name (判陳志明無罪), after an instance or a reversal (二審逆轉無罪), or as 無罪定讞 / 無罪確定. 堅稱無罪 is a claim, 應判無罪 a plea, 無罪推定 a presumption, 撤銷無罪判決 no acquittal, and in 判陳志明罰金、李四無罪 the acquittal is the other person's.
  - 定讞 alone settles what came before it: after an indictment, or with nothing told, it is CONVICTED; 不起訴處分，全案定讞 stays DISMISSED and 判決無罪，全案定讞 stays ACQUITTED. 定讞 said of someone or something else (共犯李小四日前定讞, 同案被告已定讞, 另案定讞) or not yet reached (定讞前) settles nothing.
- 聲請簡易判決處刑 is an indictment in its summary form. 起訴罪名 and 起訴法條 are headings, not a step.
- 通緝 in a sentence naming the person is WANTED, but only until a judicial stage is told, before or after it: 起訴後棄保潛逃遭通緝 stays INDICTED. Outside a judgment, a wanted notice in a sentence about a group (李小四等3人潛逃出境，發布通緝) is everyone's, unless the sentence points at someone else (同案, 另名 …). 提起公訴 of a group is not read this way: tried on the second dev set, it gave the indictment to five people the group did not hold and to three it did. 撤銷通緝, 解除通緝 and 撤回…通緝 are none.
- **Someone close to the person is not the person.** Where a Profile name is followed by 的 or 之 and a relation word (妻, 夫, 丈夫, 子, 女, 父, 母, 兄, 弟, 哥, 姊, 妹, 配偶, 前妻, 男友, 友人, 助理, 秘書, 司機, 部屬, 員工, 合夥人, 律師 …; 子公司 is none), the sentence speaks of that other person: 陳志明的哥哥遭起訴 and 陳志明之妻林美玲遭判處有期徒刑 give the Profile person nothing, nor does a later clause of the same sentence. A sentence that also names the person outright (陳志明的助理被起訴，陳志明未遭起訴) is read clause by clause. 陳志明與其妻均遭起訴 names the person. A relation written without 的 / 之 (陳志明胞弟) is not recognised (a known limit).
- A dismissal or indictment counts in a sentence naming the Profile person, or in one that points at nobody else: a subjectless 檢方已起訴 or a collective 起訴七人 (GC-12) is the person's, while 同案, 另一, 另名, 其他, 其餘, 共犯 or 共同被告 mark someone else.
- Quoted speech (「…」, 『…』, “…”) is a claim, not a report: no stage is read inside it, though a name in it still names the person.
- From a verb of saying to 云云 is what someone said (辯稱遭人陷害云云, 稱其帳戶涉及洗錢、已遭通緝云云; the verbs are 辯稱, 佯稱, 謊稱, 誆稱, 訛稱, 詐稱, 聲稱, 宣稱, 供稱, 陳稱, 堅稱, 表示, 主張 and a bare 稱 that is not part of 下稱, 簡稱, 名稱, 俗稱, 職稱, 號稱, 堪稱, 又稱, 暱稱, 人稱 or 對稱): nothing is read there. The span starts at the verb nearest to 云云 and crosses one comma at most, so what stands before that verb or after 云云 is read (陳志明遭判刑，辯稱冤枉云云; 陳志明號稱投資達人，遭判處有期徒刑二年，他辯稱無辜云云), and a saying longer than two clauses is not recognised (a known limit).
- Named as 被害人, 受害人, 證人, 告訴人, 告發人 or 檢舉人 (the role word right before the name), the person is not whom the sentence's steps are about: 李小四等3人涉詐欺，被害人陳志明報案，警方發布通緝 gives them nothing. A sentence that also names them outright is read.
- A fraudster's story is no report either: after 佯稱, 謊稱, 誆稱, 訛稱 or 詐稱 nothing is read to the end of that clause and through the next one (佯稱被害人涉及洗錢，已遭通緝). A next clause that opens with 遭, 被 or 經 is the fraudster's own (陳志明佯稱可獲利，遭起訴) and is read. The sentence may then go on to the fraudster's own arrest and indictment, which are read.
- An aside in brackets is about whoever stands right before it. When that is another person's name (two or three characters opening with a common surname other than the Profile person's, the rest ideographs or ○: 李小四（另案起訴）、陳志明), nothing is read inside it; after one of the Profile's own names it is theirs (陳志明（業經另案起訴）), and an aside that opens with a word for all of a list (均, 皆, 同, 等, 兩人 …: 陳志明、李小四（均遭起訴）) is read for everyone. A noun opening with a surname character is taken for a name too (a known limit).
- A clause whose own subject is another person is not the Profile person's, even in a sentence that names them: in 王大明與李四涉詐欺，李四遭起訴 the indictment is 李四's. Such a clause opens (past 而, 但, 則, 另, 隨後, 其後, 至於) with a 2 or 3 character name of a common Taiwanese surname other than the person's own, or `X姓男子`, right before 遭, 被 or 獲 (李四遭, 林姓男子被). 王男遭 in news about 王大明 is the person; 一審遭判處, 施用毒品遭起訴 and 他曾涉案遭起訴 open with no name and stay the person's. A person with a rarer surname, or named later in the clause, is not recognised (a known limit).
- Nor is a clause whose indictment or dismissal has another person for its object, when the clause does not name the Profile person: 檢方已起訴李小四 or 對李小四為不起訴處分 in a report where the Profile person is the victim. Losing the Profile person's own indictment is the worse error, so the test is narrow: the object is three characters opening with a common surname, written at least twice in the text (起訴高層, 起訴官員 and 起訴金額 are no names), following 起訴 to the end of the clause or into a list (、及與和) or standing between 對 and 不起訴 / 緩起訴, and the clause holds no character of the Profile person's own surname (起訴李小四、王男 may include 王大明). 起訴李小四等七人 stays collective. A two-character name, a rare surname and a person sharing the Profile person's surname are not told apart, so their indictment still reads as the Profile person's.
  - The same holds for a judgment or a wanted notice with another person for its object, where what follows the name shows it is one, written once or not: 法院另判處李小四有期徒刑一年, 另判李四無罪, 警方通緝的李小四落網. A title in that place is no other person (判處董事長有期徒刑 may be the Profile person).
- 不起訴, 緩起訴, 撤回起訴, 不予起訴 and 免予起訴 are DISMISSED, never INDICTED. A negation or question just before the keyword, past 被, 遭, 遭到 or 受 (未, 尚未, 並未, 沒有, 不會, 無, 是否), is no stage: 尚未起訴, 沒有被起訴, 是否起訴, 未遭判刑.
- A modal, request, plan, condition or hearsay before the keyword in its clause (可, 恐, 求, 最重, 推定, 擬, 預計, 即將, 將會, 若, 如果, 一旦, 據傳) is no stage: 最重可判處, 檢方請求法院依法從重判處, 恐遭起訴, 可能遭到起訴, 不可能被起訴, 擬起訴 and 據傳已遭起訴 state no stage. Words that only hold those characters (恐嚇, 恐怖, 恐龍, 可疑, 可是, 許可, 認可, 求償, 要求, 訴求, 模擬, 若干) are not modals, so 因恐嚇取財罪被判處 is a conviction. A name holding 可 or 求 still reads as a modal, so no stage (the safe side).
- The last stage in the text wins.
- With no stage, 偵辦 or 調查 anywhere in the text give ALLEGED when a sentence naming the person says they are under suspicion or names a step or a role (涉, 嫌, 偵辦, 調查, 逮捕, 拘提, 約談, 羈押, 收押, 移送, 函送, 交保, 搜索, 查獲, 到案, 被告, 共犯, 首腦, 主謀, 成員, 車手, 起訴, 通緝) or lists them in a group (…等9名). Someone the text names only as a counterpart in the facts (李小四曾將股票售予陳志明) stays UNKNOWN. A text that never writes one of the Profile's names is about them as a whole and keeps ALLEGED. Otherwise the stage is UNKNOWN.

Measured on the annotated documents, each annotated suspect taken in turn as the Profile person, against the stage their own annotated events imply (prior records left out): the same stage for 173 of 185 suspects of the dev documents (0.935) and 109 of 126 of the second dev set (0.865). Before the rules for people a judgment only names, the order's other forms, the only defendant, WANTED, asides, the ALLEGED condition and the report wordings above: 0.519 and 0.810. Court press releases were measured on three sets of 40 releases. The first is fully annotated (90 suspects, nearly all convicted): the same stage for 12 before the release was read by rule, and for 88 (0.978) with the rules above, which were written looking at it. The other two hold, for each named accused person, the stage two blind readers gave alike. On the second, not looked at until measured, the rules of the first gave 45 of 93 (0.484); the reading was then made general (the whole text, the ruling in running text, tables) looking at it: 87 of 93. On the third, not looked at at all, those rules give 108 of 172 (0.628), before and after the guards a review added (the heading first, requests, another's clause): 16 of 17 acquittals and 81 of 130 convictions are read, 41 convicted people are read as nothing, and three as acquitted. 18 of the 64 that differ are in one release that masks its 23 defendants to forms several of them share; most of the rest are in four releases with seven or more defendants. The forms of large cases were then written looking at the third set (141 of 172; the first two unchanged at 88 and 87), after a fourth set of 40 had been held back and labelled the same way. On the fourth, not looked at at all, the rule gave 189 of 257 (0.735), and gives 185 (0.720) with the guards a review then added (probation only right after the person's name, no carrying on after an acquittal, row numbers only bracketed and under the heading, the appeal told in the opening): 164 of 223 convictions, 9 of 11 acquittals, all 4 wanted; 59 convicted people are read as nothing or as indicted, 47 of them in two releases, and 31 of the 40 releases are read wholly as labelled. So a release about one or a few defendants is mostly read, a large case is read or missed as a whole by the form it uses, and the sets the rules were written against overstate what the rule does on a release it has not seen. The disagreements were each read against the text. In about half of what still differs the rule says what the text says and the reference does not hold it: a stage the text states of the person in another case (另案起訴, 判刑確定), which the annotation of this case's events leaves out, and X等N人 indictments covering everyone listed. The rules' own remaining faults: a person listed in a sentence beside an indictment or conviction that is another's with no bracket to mark it, a collective indictment of fewer people than the report lists, and a group indictment (對X等4人提起公訴) not given to the others it covers.

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
