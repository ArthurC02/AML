# ADR-0001: Evidence-driven identity adjudication

- Status: Accepted
- Date: 2026-11-02; updated 2026-11-09 (merged former ADR-0002, 0003, 0018, 0019)

## Context

Name-screening systems often reduce identity to one similarity score. A score cannot explain why a pair matched, hides conflicting facts and mixes retrieval ranking with identity. Many weak agreements (same name, gender, nationality) can look convincing together even when one reliable fact proves two records are different people.

The bank's legacy rules (`Rule.md`) describe an earlier product: an LLM reads each case, writes an Excel report with a match/no-match recommendation and two probabilities, and humans score the report against a 100-point rubric. Many of its evidence rules are sound and are in the feature specs; its decision mechanism is not adopted.

Fellegi-Sunter, which sums per-field weights log₂(m/u), pays off only once m and u are estimated and calibrated against labelled cases. No population distribution and no labelled set exist yet.

## Decision

- **Explicit records, no score.** Adjudication works on CandidateProfile, Evidence, Conflict and SystemDecision. Every decision lists its supporting evidence, conflicts, unknown fields, reason codes and policy version ([explainability](../feature/identity-adjudication.md#explainability)). Retrieval scores (BM25, displayed similarity) rank candidates only and are never identity probabilities.
- **Three ordered stages:** Hard Conflict Gate, Evidence Strength Assessment, Decision Policy. A confirmed hard conflict cannot be offset by weak supporting attributes. Each hard-conflict rule names its evidence and policy version and carries its own validation status; only validated rules close a pair automatically, unvalidated ones surface as REVIEW ([hard conflict gate](../feature/identity-adjudication.md#hard-conflict-gate)).
- **Strength table before Fellegi-Sunter.** Phase 1 uses a decision table over `strength` (`LOW | MEDIUM | HIGH | UNIQUE`) ([decision table](../feature/identity-adjudication.md#decision-policy)). Levels follow Fellegi-Sunter order (the less likely a chance agreement, the higher), so a later switch to weights keeps the ranking. Hit prerequisites (NAME, NATIONALITY, GENDER) have no strength. Fellegi-Sunter with term-frequency adjustment and calibration waits for Gate D: historical decisions with human gold answers, and public or list-side statistics for u.
- **PROBABLE_MATCH is internal.** It ranks REVIEW first; consumers receive REVIEW ([combination matrix](../feature/data-contracts.md#combination-matrix)). There is no automatic MATCH.
- **Legacy rules not adopted:**

| Legacy rule | Replacement |
|---|---|
| AI-assigned match/no-match probabilities | evidence, conflicts and reason codes |
| 65% probability threshold | `NO_BILATERAL_DIRECT_IDENTIFIER`; the 2026-10-14 ruling is an [open question](../plan/open-questions.md#article-22) |
| case probability not lower than any hit | [case-level precedence](../feature/case-management.md#case-level-result) |
| LLM reads news, judges AML relevance | [ADR-0012](0012-sources-and-web-information.md) |
| four-sheet Excel report | [ADR-0023](0023-business-api-data-and-workbook.md) |
| PDF page images, repair loops, 100-point rubric | none |
| photo appearance never used | kept as a rule; `PHOTO` is not an evidence group |

## Consequences

- More data is stored per decision; that is the price of traceability.
- Every table row is testable with Golden Cases and readable by the business.
- The PROBABLE_MATCH combination and the 65% ruling stay [open questions](../plan/open-questions.md#identity-policy) until GC-06/GC-07 and calibrated probabilities exist.
- Probabilistic scoring comes only through a calibrated model, by updating this record; uncalibrated scores are never shown as percentages.
