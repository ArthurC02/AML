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
| identity | NO_MATCH 84 (all another person: 80 by the DOB conflict and 4 by the PEP role veto; 81 pairs carry a DOB conflict, 53 `DOB_CONFLICT` and 28 `FULL_DOB_CONFLICT`, one of them closed first by the role veto), PROBABLE_MATCH 57 (51 same person), REVIEW 218; no wrong automatic decision |
| pair result | 84/359 concluded without a reviewer: 79 right, 5 NO_HIT on a delisted sanction of another person whose truth cell is NOT_APPLICABLE |

#### End results

Before the DOB conflict was released (2026-11-26 run) the product decided 0 pairs and the score band 7; with the DOB conflict released and the Sanction/PEP/RCA release control still in place it decided 38.

| Measure | Product (stage 3) | Score band (`score-proposal-0.2`) |
|---|---|---|
| decided (no reviewer) | 23.4% (84/359), was 10.6% (38/359) under the release control | 24.2% (87/359), was 1.9% (7/359) |
| automatic HIT (MATCH), precision | — | 3, 100% |
| automatic NO_HIT (NO_MATCH), precision | 84, identity 100%; 79 cells right, 5 NO_HIT where the truth's cell is NOT_APPLICABLE (another person on a delisted sanction, the condition UNKNOWN) | the same 84 |
| automatic NOT_APPLICABLE | 0 | 0 |
| PROBABLE_MATCH (same person) | 57 (51), was 53 (51) | 55 (49), was 51 (49) |

Automatic NO_HIT of the product per list type, all another person (identity NO_MATCH precision 100% in each): Sanction 23 (18 cells right, 5 delisted), PEP 13, RCA 10, wanted list 7, news 13, press release 8, court ruling 10. Before the release control was lifted, the Sanction, PEP and RCA ones (46) were held at REVIEW.

Wrong automatic decisions, by kind:

| Kind | Product | Score band |
|---|---|---|
| NO_MATCH of the same person | 0 | 0 |
| MATCH of another person | 0 (none emitted) | 0 |
| NO_HIT where the truth's cell is NOT_APPLICABLE (another person on a delisted sanction, the condition UNKNOWN; identity right) | 5 | 5 |
| NO_HIT or NOT_APPLICABLE where the truth is HIT or REVIEW | 0 | 0 |

Where each end result was lost, first stage of the chain that lost it (product); what still holds the 275 pairs in REVIEW:

| Lost at | Pairs |
|---|---|
| identity REVIEW, row 6: nothing beyond the prerequisites on both sides | 154 |
| identity PROBABLE_MATCH of the customer: no automatic MATCH in Phase 1 | 49 |
| identity REVIEW, row 5 partial support | 28 |
| identity REVIEW, row 1: stated age differs; stated birth date differs; document conflict | 26; 8; 1 |
| condition UNKNOWN: delisted sanction | 9 (4 in REVIEW; 5 concluded NO_HIT, the wrong cells above) |
| release control | 0 (was 41; lifted 2026-11-27) |
| identity PROBABLE_MATCH of another person | 5 (was 2) |
| identity REVIEW, row 1: Profile DOB conflict | 0 (was 85: now NO_MATCH, or within tolerance) |
| extraction, candidate | 0 |

Score band: 154 nothing beyond the prerequisites, 61 support below the thresholds, 47 PROBABLE_MATCH of the customer below `T_match`, 9 condition (5 of them concluded NO_HIT on a delisted sanction, see above), 5 PROBABLE_MATCH of another person, 1 held by the pending document rule; 82 right.

Reading the age after a gender (below) moved 19 pairs from row 6 to row 1, all of another person (a stated age more than 2 years off the customer's; none of the customer's stated ages differs), and in the score band from no item to support below the thresholds; no decision changed.

The set is missing data first: 154 of 359 pairs carry nothing comparable beyond name, gender and nationality, and no threshold decides them. Second comes the missing automatic MATCH (49 PROBABLE_MATCHes of the customer), third partial support (28).

#### Identity: stage 3 table and score band

