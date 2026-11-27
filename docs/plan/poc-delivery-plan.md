# PoC Delivery Plan

## Validation dimensions

Measure separately:

- extraction correctness and data quality;
- Candidate Profile recall and duplicate rate;
- identity-adjudication safety;
- source-condition mapping correctness;
- Case versioning, retry and completeness;
- explanation and decision traceability.

## Metrics

The system must be able to say match or not: a system that leaves every pair to review is conservative but worthless (user, 2026-11-26). The primary identity measure is the **decided rate**, the share of pairs concluded MATCH or NO_MATCH without a reviewer, guarded by Auto-MATCH precision and Auto-NO_MATCH precision; the Review Rate is its complement.

- decided rate (primary), with Auto-MATCH and Auto-NO_MATCH precision as its guards;
- field extraction correctness;
- missing/unparseable/ambiguous/conflict rates;
- Candidate Profile Recall@K;
- Duplicate Hit Rate;
- Auto-NO_MATCH Precision;
- Auto-MATCH Precision;
- Review Rate;
- Probable-Match Yield;
- Evidence Completeness;
- Decision Traceability;
- Human Override Rate;
- Re-adjudication Stability;
- batch completion, retry recovery and latency distribution.

No target percentage or latency is committed before representative data and bank tolerance are agreed.

### Golden Case results

`tests/Aml.Tests/GoldenCases/GoldenMetricsTests` computes these over the Golden Cases in `samples/golden` (GC-01 to GC-03 synthetic, GC-11 and GC-12 anonymized real; six pairs). The comparison-cell rates come from `AnalysisQuality.Of`, the function that computes the data quality stored with every revision. Augmented cases are excluded. Five cases are too few for any rate to mean more than "the rules reproduce the cases".

