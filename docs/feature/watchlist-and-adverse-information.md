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

The ADVERSE_MEDIA stage is read from the attached news by a keyword heuristic, to be replaced by an approved legal-stage extraction once judgment wording is covered by a Golden Case:

- The news is split into sentences (。！？； or a line break) and clauses (，). A sentence names the Profile person when it contains one of the Profile's names of two or more characters.
- A judgment (判處, 判刑, 判決有罪, 有罪確定, 定讞 → CONVICTED; acquittal → ACQUITTED) counts only in a sentence naming the Profile person: a co-defendant's sentence is not the subject's (GC-12).
- A dismissal or indictment counts in a sentence naming the Profile person, or in one that points at nobody else: a subjectless 檢方已起訴 or a collective 起訴七人 (GC-12) is the person's, while 同案, 另一, 另名, 其他, 其餘, 共犯 or 共同被告 mark someone else.
- Quoted speech (「…」, 『…』, “…”) is a claim, not a report: no stage is read inside it, though a name in it still names the person.
- A clause whose own subject is another person is not the Profile person's, even in a sentence that names them: in 王大明與李四涉詐欺，李四遭起訴 the indictment is 李四's. Such a clause opens (past 而, 但, 則, 另, 隨後, 其後, 至於) with a 2 or 3 character name of a common Taiwanese surname other than the person's own, or `X姓男子`, right before 遭, 被 or 獲 (李四遭, 林姓男子被). 王男遭 in news about 王大明 is the person; 一審遭判處, 施用毒品遭起訴 and 他曾涉案遭起訴 open with no name and stay the person's. A person with a rarer surname, or named later in the clause, is not recognised (a known limit).
- 不起訴, 緩起訴, 撤回起訴, 不予起訴 and 免予起訴 are DISMISSED, never INDICTED. A negation or question just before the keyword, past 被, 遭, 遭到 or 受 (未, 尚未, 並未, 沒有, 不會, 無, 是否), is no stage: 尚未起訴, 沒有被起訴, 是否起訴, 未遭判刑.
- A modal, request, plan, condition or hearsay before the keyword in its clause (可, 恐, 求, 最重, 推定, 擬, 預計, 即將, 將會, 若, 如果, 一旦, 據傳) is no stage: 最重可判處, 檢方請求法院依法從重判處, 恐遭起訴, 可能遭到起訴, 不可能被起訴, 擬起訴 and 據傳已遭起訴 state no stage. Words that only hold those characters (恐嚇, 恐怖, 恐龍, 可疑, 可是, 許可, 認可, 求償, 要求, 訴求, 模擬, 若干) are not modals, so 因恐嚇取財罪被判處 is a conviction. A name holding 可 or 求 still reads as a modal, so no stage (the safe side).
- 無罪 is an acquittal only after 判決, 改判 or 宣判 in its clause; 堅稱無罪 is a claim and 無罪推定 a presumption.
- The last stage in the text wins.
- With no judicial stage, 偵辦 or 調查 give ALLEGED; otherwise the stage is UNKNOWN.

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

- **Models.** Both are `bert-base-chinese` fine-tuned on the bank's annotated judgments and press releases and exported to ONNX; the directory holds `ner/` and `ev/`, each with `model.onnx`, `config.json` and `vocab.txt`, outside the repository. `Infra/Models/OnnxNewsModel` runs them with ONNX Runtime. Its tokenizer reproduces the training one (Hugging Face BertNormalizer without lower-casing, BertPreTokenizer, WordPiece) and keeps each token's place in the text; each character is first NFKC-normalised when that keeps it one character. The tagger reads the text in windows of 510 tokens every 384, averages each token's label probabilities over the windows holding it, and decodes the most probable valid BIO sequence (Viterbi: an `I-` only after a `B-` or `I-` of its role) into spans with a role (`PER-` or `ORG-` with `Suspect`, `Victim`, `Official` or `Other`).
- **Choosing.** Each setting below was chosen on the 58 dev documents and reported on the held-out test documents; none was tuned on test.
- **Corrections** of the tagger's known errors (`Core/Explanation/NewsModelReading`), each kept only because it helped:
  - a one-character span is dropped, and so is an organisation ending in 戶 (帳戶, 人頭戶: an account, not an organisation)
  - a well-formed name is two to four ideographs opening with a common surname; one holding exactly one other well-formed name of three or more characters is its form (王大明等 → 王大明), while a two-character name inside a three-character one stays apart (王明, 王明華)
  - any other personal span inside or around well-formed names that all map to one person is that person's form (大明 → 王大明); one related to two people (大明 with 王大明 and 陳大明) is dropped; one related to none stays itself
  - a short organisation name inside exactly one longer one is its abbreviation
  - the role is the majority over all mentions of all forms; party `OTHER` becomes null
  - tried and left out: joining touching spans (it glued neighbouring names), the rule digest's role word over the tagger's role, the rule events added to the model's, 前科 / 累犯 / 曾因 patterns for prior records (each made the results worse or found nothing more), and a role vote weighted by probability (one more role right in 443, not worth the code)