| Pairs | n | stage 3 NO_MATCH (right) | stage 3 PROBABLE (same) | score MATCH (right) | score NO_MATCH (right) | score PROBABLE (same) |
|---|---|---|---|---|---|---|
| all | 359 | 84 (84) | 57 (51) | 3 (3) | 84 (84) | 55 (49) |
| Sanction | 45 | 23 (23) | 12 (10) | 2 (2) | 23 (23) | 10 (8) |
| PEP | 52 | 13 (13) | 9 (8) | 0 | 13 (13) | 9 (8) |
| RCA | 38 | 10 (10) | 2 (2) | 0 | 10 (10) | 2 (2) |
| official wanted list | 36 | 7 (7) | 15 (13) | 1 (1) | 7 (7) | 14 (12) |
| news / press release / court | 73 / 60 / 55 | 13 / 8 / 10 (all right) | 7 (6) / 4 / 8 (all same) | 0 | 13 / 8 / 10 (all right) | 7 (6) / 4 / 9 (all same) |
| carries 0 groups beyond the prerequisites | 100 | 2 (2) | 0 | 0 | 2 (2) | 0 |
| carries 1 | 155 | 44 (44) | 24 (19) | 0 | 44 (44) | 25 (20) |
| carries 2 or more | 104 | 38 (38) | 33 (32) | 3 (3) | 38 (38) | 30 (29) |

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
| proposed: `T_match` 60, `T_probable` 30, `T_probable_exact` 8, `T_no_match` −25 | 87 (24.2%) | 3 (100%) | 84 (100%) | 55 (89.1%) |
| `T_match` 40 / 50 / 70 | 91 / 87 / 84 | 7 / 3 / 0 (100%) | 84 | 51 / 55 / 58 (88.2 / 89.1 / 89.7%) |
| `T_probable` 20 / 40 | 87 / 87 | 3 | 84 | 57 (89.5%) / 55 (89.1%) |
| `T_probable_exact` 15 / 25 | 87 / 87 | 3 | 84 | 28 (100%) / 21 (100%) |
| `T_no_match` −10 / −40 | 87 / 87 | 3 | 84 | 55 (89.1%) |
| agreement within tolerance +25 (instead of +8) | 87 | 3 | 84 | 55 (89.1%) |

