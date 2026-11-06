# Watchlist and Adverse Information Screening

> Module: Source Screening and Risk Assessment.

## Scope

Covers structured official lists, structured adverse-information Profiles and the list-type determination shared by all source policies. Replaces the narrower Fraud-only design ([ADR-0005](../adr/0005-watchlist-model-beyond-fraud.md)); list types follow the bank's vocabulary ([ADR-0016](../adr/0016-bank-list-type-vocabulary.md)).

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
- `BANK_INTERNAL_LIST` is in the bank's vocabulary ([ADR-0016](../adr/0016-bank-list-type-vocabulary.md)) but no mapping produces it: no export field marks a bank-internal list yet ([open questions](../plan/open-questions.md#data-and-source)). A bank-internal category is therefore unmapped, `UNKNOWN` with `UNMAPPED_CATEGORY`, and goes to review.
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
OTHER
```

A source Profile can carry multiple categories.

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

The ADVERSE_MEDIA stage is read from the attached news by a keyword heuristic, to be replaced by an approved legal-stage extraction once judgment wording is covered by a Golden Case:

- The news is split into sentences (。！？； or a line break) and clauses (，). A sentence names the Profile person when it contains one of the Profile's names of two or more characters.
- A judgment (判處, 判刑, 判決有罪, 有罪確定, 定讞 → CONVICTED; acquittal → ACQUITTED) counts only in a sentence naming the Profile person: a co-defendant's sentence is not the subject's (GC-12).
- A dismissal or indictment counts in a sentence naming the Profile person, or in one that points at nobody else: a subjectless 檢方已起訴 or a collective 起訴七人 (GC-12) is the person's, while 同案, 另一, 另名, 其他, 其餘, 共犯 or 共同被告 mark someone else.
- 不起訴, 緩起訴 and 撤回起訴 are DISMISSED, never INDICTED.
- A modal, request or presumption before the keyword in its clause (可, 恐, 求, 最重, 推定) is no stage: 最重可判處, 請求從重判處 and 求刑 state no sentence.
- 無罪 is an acquittal only after 判決, 改判 or 宣判 in its clause; 堅稱無罪 is a claim and 無罪推定 a presumption.
- The last stage in the text wins.
- With no judicial stage, 偵辦 or 調查 give ALLEGED; otherwise the stage is UNKNOWN.

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

`RiskScoring` runs downstream of identity and never feeds back into it ([ADR-0021](../adr/0021-deferred-capabilities-built-behind-controls.md)). It needs a bank-approved `RiskPolicy`; none ships, so by default no pair is scored. A pair whose identity is NO_MATCH is not scored. Otherwise:

- HIT conditions are scored; an UNKNOWN Sanction or PEP condition scores as a HIT, other UNKNOWN conditions score nothing;
- an ADVERSE_MEDIA condition set aside by Article 22 ② (`amlRelevance: UNRELATED`) scores nothing;
- each scored condition adds its list weight (`list:<type>`) and the weight of each of its risk categories; the legal-stage weight (`stage:<stage>`) applies to ADVERSE_MEDIA conditions only;
- any scored Sanction condition makes the level High whatever the weights; otherwise the policy thresholds decide High, Medium or Low.

## Phase 1 constraint

General free-news event understanding is out of scope. Structured Profile fields, bounded Notes sections and explicit citations are used; no LLM is involved ([ADR-0018](../adr/0018-legacy-ai-report-rules-not-adopted.md)).

When news content is used:

- every attached article is listed; a fact stated by one article is never generalised to "all reports";
- rewording never swaps actor and object, instructor and instructed, payer and payee;
- the event date is not the publication date and not a later court date;
- several reports of the same event (same party, event time and charge) are one event and one piece of evidence.
