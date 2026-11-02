# ADR-0019: Evidence-strength decision table before Fellegi-Sunter

- Status: Accepted
- Date: 2026-11-02

## Context

The legacy rules relied on AI-assigned probabilities ([ADR-0018](0018-legacy-ai-report-rules-not-adopted.md)). A numeric score was considered instead, in particular Fellegi-Sunter, which sums per-field weights log₂(m/u).

Fellegi-Sunter only pays off once m and u are estimated and the result is calibrated against labelled cases. No population distribution and no labelled set are available yet. Until then it would be an expert-weighted points table that has to be maintained. Phase 1 needs identity routing only: REVIEW, PROBABLE_MATCH and review order. It makes no automatic decisions and does not settle the 65% ruling.

## Decision

- Phase 1 uses a decision table over the existing `strength` levels (`LOW | MEDIUM | HIGH | UNIQUE`); there is no numeric score ([decision table](../feature/identity-adjudication.md#decision-policy)).
- Strength levels are assigned in Fellegi-Sunter order: the less likely a chance agreement (smaller u), the higher the level. A later switch to weights keeps the ranking.
- Hit prerequisites (NAME, NATIONALITY, GENDER) have no strength and do not count, as the bank rules require.
- Fellegi-Sunter, with term-frequency adjustment and calibration, is deferred to Gate D. It needs:
  - historical decisions and the human gold answers as labels;
  - public or list-side statistics for u.

## Consequences

- Every table row is testable with Golden Cases and readable by the business.
- The 65% ruling stays an [open question](../plan/open-questions.md#article-22) until calibrated probabilities exist.
- If Fellegi-Sunter is adopted later, a new ADR supersedes this one; uncalibrated scores are never shown as percentages.