| Metric | Value | Note |
|---|---|---|
| Decided rate | 33% (2/6) | both GC-01 pairs, NO_MATCH by the validated veto; no MATCH |
| Duplicate Hit Rate | 0% | 9 candidate rows fold into 6 hits, one per Profile |
| Auto-NO_MATCH Precision | 100% (2/2) | both GC-01 pairs |
| Auto-MATCH Precision | n/a | no MATCH is emitted until the scoring rule decides; then this measures it, and it gates the switch ([scoring rule](../feature/scoring-rule.md#validation)) |
| Review Rate | 67% (4/6) | |
| Probable-Match Yield | 100% (2/2) | GC-03, GC-11 |
| Evidence Completeness | 100% | 9 comparison rows per pair |
| Decision Traceability | 100% | reason codes and a conclusion on every pair |
| Missing / ambiguous / conflict rate | 56% / 2% / 0% | comparison cells; missing is mostly Profile DOB, document and address, and the customer's employer (an occupation alone is not comparable with Profile roles, so GC-01's employment cell is missing, not ambiguous) |
| Re-adjudication Stability | 100% | an unchanged re-run lists no changes |

### Scoring rule beside the stage 3 table

`GoldenMetricsTests.Score_bands_beside_the_stage_3_table` computes the [scoring rule](../feature/scoring-rule.md) with the PROPOSED table (`score-proposal-0.1`) on the same six pairs, in measurement only (the table decides).

| Measure | Stage 3 table | Score bands |
|---|---|---|
| agreement with the other | — | 6/6 pairs; no disagreement |
| decided (MATCH or NO_MATCH) | 2/6 | 2/6 (GC-01 ×2, NO_MATCH by veto) |
| PROBABLE_MATCH | 2 (GC-03, GC-11) | 2, total 8 = `T_probable_exact`, zero margin |
| REVIEW | 2 (GC-02, GC-12) | 2, total 0 |

Why the four undecided pairs stay undecided:

| Reason | Pairs | What the data lacks |
|---|---|---|
| only the prerequisites agree (no item) | 2 (GC-02, GC-12) | GC-02: the Profile has no DOB, document, employment or address; GC-12: the Profile has no DOB, address or other identifier, the customer no document, employer or relatives |
| support below `T_match` (birth year +8) | 2 (GC-03, GC-11) | the Profile has no document and no employment; GC-11 no address |
| held or capped by a conflict | 0 | |
| entity record | 0 | |

Could any pair ever conclude, given the data it carries? Bound: a compared group keeps its points, a group present on both sides but undecided gets its best proposed points, a missing group 0. The highest reachable totals are 0 (GC-02, GC-12), 8 (GC-11) and 23 (GC-03, if its cross-country address could ever agree), so **no pair can reach MATCH (60)**. **No pair can reach NO_MATCH by the sum**: no negative item comes from a validated rule, and none of the four carries a conflict at all. The undecided pairs are a missing-data problem, not a rule problem: every one lacks a Profile document and a full Profile DOB (GC-03 and GC-11 have a birth year only). Whether the rule could conclude more is the [proposal on reaching MATCH and NO_MATCH](../feature/scoring-rule.md#proposal-reaching-match-and-no_match-by-the-sum); five cases say nothing about its precision.

Not measured: field extraction correctness and Recall@K (need HTML samples and labeled extraction), Human Override Rate (no human workflow in the PoC), batch completion, retry recovery and latency (need representative volume; the `aml.*` metrics collect them). The [synthetic end-to-end set](#synthetic-end-to-end-set) measures field extraction on generated exports.

### Synthetic end-to-end set

Six golden pairs cannot say how often the system concludes, so the user decided (2026-11-26) to measure on synthesized data. `tests/Aml.Tests/Synthetic` generates Cases from a fixed seed (no clock): invented customers and related parties, invented list Profiles and attached texts, written as the bank's `.txt` / `.html` export the readers parse, each candidate with its truth. The set is generated at test time, not committed. It runs through `TxtCaseReader` (with `HtmlExport`) and `CaseAnalyzer` in process; `SyntheticCaseServiceTests` imports 14 of the Cases through `CaseService` on SQLite and `SyntheticCasesApiTests` uploads 2 through `POST /api/cases`, and both read back the same hits as the in-process run. The stage is the rules' (no model in CI); `SyntheticSetTests.Report_with_the_stage_models` runs the same set with both stage models when `AML_STAGE_MODEL`, `AML_STAGE_LLM_URL` and `AML_STAGE_LLM_MODEL` are set (not run here). `AML_SYNTHETIC_REPORT=<file>` writes every table below. `SyntheticSetTests` pins the generator and every count of the tables below (chain stages, legal-stage split, losses, identity per kind, sensitivity). The set measures and changes nothing itself; the reader holes it found were fixed in the readers ([below](#reader-holes-found)).

**What it can and cannot show.** It finds logic holes (a rule that mis-decides a pair that is plainly the same or another person), reader holes (a form the readers do not ingest), and compares thresholds under the stated assumptions. Its precisions are not evidence for release: the chance agreements of strangers and the completeness of records are assumptions, not measured, and real labelled pairs are still needed before any switch gate. The texts are templates written for this set (no sentence copied from a real document), so a reader that reads them proves no more than that it reads these forms.

**Unit of truth.** Each candidate of a Case has: identity (same person or another), the source condition (list type, whether it holds, the legal stage the record tells), and so the expected cell of the combination matrix: NOT_APPLICABLE when the condition does not hold, NO_HIT for another person, HIT for the customer. An automatic end result is right when it equals that cell.

#### Assumptions

Every number is an assumption in `SyntheticAssumptions` (one record), except the comparison-cell missing rate, which was tuned towards the golden 56% ([Golden Case results](#golden-case-results)): measured 49% on list records and 52% on adverse records. The bank supplies no customer employer or relatives ([scoring rule](../feature/scoring-rule.md#proposal-reaching-match-and-no_match-by-the-sum)), so the customers carry none.

| Assumption | Value |
|---|---|
| seed; analysis date | 20261126; 2026-11-26 |
| Cases per first Profile's kind | Sanction 40, PEP 40, RCA 30, official wanted list 30, adverse with news 60, with a prosecutors' press release 50, with a court release or judgment 50 |
| first Profile is the customer | 50% |
| a second Profile of a same-name stranger in the Case | 20% |
| a stranger has the same full name (else one given character differs) | 70% |
| stranger agrees by chance: gender; birth year; whole date given the year | 90%; 10%; 1% |
| stranger born within two years of the customer (not set: implied by the 10% shared year and births drawn uniformly over 1955–1998) | 14 of the 95 strangers' pairs with a DOB on both sides (15%), above the about 7% a 60-year spread would give |
| same person on the Profile: romanized name only / Hanyu Pinyin (list records); simplified script; customer's name only as an alias | 15% / 5%; 10%; 10% |
| same person: a one-day recording error in the Profile's birth date | 3% |
| same person: an old address on the Profile; the Profile's passport is the old one | 50%; 30% |
| customer: passport (else national ID); birth date, gender, document missing | 10%; 2% each |
| sanction delisted (the condition does not hold); PEP former (holds) | 25%; 25% |
| an RCA Case screens the customer's related party | 50% |
| candidate flags filled, consistent with the truth (else empty, as in the golden exports) | 20% |
| customer and Profile files as HTML | 20% |
| text names a second person with another outcome; of those in the person's sentence | 30%; 50% |
| press release writes a masked name (陳姓男子); stated age one year off | 30%; 10% |

| Profile field present (DOB precision day / month / rest year) | Sanction | PEP | RCA | Wanted | News | Press release | Court |
|---|---|---|---|---|---|---|---|
| birth date | 70% (60/10) | 50% (30/10) | 40% (20/10) | 60% (50/10) | 30% (10/10) | 30% (10/10) | 40% (30/10) |
| gender | 90% | 95% | 90% | 90% | 80% | 80% | 80% |
| citizenship | 95% | 100% | 90% | 100% | 90% | 90% | 90% |
| document (half national ID, half passport) | 40% | 5% | 0 | 30% | 0 | 0 | 0 |
| address | 30% | 20% | 10% | 30% | 30% | 30% | 30% |
| text states age / birth date (民國, full) / gender | — | — | — | — | 60% / 0 / 50% | 70% / 0 / 80% | 0 / 90% / 90% |

Legal stages, equally often: news ALLEGED, INDICTED, CONVICTED, ACQUITTED; press release ALLEGED, INDICTED, DISMISSED; court release or judgment (half each) CONVICTED, ACQUITTED. PEP roles start 30 to 44 years after the person's birth.

#### The set

300 Cases (58 as HTML), 359 candidates, 147 of them the customer.

| Kind | same, holds | same, does not hold | another, holds | another, does not hold |
|---|---|---|---|---|
| Sanction | 12 | 2 | 24 | 7 |
| PEP | 22 | 0 | 30 | 0 |
| RCA | 13 | 0 | 25 | 0 |
| official wanted list | 19 | 0 | 17 | 0 |
| adverse, news | 27 | 0 | 46 | 0 |
| adverse, press release | 28 | 0 | 32 | 0 |
| adverse, court | 24 | 0 | 31 | 0 |

#### Chain stages (the stage 3 table deciding, the DOB conflict released 2026-11-27)

| Stage | Result |
|---|---|
| ingest | 300/300 Cases read; no file refused |
| fields | 359/359 candidates with every checked field right (subject names, gender, DOB, nationality, document; Profile names, gender, DOBs, citizenship, documents, list status; news date), `.txt` and `.html` alike |
| stated next to the name | age alone 22/22; age after the gender in one bracket, （男，45歲）, 50/50 (0/50 before the fix); birth date on a party line 23/23; gender 121/124 (the 3 not looked into; a customer without a gender gives no reading by design) |
| candidate | 359/359 found, one hit per Profile, rows folded |
| source condition | 350/359 right; 9 UNKNOWN (delisted sanctions, which the spec never makes NOT_HIT); 0 wrong |
| legal stage | 224/224 right: wanted list 36/36, judgments 26/26, news 73/73, press release 60/60, court release 29/29 (before the reader fixes 186/224: news 62, press release 48, court release 14) |
| identity | NO_MATCH 118 (all another person: 80 by the DOB conflict, 4 by the PEP role veto, 34 by a stated age or birth date more than two years off, `STATED_DOB_CONFLICT`: ages 26, birth dates 8; 81 pairs carry a DOB conflict, 53 `DOB_CONFLICT` and 28 `FULL_DOB_CONFLICT`, one of them closed first by the role veto), PROBABLE_MATCH 57 (51 same person), REVIEW 184; no wrong automatic decision |
| pair result | 118/359 concluded without a reviewer: 113 right, 5 NO_HIT on a delisted sanction of another person whose truth cell is NOT_APPLICABLE |

#### End results

Before the DOB conflict was released (2026-11-26 run) the product decided 0 pairs and the score band 7; with the DOB conflict released and the Sanction/PEP/RCA release control still in place it decided 38; with the control lifted 84; with the stated-value conflict 118.

| Measure | Product (stage 3) | Score band (`score-proposal-0.2`) |
|---|---|---|
| decided (no reviewer) | 32.9% (118/359), was 23.4% (84/359) before the stated-value conflict | 33.7% (121/359), was 24.2% (87/359) |
| automatic HIT (MATCH), precision | — | 3, 100% |
| automatic NO_HIT (NO_MATCH), precision | 118, identity 100%; 113 cells right, 5 NO_HIT where the truth's cell is NOT_APPLICABLE (another person on a delisted sanction, the condition UNKNOWN) | the same 118 |
| automatic NOT_APPLICABLE | 0 | 0 |
| PROBABLE_MATCH (same person) | 57 (51), was 53 (51) | 55 (49), was 51 (49) |

Automatic NO_HIT of the product per list type, all another person (identity NO_MATCH precision 100% in each): Sanction 23 (18 cells right, 5 delisted), PEP 13, RCA 10, wanted list 7, news 26, press release 21, court ruling 18 (the 34 stated-value conflicts: news 13, press release 13, court 8). Before the release control was lifted, the Sanction, PEP and RCA ones (46) were held at REVIEW.

Wrong automatic decisions, by kind:

| Kind | Product | Score band |
|---|---|---|
| NO_MATCH of the same person | 0 | 0 |
| MATCH of another person | 0 (none emitted) | 0 |
| NO_HIT where the truth's cell is NOT_APPLICABLE (another person on a delisted sanction, the condition UNKNOWN; identity right) | 5 | 5 |
| NO_HIT or NOT_APPLICABLE where the truth is HIT or REVIEW | 0 | 0 |

Where each end result was lost, first stage of the chain that lost it (product); what still holds the 241 pairs in REVIEW:

| Lost at | Pairs |
|---|---|
| identity REVIEW, row 6: nothing beyond the prerequisites on both sides | 154 |
| identity PROBABLE_MATCH of the customer: no automatic MATCH in Phase 1 | 49 |
| identity REVIEW, row 5 partial support | 28 |
| identity REVIEW, row 1: document conflict | 1 |
| identity REVIEW, row 1: stated age or birth date differs | 0 (was 34: ages 26, birth dates 8; all more than two years off with no Profile DOB, now NO_MATCH by `STATED_DOB_CONFLICT`) |
| condition UNKNOWN: delisted sanction | 9 (4 in REVIEW; 5 concluded NO_HIT, the wrong cells above) |
| release control | 0 (was 41; lifted 2026-11-27) |
| identity PROBABLE_MATCH of another person | 5 (was 2) |
| identity REVIEW, row 1: Profile DOB conflict | 0 (was 85: now NO_MATCH, or within tolerance) |
| extraction, candidate | 0 |

Score band: 154 nothing beyond the prerequisites, 27 support below the thresholds, 47 PROBABLE_MATCH of the customer below `T_match`, 9 condition (5 of them concluded NO_HIT on a delisted sanction, see above), 5 PROBABLE_MATCH of another person, 1 held by the pending document rule; 116 right.

Of the stated ages, 19 are read after a gender (below); all of them are of another person more than 2 years off the customer's and conclude NO_MATCH with the other stated-value conflicts; none of the customer's stated ages or birth dates differs.

The set is missing data first: 154 of 359 pairs carry nothing comparable beyond name, gender and nationality, and no threshold decides them. Second comes the missing automatic MATCH (49 PROBABLE_MATCHes of the customer), third partial support (28).

#### Identity: stage 3 table and score band

| Pairs | n | stage 3 NO_MATCH (right) | stage 3 PROBABLE (same) | score MATCH (right) | score NO_MATCH (right) | score PROBABLE (same) |
|---|---|---|---|---|---|---|
| all | 359 | 118 (118) | 57 (51) | 3 (3) | 118 (118) | 55 (49) |
| Sanction | 45 | 23 (23) | 12 (10) | 2 (2) | 23 (23) | 10 (8) |
| PEP | 52 | 13 (13) | 9 (8) | 0 | 13 (13) | 9 (8) |
| RCA | 38 | 10 (10) | 2 (2) | 0 | 10 (10) | 2 (2) |
| official wanted list | 36 | 7 (7) | 15 (13) | 1 (1) | 7 (7) | 14 (12) |
| news / press release / court | 73 / 60 / 55 | 26 / 21 / 18 (all right) | 7 (6) / 4 / 8 (all same) | 0 | 26 / 21 / 18 (all right) | 7 (6) / 4 / 9 (all same) |
| carries 0 groups beyond the prerequisites | 100 | 2 (2) | 0 | 0 | 2 (2) | 0 |
| carries 1 | 155 | 67 (67) | 24 (19) | 0 | 67 (67) | 25 (20) |
| carries 2 or more | 104 | 49 (49) | 33 (32) | 3 (3) | 49 (49) | 30 (29) |

Wrong automatic identity decisions: none, under either (no customer at NO_MATCH, no stranger at MATCH). The near misses, by the items that made them:

| Kind | Items | Pairs |
|---|---|---|
| PROBABLE_MATCH of a same-name stranger (stage 3 row 4 and score) | `SCORE_DOB_YEAR` +8 alone, birth year agreeing by chance, prerequisites EXACT | 2 (wanted list) |
| PROBABLE_MATCH of a same-name stranger born within two years (stage 3 row 4 and score) | `DOB_AGREES_WITHIN_TOLERANCE` +8 alone, prerequisites EXACT | 4 (Sanction 2, PEP 1, news 1) |
| held at REVIEW only by `SCORE_HELD_PENDING_VALIDATION`; NO_MATCH if the document rule were released | `DOCUMENT_IDENTITY_CONFLICT` −40 | 1, another person |

#### Birth-date tolerance (2026-11-27)

Of the 95 strangers' pairs with a DOB on both sides, 14 are born within two years of the customer; 4 of them agree on the birth year at year precision (as before), 10 now agree within tolerance. The tolerance's effect, product and score:

| Pairs carrying | n | stage 3 | score, tolerance +8 (proposed) | score, tolerance +25 |
|---|---|---|---|---|
| another person, DOB more than two years off | 81 | NO_MATCH 81 | NO_MATCH 81 | NO_MATCH 81 |
| another person, DOB within tolerance | 10 | PROBABLE_MATCH 4, REVIEW 6 | PROBABLE_MATCH 4, REVIEW 6 | PROBABLE_MATCH 4, REVIEW 6 |
| the customer, DOB within tolerance (the one-day typo on a full date) | 1 | REVIEW | REVIEW | REVIEW |
| the customer, one-day typo on a month-precision Profile DOB | 1 | PROBABLE_MATCH | PROBABLE_MATCH | PROBABLE_MATCH |

- The one-day recording errors in the customer's Profile DOB (2 pairs at the assumed 3%) no longer conflict: before, one was `FULL_DOB_CONFLICT` and would have been the one wrong NO_MATCH had the −25 item been released; now it is agreement within tolerance (REVIEW, its prerequisites are not all EXACT), the other was never a conflict.
- Tolerance +25 instead of +8 changes no band: with all prerequisites EXACT +8 already reaches `T_probable_exact`, and +25 alone reaches neither `T_probable` 30 nor `T_match` 60. So 4 strangers born within two years reach PROBABLE_MATCH under either and none reaches MATCH; the 6 others have a prerequisite that is not EXACT (a near name or gender) and stay REVIEW.
- The generator draws a stranger's birth date from 1955–1998 (plus 10% in the customer's year), so 15% of strangers fall within two years, about twice the 7% a 60-year spread of births would give; real customer populations decide this rate.

Same person by variation (stage 3, REVIEW of all): romanized-only name 6/9, simplified script 8/13, alias 5/8, Hanyu Pinyin 1/1, against 96/147 for every pair of the customer. A renewed passport (3) never met a comparable customer passport at these rates, so the document conflict the spec warns about did not occur (0 same-person document conflicts).

#### Sensitivity (score band, identity; the PROPOSED table unchanged)

| Thresholds | Decided | Auto MATCH (precision) | Auto NO_MATCH (precision) | PROBABLE_MATCH (same person) |
|---|---|---|---|---|
| proposed: `T_match` 60, `T_probable` 30, `T_probable_exact` 8, `T_no_match` −25 | 121 (33.7%) | 3 (100%) | 118 (100%) | 55 (89.1%) |
| `T_match` 40 / 50 / 70 | 125 / 121 / 118 | 7 / 3 / 0 (100%) | 118 | 51 / 55 / 58 (88.2 / 89.1 / 89.7%) |
| `T_probable` 20 / 40 | 121 / 121 | 3 | 118 | 57 (89.5%) / 55 (89.1%) |
| `T_probable_exact` 15 / 25 | 121 / 121 | 3 | 118 | 28 (100%) / 21 (100%) |
| `T_no_match` −10 / −40 | 121 / 121 | 3 | 118 | 55 (89.1%) |
| agreement within tolerance +25 (instead of +8) | 121 | 3 | 118 | 55 (89.1%) |

The 118 automatic NO_MATCHes are vetoes (the DOB conflict, the role veto and the stated-value conflict), so no threshold moves them. `T_no_match` moves nothing: no negative item left is validated, so every sum below it is held. `T_match` 40 adds 4 automatic MATCHes, all right here only because the generator never gives two persons one document number or one full date by more than the assumed chance. `T_probable_exact` above 8 removes the six strangers raised by a birth year or a birth date within two years, and with them 21 to 28 PROBABLE_MATCHes of the customer.

#### Reader holes found

The first run found five forms the readers did not read; each was fixed in its reader (2026-11-26), the stage forms measured through the `measure-stage` skill ([decisions in force](stage-model-training.md#decisions-in-force-and-their-evidence): on the six development sets no person's rules stage moved, so the vote's precision and coverage are unchanged). Pairs wrong before the fix, none after:

| Hole | Pairs | Fix |
|---|---|---|
| an age after the gender in one bracket, `陳建宇（男，45歲）`, was not read (`StatedAgeCheck`); the age alone was | 50 of 50 | a gender may stand before the age in the bracket ([ages in source text](../feature/extraction-and-normalization.md#ages-in-source-text)) |
| a court release's acquittal written `今日宣判，{姓名}無罪` read UNKNOWN (the ruling verb is in the clause before) | 11 of 13; the other 2 read CONVICTED from a co-defendant | a clause ending 宣判 is read with the next |
| another person's outcome later in the person's own sentence won (`…判處甲有期徒刑一年，同案的乙則獲判無罪`) | 15 (news 8, court 4, press release 3) | a clause opening 同案 that does not name the person is not theirs, to the next clause naming them |
| another named person's dismissal in the next sentence was taken as the person's (`同案被告乙則獲不起訴處分`), so an indictment read DISMISSED | 7 (news 3, press release 4) | the same |
| a press release with a masked name indicts by `提起公訴`, which counted only in a sentence naming the person: read ALLEGED | 5 of 7 (the other 2 read DISMISSED from a co-defendant) | in a text that never names the person, 提起公訴 is theirs |

The court-release template writes no case number, which a real court release opens with (本院114年度訴字第1號…宣判, the form `RulingForm` knows a release by), so the template is read as a report; it was kept because news reports write the same `法院今日宣判，{姓名}無罪` without one, and the fix is for that form ([stage from attached news](../feature/watchlist-and-adverse-information.md#stage-from-attached-news)).

#### Proposed changes (the user decides)

1. **DOB conflict: decided and built** (user, 2026-11-27: more than two years apart concludes NO_MATCH, two years or less agrees). Product decided rate 0% → 10.6% (38/359) under the release control, score 1.9% → 24.2% (87/359), no wrong NO_MATCH; the one-day Profile-DOB error that would have been wrong under the earlier −25 proposal is within tolerance. The document conflict stays pending: releasing it would close 1 more pair (another person).
2. **`T_probable_exact` 8**: a birth year or a birth date within two years, with exact prerequisites, raises same-name strangers (6 here: 2 by the year, 4 by the tolerance); stage 3 row 4 does the same. Raise it, or require more than the year, if the bank finds that rate realistic.
3. **Release control: lifted and built** (user, 2026-11-27, "lift it"). The 46 NO_MATCHes it held, all another person (Sanction 23, PEP 13, RCA 10), conclude NO_HIT: product decided rate 10.6% → 23.4% (84/359); 41 of them are right cells and 5 are NO_HIT on a delisted sanction, whose truth cell is NOT_APPLICABLE (no reviewer would act on either). The cost is a wrong automatic NO_MATCH on a sanctioned person, the most expensive error; here none, but the strangers' data are assumptions, and no real Sanction or PEP Golden Case or placeholder 1 January birth date has been tried.
4. **Readers**: the five holes above, fixed.
5. **Stated-value conflict: decided and built** (user, 2026-11-27). A birth date or dated age stated in attached news (or on a received page), every one more than two years off and the Profile without a DOB of its own, concludes NO_MATCH (`STATED_DOB_CONFLICT`): product decided rate 23.4% → 32.9% (118/359), score 24.2% → 33.7% (121/359); the 34 added are all another person. Not measured: an age stated as of a past event without 時年, or a traditionally counted age at the boundary, closing the same person ([open question](open-questions.md#external-sources)).
6. **Scoring rule, source kind: built** (measurement only). `ScoringRule.Identity` takes the source kind of the record's fields and of the text's stated values ([source reliability](../feature/scoring-rule.md#source-reliability)); the synthetic measure passes each Profile's kind for the text, the record at the list's factor, so the figures above are unchanged, and a received page's person is scored with the news factor for both.
7. **Sanction priority**: every Sanction pair of another person left in REVIEW is HIGH (8/8; the other 23 conclude NO_HIT), as the priority rule says (a raised Sanction is HIGH); scorecard row 2 asks for strangers at LOW. Lifting the release control did not change the rule, only how many strangers reach it (31 → 8).

#### External sources

For the first 20 Cases' customers, `SyntheticCaseGenerator.External` writes the source documents of the three [external sources](../feature/external-sources.md) with their truth: 20 news pages received by URL (HTML, the customer and another person named, both ALLEGED, nothing stated next to either name), 20 published judgments whose defendant is masked (林○玲 form, never a candidate by itself), and 60 grounding-search results (20 relevant, 20 about a same-name stranger, 20 about no one).

**Received URL, run** (`SyntheticPageTests`): the 20 pages through the page path (URL guard, polite fetch from memory, main text, `PageReadings`), with the rules alone (CI) and with the news models NER20 / event43 (`AML_NEWS_MODELS`, skipped without it). Every page is fetched and its main text kept (20/20); no value is stated next to a name and none is read (0), and no person is invented (0 extra).

| Reading | Customer found | Co-named found | Stage right | Party SUSPECT |
|---|---|---|---|---|
| rules | 20/20 (`RULE_READ`) | 1/20 | 21/21 | 0/21 |
| rules + news models | 20/20 (`RULE_READ`) | 20/20 (19 `MODEL_READ`) | 40/40 | 8/40 |

- The customer stands right after the dateline (〔本報記者／臺北報導〕王…涉嫌), the standard lead of Taiwanese news: a dateline's closing bracket opens a clause for the digest's rules.
- The co-named person follows 同案的 (同案的林…亦遭約談), which no rule takes as a name's place; the news models find them. The one the rules count found has the customer's very name on that page, so the one person read holds both (the digest keeps one person per written name).
- Party: the rules read it only from a role word, and neither name has one; the models' party for the co-named persons is 8 suspects, 8 victims, 1 official, 2 none. Nothing decides on it.

#### Received URL, matched against the customer

`SyntheticCaseGenerator.ReceivedPages` adds to the 20 pages above 20 variants (seed 20261127, customers from the Cases after the first 20 with a full birth date, a gender and a three-character name; the other sets and their digests unchanged), 4 each: the customer as victim (被害人), as witness (證人), a same-name stranger with a birth date stated 3 to 15 years from the customer's, one stated within a year, and the customer with their own birth date stated. `AgePages` then adds 24 pages stating an age next to the name, `被告王…（45歲）` (seed 20261128, the next customers of the same kind; the pages above unchanged), 8 each: the customer (the age at the publication date, one year off at 10%), a same-name stranger whose age differs by 1 to 2 years, and one 3 to 15 years. Each states its publication date (within three years before AsOf) in `article:published_time` at 50%, on a date line under the title at 30% (half 民國), nowhere at 20%; realised 11 / 8 / 5. `SyntheticPageCaseTests` fetches each page from memory, reads it, pins it into its customer's Case files and analyses the Case naming it (`CaseAnalyzer`), with the rules alone (CI) and with the news models (`AML_NEWS_MODELS`); the truth per person is the combination-matrix cell.

| Measure | Rules | Rules + news models |
|---|---|---|
| pages read | 64/64 | 64/64 |
| publication date read (metadata / text / missing), each as written | 11 / 8 / 45 (the 40 earlier pages state none) | same |
| persons screened (recorded in the revision) / not a candidate | 72 / 8 | 91 / 27 (19 `MODEL_READ`) |
| right person found: suspect customers a candidate | 32/32 | 32/32 |
| right person found: customers as victim or witness a candidate | 8/8 | 8/8 |
| wrong person excluded: other persons, another name, never a pair | 27/27 (19 not read after 同案的, 8 by name) | 27/27 (by name) |
| other persons with the customer's very name, a candidate | 25 (24 strangers, 1 page naming the customer twice) | 25 |
| condition right / stage right | 61/65 / 57/57 (the 4 witnesses `HIT`: 證人 concludes nothing, and the indictment in the witness's own sentence is read for the name; the pair stays `REVIEW`) | 61/65 / 57/57 |
| pairs carrying a comparable stated age | 19/24 age pairs (the 5 undated: `AGE_NOT_COMPARABLE`) | same |
| pairs decided, all right | 14/65 (the 4 victims `NOT_APPLICABLE`; 10 strangers `NO_HIT` by `STATED_DOB_CONFLICT`) | 14/65 |
| customers concluded NO_MATCH | 0 | 0 |
| wrong automatic results | 0 | 0 |

The two-year rule on what a page states (user, 2026-11-27, [two-year conflict on stated values](../feature/identity-adjudication.md#two-year-conflict-on-stated-values)) concludes 10 strangers NO_MATCH (`STATED_DOB_CONFLICT`): the 4 birth dates stated 3 to 15 years from the customer's and the 6 dated ages more than 2 off; none of them a customer. Before it, the pairs decided were 8/65 and these 10 sat on row 1 (`NEWS_BIRTH_DATE_DIFFERENCE` 4, `AGE_DIFFERENCE_AT_REPORT` 6).

Of the 51 pairs left in REVIEW, the 4 customers named as witnesses wait on the condition as well (their condition is `HIT` though the truth is a witness: 證人 concludes nothing, since on real labelled news 9 of 311 labelled suspects, 2.9%, were written 證人 where a co-defendant testifies, [external sources](../feature/external-sources.md#matched-against-the-customer); before that change the 4 were `NOT_APPLICABLE` and 18/65 decided). The other 47 all wait on identity: a page has no field beside the name, so nothing beyond it is comparable (row 6, `NO_NON_PREREQUISITE_SUPPORT`: customers 28, strangers 15). Birth dates: the customer's own agrees (row 5, `PARTIAL_SUPPORT_ONLY`: 4); a stranger's within a year is neutral (`PAGE_BIRTH_DATE_WITHIN_TOLERANCE`: 4, row 6). Ages, at the publication date: the customer's 5 `AGE_COMPATIBLE_AT_REPORT` (no support, row 6), the near strangers' 8 compatible too (within ±2, row 6); the 5 undated pages' ages (customers 3, far strangers 2) are recorded, `AGE_NOT_COMPARABLE`, and conclude nothing. The 15 strangers' row 6: 8 near ages, 4 near birth dates, 2 undated ages, 1 page naming the customer twice. Within two years a page value stays neutral because agreement would be wrong here (measured only): taking a dated age as a birth-year field under the Profile DOB rule, agreement included, makes the 8 near strangers and the 5 customers agree, and the 4 near birth dates would agree too: 12 strangers raised wrongly (within two years, as built into the set). The score band (news factor for both sides) vetoes the same 10 (NO_MATCH) and leaves every other pair in REVIEW (+9 at most, the prerequisites not all EXACT). The news models change no decision: the persons only they read are other persons, excluded by name.

The same rule applies to a list Profile's attached news; on the [synthetic set](#synthetic-end-to-end-set) it concludes the 34 pairs once on row 1 for a stated difference (`AGE_DIFFERENCE_AT_REPORT` 26, `NEWS_BIRTH_DATE_DIFFERENCE` 8), none of them the same person. Within two years a page's birth date is neutral, attached news keeps its difference.

**Received URL, publication date on real pages** (`PublishedDates` through `MainText.FromHtml` for the stored HTML and `PdfText` for the PDFs, as `WebPages.ReadAsync` reads them; the real labelled development documents, counts only). A date on 158 of 190: a publication date on 55 (dev 27/161, dev2 28/29), the update date in its place (`UPDATE_DATE`) on 103 (all dev), `MISSING` on 32; 0 of 190 before the labels were read (every HTML page and PDF states its date under a label or states only an update date, and no page carries the metadata the reader knew). By kind: HTML publication 22/128 (every page with a publication label: templates of 13, 6, 2 and 1 pages), update 103/128 (one template that writes no publication label; the article's 更新日期 on every one, not the site footer's), `MISSING` 3 (a template whose update label is not reached); PDF 33/33 publication (`發稿日期` or `新聞稿（…）` at the head, after a letterhead of up to four lines); judgments 0/29 (their date stands at the end and is the ruling's). On the 22 pages stating both, the labelled update date is the publication day on 20, 1 to 30 days later on 1, 31 days to a year later on 1, never earlier; `DC.Date` (not read) is the publication day on 9 of those 22 and more than two years before it on 13; on the 103 update-only pages it equals the article's 更新日期. So an update date is shown but is no report date: the 103 pages' stated ages stay `AGE_NOT_COMPARABLE`. The truth is a mechanical check, not a person reading each page: a looser independent pattern (any date after a publication or update label anywhere) and masked page shapes (labels and digit patterns, no words). No date read disagrees with it, on the 190 and on 40 HTML pages stratified by template (17 publication, 20 update, 3 `MISSING`; 0 disagreeing, 95% interval 0 to 8.8%). The synthetic pages state their date in `article:published_time` or a bare date line, forms none of the measured real pages uses; they are kept, being the forms of commercial news sites.

**Judgments and search results: generated, not run** (the paths are not built).

### Adversarial synthetic set

A wrong conclusion is very costly, so an automatic MATCH may be proposed only on direct evidence and only after its wrong-MATCH risk is measured on data that holds the failure modes. The [end-to-end set](#synthetic-end-to-end-set) holds none of them: no stranger shares a document number, a type or country mismatch, a placeholder date, a twin or a relative. `SyntheticCaseGenerator.Adversarial` (seed 20261129, its own Cases; the end-to-end set and its numbers unchanged) builds them: one invented customer and one Sanction Profile (Active, flags empty, the condition holds) per Case, written as the bank's `.txt` export (customer 證件種類, 發照地, 證件號碼, contact address; list 性別, 日期, 國家資料, 地址, 身分證明 with 類型, 號碼, 國家), truth by construction. `AdversarialSetTests` runs them through `TxtCaseReader` and `CaseAnalyzer` and pins every count below; expected values follow from the construction and the stated rules. 335 Cases, 115 of them the customer; every field read right, every pair found, every condition HIT.

It measures the candidate MATCH rule **B′** ([scoring rule](../feature/scoring-rule.md#candidate-match-condition-b), measurement only, `DocumentMatch` in the test project): a list document agrees with the customer's in number, type and a known, equal issuing country; the name EXACT (after the product's folding) or the same reading across romanisations; no hard conflict or downgrade; no evidence group in conflict (birth date, gender, nationality, stated values, document); a Person record; nothing `MODEL_READ`. **B** drops the type and country requirement; **B′∅** also accepts a document with no issuing country on either side. **B″** is B′ and a list birth date agreeing with the customer's: **B″-exact** at the list's own precision with no tolerance (full date equal, or a year-only list value equal to the birth year), a list 1 January read as its year only (detected as a full date with month 1 and day 1); **B″-tol** the product's DOB support, `DOB_AGREES_WITHIN_TOLERANCE` (two years) included.

The kinds fall in two groups. **Realistic**: what real lists and customer records show. **Theoretical limit**: built to probe the rules, not expected in real data; reported only as what a rule cannot see, never in a wrong-MATCH rate. The values follow the formats as general knowledge describes them, unverified against an official source: a Taiwan national ID is a letter, a gender digit 1 or 2, seven digits and a check digit, issued once for life (every national ID here passes that check; a stranger's near number is another valid ID, a customer's typo on the list fails the check, as an entry error would); a Taiwan passport number is nine digits and a renewal gives a new one; a Japanese passport number is two letters and seven digits. The end-to-end set's national IDs are not check-digit valid and stay unchanged (byte for byte); no rule reads the check digit, so nothing it measures depends on it.

Realistic (260 pairs: 115 customers, 145 strangers):

| Kind | n | truth | product today (identity, code) | B | B′ | B′∅ | B″-exact | B″-tol |
|---|---|---|---|---|---|---|---|---|
| same name, same full birth date, no list document | 15 | stranger | PROBABLE_MATCH, row 4 | 0 | 0 | 0 | 0 | 0 |
| same name, list birth year only | 15 | stranger | PROBABLE_MATCH, row 4 | 0 | 0 | 0 | 0 | 0 |
| same name, list 1 January of the customer's year (placeholder) | 15 | stranger | PROBABLE_MATCH, row 4 (`DOB_AGREES_WITHIN_TOLERANCE`) | 0 | 0 | 0 | 0 | 0 |
| same name, 1 January of the same year on both sides | 15 | stranger | PROBABLE_MATCH, row 4 | 0 | 0 | 0 | 0 | 0 |
| same name, another valid national ID one body digit off | 15 | stranger | REVIEW, row 1 `DOCUMENT_IDENTITY_CONFLICT` | 0 | 0 | 0 | 0 | 0 |
| same name, another valid national ID, two adjacent body digits swapped | 15 | stranger | REVIEW, row 1 | 0 | 0 | 0 | 0 | 0 |
| relative: same surname, gender, address, one given character, born 3+ years apart | 10 | stranger | NO_MATCH, `FULL_DOB_CONFLICT` | 0 | 0 | 0 | 0 | 0 |
| relative, born one calendar year apart | 10 | stranger | REVIEW, row 5 | 0 | 0 | 0 | 0 | 0 |
| twin: same surname, gender, address, birth date, one given character | 15 | stranger | REVIEW, row 5 | 0 | 0 | 0 | 0 | 0 |
| same name and address, no list birth date | 10 | stranger | REVIEW, row 6 | 0 | 0 | 0 | 0 | 0 |
| same passport number held by another person, no issuing country either side (lists often omit it; a nine-digit number can collide across issuers) | 10 | stranger | REVIEW, row 6 (not comparable) | **10 wrong** | 0 | **10 wrong** | 0 | 0 |
| national ID typo on the list failing the check digit (one digit 8, swap 7), birth date agrees | 15 | customer | REVIEW, row 1 | 0 | 0 | 0 | 0 | 0 |
| same national ID, list name Wade-Giles only / Pinyin only / simplified only (5 each) | 15 | customer | PROBABLE_MATCH, row 2 | 14 | 14 | 14 | 14 | 14 |
| same national ID, no list birth date | 15 | customer | PROBABLE_MATCH, row 2 | 15 | 15 | 15 | 0 | 0 |
| same national ID, list birth year only | 15 | customer | PROBABLE_MATCH, row 2 | 15 | 15 | 15 | 15 | 15 |
| renewed passport (list holds the old number), birth date agrees | 15 | customer | REVIEW, row 1 | 0 | 0 | 0 | 0 | 0 |
| same national ID, list birth date one year (8) or 1–5 days (7) off | 15 | customer | PROBABLE_MATCH, row 2 | 15 | 15 | 15 | 0 | 15 |
| same national ID, list birth date 3–15 years off | 15 | customer | REVIEW, `FULL_DOB_CONFLICT` downgraded | 0 | 0 | 0 | 0 | 0 |
| same passport, no issuing country either side, birth date agrees | 10 | customer | PROBABLE_MATCH, row 4 | 10 | 0 | 10 | 0 | 0 |

Theoretical limit (75 pairs, strangers only; why each is not expected):

| Kind | n | why not expected | product today | B | B′ | B′∅ | B″-exact | B″-tol |
|---|---|---|---|---|---|---|---|---|
| customer's passport number as a list Taiwan national ID | 15 | impossible by format: nine digits against a letter and nine digits | REVIEW, row 6 (not comparable) | 15 | 0 | 0 | 0 | 0 |
| same passport number as a Japanese passport, no list citizenship | 15 | impossible by format (a Japanese passport is two letters and seven digits); a collision with a nine-digit foreign format is unverified | REVIEW, row 6 | 15 | 0 | 0 | 0 | 0 |
| recycled passport (same type, country, number, another person), list birth date 3–15 years off, opposite gender | 10 | reuse of a Taiwan passport number is unverified | REVIEW, `FULL_DOB_CONFLICT` downgraded | 0 | 0 | 0 | 0 | 0 |
| recycled passport, list without birth date or gender | 10 | the same | PROBABLE_MATCH, row 2 | 10 | 10 | 10 | 0 | 0 |
| recycled passport, same gender, list gives the customer's birth year only | 10 | the same | PROBABLE_MATCH, row 2 | 10 | 10 | 10 | 10 | 10 |
| same name, gender, birth date and address, another person, no list document | 15 | not expected: twins are not given one name | PROBABLE_MATCH, row 4 | 0 | 0 | 0 | 0 | 0 |

Totals, realistic group and end-to-end set (the theoretical group is not in them):

| Rule | realistic: MATCH right / **wrong** | decided, today 10/260 | end-to-end set: right / wrong | decided, today 118/359 |
|---|---|---|---|---|
| B | 69 / **10** | +79 | 7 / 0 | +7 (125) |
| B′ | 59 / **0** | +59 | 7 / 0 | +7 (125) |
| B′∅ | 69 / **10** | +79 | 7 / 0 | +7 (125) |
| B″-exact | 29 / **0** | +29 | 5 / 0 | +5 (123) |
| B″-tol | 44 / **0** | +44 | 5 / 0 | +5 (123) |

The 10 wrong under B and B′∅ are another holder of the same passport number with no issuing country on either side: this is why a missing issuing country must not count as agreement.

What the rules cannot see (theoretical group, MATCH on another person): B 50, B′ 20, B′∅ 20, B″-exact 10, B″-tol 10.

Every pair a rule concludes is one the product leaves in REVIEW today, so the added count is its MATCH count. On the end-to-end set B, B′ and B′∅ conclude exactly the 7 customers whose list record holds their own document, B″ the 5 of them whose list birth date also agrees (the 2 others have none); both counts are checked against the generator's truth. No rule concludes a NO_MATCH, so none adds a wrong NO_MATCH.

**The product today** concludes no customer NO_MATCH (0 of 115); its only conclusions are the 10 relatives born 3+ years apart (NO_MATCH, right). It raises 60 realistic strangers to PROBABLE_MATCH (REVIEW outside): same name with an agreeing or tolerated birth date (row 4); in the theoretical group 35 more (same name, birth date and address 15, row 4; the recycled passport bare or with the same birth year 20, row 2). A list birth date more than two years from the customer's with an agreeing document: the document wins, the validated DOB conflict is downgraded to REVIEW (`SOURCE_QUALITY_DOWNGRADE`), for the customer (15) and the recycled-passport stranger (10) alike. The Pinyin-only name reads the same reading (`ROMANIZATION_VARIANT`) 5/5, the simplified-only name EXACT with `VARIANT_CHARACTERS` 4/5: the fifth writes 杰 for 傑, which the controlled variant list does not fold, so the name is only similar (PARTIAL) and every rule misses that customer (the product still PROBABLE_MATCH by the document).

**What the rules cannot see** (theoretical group). On the realistic group B′ and B″ conclude no wrong MATCH: none of its strangers' list records carries the customer's document with its type and country (none is built and none is expected; an assumption, true here by construction only). A recycled number on a list record whose only fields are the name and the same document (type, country, number) is the customer on every field the record carries: B′ concludes MATCH on all 10; B″ requires a list birth date and so leaves them to REVIEW. With a birth date and gender on the list (3–15 years off, opposite gender) every rule excludes them. What still beats B″ is a recycled number whose holder shares the customer's gender and birth year on a year-only list record: B″-exact and B″-tol conclude MATCH on all 10, as the record agrees with the customer on everything it carries. A recycled number whose holder shares the full birth date is not generated: it is the same failure (the record agrees on everything), only rarer, about one in 365 of the same-year holders if birth days are uniform, so the residual is small but not zero and no rule over these fields can remove it. A document with no issuing country on either side is undecidable between the customer and a foreign holder of the same number (B′∅ concludes the 10 customers and the 10 other holders, both realistic); B′ and B″ leave it to REVIEW. A number under another type or issuer matches only under plain B (30, theoretical); B′'s type and country requirement removes them, and the exact collisions built here cannot occur by format.

**What each misses among the customers** (a missed MATCH stays in REVIEW, acceptable; none of the 115 is concluded NO_MATCH by any rule or by the product): B′ misses 56 (typo 15, renewed 15, birth date 3+ years off 15, no country 10, the 杰 name 1); B″-tol misses those and the 15 without a list birth date, 71; B″-exact also the 15 within two years, 86.

**Fields.** Every field B′ and B″ need is read today (birth date on both sides; a 1 January list date is detected by its month and day, there is no placeholder marker in the export): document type and issuing country on both sides (customer 證件種類 and 發照地, a Taiwan type implying TW; list 身分證明 類型 and 國家). Not carried: a customer employer, relatives or dated addresses (no such field in the customer export; an address is never support), so those kinds are not built. The list's 身分證明 layout is still assumed, no supplied export has one.

**What it does not prove.** The kinds and counts are probes, chosen, not sampled: they say what each rule does on each failure mode, not how often the mode occurs, so the set gives no rate, and the "0 wrong" on the realistic group is not a precision. No figure here is evidence for release; real labelled pairs, including recycled and reissued documents, are still absent ([open questions](open-questions.md#identity-policy)).

## Requirement scorecard

The system is judged by what it must do for each of its four sources ([external sources](../feature/external-sources.md)): find the right person, exclude the wrong one, get the source condition right, and state the facts right. Each measure runs through the Case output (pair result and explanation), never one component's count. No target is set before the user agrees one.

| # | Source | Measure | Data it needs | Exists |
|---|---|---|---|---|
| 1 | Sanction / PEP lists | right person kept: true matches never NO_MATCH, share at MATCH and PROBABLE_MATCH | labelled customer × list pairs, GC-04 (PEP), GC-05 (Sanction) | no: only GC-01 to GC-03, GC-11, GC-12; no Sanction or PEP case. Synthetic ([set](#synthetic-end-to-end-set)): no customer at NO_MATCH; PROBABLE_MATCH 10/14 Sanction, 8/22 PEP, 2/13 RCA |
| 2 | Sanction / PEP lists | wrong person excluded: automatic MATCH precision (no different person at MATCH), NO_MATCH precision, share of different persons at LOW priority | GC-06, GC-07, labelled different-person pairs | no. Adversarial ([set](#adversarial-synthetic-set)): realistic group, MATCH right / wrong: B′ 59 / 0, B″-tol 44 / 0, B″-exact 29 / 0 (B′∅ 69 / 10: a missing issuing country taken as agreement); theoretical limit (not a rate): a recycled passport on a record with no birth date beats B′ (10), one with the customer's birth year only beats B″ (10); product today no customer NO_MATCH, 60 realistic strangers at PROBABLE_MATCH. Synthetic: no stranger at MATCH (score), no customer at NO_MATCH; strangers at identity NO_MATCH (the DOB conflict, the role veto; their synthetic texts state no age or birth date, so the stated-value conflict adds none), concluded NO_HIT: Sanction 23/31, PEP 13/30, RCA 10/25; of the strangers left in REVIEW, at LOW: PEP 15/17, RCA 14/15, Sanction 0/8 (a raised Sanction is HIGH); same-name strangers born within two years reach PROBABLE_MATCH (Sanction 2, PEP 1) |
| 3 | Sanction / PEP lists | condition right: list type and status against the export's flags | exports with the flags set | no: GC-11 and GC-12 have every flag empty. Synthetic (20% with flags): 350/359 right, 9 delisted sanctions UNKNOWN |
| 4 | judgments | right person found: labelled defendants read as persons with name and stage, end to end | annotated judgments | yes (gold labels outside the repo) |
| 5 | judgments | condition right: unanimous-vote stage precision and coverage on court sets | measured stage sets | yes ([stage model training](stage-model-training.md)). Synthetic (rules): judgments 26/26, court releases 29/29 |
| 6 | judgments | wrong person excluded under redacted names: same-surname others not raised above REVIEW LOW / NORMAL | labelled customer × judgment pairs | no. Synthetic (a court text attached to a Profile): of 31 strangers, 18 concluded NO_HIT, 10 in REVIEW at LOW, 3 at NORMAL; masked judgments generated, not run |
| 7 | URL page | page read: share of pages whose main text holds every labelled person's sentences | annotated news and press-release pages with their URLs | partly: texts annotated, the fetch path built (`POST /api/pages`) but not measured on them. Synthetic received pages ([external sources](#external-sources), synthetic): read 20/20; matched against the customer ([run](#received-url-matched-against-the-customer)): read 40/40, suspect customers a candidate 24/24, other persons with another name never a pair 27/27 |
| 8 | URL page | facts right: persons, events and stated age, birth date and gender next to a name, against gold | annotated news and press releases | partly: persons and events yes ([news model training](news-model-training.md)), identifiers not labelled. Synthetic (attached text): age 22/22 alone, 50/50 after a gender; birth date 23/23; gender 121/124; stated values more than two years off conclude NO_MATCH 34/34, none the customer. Synthetic received pages (page path): persons 21/40 by the rules (customers 20/20 after the dateline; 同案的 not read), 40/40 with the news models (19 `MODEL_READ`); stated values none to read and none invented; in a Case, stated values under the two-year rule: the customer's own birth date agrees 4/4; a stranger's more than two years off concludes NO_MATCH 4/4, within a year neutral 4/4; a dated stranger's age more than 2 off concludes 6/6, no customer concluded |
| 9 | URL page | condition right: stage of the matched person; non-suspects raise nothing | measured stage sets, non-suspect persons | yes. Synthetic (attached text, rules): 188/188. Synthetic received pages: stage right 21/21 by the rules, 40/40 with the news models; in a Case ([run](#received-url-matched-against-the-customer)): condition right 61/65 (witnesses go to review: 證人 concludes nothing, measured on real news), stage right 57/57, victims `NOT_APPLICABLE` 4/4, decided 14/65, wrong automatic 0 |
| 10 | grounding search | right person found: share of labelled customers whose known adverse page is among the kept, fetched results | labelled customers with known pages; a search provider | no. Synthetic result sets generated, not run (path not built) |
| 11 | grounding search | wrong person excluded: same-name others among the results not raised above REVIEW NORMAL | the same, with same-name pages labelled | no. Synthetic same-name results generated, not run |
| 12 | all | decision reproducible and explained: every validated Golden Case reproduced; every item and veto has a catalog code; a re-score of stored revisions is identical | Golden Cases, stored revisions | data yes; the re-score test is proposed, not built |

## Delivery gates

### Gate A: Core build

- contracts and schema;
- extraction/normalization;
- Profile deduplication;
- evidence and decisions;
- GC-01 to GC-03, GC-11 and GC-12 regression tests.

The build follows the [implementation baseline](implementation-baseline.md). GC-11 and GC-12 are anonymized real cases (`.txt` export); GC-01 to GC-03 are synthetic until the real data is supplied. Closing Gate A also needs the supplied HTML samples, so that the HTML conversion is checked against real pages.

### Gate B: Before integrated PoC acceptance

- obtain GC-06 and GC-07;
- calibrate hard conflict and PROBABLE_MATCH;
- settle the [Article 22 questions](open-questions.md#article-22).

### Gate C: Before category capability acceptance

- GC-04 before PEP/RCA acceptance;
- GC-05 before Sanction acceptance;
- GC-08 before automated re-adjudication acceptance;
- GC-09 and GC-10 before Article 22 recommendation acceptance.

### Gate D: Before production

- representative labeled set;
- separate validation of automatic MATCH and NO_MATCH;
- target database (SQL Server or Oracle) passes the repository contract tests;
- security, privacy, AML, legal/compliance and model-risk approvals;
- disaster recovery, audit and operations runbooks.

## Phase 1 release controls

- automatic MATCH allowed by the user (2026-11-26) for the scoring rule's MATCH band without a veto or cap; it takes effect only when the score becomes the decision (approved weights, the proposed switch gates, a measured and accepted automatic MATCH precision); until then the stage 3 table decides and no MATCH is emitted;
- automatic NO_MATCH limited to validated rules: impossible age at an attributed role (GC-01) and, since 2026-11-27, every Profile DOB more than two years from the customer's (the user's decision; [birth-date tolerance](../feature/identity-adjudication.md#birth-date-tolerance)), and the same for every birth date and dated age attached news or a received page states next to the name when the Profile has no DOB of its own (`STATED_DOB_CONFLICT`); the document conflict, impossible role and death before birth stay pending;
- no blanket human review for PEP, Sanction or RCA candidates (the user's decision of 2026-11-27): an identity NO_MATCH gives NO_HIT for every list type today, and the scoring rule's automatic MATCH will apply to every list type once it decides. A wrong automatic NO_MATCH on a sanctioned person is the most expensive error; only the validated rules above conclude NO_MATCH, with the gate's downgrades (source quality, strong support beside a conflict), and they are not yet checked on a real Sanction or PEP Golden Case or against placeholder 1 January birth dates;
- entity Profiles stay REVIEW until the entity rules are validated ([ADR-0008](../adr/0008-validation-and-deferred-capabilities.md));
- PROBABLE_MATCH is high-priority REVIEW;
- source changes create new revisions and do not silently rewrite final results.
