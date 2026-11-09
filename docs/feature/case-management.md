# Case Management

> Module: Case and Adjudication Management.

## Case lifecycle

Case status follows the latest analysis; no person moves a Case ([ADR-0004](../adr/0004-identification-only.md)).

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
- `effectiveResultRevision`: latest published result; a revision is published automatically when it is complete ([case-level result](#case-level-result)).
- Technical retry with identical inputs and policies reuses the revision. Repeating a create with the same trigger and subject returns the existing Case; two creates running at the same time store one first revision, and a Case left at revision 0 by an interrupted create gets its first analysis when the create is repeated.
- Changed customer data, source Profile, scope or policy creates a new revision.
- A revision is the Case's current revision + 1. When a concurrent analysis took that number, the Case is re-read and the number, status and changes are recomputed, up to three attempts.
- Each revision is stored together with the input files it was run on, in one transaction ([atomic writes](persistence.md#atomic-writes)).
- The changes listed in a revision are compared against the last effective revision, never a PARTIAL or FAILED one.
- Late results are retained in their original revision and cannot overwrite newer effective results.

## Re-analysis after a Profile change

When a Profile import captures a new version, every Case whose latest stored input holds that Profile with different content gets a `REANALYZE_CASE` job, written in the same transaction as the version ([durable batch execution](pipeline-api-and-batch.md#durable-batch-execution)). The job re-analyzes the Case's latest stored input with each Profile file replaced, matched by Profile ID, by the current list version, whatever name or format the Case used for it. When the input already matches the list database, the Case is left unchanged and no revision is created.

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
