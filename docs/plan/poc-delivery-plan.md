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
| Duplicate Hit Rate | 0% | 9 candidate rows fold into 6 hits, one per Profile |
| Auto-NO_MATCH Precision | 100% (2/2) | both GC-01 pairs |
| Auto-MATCH Precision | n/a | automatic MATCH is disabled in Phase 1 |
| Review Rate | 67% (4/6) | |
| Probable-Match Yield | 100% (2/2) | GC-03, GC-11 |
| Evidence Completeness | 100% | 9 comparison rows per pair |
| Decision Traceability | 100% | reason codes and a conclusion on every pair |
| Missing / ambiguous / conflict rate | 56% / 2% / 0% | comparison cells; missing is mostly Profile DOB, document and address, and the customer's employer (an occupation alone is not comparable with Profile roles, so GC-01's employment cell is missing, not ambiguous) |
| Re-adjudication Stability | 100% | an unchanged re-run lists no changes |

Not measured: field extraction correctness and Recall@K (need HTML samples and labeled extraction), Human Override Rate (no human workflow in the PoC), batch completion, retry recovery and latency (need representative volume; the `aml.*` metrics collect them).

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

- automatic MATCH broadly disabled;
- automatic NO_MATCH limited to validated rules;
- PEP/Sanction candidates require human review;
- PROBABLE_MATCH is high-priority REVIEW;
- source changes create new revisions and do not silently rewrite final results.
