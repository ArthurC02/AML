# PoC Delivery Plan

> Design baseline 2.0-PoC, 2026-11-02.

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

## Delivery gates

### Gate A: Start now

- contracts and schema;
- extraction/normalization;
- Profile deduplication;
- evidence and decisions;
- GC-01 to GC-03, GC-11 and GC-12 regression tests.

Prerequisite: supplied HTML samples and real GC-01 to GC-03 data (pending as of 2026-11-02); synthetic GC-01 to GC-03 samples stand in until then. GC-11 and GC-12 sample data are in place (`.txt` export, anonymized). Build follows the [implementation baseline](implementation-baseline.md).

### Gate B: Before integrated PoC acceptance

- obtain GC-06 and GC-07;
- calibrate hard conflict and PROBABLE_MATCH;
- confirm reviewer reason codes;
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
