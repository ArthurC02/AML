# Scoring Rule

> Cross-cutting: Candidate and Identity Adjudication, Source Screening and Risk Assessment. Decided 2026-11-26 ([ADR-0001](../adr/0001-identity-adjudication.md), [ADR-0004](../adr/0004-identification-only.md)). The calculation is built as measurement only (`Core/Scoring/ScoringRule.cs`, policy record `ScoringPolicy.Proposed`): tests compute it beside the stage 3 table; no Case output, API contract or stored revision carries it. Every number below is a **proposal the user has not approved**; it is used for trial calculation only, while the stage 3 table decides ([validation](#validation)).

Once its weights are approved and its gates passed, the scoring rule decides whether a pair matches, up to an automatic MATCH. It is its own spec, not a section of [identity adjudication](identity-adjudication.md), because it scores both questions of a pair (identity and source condition) and the combination of the two, and because its proposal table is the one place the user edits.

## Purpose

The rule exists to **conclude**: MATCH or NO_MATCH wherever the evidence allows (user, 2026-11-26: a system that leaves everything to review is conservative but worthless). REVIEW is the remainder the evidence cannot settle, not the default. The primary measure is therefore the **decided rate**, the share of pairs concluded MATCH or NO_MATCH without a reviewer, guarded by Auto-MATCH precision and Auto-NO_MATCH precision; the Review Rate is its complement. No target is set. Decisiveness never overrides the invariants below: a missing value is still no evidence against, and only validated items close a pair. Where pairs stay undecided, the [measurement](../plan/poc-delivery-plan.md#scoring-rule-beside-the-stage-3-table) says whether the rule or the missing data holds them.

## Invariants

- **Deterministic.** The same inputs under the same scoring-policy version give the same scores, the same bands and the same decision. The algorithm only adds rule items and compares the sums with thresholds.
- **A sum of rule items.** Each score is the sum of items; each item names its evidence group or condition, its source `[S#]`, the points and a reason code in the [code catalog](pipeline-api-and-batch.md#rest-resources). An item without a catalog code is a bug.
- **No model decides.** No model output is the decision, a score or an item as such; there is no match probability. A model appears only upstream, as an extractor of fields and facts from text: persons, attributes, events, the legal stage. What it extracts is an input with provenance `MODEL_READ` (and the model named in lineage); the rule decides what it is worth, through a reliability factor in the proposal table. A missing model answer stays missing ([missing data](#missing-data)).
- **Two questions, two scores.** Identity and source condition are scored separately; no point of one is added to the other ([ADR-0004](../adr/0004-identification-only.md)). A strong condition never makes a person more likely the customer.
- **Vetoes stay outside the sum.** A veto sets or bounds a band whatever the points; it is listed with its code.
- **Missing is not evidence against.** A value missing on either side scores 0 and can never move a pair towards NO_MATCH.

## What is scored

| Score | Of | Bands | Replaces |
|---|---|---|---|
| identity score `I` | one pair (screened subject × Profile) | NO_MATCH, REVIEW, PROBABLE_MATCH, MATCH (automatic) | the stage 3 table |
| condition score `C` | one source condition of a Profile | NOT_HIT, UNKNOWN, HIT | the flag / mapping outcome, the stage for external sources |
| pair result | the two bands | the [combination matrix](data-contracts.md#combination-matrix), unchanged | nothing: the matrix stays |

One combined number was rejected: it would let a strong source condition push identity, the fault ADR-0004 exists to prevent, and a reviewer could not tell which question a point answers.

## How today's pieces map

| Today | In the scoring rule |
|---|---|
| evidence groups and their `strength` | identity items: points by group and level |
| hit prerequisites (NAME, GENDER, NATIONALITY) | 0 points (the bank's rule: they carry no weight); all three EXACT is a precondition of the lower PROBABLE_MATCH threshold, as in row 4 |
| `HardConflictGate`, validated rule (impossible age; a Profile DOB more than two years off, `FULL_DOB_CONFLICT` / `DOB_CONFLICT`; every value attached news or a received page states next to the name more than two years off, no Profile DOB and no HIGH support, `STATED_DOB_CONFLICT`; released 2026-11-27) | veto: NO_MATCH, outside the sum (the DOB conflict adds no item; under the stated-value veto the stated differences stay listed as their items) |
| `HardConflictGate`, pending rule; `SOURCE_QUALITY_DOWNGRADE` | the band is REVIEW whatever the sum, listed with the rule's code and `SCORE_HELD_PENDING_VALIDATION`; no points are proposed, so it adds no item (as stage 3, which stops there) |
| stage 3 row 1 (document conflict) | negative item; held like a pending hard rule |
| a Profile DOB within two years, not equal (`DOB_AGREES_WITHIN_TOLERANCE`) | support item at the birth-year level |
| stage 3 rows 2 to 6 | identity bands and review priority |
| opposite genders (blocks rows 3 and 4) | cap: no PROBABLE_MATCH (`SCORE_CAPPED_GENDER_CONFLICT`) |
| stated age, birth date, gender in a source | items at most MEDIUM, times the source's reliability; a difference is a negative item that never takes the pair below REVIEW (every value more than two years off: the veto above); a page birth date within two years (`PAGE_BIRTH_DATE_WITHIN_TOLERANCE`) is neutral, no item |
| entity records | items from `ENTITY_IDENTIFIER`; cap at REVIEW until an entity Golden Case |
| `SourceConditions` (flags, category mapping) | condition items: the basis gives the points |
| three-reader stage vote | a rule over three readings, upstream of the score; its stage is a condition item ([three readers](watchlist-and-adverse-information.md#three-readers)) |
| combination matrix | unchanged |
| list type (Sanction, PEP, RCA) | not part of the rule: automatic MATCH and NO_MATCH apply to every list type (no blanket human review, the user's decision of 2026-11-27; already so for today's NO_MATCH under the stage 3 table) |
| review priority | from `I` and a raised Sanction |
| `RiskScoring` / `RiskPolicy` | not part of the rule: an AML risk level graded after the decision; its list and stage weights may be superseded by the condition items (not decided) |
| `FellegiSunter` | not part of the rule: a calibration tool; calibrated weights may later replace the identity points as a new policy version |
| retrieval score (BM25, n-gram, RRF) | not scored: ranks candidates only |

## Identity score

`I` = sum of identity items. Each comparable evidence group gives at most one item: support points at its level, or the conflict points. A stated value gives points × the reliability of its source; the same fact from several reports of one event counts once, at its most reliable source ([one event](external-sources.md#same-event)); a stated value the Profile's own field already gives does not count again.

Bands, applied in order:

1. a validated hard conflict → NO_MATCH (veto);
2. `I ≥ T_match` with no veto or cap → MATCH, concluded without a reviewer (automatic MATCH, allowed by the user 2026-11-26). It takes effect only once the score is the decision ([validation](#validation)); until then it is a measured outcome only, and today's code never emits MATCH. It applies to every list type: nothing holds a Sanction, PEP or RCA pair back from it ([ADR-0001](../adr/0001-identity-adjudication.md)). An automatic MATCH is an identification, not a disposition: the Article 22 recommendation stays advisory ([ADR-0004](../adr/0004-identification-only.md));
3. `I ≥ T_probable`, or `I ≥ T_probable_exact` with all three prerequisites EXACT and no DOCUMENT or EMPLOYMENT value CONFLICT or UNCERTAIN (other than `NOT_COMPARABLE`) → PROBABLE_MATCH, unless capped;
4. `I ≤ T_no_match`, reached only through items whose rules are validated → NO_MATCH; reached otherwise → REVIEW with `SCORE_HELD_PENDING_VALIDATION`;
5. otherwise REVIEW.

Caps (proposed), each outside the sum and listed with its code:

- a CONFLICT in the Profile's own DOCUMENT (pending rule) caps the pair at REVIEW (`SCORE_CAPPED_PROFILE_CONFLICT`), so stage 3 row 1 is kept: without it a full DOB, two associations and more could outweigh the −40. A DOB conflict needs no cap: it is the validated veto, and with an agreeing document it is downgraded and held at REVIEW;
- opposite genders, in the Profile or stated next to the name in the attached news, cap at REVIEW (`SCORE_CAPPED_GENDER_CONFLICT`);
- an entity record caps at PROBABLE_MATCH, and at REVIEW without an agreeing entity identifier (`SCORE_CAPPED_ENTITY_RECORD`);
- a Profile from an external source caps at PROBABLE_MATCH and cannot reach NO_MATCH by the sum until its Golden Case ([external sources](external-sources.md)); not built: a received page's persons are candidates (`Core/Pages/PageCandidates`), but `ScoringRule` has no such cap.

A cap is listed only when it lowered the band; the threshold the sum crossed is kept beside it.

Review priority among REVIEW pairs (not built in the trial calculation): HIGH for a raised Sanction or the PROBABLE_MATCH band (NORMAL when the news disagrees with the Profile, as today), NORMAL for `I > 0`, LOW otherwise.

NO_MATCH comes today from the validated vetoes: impossible age, the DOB conflict (more than two years) and its stated-value form (every age and birth date attached news or a received page states more than two years off). The NO_MATCH band of the sum cannot be reached: every negative item left (the document conflict, a stated difference) comes from a pending rule or is soft by design. It opens only when such a rule is validated and released as a negative item.

**Arithmetic.** Points and factors are exact decimals (no binary floating point); a product is not rounded; a sum equal to a threshold reaches it (`≥`, and `≤` for `T_no_match`). So 0.6 × 100 = 60 reaches `T_hit` 60.

With the proposed numbers the Golden Cases come out as today:

- GC-01: impossible age at an attributed role, validated → NO_MATCH (veto).
- GC-02: name and nationality EXACT (0), nothing else comparable → `I` = 0, REVIEW LOW.
- GC-12: name and nationality EXACT, gender missing (0), no Profile DOB → `I` = 0, REVIEW LOW.
- GC-03: name, gender, nationality EXACT; birth year agrees, Profile year only (+8); cross-country address (0) → `I` = 8 = `T_probable_exact`, PROBABLE_MATCH.
- GC-11: name, gender, nationality EXACT; one of two Profile birth years agrees (+8) → `I` = 8 = `T_probable_exact`, PROBABLE_MATCH.

GC-03 and GC-11 sit exactly on the threshold, with zero margin: any change to the year points or to `T_probable_exact` moves them.

Off the Golden Cases the rule differs from the stage 3 table where points add up. Each difference must be accepted or changed by the user:

- **two MEDIUM supports** (for example DOB to the month +15 and an address at house number +15 = 30) reach `T_probable` and give PROBABLE_MATCH; the table gives row 5, REVIEW;
- **weak items with exact prerequisites**: two LOW items (an address at district +5 and an other identifier +5 = 10), or one agreeing employer (+15), reach `T_probable_exact` 8 without any DOB support and give PROBABLE_MATCH; the table's row 4 needs DOB support, so it gives row 5, REVIEW;
- **opposite genders with an agreeing document**: the table's row 2 comes before the gender check and gives PROBABLE_MATCH; the gender cap holds the score at REVIEW;
- **a stated age or birth-date difference** in the attached news that does not conclude (the Profile has an agreeing DOB, or a birth date within two years): the table puts the pair on row 1, REVIEW; the score subtracts 4.8 and the pair can still reach PROBABLE_MATCH (a full DOB +25 and an employer +15 give 35.2).

These differences are what the [validation](#validation) measures; none occurs on the Golden Cases.

### Proposal: reaching MATCH and NO_MATCH by the sum

**PROPOSED — not approved; nothing here is a new number.** It reads the proposal table for what the [purpose](#purpose) asks: which evidence lets the sum conclude.

MATCH (`T_match` 60) is reached by these combinations of the table's items, among others:

- a document (+40) with a full DOB (+25) = 65, or with a DOB to the month (+15) and one LOW item (+5) = 60;
- a document and one association (+15) and an other identifier (+5) = 60;
- a full DOB (+25) and two associations or an association and an address at house number (+30) and one LOW item (+5) = 60;
- without any direct identifier: a DOB to the month and three MEDIUM groups (employment, relationship, address at house number, +45) = 60. **To decide:** whether MATCH without a document or a full DOB is acceptable, or MATCH should need one of them.

On today's data these combinations are rare: an address is never support (no validity periods on either side), and the bank supplies no customer employer or relatives, so in practice MATCH needs a Profile document or full DOB.

NO_MATCH by the sum needs a negative item from a validated rule. The DOB conflict is not such an item (user, 2026-11-27): a Profile DOB more than two years off is a veto, a difference of two years or less is agreement within tolerance, and no DOB item is negative ([birth-date tolerance](identity-adjudication.md#birth-date-tolerance); scattered or UNPARSEABLE Profile values downgrade it to REVIEW; a placeholder day is not checked). The candidates left, with what each needs:

| Item | Points | Alone reaches `T_no_match` −25 | Needs before release |
|---|---|---|---|
| `DOCUMENT_IDENTITY_CONFLICT` (same type and country, different number) | −40 | yes | a Golden Case; a rule for documents that are reissued with a new number (a passport), which a national ID is not |
| stated age or birth-date difference that does not conclude | −8 × reliability | no (at most −8) | stays a soft conflict by design; more than two years off, it is the `STATED_DOB_CONFLICT` veto |

Each validated item is released as its own policy version; agreeing evidence in other groups still offsets it (a document conflict −40 and a full DOB +25 give −15, REVIEW). A missing value never scores, so a pair with only the prerequisites (GC-02, GC-12) can reach neither band: that is missing data, not the rule.

### Candidate MATCH condition B′

**PROPOSED — under measurement, not approved; nothing emits it.** A wrong conclusion is very costly, so automatic MATCH is proposed only on direct evidence, and only once its wrong-MATCH risk is measured on data holding the failure modes. B′ is that direct-evidence condition, a predicate beside the sum (`DocumentMatch` in the test project; no production caller): MATCH only when

- a list document agrees with the customer's in number, document type and issuing country, the country known on both sides (a Taiwan type implies TW);
- the name prerequisite holds: EXACT after the product's folding, or the same reading across romanisations (`ROMANIZATION_VARIANT`);
- nothing stands against it: no hard conflict, validated or pending, and no `SOURCE_QUALITY_DOWNGRADE`; no evidence group in conflict (birth date beyond two years, gender, nationality, stated age, birth date or gender, document); a Person record; no `MODEL_READ` person.

A birth date within two years (`DOB_AGREES_WITHIN_TOLERANCE`) does not stand against it. For comparison: B, the number agreeing whatever the type and country, with the same name prerequisite and nothing against it, and B′∅, B′ also accepting a document with no issuing country on either side.

**B″** adds a second direct field: B′ and a list birth date that agrees with the customer's. **B″-exact**: equal at the list's own precision with no tolerance (the full date, or a year-only value equal to the birth year); a list 1 January (a full date with month 1 and day 1, the likely placeholder) counts as its year only. **B″-tol**: the product's DOB support, two-year tolerance included.

Measured ([adversarial set](../plan/poc-delivery-plan.md#adversarial-synthetic-set): a realistic group of 260 pairs, 115 of them the customer, and a theoretical-limit group of 75 strangers built to probe the rules, not expected in real data; [end-to-end set](../plan/poc-delivery-plan.md#synthetic-end-to-end-set), 359 pairs, 118 decided today). The counts are probes, not frequencies: no figure is a rate.

| Rule | realistic: right / wrong MATCH | customers missed (REVIEW) | end-to-end: right / wrong, decided | cannot see (theoretical, MATCH on another person) |
|---|---|---|---|---|
| B | 69 / 10 (no issuing country) | 46 | 7 / 0, 125/359 | 50 (number under another type 15, another country 15, recycled 20) |
| B′ | 59 / 0 | 56 | 7 / 0, 125/359 | 20 (recycled passport: bare record 10, customer's birth year only 10) |
| B′∅ | 69 / 10 (no issuing country) | 46 | 7 / 0, 125/359 | 20 (recycled) |
| B″-exact | 29 / 0 | 86 | 5 / 0, 123/359 | 10 (recycled, customer's birth year only) |
| B″-tol | 44 / 0 | 71 | 5 / 0, 123/359 | 10 (the same) |

No rule and not the product concludes a customer NO_MATCH. A missed customer stays in REVIEW, which is acceptable; a wrong MATCH or a wrong NO_MATCH is not.

Open risks, to be decided by the user before B′ or B″ can be approved:

- **Recycled or reissued numbers** (theoretical limit: reuse of a Taiwan passport number by another person is unverified, a national ID is issued once for life). A list record carrying only the name and the same document is the customer on every field it carries: B′ concludes MATCH on all 10 such strangers, B″ on none (it needs a list birth date). A holder of the same gender and birth year on a year-only record still beats B″ (10 of 10 wrong under both sub-variants). A holder with the same full birth date is not generated: the same failure, rarer (about one in 365 same-year holders if birth days are uniform), small but not zero, and invisible to any rule over these fields. Whether to require a full birth date (B″-exact at day precision), allow automatic MATCH only for document types that are never reissued (a national ID, not a passport), or accept the measured risk, is open.
- **Missing issuing country.** Both sides without a country: B′ and B″ leave the pair to REVIEW; B′∅ (and B) MATCH the customer 10 and another holder of the same number 10, a realistic case, which is why a missing country must not count as agreement.
- **Missed customers.** A mistyped (15) or renewed (15) list number, a list birth date more than two years off (15, the document wins today: the DOB conflict is downgraded to REVIEW) and a simplified name the variant list does not fold (1, 杰 for 傑) give no MATCH under any rule, no issuing country (10) none under B′ or B″; B″ also misses the customer with no list birth date (15), and B″-exact the one whose list date is within two years but not equal (15).
- **Data.** The kinds and counts are built, not sampled: no rate here is a precision for release. Real labelled pairs, with recycled and reissued documents among them, are still absent.

## Condition score

`C` per source condition = basis points × source reliability, plus nothing else. For a list Profile the basis is the flag or category mapping, as today ([list-type determination](watchlist-and-adverse-information.md#list-type-determination)): a condition today's determination makes HIT scores 100 (`SCORE_CONDITION_BASIS`), an unmapped category 40 (`UNMAPPED_CATEGORY`), any other UNKNOWN condition (unparseable flag, flag against category, sanction measure not confirmed) 40 (`SOURCE_CONDITION_UNKNOWN`), a NO flag 0. For a source person read from a page or judgment ([external sources](external-sources.md)) the basis is the person's legal stage as the vote gives it, and the role: a person who is not a suspect (victim, witness, official) has no adverse basis and scores 0.

Bands: `C ≥ T_hit` → HIT; `T_unknown ≤ C < T_hit` → UNKNOWN; below → NOT_HIT. One proposed rule stands outside the sum: a source person's condition whose stage is UNKNOWN, for any reason (the readers disagree, or a reader gave no answer, `STAGE_READER_NO_ANSWER`), is UNKNOWN whatever the source's factor and the role (`SCORE_CONDITION_STAGE_UNKNOWN`); an unknown stage is a missing input and is never scored down to NOT_HIT. AML relevance does not score: Article 22 ② still sets an unrelated condition aside in the [disposition recommendation](review-and-disposition.md).

## Source reliability

One factor per kind of source, applied to stated identity values and to condition basis points. A search result has the reliability of the page it leads to; its snippet is never read as evidence. *Built* (measurement only): `ScoringRule.Identity` takes the source kind of the record's field values (`record`, default the official list) and of the attached text's stated values (`text`, default allowlisted news), so a received page's person is scored with the news factor for both; the synthetic measure passes each Profile's kind.

| Source kind | Why it differs |
|---|---|
| official list, list vendor Profile field | structured, maintained record |
| court judgment | the court's own finding; party lines are the court's |
| government or prosecutors' press release | official, but before the court |
| news on the allowlist | a report of what others said |
| `MODEL_READ` value | what a model extracted, not a rule reading |

## Missing data

- A value missing on either side scores 0 and is listed under unknown fields with its finding (`MISSING`, `AMBIGUOUS`, …).
- Missing data never reaches NO_MATCH: only negative items do, and only validated ones close.
- A missing model answer is not a reading: no item, the stage UNKNOWN with `STAGE_READER_NO_ANSWER`, the batch retry as today ([durable batch execution](pipeline-api-and-batch.md#durable-batch-execution)).
- `analysis_quality` gains (not built) the share of items missing per pair, so a low score from little data is told apart from a low score from conflicts.

## Proposal table

**PROPOSED — not approved by the user.** Policy version `score-proposal-0.2` (`ScoringPolicy.Version`; the DOB conflict is a veto, not an item). Points follow the strength order and roughly round log₂(m/u) of [support strength](identity-adjudication.md#support-strength), so calibration can replace them.

| Item | Code | Points / value |
|---|---|---|
| DOCUMENT comparable, EXACT (UNIQUE) | `UNIQUE_IDENTIFIER_AGREEMENT` | +40 |
| DATE_OF_BIRTH day, EXACT (HIGH) | `SCORE_DOB_DAY` | +25 |
| DATE_OF_BIRTH month (MEDIUM) | `SCORE_DOB_MONTH` | +15 |
| DATE_OF_BIRTH year only (LOW) | `SCORE_DOB_YEAR` | +8 |
| DATE_OF_BIRTH not equal, within two years (LOW) | `DOB_AGREES_WITHIN_TOLERANCE` | +8, as a birth year; the user may raise it (+25, the full-date value, changes no band on the synthetic set) |
| ADDRESS same period, house number / district | `SCORE_ADDRESS` | +15 / +5 |
| EMPLOYMENT, COMPANY_ASSOCIATION, RELATIONSHIP explicit, same period | `SCORE_ASSOCIATION` | +15 |
| OTHER_IDENTIFIER EXACT | `SCORE_OTHER_IDENTIFIER` | +5 |
| ENTITY_IDENTIFIER EXACT | `ENTITY_IDENTIFIER_AGREEMENT` | +40 |
| NAME, GENDER, NATIONALITY agreement | — | 0 |
| DATE_OF_BIRTH more than two years apart (validated hard rule, 2026-11-27) | `FULL_DOB_CONFLICT` / `DOB_CONFLICT` | veto NO_MATCH, no item |
| stated birth dates and dated age (attached news or a received page) all more than two years apart, no Profile DOB (validated hard rule, 2026-11-27) | `STATED_DOB_CONFLICT` | veto NO_MATCH |
| customer under 14 on the header date of an attached item naming the person as accused or officer, or at the earliest event the Profile notes date for the person, no Profile DOB (advisory, 2026-11-27: a hint until measured on real Cases) | `AGE_IMPLAUSIBLE_AT_DOCUMENT` | no item (0) and no veto: an unapproved weight would be a new weight; the hint is for the reviewer, and stage 3 row 1 holds the pair at REVIEW |
| a received page's stated birth date within two years, not equal | `PAGE_BIRTH_DATE_WITHIN_TOLERANCE` | 0 (neutral) |
| DOCUMENT conflict (pending hard rule) | `DOCUMENT_IDENTITY_CONFLICT` | −40 |
| stated age or birth date differs | `AGE_DIFFERENCE_AT_REPORT` / `NEWS_BIRTH_DATE_DIFFERENCE` | −8 × reliability |
| stated birth date agrees, the Profile's own DOB not agreeing | `NEWS_BIRTH_DATE_AGREES` | the DOB points at its level (full date MEDIUM +15, else LOW +8) × reliability |
| ENTITY_IDENTIFIER conflict | — | 0 (the entity cap holds the pair at REVIEW) |
| address, employer, location difference | — | 0 |
| `T_match` (automatic MATCH once the score decides) | `SCORE_BAND_MATCH` | 60 |
| `T_probable` | `SCORE_BAND_PROBABLE_MATCH` | 30 |
| `T_probable_exact` (prerequisites EXACT) | `SCORE_BAND_PROBABLE_MATCH` | 8 |
| `T_no_match` (validated items only) | `SCORE_BAND_NO_MATCH` | −25 |
| reliability: official list / vendor field | — | 1.0 |
| reliability: court judgment | — | 1.0 |
| reliability: press release | — | 0.8 |
| reliability: news on the allowlist (also a list Profile's attached news) | — | 0.6 |
| reliability: `MODEL_READ` identity value | — | 0 (as today: a model reading is not identity evidence) |
| condition basis: flag or category mapping | `SCORE_CONDITION_BASIS` | 100 |
| condition basis: unmapped category | `UNMAPPED_CATEGORY` | 40 |
| condition basis, source person: CONVICTED / WANTED / INDICTED / ALLEGED | `SCORE_CONDITION_STAGE` | 100 / 90 / 80 / 50 |
| condition basis, source person: ACQUITTED, DISMISSED / not a suspect | `SCORE_CONDITION_STAGE` | 10 / 0 |
| source person, stage UNKNOWN (any reason) | `SCORE_CONDITION_STAGE_UNKNOWN` | UNKNOWN by rule, outside the sum |
| `T_hit` / `T_unknown` | `SCORE_CONDITION_BAND` | 60 / 30 |

With these, a vendor Profile's conditions come out as today (flag or mapping 100 → HIT, unmapped 40 → UNKNOWN); a news report of an indictment (0.6 × 80 = 48) is UNKNOWN, a press release of it (0.8 × 80 = 64) is HIT, and a conviction in the news (0.6 × 100 = 60) is HIT exactly at the threshold; an allegation in the news (0.6 × 50 = 30) is UNKNOWN exactly at `T_unknown`; an acquittal (at most 10) is NOT_HIT; a stage left UNKNOWN is UNKNOWN by rule.

## Explanation

Not built: while the score is measurement only, no explanation, lineage or `hit_evidence` carries it. Once it decides, each pair's explanation gains a score block, restating the record and adding nothing:

- per item: group or condition, comparison status, level, source `[S#]` and its reliability, provenance (`RULE_READ`, `MODEL_READ`, `PROFILE_FIELD`), points and code;
- the vetoes and caps that applied, each with its code;
- the sum, the band, the threshold it crossed and the policy version with its approval state; the summary's conclusion says the band in words first and the codes after it, as today;
- `wouldChangeIf` re-runs the sum with one assumed item, as the [contrastive explanation](identity-adjudication.md#contrastive-explanation) does with the table.

The policy version is recorded in lineage; `hit_evidence` gains the points per item, so calibration and monitoring read the items, not the JSON. The `SCORE_*` codes (kind `SCORE`, among them `SCORE_HELD_PENDING_VALIDATION`, `SCORE_CAPPED_PROFILE_CONFLICT`, `SCORE_CAPPED_GENDER_CONFLICT`, `SCORE_CAPPED_ENTITY_RECORD`, `SCORE_CONDITION_STAGE_UNKNOWN`) are in the code catalog; each item otherwise reuses the evidence's own code (`UNIQUE_IDENTIFIER_AGREEMENT`, `FULL_DOB_CONFLICT` …).

## Validation

- **Trial calculation** (user, 2026-11-26). The score runs in measurement only, computed beside the stage 3 table and compared with it on the Golden Cases and labelled sets; it is not the decision, is not in a Case's result, and the weights stay PROPOSED. `ScoringRuleTests` holds every cell of the table, `GoldenMetricsTests` the Golden Case lines and the comparison.
- **Synthetic sets** (user, 2026-11-26). The end-to-end, adversarial and real-shape synthetic sets run generated Cases with known truth through the readers, `CaseAnalyzer` and this rule; they find logic holes and compare thresholds under stated assumptions, so their counts are not evidence for release and real labelled pairs are still needed. Figures: [current evidence](../plan/poc-delivery-plan.md#current-evidence).
- **Switch gates (proposed).** The score replaces the stage 3 table in the analysis only when the user has approved the table above, it reproduces every validated Golden Case, it is not worse than the table on the [scorecard](../plan/poc-delivery-plan.md#requirement-scorecard) on the labelled sets, and the precision of its automatic MATCH has been measured on labelled pairs and accepted by the user.
- **Pair measures.** Decided rate (MATCH or NO_MATCH without a reviewer) first, guarded by the two precisions below; right person kept (true matches not at NO_MATCH, share at MATCH and PROBABLE_MATCH), automatic MATCH precision (different persons at MATCH), wrong person excluded (different persons at LOW priority or NO_MATCH, never a true match there), per source; NO_MATCH precision stays 100% on every labelled set before any NO_MATCH band opens.
- **Data.** Golden Cases (GC-01 to GC-03, GC-11, GC-12; no Sanction or PEP case yet), labelled customer × Profile pairs (none yet beyond the Golden Cases), the annotated judgments, press releases and news for the extracted inputs ([news model training](../plan/news-model-training.md), [stage model training](../plan/stage-model-training.md)).
- **Determinism** (proposed, not built). A test re-scores stored revisions and fails on any difference under the same policy version.
