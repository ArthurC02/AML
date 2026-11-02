# Case Management

> Module: Case and Adjudication Management. Design baseline 2.0-PoC, 2026-11-02.

## Case lifecycle

```text
OPEN → IN_REVIEW → CLOSED
  ↑        │          │
  └────────┴── REOPENED
```

| From → To | Allowed when |
|---|---|
| OPEN → IN_REVIEW | published result contains at least one REVIEW or HIT pair |
| OPEN → CLOSED | published result has no REVIEW or HIT pair, and every NO_HIT relies on a validated automatic rule ([hard conflict gate](identity-adjudication.md#hard-conflict-gate)) |
| IN_REVIEW → CLOSED | every REVIEW and HIT pair has an approved HumanDecision and a FinalDisposition; an `UNRESOLVED` decision also needs senior approval ([Article 22 ③](review-and-disposition.md#article-22-3-cannot-be-determined)) |
| CLOSED → REOPENED | new analysis revision with changed inputs or policy, or authorized manual reopen |
| REOPENED → IN_REVIEW / CLOSED | same rules as from OPEN |

Every transition records actor, time, reason code and the analysis revision it is based on.

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
- **DispositionRecommendation**: Article 22 recommendation (ON_LIST / NOT_ON_LIST) and its basis ([review and disposition](review-and-disposition.md)).
- **HumanDecision**: reviewer decision (`MATCH | NO_MATCH | UNRESOLVED`), reason codes, evidence and [verification records](review-and-disposition.md#verification-records); approved by the approver, and by the senior approver when UNRESOLVED.
- **FinalDisposition**: effective business result (ON_LIST / NOT_ON_LIST) and the decision or Article 22 rule it relies on.

A Case's disposition is ON_LIST when any pair is ON_LIST, and NOT_ON_LIST only when every pair is.

## Decision preservation rules

- Imported decisions never masquerade as system output.
- Human decisions never rewrite source attributes.
- Final disposition identifies its basis.
- A new analysis revision does not silently invalidate a human decision; policy must define whether re-review is required.

Data shapes are in [data contracts](data-contracts.md).
