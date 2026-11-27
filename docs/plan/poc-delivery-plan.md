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

Six golden pairs cannot say how often the system concludes, so the user decided (2026-11-26) to measure on synthesized data. `tests/Aml.Tests/Synthetic` generates Cases from a fixed seed (no clock): invented customers and related parties, invented list Profiles and attached texts, written as the bank's `.txt` / `.html` export the readers parse, each candidate with its truth. The set is generated at test time, not committed. It runs through `TxtCaseReader` (with `HtmlExport`) and `CaseAnalyzer` in process; `SyntheticCaseServiceTests` imports 14 of the Cases through `CaseService` on SQLite and `SyntheticCasesApiTests` uploads 2 through `POST /api/cases`, and both read back the same hits as the in-process run. The stage is the rules' (no model in CI); `SyntheticSetTests.Report_with_the_stage_models` runs the same set with both stage models when `AML_STAGE_MODEL`, `AML_STAGE_LLM_URL` and `AML_STAGE_LLM_MODEL` are set (not run here). `AML_SYNTHETIC_REPORT=<file>` writes every table below. `SyntheticSetTests` pins the generator and the headline figures. Measurement only: no Case output, contract or policy changes.

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

#### Chain stages (the stage 3 table deciding)

| Stage | Result |
|---|---|
| ingest | 300/300 Cases read; no file refused |
| fields | 359/359 candidates with every checked field right (subject names, gender, DOB, nationality, document; Profile names, gender, DOBs, citizenship, documents, list status; news date), `.txt` and `.html` alike |
| stated next to the name | age alone 22/22; **age after the gender in one bracket, （男，45歲）, 0/50**; birth date on a party line 23/23; gender 121/124 (the 3 not looked into; a customer without a gender gives no reading by design) |
| candidate | 359/359 found, one hit per Profile, rows folded |
| source condition | 350/359 right; 9 UNKNOWN (delisted sanctions, which the spec never makes NOT_HIT); 0 wrong |
| legal stage | 186/224 right: wanted list 36/36, judgments 26/26, news 62/73, press release 48/60, court release 14/29 |
| identity | NO_MATCH 4 (all another person, the PEP role veto), PROBABLE_MATCH 53 (51 same person), REVIEW 302; no wrong automatic decision |
| pair result | 0/359 concluded without a reviewer |

#### End results

| Measure | Product (stage 3) | Score band (`score-proposal-0.1`) |
|---|---|---|
| decided (no reviewer) | 0% (0/359) | 1.9% (7/359) |
| automatic HIT (MATCH), precision | — | 3, 100% |
| automatic NO_HIT (NO_MATCH), precision | 0 (4 NO_MATCH held by the release control) | 4, 100% |
| automatic NOT_APPLICABLE | 0 | 0 |
| PROBABLE_MATCH (same person) | 53 (51) | 51 (49) |

Where each end result was lost, first stage of the chain that lost it (product):

| Lost at | Pairs |
|---|---|
| identity REVIEW, row 6: nothing beyond the prerequisites on both sides | 173 |
| identity REVIEW, row 1: Profile DOB conflict (pending rule) | 85 (`DOB_CONFLICT` 54, `FULL_DOB_CONFLICT` 31) |
| identity PROBABLE_MATCH of the customer: no automatic MATCH in Phase 1 | 49 |
| identity REVIEW, row 5 partial support | 21 |
| identity REVIEW, row 1: stated birth date or age differs; document conflict | 15; 1 |
| condition UNKNOWN: delisted sanction | 9 |
| release control: a right NO_MATCH (PEP veto) held | 4 |
| identity PROBABLE_MATCH of another person | 2 |
| extraction, candidate | 0 |

Score band: 173 nothing beyond the prerequisites, 86 held by a pending rule, 47 PROBABLE_MATCH of the customer below `T_match`, 35 support below the thresholds, 9 condition, 2 PROBABLE_MATCH of another person; 7 right.

The set is missing data first: 173 of 359 pairs carry nothing comparable beyond name, gender and nationality, and no threshold decides them. Second come the pending conflict rules (86), third the missing automatic MATCH.

#### Identity: stage 3 table and score band