The 84 automatic NO_MATCHes are vetoes (the DOB conflict and the role veto), so no threshold moves them. `T_no_match` moves nothing: no negative item left is validated, so every sum below it is held. `T_match` 40 adds 4 automatic MATCHes, all right here only because the generator never gives two persons one document number or one full date by more than the assumed chance. `T_probable_exact` above 8 removes the six strangers raised by a birth year or a birth date within two years, and with them 21 to 28 PROBABLE_MATCHes of the customer.

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
5. **Scoring rule, source kind: built** (measurement only). `ScoringRule.Identity` takes the source kind of the record's fields and of the text's stated values ([source reliability](../feature/scoring-rule.md#source-reliability)); the synthetic measure passes each Profile's kind for the text, the record at the list's factor, so the figures above are unchanged, and a received page's person is scored with the news factor for both.
6. **Sanction priority**: every Sanction pair of another person left in REVIEW is HIGH (8/8; the other 23 conclude NO_HIT), as the priority rule says (a raised Sanction is HIGH); scorecard row 2 asks for strangers at LOW. Lifting the release control did not change the rule, only how many strangers reach it (31 → 8).

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
| condition right / stage right | 65/65 / 57/57 | 65/65 / 57/57 |
| pairs carrying a comparable stated age | 19/24 age pairs (the 5 undated: `AGE_NOT_COMPARABLE`) | same |
| pairs decided, all right | 8/65 (the non-suspects, `NOT_APPLICABLE`) | 8/65 |
| wrong automatic results | 0 | 0 |

The 57 pairs left in REVIEW all wait on identity: a page has no field beside the name, so nothing beyond it is comparable (row 6, `NO_NON_PREREQUISITE_SUPPORT`: customers 28, strangers 11, of them the twice-named page 1); a birth date or an age written next to the name is a stated value (ADR-0012), never concluding. Birth dates: the customer's own agrees (row 5, `PARTIAL_SUPPORT_ONLY`: 4), a stranger's differs (row 1, `NEWS_BIRTH_DATE_DIFFERENCE`: 8). Ages, at the publication date: the customer's 5 `AGE_COMPATIBLE_AT_REPORT` (no support, row 6), the near strangers' 8 compatible too (within ±2, row 6), the far strangers' 6 `AGE_DIFFERENCE_AT_REPORT` (row 1); the 5 undated pages' ages (customers 3, far strangers 2) are recorded, `AGE_NOT_COMPARABLE`. Measured only ([open question](open-questions.md#external-sources)): taking the stated birth date as the record's DOB field under the two-year rule would turn the 4 strangers stated 3 to 15 years apart to NO_MATCH, the 4 within a year agree; taking a dated age as a birth-year field (either year it allows) would turn the 6 far strangers to NO_MATCH (right) and make the 8 near strangers and the 5 customers agree (the 8 strangers' agreement wrong), no customer to NO_MATCH. Together: 10 pairs to NO_MATCH, none wrong; 17 newly to agreement (4 + 8 strangers, 5 customers by age), the 12 strangers' wrong (within two years, as built into the set); the 4 customers' own birth dates agree already. The score band (news factor for both sides) leaves every pair in REVIEW too (+9 at most, the prerequisites not all EXACT). The news models change no decision: the persons only they read are other persons, excluded by name.

**Judgments and search results: generated, not run** (the paths are not built).

## Requirement scorecard

The system is judged by what it must do for each of its four sources ([external sources](../feature/external-sources.md)): find the right person, exclude the wrong one, get the source condition right, and state the facts right. Each measure runs through the Case output (pair result and explanation), never one component's count. No target is set before the user agrees one.

| # | Source | Measure | Data it needs | Exists |
|---|---|---|---|---|
| 1 | Sanction / PEP lists | right person kept: true matches never NO_MATCH, share at MATCH and PROBABLE_MATCH | labelled customer × list pairs, GC-04 (PEP), GC-05 (Sanction) | no: only GC-01 to GC-03, GC-11, GC-12; no Sanction or PEP case. Synthetic ([set](#synthetic-end-to-end-set)): no customer at NO_MATCH; PROBABLE_MATCH 10/14 Sanction, 8/22 PEP, 2/13 RCA |
| 2 | Sanction / PEP lists | wrong person excluded: automatic MATCH precision (no different person at MATCH), NO_MATCH precision, share of different persons at LOW priority | GC-06, GC-07, labelled different-person pairs | no. Synthetic: no stranger at MATCH (score), no customer at NO_MATCH; strangers at identity NO_MATCH (the DOB conflict, the role veto), concluded NO_HIT: Sanction 23/31, PEP 13/30, RCA 10/25; of the strangers left in REVIEW, at LOW: PEP 15/17, RCA 14/15, Sanction 0/8 (a raised Sanction is HIGH); same-name strangers born within two years reach PROBABLE_MATCH (Sanction 2, PEP 1) |
| 3 | Sanction / PEP lists | condition right: list type and status against the export's flags | exports with the flags set | no: GC-11 and GC-12 have every flag empty. Synthetic (20% with flags): 350/359 right, 9 delisted sanctions UNKNOWN |
| 4 | judgments | right person found: labelled defendants read as persons with name and stage, end to end | annotated judgments | yes (gold labels outside the repo) |
| 5 | judgments | condition right: unanimous-vote stage precision and coverage on court sets | measured stage sets | yes ([stage model training](stage-model-training.md)). Synthetic (rules): judgments 26/26, court releases 29/29 |
| 6 | judgments | wrong person excluded under redacted names: same-surname others not raised above REVIEW LOW / NORMAL | labelled customer × judgment pairs | no. Synthetic (a court text attached to a Profile): strangers 31/31 at LOW; masked judgments generated, not run |
| 7 | URL page | page read: share of pages whose main text holds every labelled person's sentences | annotated news and press-release pages with their URLs | partly: texts annotated, the fetch path built (`POST /api/pages`) but not measured on them. Synthetic received pages ([external sources](#external-sources), synthetic): read 20/20; matched against the customer ([run](#received-url-matched-against-the-customer)): read 40/40, suspect customers a candidate 24/24, other persons with another name never a pair 27/27 |
| 8 | URL page | facts right: persons, events and stated age, birth date and gender next to a name, against gold | annotated news and press releases | partly: persons and events yes ([news model training](news-model-training.md)), identifiers not labelled. Synthetic (attached text): age 22/22 alone, 50/50 after a gender; birth date 23/23; gender 121/124. Synthetic received pages (page path): persons 21/40 by the rules (customers 20/20 after the dateline; 同案的 not read), 40/40 with the news models (19 `MODEL_READ`); stated values none to read and none invented; in a Case, stated birth dates compared as stated values: the customer's own agrees 4/4, a stranger's differs 8/8, none concludes |
| 9 | URL page | condition right: stage of the matched person; non-suspects raise nothing | measured stage sets, non-suspect persons | yes. Synthetic (attached text, rules): 188/188. Synthetic received pages: stage right 21/21 by the rules, 40/40 with the news models; in a Case ([run](#received-url-matched-against-the-customer)): condition right 41/41, stage right 33/33, victims and witnesses `NOT_APPLICABLE` 8/8, decided 8/41, wrong automatic 0 |
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
- automatic NO_MATCH limited to validated rules: impossible age at an attributed role (GC-01) and, since 2026-11-27, every Profile DOB more than two years from the customer's (the user's decision; [birth-date tolerance](../feature/identity-adjudication.md#birth-date-tolerance)); the document conflict, impossible role and death before birth stay pending;
- no blanket human review for PEP, Sanction or RCA candidates (the user's decision of 2026-11-27): an identity NO_MATCH gives NO_HIT for every list type today, and the scoring rule's automatic MATCH will apply to every list type once it decides. A wrong automatic NO_MATCH on a sanctioned person is the most expensive error; only the validated rules above conclude NO_MATCH, with the gate's downgrades (source quality, strong support beside a conflict), and they are not yet checked on a real Sanction or PEP Golden Case or against placeholder 1 January birth dates;
- entity Profiles stay REVIEW until the entity rules are validated ([ADR-0008](../adr/0008-validation-and-deferred-capabilities.md));
- PROBABLE_MATCH is high-priority REVIEW;
- source changes create new revisions and do not silently rewrite final results.
