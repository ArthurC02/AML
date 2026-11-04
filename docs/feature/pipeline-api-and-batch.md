# Pipeline, API and Batch

> Modules: all; Batch and Work Management owns execution.

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
13. evaluate DispositionPolicy (Article 22) and assign review priority;
14. assemble explanation and publish if version/completeness checks pass.

## Triggers

- customer or related-party data change;
- watchlist/source Profile addition, change, removal or correction;
- policy-version change;
- authorized manual re-analysis.

All triggers are events; timed triggers are an [open question](../plan/open-questions.md#technology-governance).

## Services

| Service (Aml.Services) | Serves | Calls |
|---|---|---|
| CaseService | `/api/cases` | the readers (Extraction and Data Quality), `CaseAnalyzer` (Candidate and Identity Adjudication, Source Screening and Risk Assessment), `Explanation` (Explanation and Reporting) |
| BatchService | `/api/batches` | CaseService and ListService, through the job table (Batch and Work Management) |
| ListService | `/api/lists` | the list database, `ProfileIndex` and `ProfileMerge`; queues and runs the re-analysis of Cases whose Profile changed |

The module policies in Aml.Core are plain functions called by these services; they hold no state.

## REST resources

```text
POST /api/cases
POST /api/cases/{caseId}/analyses
GET  /api/cases/{caseId}
GET  /api/cases/{caseId}/analyses/{revision}
GET  /api/cases/{caseId}/explanation
POST /api/batches
GET  /api/batches/{batchId}
POST /api/batches/{batchId}/rerun
POST /api/lists/profiles
GET  /api/lists/profiles/{profileId}/versions
GET  /api/lists/candidates?mode=
GET  /api/lists/merges
```

An unknown case, revision, batch or Profile id returns 404.

Uploads accept `.txt` and `.html` files (extension in any case) up to 5 MB each. Multipart uploads are read as raw bytes: the case reader decodes export files as strict UTF-8 (BOM dropped), else CP950 (Big5) with an `ENCODING_FALLBACK:<file>` finding, and HTML pages by their own declared encoding. The reader records each file's SHA-256 as its source hash on the case input (`SourceHashes`): of the uploaded bytes for an upload, of the UTF-8 encoding of the text for batch items (files sent as JSON text) and re-analyses; the hashes are not stored with the revision. Each revision stores its input files as decoded text for re-analysis.

- Publication has no endpoint: step 14 publishes automatically when the version and completeness checks of the [case-level result](case-management.md#case-level-result) pass.
- Imported decisions are read from the input files, stored with `independentlyVerified` and never become SystemDecisions; there is no endpoint to submit decisions ([ADR-0020](../adr/0020-identification-only.md)).
- `GET …/explanation` is the explanation and audit view ([ADR-0009](../adr/0009-api-only.md)).
- Authentication is OIDC/OAuth 2.0 with policy-based authorization, wired once the identity provider is known.

### Profile import

`POST /api/lists/profiles` takes `名單資料_<id>.txt` or `.html` files (file pattern matched in any case), decoded as strict UTF-8, else CP950 (Big5), and returns:

```text
profiles[]          profileId, version, changed
batchId             the batch of queued re-analyses, or null when none was queued
queuedReanalyses[]  jobId, caseId, profileId, version
rejected[]          file names not imported
```

- The content hash is the SHA-256 of the normalized export text: an HTML page is first converted to its export text; the BOM is dropped, line endings become LF, trailing blanks on each line and trailing empty lines are removed. CRLF and LF copies, and the `.txt` and `.html` of one Profile, therefore hash the same.
- An unchanged hash keeps the current version; a different one captures a new version, and the old one is kept.
- Rejected: a name that is not a Profile file; one Profile twice in one import (for example `.txt` and `.html`); content whose `Profile_ID` is not the ID in the file name, or that the Profile reader cannot read.
- A new version queues a `REANALYZE_CASE` job for every Case whose latest input holds that Profile with different content, in the capture transaction ([re-analysis after a Profile change](case-management.md#re-analysis-after-a-profile-change)). All jobs of one import share `batchId`; track them with `GET /api/batches/{batchId}`.

### Retrieval and merges

- `GET /api/lists/candidates` returns candidates with `profileKey` (`DJ:<id>`), `matchedName`, `route`, `score`, `mode` and `truncated`. `truncated` is true on every candidate when the result holds fewer Profiles than matched, or more than `top` because exact alias matches are never cut. `DIRECT_REFERENCE` accepts the Profile ID with or without the `DJ:` prefix. The `INDEX_SEARCH` index is built once per list database version and rebuilt on the first search after a capture.
- `GET /api/lists/merges` returns `{ groups, rejected }`. Each group has `profileKeys`, `edges` (each accepted merge with `a`, `b`, `basis` and `documentKey`), `findings` and `basis`; `rejected` lists the merge edges refused because of a DOB or gender conflict, with `conflict`.

## Explanation contents

`GET …/explanation` returns, per pair:

- hit reason: hit number, screened subject (`customer` or `related party` with name), Profile ID, list types, Name Property, `Is RCA?`, matched field, similarity type and why the flags or category were used;
- comparison table in fixed order: Chinese name (with aliases, former and maiden names), English name / romanization / order, nationality, DOB or age, gender, document, address, occupation or employer, other identifiers; each row has subject value, Profile value, comparisonStatus and difference;
- evidence, conflicts, unknown fields and reason codes;
- source conditions, DispositionRecommendation and the pending verification checks;
- a five-part summary: internal data, external data, agreements and differences, key evidence or gaps, conclusion.

Per pair the response also carries `reviewPriority` (HIGH for PROBABLE_MATCH or Sanction, NORMAL for stage 3 row 5, LOW otherwise) and `pairReasonCodes` (e.g. `RELEASE_CONTROL_HUMAN_REVIEW`, `SOURCE_CONDITION_UNKNOWN`); per Case, `notEvaluatedProfiles` and `changesFromPreviousRevision`, compared against the last effective revision (`ADDED`, `REMOVED`, `RESULT_CHANGED`, `IDENTITY_CHANGED`, `REASONS_CHANGED`, each with from and to).

The summary only restates recorded facts; it adds none. Sources are listed with id, name, URL, retrieval date, where used and whether attached to the Profile. Nothing is dropped to shorten the response.

## Durable batch execution

A database job table ([ADR-0011](../adr/0011-database-job-table-for-batch.md)) with:

```text
jobId, jobType, caseId, analysisRevision, status, priority,
attemptCount, nextAttemptAt, leaseOwner, leaseExpiry,
createdAt, completedAt, errorCode
```

Job types:

| Type | Queued by | Payload | Runs |
|---|---|---|---|
| `ANALYZE_CASE` | `POST /api/batches` | subjectRef, triggerEventId, files | Case creation and first analysis, as `POST /api/cases` |
| `REANALYZE_CASE` | `POST /api/lists/profiles`, in the capture transaction | jobId, caseId, profileId, version | re-analysis of the Case's latest input with the current list versions of its Profiles |

Jobs are claimed optimistically ([job claim](persistence.md#job-claim)). Controls:

| Control | How |
|---|---|
| bounded workers | `JobWorker` keeps at most `Batch:MaxWorkers` (4) jobs running; a finished job's slot is refilled at once, and an empty queue is polled every `Batch:PollInterval` (2 s) |
| timeout and cancellation | `Batch:Timeout` (2 min) per job; the analysis itself is not interrupted, but a job past its deadline does not write its revision and fails with `TIMEOUT`. Host shutdown leaves the job RUNNING until its `Batch:Lease` (5 min) expires |
| lease | renewed just before the revision is written; a job that lost its lease stops without writing. Complete and fail change the job only for its lease owner; otherwise a warning is logged and the state is left to the owner |
| retry budget | `Batch:MaxAttempts` (3); backoff `Batch:RetryDelay` (10 s), doubling per attempt |
| idempotency key | unique across the job table. `ANALYZE_CASE:{triggerEventId}:{subjectRef}`: a repeat is reported as a duplicate, not queued. `REANALYZE:{caseId}:{profileId}:{version}`: a repeat is skipped in the outbox |
| checkpoint | Case creation is idempotent on trigger and subject, so a retry after a crash reuses the stored revision; a re-analysis whose input already matches the list database creates no revision |
| failed/dead-letter | `DEAD_LETTER` when the budget is spent, or at once without retry when the payload is unreadable (`INVALID_PAYLOAD`: unknown job type, bad JSON or a missing field) or the files are unusable (`INVALID_INPUT`); `LEASE_EXPIRED` for abandoned jobs |
| outcome not recorded | a database error while recording the result is logged as a warning; the job returns to the queue when its lease expires |
| operator re-run | `POST /api/batches/{batchId}/rerun` gives dead-lettered items a fresh budget |

Job statuses: `PENDING`, `RUNNING`, `SUCCEEDED` (with `caseId` and `analysisRevision`), `DEAD_LETTER`. The payload is stored in the job row so a restart loses nothing. `GET /api/batches/{batchId}` reports counts per status and each item with its idempotency key, attempts, `caseId`, `analysisRevision` and `errorCode`, for submitted batches and re-analysis batches alike.

## Idempotency and late results

Keys:

- Case creation: triggerEventId + subjectRef + scope.
- Stage result: caseId + revision + stage + subject/profile + input/policy versions.
- Candidate Profile: source namespace + Profile ID.

Late revision results remain historical and cannot republish over a later effective revision.