| Pairs | n | stage 3 NO_MATCH (right) | stage 3 PROBABLE (same) | score MATCH (right) | score NO_MATCH (right) | score PROBABLE (same) |
|---|---|---|---|---|---|---|
| all | 359 | 4 (4) | 53 (51) | 3 (3) | 4 (4) | 51 (49) |
| Sanction | 45 | 0 | 10 (10) | 2 (2) | 0 | 8 (8) |
| PEP | 52 | 4 (4) | 8 (8) | 0 | 4 (4) | 8 (8) |
| RCA | 38 | 0 | 2 (2) | 0 | 0 | 2 (2) |
| official wanted list | 36 | 0 | 15 (13) | 1 (1) | 0 | 14 (12) |
| news / press release / court | 73 / 60 / 55 | 0 | 6 / 4 / 8 (all same) | 0 | 0 | 6 / 4 / 9 (all same) |
| carries 0 groups beyond the prerequisites | 128 | 2 (2) | 0 | 0 | 2 (2) | 0 |
| carries 1 | 146 | 2 (2) | 21 (20) | 0 | 2 (2) | 22 (21) |
| carries 2 or more | 85 | 0 | 32 (31) | 3 (3) | 0 | 29 (28) |

Wrong automatic identity decisions: none, under either. The near misses, by the items that made them:

