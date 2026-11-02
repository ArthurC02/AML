# Pipeline, API and Batch

> Modules: all; Batch and Work Management owns execution. Design baseline 2.0-PoC, 2026-11-02.

## AnalyzeCase flow

1. create Case and accept AnalysisRevision;
2. pin customer, source and policy versions;
3. extract customer snapshot and quality findings;
4. normalize names, dates, documents and addresses;
5. retrieve CandidateHits;
6. deduplicate CandidateProfiles;
7. load pinned source Profile snapshots;
8. create field-level evidence;
9. run temporal/role and Hard Conflict gates;
10. produce SystemDecision;
11. evaluate source-condition policies;
12. form ScreeningAssessments;
13. evaluate DispositionPolicy (Article 22) and assign human-review priority;
14. accept HumanDecision, verification records and approvals when provided;
15. create FinalDisposition;
16. assemble explanation and publish if version/completeness checks pass.

## Triggers

- customer or related-party data change;
- watchlist/source Profile addition, change, removal or correction;
- policy-version change;
- authorized manual re-analysis.

All triggers are events; timed triggers are an [open question](../plan/open-questions.md#technology-governance).

## Services

| Service | Module |
|---|---|
| CaseService, ReviewService, DispositionService | Case and Adjudication Management |
| BatchService | Batch and Work Management |
| ExtractionService | Extraction and Data Quality |
| CandidateService, IdentityAdjudicationService | Candidate and Identity Adjudication |
| SourceScreeningService | Source Screening and Risk Assessment |
| ExplanationService | Explanation and Reporting |

ReviewService sits in Case and Adjudication Management because only that module publishes Case state ([write ownership](system-overview.md#write-ownership)).

## REST resources

```text
POST /api/cases
POST /api/cases/{caseId}/analyses
GET  /api/cases/{caseId}
GET  /api/cases/{caseId}/analyses/{revision}
POST /api/cases/{caseId}/human-decisions
POST /api/cases/{caseId}/human-decisions/{decisionId}/approvals
POST /api/cases/{caseId}/verifications
POST /api/cases/{caseId}/imported-decisions
POST /api/cases/{caseId}/close
POST /api/cases/{caseId}/reopen
GET  /api/cases/{caseId}/explanation
POST /api/batches
GET  /api/batches/{batchId}
```

- Publication has no endpoint: step 16 publishes automatically when the version and completeness checks of the [case-level result](case-management.md#case-level-result) pass.
- `close` and `reopen` enforce the [lifecycle transitions](case-management.md#case-lifecycle).
- Imported decisions are stored with `independentlyVerified` and never become SystemDecisions.
- `GET …/explanation` is the reviewer and audit view ([ADR-0009](../adr/0009-api-only.md)).
- Approvals check the caller's role: approver, or senior approver for UNRESOLVED ([roles](review-and-disposition.md#roles)).
- Authentication is OIDC/OAuth 2.0 with policy-based authorization, wired once the identity provider is known.

## Explanation contents

`GET …/explanation` returns, per pair:

- hit reason: hit number, screened subject (`customer` or `related party` with name), Profile ID, list types, Name Property, `Is RCA?`, matched field, similarity type and why the flags or category were used;
- comparison table in fixed order: Chinese name (with aliases, former and maiden names), English name / romanization / order, nationality, DOB or age, gender, document, address, occupation or employer, other identifiers; each row has subject value, Profile value, comparisonStatus and difference;
- evidence, conflicts, unknown fields and reason codes;
- source conditions, DispositionRecommendation and verification records;
- a five-part summary: internal data, external data, agreements and differences, key evidence or gaps, conclusion.

The summary only restates recorded facts; it adds none. Sources are listed with id, name, URL, retrieval date, where used and whether attached to the Profile. Nothing is dropped to shorten the response.

## Durable batch execution

A database job table ([ADR-0011](../adr/0011-database-job-table-for-batch.md)) with:

```text
jobId, jobType, caseId, analysisRevision, status, priority,
attemptCount, nextAttemptAt, leaseOwner, leaseExpiry,
createdAt, completedAt, errorCode
```

Required controls:

- bounded workers;
- timeout and cancellation;
- retry budget;
- idempotency key;
- checkpoint;
- failed/dead-letter status;
- operator re-run.

Jobs are claimed optimistically ([job claim](persistence.md#job-claim)).

## Idempotency and late results

Keys:

- Case creation: triggerEventId + subjectRef + scope.
- Stage result: caseId + revision + stage + subject/profile + input/policy versions.
- Candidate Profile: source namespace + Profile ID.

Late revision results remain historical and cannot republish over a later effective revision.