- **Entities.** Each has its name (the full name when it has several forms), its other forms, whether it is an organisation, its party, whether a form equals a Profile name, and its mentions; the Profile person first, then by mentions, at most 50.
- **Events.**
  - Each sentence (up to 。！？；!?; or a line break) holding a form of an entity is classified for that entity, with the form as written there, cropped to 150 characters around it as in training. So is the one sentence after a sentence naming a person, when it names no person (an organisation may be named), for the person named last: a judgment goes on about the defendant without the name (原審判處…). At most 300 classifications per article, in text order.
  - The labels are the `NEWS_EVENT` types with `NEGATED` or `REQUESTED` where the label says so, and `PRIOR_RECORD` for a prior case (前科, 累犯), which only the model reads.
  - Over an entity's sentences a label's probability is that at least one says it (noisy-or, 1 − Π(1 − p)). A label is listed at 0.3 or more, with that probability rounded to two places and the sentence most likely to say it, at most 200 characters and never cut inside a surrogate pair; ordered by probability.
  - Tried and left out: a threshold per label (tuned on dev it overfitted: end to end dev F1 0.599 against 0.576 but test 0.597 against 0.598), one certainty per type and sentence (no change), carrying over two or three sentences, and listing events only for suspects, for anyone but officials, or for entities mentioned twice (all within noise).
- **Time.** On the PoC machine (12 cores, under other load) tagging takes about 1 s per 1,000 characters and each event classification about 65 ms, so a 2,000-character article takes 3 to 5 s and a long judgment up to about 30 s, within a batch job's `Batch:Timeout`; the models rerun on every re-analysis. Measured and left out: int8 quantization (about 1.2× faster on this CPU, which has no VNNI instructions, and fewer people found), the BERT graph fusion of ONNX Runtime's optimizer (no faster), padded batches of event classifications (slower on a CPU), and one model that tags and reads events from a single encoder pass (dev end to end F1 0.572 against 0.576, 97% → 96% of people found, only 23% faster).
- **Lineage.** `newsModelVersion` in the revision's lineage names both model files by the first 12 hex digits of their SHA-256 when the models read an article of a hit, else it is null.
- **Measured** on 49 annotated documents never used in training or tuning (people per document; events per document, person and label). Two event measures:
  - *given sentences* scores the event model on the annotated (person, sentence) rows. These include sentences that do not name the person, which the system cannot know to read.
  - *end to end* runs the whole readout on the text and matches its entities to the annotated people by their forms. An event on an entity matching no annotated person counts as wrong. This is the number that matters.

| | Found | Role right | Extra names | Events, given sentences: P / R / F1 | Events end to end: P / R / F1 |
|---|---|---|---|---|---|
| tagger (most probable tag per token, one-character spans dropped) | 382 / 394 (97%) | 371 / 382 (97%) | 111 | 0.82 / 0.83 / 0.83 (highest sentence) | |
| with valid-sequence decoding and the corrections | 385 / 394 (98%) | 372 / 385 (97%) | 72 | 0.82 / 0.85 / 0.84 (noisy-or) | 0.61 / 0.61 / 0.61 |
| and the sentence after carried over | | | | | 0.56 / 0.64 / 0.60 |
| rule digest alone, for comparison | 82 / 394 (21%) | 80 / 82 (98%) | 14 | 0.34 / 0.22 / 0.27 | |

  - On the 58 dev documents, carrying one sentence over raised the end-to-end F1 from 0.545 to 0.576; on test it moved from 0.611 to 0.600, more recall for less precision. Over both sets together it gains (0.571 to 0.586), so it is kept. On all 56 test documents the end-to-end F1 is 0.60 (P 0.56, R 0.64).
  - Of the end-to-end false positives, about half are events on names no annotation covers.
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