| Kind | Items | Pairs |
|---|---|---|
| PROBABLE_MATCH of a same-name stranger (stage 3 row 4 and score) | `SCORE_DOB_YEAR` +8 alone, birth year agreeing by chance, prerequisites EXACT | 2 (wanted list) |
| held at REVIEW only by `SCORE_HELD_PENDING_VALIDATION`; NO_MATCH if the conflict rules were released | `DOB_CONFLICT` / `FULL_DOB_CONFLICT` −25, `DOCUMENT_IDENTITY_CONFLICT` −40, alone or with a stated difference | 92: 91 another person, **1 the customer** (a one-day error in the Profile's full DOB, `FULL_DOB_CONFLICT`) |

Same person by variation (stage 3, REVIEW of all): romanized-only name 6/9, simplified script 8/13, alias 5/8, Hanyu Pinyin 1/1, against 96/147 for every pair of the customer. A renewed passport (3) never met a comparable customer passport at these rates, so the document conflict the spec warns about did not occur (0 same-person document conflicts).

#### Sensitivity (score band, identity; the PROPOSED table unchanged)

| Thresholds | Decided | Auto MATCH (precision) | Auto NO_MATCH (precision) | PROBABLE_MATCH (same person) |
|---|---|---|---|---|
| proposed: `T_match` 60, `T_probable` 30, `T_probable_exact` 8, `T_no_match` −25 | 7 (1.9%) | 3 (100%) | 4 (100%) | 51 (96.1%) |
| `T_match` 40 / 50 / 70 | 11 / 7 / 4 | 7 / 3 / 0 (100%) | 4 | 47 / 51 / 54 (95.7 / 96.1 / 96.3%) |
| `T_probable` 20 / 40 | 7 / 7 | 3 | 4 | 53 (96.2%) / 51 (96.1%) |
| `T_probable_exact` 15 / 25 | 7 / 7 | 3 | 4 | 28 (100%) / 21 (100%) |
| `T_no_match` −10 / −40 | 7 / 7 | 3 | 4 | 51 (96.1%) |

`T_no_match` moves nothing: no negative item is validated, so every sum below it is held. `T_match` 40 adds 4 automatic MATCHes, all right here only because the generator never gives two persons one document number or one full date by more than the assumed chance. `T_probable_exact` above 8 removes the two strangers raised by a birth year and with them 21 to 28 PROBABLE_MATCHes of the customer.

#### Reader holes found

Counts of the legal stage the rules read against the truth, and stated values the identity checks did not read; each is a proposed change for the user (stage changes through the `measure-stage` skill), none made:

| Hole | Pairs |
|---|---|
| an age after the gender in one bracket, `陳建宇（男，45歲）`, is not read (`StatedAgeCheck`); the age alone is | 50 of 50 |
| a court release's acquittal written `今日宣判，{姓名}無罪` reads UNKNOWN (the ruling verb is in the clause before) | 11 of 13; the other 2 read CONVICTED from a co-defendant |
| another person's outcome later in the person's own sentence wins (`…判處甲有期徒刑一年，同案的乙則獲判無罪`) | 15 (news 8, court 4, press release 3) |
| another named person's dismissal in the next sentence is taken as the person's (`同案被告乙則獲不起訴處分`), so an indictment reads DISMISSED | 7 (news 3, press release 4) |
| a press release with a masked name indicts by `提起公訴`, which counts only in a sentence naming the person: read ALLEGED | 5 of 7 (the other 2 read DISMISSED from a co-defendant) |

#### Proposed changes (the user decides; nothing changed)

1. **Release the DOB and document conflict rules as validated negative items** (scoring-rule.md, candidates for NO_MATCH by the sum): the score's decided rate would go from 1.9% to 27.6% (99/359), with 1 wrong NO_MATCH of 92 here, the Profile-DOB recording error the spec's release condition already names ("a check that neither side's DOB is … scattered or UNPARSEABLE" does not catch a one-day error).
2. **`T_probable_exact` 8**: a birth year alone with exact prerequisites raises same-name strangers who share the year (2 here, at the assumed 10%); stage 3 row 4 does the same. Raise it, or require more than the year, if the bank finds that rate realistic.
3. **Release control**: the 4 right NO_MATCHes it holds are the PEP veto; the spec already drops it once the score decides.
4. **Readers**: the five holes above.
5. **Scoring rule, source kind of a Profile**: `ScoringRule` scores a Profile's own fields at the list's factor 1.0 even when the Profile was built from a news text or a judgment, and stated values at the news factor 0.6 whatever the text; the spec says each value counts at its source's factor and at most MEDIUM. The measurement substitutes the text kind's factor for stated values only (`SyntheticMeasure.PolicyFor`). Proposed: the source kind as an input of the rule.
6. **Sanction priority**: every Sanction pair of another person is REVIEW HIGH (31/31), as the priority rule says (a raised Sanction is HIGH); scorecard row 2 asks for strangers at LOW.

#### Generated, not run (the paths are not built)

For the first 20 Cases' customers, `SyntheticCaseGenerator.External` writes the source documents of the three [external sources](../feature/external-sources.md) with their truth: 20 news pages received by URL (HTML, the customer and another person named), 20 published judgments whose defendant is masked (林○玲 form, never a candidate by itself), and 60 grounding-search results (20 relevant, 20 about a same-name stranger, 20 about no one). No product path reads them yet.

## Requirement scorecard

The system is judged by what it must do for each of its four sources ([external sources](../feature/external-sources.md)): find the right person, exclude the wrong one, get the source condition right, and state the facts right. Each measure runs through the Case output (pair result and explanation), never one component's count. No target is set before the user agrees one.

| # | Source | Measure | Data it needs | Exists |
|---|---|---|---|---|
| 1 | Sanction / PEP lists | right person kept: true matches never NO_MATCH, share at MATCH and PROBABLE_MATCH | labelled customer × list pairs, GC-04 (PEP), GC-05 (Sanction) | no: only GC-01 to GC-03, GC-11, GC-12; no Sanction or PEP case. Synthetic ([set](#synthetic-end-to-end-set)): no customer at NO_MATCH; PROBABLE_MATCH 10/14 Sanction, 8/22 PEP, 2/13 RCA |
| 2 | Sanction / PEP lists | wrong person excluded: automatic MATCH precision (no different person at MATCH), NO_MATCH precision, share of different persons at LOW priority | GC-06, GC-07, labelled different-person pairs | no. Synthetic: no stranger at MATCH (score) or NO_MATCH wrongly; at LOW: PEP 30/30, RCA 24/25, Sanction 0/31 (a raised Sanction is HIGH) |
| 3 | Sanction / PEP lists | condition right: list type and status against the export's flags | exports with the flags set | no: GC-11 and GC-12 have every flag empty. Synthetic (20% with flags): 350/359 right, 9 delisted sanctions UNKNOWN |
| 4 | judgments | right person found: labelled defendants read as persons with name and stage, end to end | annotated judgments | yes (gold labels outside the repo) |
| 5 | judgments | condition right: unanimous-vote stage precision and coverage on court sets | measured stage sets | yes ([stage model training](stage-model-training.md)). Synthetic (rules): judgments 26/26, court releases 14/29 |
| 6 | judgments | wrong person excluded under redacted names: same-surname others not raised above REVIEW LOW / NORMAL | labelled customer × judgment pairs | no. Synthetic (a court text attached to a Profile): strangers 31/31 at LOW; masked judgments generated, not run |
| 7 | URL page | page read: share of pages whose main text holds every labelled person's sentences | annotated news and press-release pages with their URLs | partly: texts annotated, the fetch path not measured. Synthetic pages generated, not run (path not built) |
| 8 | URL page | facts right: persons, events and stated age, birth date and gender next to a name, against gold | annotated news and press releases | partly: persons and events yes ([news model training](news-model-training.md)), identifiers not labelled. Synthetic (attached text): age 22/22 alone, 0/50 after a gender; birth date 23/23; gender 121/124 |
| 9 | URL page | condition right: stage of the matched person; non-suspects raise nothing | measured stage sets, non-suspect persons | yes. Synthetic (attached text, rules): 150/188 |
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
- automatic NO_MATCH limited to validated rules;
- no blanket human review for PEP, Sanction or RCA candidates under the scoring rule: its automatic MATCH and NO_MATCH apply to every list type once it decides (read from the user's statement of 2026-11-26; the user can restore the control). While the stage 3 table decides, the code keeps `RELEASE_CONTROL_HUMAN_REVIEW` (a raised Sanction, PEP or RCA condition turns NO_HIT into REVIEW);
- PROBABLE_MATCH is high-priority REVIEW;
- source changes create new revisions and do not silently rewrite final results.
