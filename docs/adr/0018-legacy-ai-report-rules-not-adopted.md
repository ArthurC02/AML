# ADR-0018: Legacy AI-report rules not adopted

- Status: Accepted
- Date: 2026-11-02

## Context

The legacy rules (`Rule.md`) describe an earlier product: an LLM reads each case, writes a Traditional Chinese Excel report with a match/no-match recommendation and two probabilities, and humans score the report against a 100-point rubric. Many of its evidence rules are sound and have been merged into the feature specs. Its decision mechanism conflicts with [ADR-0001](0001-evidence-driven-adjudication.md), [ADR-0009](0009-api-only.md) and the Phase 1 exclusion of LLM adjudication.

## Decision

Not adopted:

| Legacy rule | Reason | Replacement |
|---|---|---|
| AI-assigned match/no-match probabilities | not calibrated, not explainable | evidence, conflicts and reason codes |
| 65% probability threshold | depends on the probabilities | `NO_BILATERAL_DIRECT_IDENTIFIER` reason code; translation of the 2026-10-14 ruling is an [open question](../plan/open-questions.md#article-22) |
| case probability not lower than any hit | depends on the probabilities | [case-level precedence](../feature/case-management.md#case-level-result) |
| LLM summarises news, groups events, judges AML relevance | free-news understanding is out of Phase 1 | reviewer sets `amlRelevance`; structured fields only |
| Excel four-sheet report | API only | explanation API ([pipeline](../feature/pipeline-api-and-batch.md#explanation-contents)) |
| PDF page-image flow, repair loops, 100-point report rubric | belong to the LLM report product | none |
| photo appearance never used | kept as a rule, not as a group | `PHOTO` removed from the evidence groups |

## Consequences

- Business vocabulary (list types, Article 22, comparison statuses, verification records) is preserved.
- If the bank later wants probabilistic scoring, it comes through a calibrated model (e.g. Fellegi-Sunter) and a new ADR, never by reviving uncalibrated probabilities.
