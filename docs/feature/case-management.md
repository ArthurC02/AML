# Case Management

> Module: Case and Adjudication Management. Design baseline 2.0-PoC, 2026-11-02.

## Case lifecycle

Case status follows the latest analysis; no person moves a Case ([ADR-0020](../adr/0020-identification-only.md)).

| Status | When |
|---|---|
| OPEN | created; no analysis accepted yet, or the latest revision is incomplete |
| IN_REVIEW | published result contains at least one REVIEW or HIT pair |
| CLOSED | published result has no REVIEW or HIT pair, and every NO_HIT relies on a validated automatic rule ([hard conflict gate](identity-adjudication.md#hard-conflict-gate)) |

A new analysis revision recomputes the status. Every change records the analysis revision it is based on.

## Execution status

Execution status is separate from Case status:

```text
PENDING | RUNNING | COMPLETED | PARTIAL | FAILED
```

A COMPLETED analysis can still produce REVIEW.

## Version model

- `currentAnalysisRevision`: latest accepted analysis.
- `effectiveResultRevision`: latest explicitly published result.
- Technical retry with identical inputs and policies reuses the revision.
- Changed customer data, source Profile, scope or policy creates a new revision.
- Late results are retained in their original revision and cannot overwrite newer effective results.

## Case-level result

Proposed default, pending ResultPublicationPolicy approval ([open questions](../plan/open-questions.md#identity-policy)).

A Case aggregates every pair result in its analysis revision by precedence:

```text
NOT_EVALUATED > HIT > REVIEW > NO_HIT > NOT_APPLICABLE
```

- Any NOT_EVALUATED pair, or execution status PARTIAL or FAILED, makes the revision incomplete: it is persisted but not published as the effective result.
- HIT and REVIEW pairs are listed individually; aggregation never hides a pair.
- A Case with zero CandidateProfiles after a COMPLETED retrieval is NO_HIT, with retrieval scope recorded as a limitation.

## Decision records

- **ImportedDecision**: external or legacy decision, with verification status (`independentlyVerified`). Includes earlier manual results found in the input; never used as evidence.
- **SystemDecision**: decision produced by a named policy version.
- **DispositionRecommendation**: advisory Article 22 recommendation (ON_LIST / NOT_ON_LIST) and its basis ([disposition recommendation](review-and-disposition.md)).

## Decision preservation rules

- Imported decisions never masquerade as system output.
- System decisions never rewrite source attributes.
- Every system decision identifies its policy version and evidence.

Data shapes are in [data contracts](data-contracts.md).
