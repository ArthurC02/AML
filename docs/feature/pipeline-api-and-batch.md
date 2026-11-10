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
| ListService | `/api/profiles`, `/api/cases/{caseId}/candidates` | the list database, `ProfileIndex` and `ProfileMerge`; queues and runs the re-analysis of Cases whose Profile changed |

The module policies in Aml.Core are plain functions called by these services; they hold no state.

## REST resources

```text
POST /api/cases
POST /api/cases/{caseId}/analyses
GET  /api/cases?subjectRef=&triggerEventId=&pageSize=&pageToken=
GET  /api/cases/{caseId}?view=full|summary
GET  /api/cases/{caseId}/hits/{hit}?view=detail|summary&revision=
GET  /api/cases/{caseId}/revisions
GET  /api/cases/{caseId}/revisions/{revision}
GET  /api/cases/{caseId}/workbook?view=summary|full&revision=
GET  /api/cases/{caseId}/candidates?source=table|previous&top=
POST /api/batches
GET  /api/batches/{batchId}?view=summary|full&status=&pageSize=&pageToken=
POST /api/batches/{batchId}/rerun
POST /api/profiles/import
GET  /api/profiles/search?name=&top=
GET  /api/profiles/{profileId}?view=full|summary&version=
GET  /api/profiles/{profileId}/versions
GET  /api/profiles/merges
GET  /api/codes?kind=
GET  /api/codes/{code}
GET  /api/schemas
GET  /api/schemas/{name}
```

`/api/codes` is the code catalog (`Core/Codes/CodeCatalog`): every code the system emits, with its kind, Chinese name, one-line meaning, the rule that emits it and the document section that defines it. A value with detail attached is listed under its key (`SOFT_DIFFERENCE（Jaccard …）` → `SOFT_DIFFERENCE`, `ENCODING_FALLBACK:<file>` → `ENCODING_FALLBACK`); a value joined with `；` holds several codes, each looked up on its own. `/api/schemas` publishes the JSON Schemas of the stored records (`case-analysis`, `case-explanation`, `analysis-lineage`, `analysis-quality`, `revision`) and of the views (`case-detail`, `case-summary`, `hit-summary`, `revision-line`, `case-page`; [data contracts](data-contracts.md#published-schemas)).

Every operation in the OpenAPI document (`/openapi/v1.json`) has a unique `operationId` (`createCase`, `getCase`, `getHit`, …), one tag for its business area (`Cases`, `Batches`, `Profiles` or `Reference data`) and a description of what it does and the business rules that apply ([ADR-0023](../adr/0023-business-api-data-and-workbook.md)). The response examples are the GC-03 Case and hit summaries (`src/Aml.Api/Examples`), with a made-up Case id, subject reference, trigger event id and Profile name: the example of `createCase` and the `view=summary` example of `getCase` and `getHit`. The full views have no example, since they hold identifier values. A Golden Case test fails when GC-03's summaries no longer match the examples. The document and the published schemas are kept as contract snapshots; a test fails on any difference.

`POST /api/cases` takes multipart form fields (`subjectRef`, `triggerEventId`, `files`) or a JSON body shaped as one batch item (`{subjectRef, triggerEventId, files: {name: text}}`). It returns `201` with `Location` when this call created the Case, and `200` when a Case for the trigger and subject already existed: created earlier, left at revision 0 and resumed now, or created by a concurrent request. A request body over `Api:MaxRequestBytes` (100 MB) is refused with `413` (`HTTP_ERROR`), multipart or JSON. Every `/api` response, errors included, is sent with `Cache-Control: no-store` and `X-Content-Type-Options: nosniff`: answers hold personal data. For the same reason the hosting request log lines, which carry the URL and its query, are below the default log level (`Microsoft.AspNetCore.Hosting.Diagnostics` at Warning), and an uploaded file up to 5 MB is buffered in memory, not in a temp file.

## Views

A Case, a hit and a Profile are returned whole by default; `view=summary` returns a summary, which leaves detail out and never adds any (`Core/Cases/CaseViews`, pure functions over the stored revision):

- **Case** (`GET /api/cases/{caseId}`, `view=full` by default): the Case record (ids, status, current and effective revision, times), the full record of its effective revision (`effective`, left out when there is none) and the link to its revision list.
- **Case summary** (`view=summary`, also returned by `POST /api/cases` and `POST …/analyses`): the Case's ids and status, the effective revision and its time, the case result, a conclusion sentence and one line per hit (Profile ID, key and name, pair result, review priority, identity decision, and a link to that hit's summary in the same revision). Hits are ordered by review priority, then hit number; at most 10 are listed and the rest counted in `omitted`. Without an effective revision there is no result, the conclusion says why and `revision` links the latest one (none before the first analysis). `newerRevisionIncomplete` marks a newer revision that is not complete and so did not replace the result.
- **Hit** (`GET …/hits/{hit}`, `view=detail` by default): the full hit explanation ([explanation contents](#explanation-contents)).
- **Hit summary** (`view=summary`): the Profile, the source condition types that are HIT, the identity decision with its hard conflict or stage 3 row and its reason codes, the pair result and priority, up to 3 decisive pieces of evidence and `wouldChangeIf`. The decisive evidence is what decided the hit: on stage 3 rows 2 to 5 the strongest support; otherwise (row 1, row 6, a hard conflict, a non-Person rule) the conflicts whose code is one of the hit's reason codes, in reason-code order, so the deciding conflict comes first. A rule with no evidence of its own, such as a death before the subject's birth, lists none. On row 6 without conflicts the note 「僅前提條件相符」 stands in. The reason codes name the rule, such as a hard conflict downgraded to REVIEW for source quality (`SOURCE_QUALITY_DOWNGRADE`). Evidence is listed by group, status, strength and code, without the compared values; `detail` links the full hit.
- **Revision** (`GET …/revisions/{revision}`): the full record of one revision: result, explanation, lineage and data quality. Nothing is left out of it, unlike the summaries. `GET …/revisions` lists every revision with its time, execution status, case result, whether it is effective and how many changes it made against the previous effective revision (none for the first analysis or a revision without an explanation).

The hit views read the effective revision unless `?revision=` names another; any other `view` is `INVALID_REQUEST`. The full views hold identifier values; the summaries hold no field marked `IDENTIFIER` and, of the `PERSONAL` fields, only names ([data classification](security-and-privacy.md#data-classification)). Views and revision records leave out null fields; the Case and hit summaries also leave out empty lists.

## Case workbook

`GET /api/cases/{caseId}/workbook` returns the effective revision, or `?revision=`, as an Excel workbook (`Infra/Excel/CaseWorkbook`, written by `Infra/Excel/Xlsx` with the standard library; [ADR-0023](../adr/0023-business-api-data-and-workbook.md)). It restates the stored revision and adds nothing. Its eleven sheets are each one table with a bold, frozen and filterable header row, and the hit number joins them:

| Sheet | Rows |
|---|---|
| 說明 | the Case (ids, status, revision, execution status, analysis time in UTC, case result, conclusion, changes), lineage (input file hashes, Profile versions, rule versions, the news model version when the models ran), quality counts and rates, and a data dictionary naming the field and meaning of every column |
| 命中 | one per hit: Profile, list categories, HIT conditions, identity decision, stage 3 row, hard conflict, reason codes, pair result, review priority, Article 22 recommendation, findings, unknown fields, `wouldChangeIf`, the summary sentences and the news summary (full view only) |
| 比對 | one per comparison row: field, status and difference |
| 證據 | one per support and conflict item: group, direction, status, strength and detail |
| 來源 | one per source: ref, id, name and URL (full view only), dates, use and whether it is attached to the Profile |
| 新聞人物 | one per person the [news digest](watchlist-and-adverse-information.md#news-digest) names: name (full view only), match to the Profile person, roles, party and mentions |
| 新聞事件 | one per news event: type, certainty, source, actor (full view only), actor match, date as written and resolved, agency, offences and their risk categories, amount, penalty, requested penalty, finality and the quoted sentence (full view only) |
| 模型人物 | one per person or organisation the [news models](watchlist-and-adverse-information.md#news-models) found: name and other forms (full view only), organisation or not, party, whether it is the Profile person, mentions and model version; empty without the models |
| 模型事件 | one per event the news models read: name (full view only), type, certainty, probability and the sentence (full view only) |
| 變更 | one per change from the previous effective revision; none for a first analysis, which 說明 says |
| 代碼 | every catalogued code the other sheets show, with its kind, Chinese name and meaning |

`view=summary`, the default, holds what the summaries hold: no `IDENTIFIER` field, no compared value or note and, of personal data, only the Profile's main name. `view=full` adds the screened subject, matched names, the internal and external data sentences, compared values, notes, and source names and URLs (a news title can quote personal data). The 說明 conclusion is the Case summary's for the effective revision; another revision's says which revision is effective. A value joined with `；`, or with `,` in a change, is looked up as several codes for 代碼. Text is a text cell formatted as Text, never a formula, and Excel does not flag text that looks like a number; characters XML cannot carry are dropped, text over 32,767 characters is cut with a note, and literal `_xHHHH_` text is then escaped so Excel shows it as given (the limit counts the text shown, not the escapes). URLs are text, not links. Times are UTC. The same revision gives the same bytes on the same .NET runtime; another runtime may compress differently. The file is named `case-<caseId>-revision-<n>.xlsx` and sent with `Cache-Control: no-store` and `X-Content-Type-Options: nosniff`, and each workbook issued is logged with its Case, revision and view, never its content (`Aml.Api.Workbook`); the entry is written before the transfer, so it does not prove the file arrived. An unknown `view` is `INVALID_REQUEST`; an unknown Case or revision, or a Case without an effective revision and no `?revision=`, is `404`.

Known limits:

- A downloaded file opens in Excel's Protected View (Mark of the Web); it has no macros, links or external references to enable.
- Excel saves text it was given as formula-like (`=`, `+`, `-`, `@` first) as text in `.xlsx`; saved as CSV and reopened, such a value can run as a formula. The workbook does not alter values to prevent it.
- Cells are inline strings, which Excel, LibreOffice and openpyxl read; a reader that supports only shared strings may fail on them.

How it is sent:

- The workbook is built in memory before the response starts, so a failure while building it is a `500` problem, not a cut file (a transfer the client aborts can still end short of `Content-Length`), and the response carries `Content-Length` and `Content-Disposition: attachment` with the file name, encoded by ASP.NET Core. `ZipArchive` writes synchronously, which ASP.NET Core forbids on the response body, so it never writes there. A workbook is tens of kilobytes; a Case large enough for the copy to matter would stream from a temporary file instead.
- There are no range requests, `ETag` or `Last-Modified`: a `no-store` response is not resumed or revalidated.
- Workbooks are built a few at a time: a concurrency limit (ASP.NET Core rate limiting) lets `Api:WorkbookConcurrency` (default 16) build at once and `Api:WorkbookQueue` (default 64) wait, oldest first. A request past both is refused before the Case is read, with `429`, `Retry-After: 1` and an `HTTP_ERROR` problem; other endpoints are not limited. The limit is per instance, not per user; values that cannot work (concurrency under 1, a negative queue) stop the start. A workbook takes about 1 ms of CPU and 13 KB for a Golden Case, so the limit mainly bounds the database reads and memory a burst takes.
- Telemetry: each workbook issued adds to `aml.workbooks` and records `aml.workbook.duration` (ms, reads included, the wait for a permit not: that is `aspnetcore.rate_limiting.request.time_in_queue`) and `aml.workbook.size` (bytes), each tagged with `view`; a `BuildCaseWorkbook` trace span carries the view and, once the Case is found, its id and the revision. Rejected requests appear in ASP.NET Core's own rate-limiting metrics (`aspnetcore.rate_limiting.*`) and as `429` in the HTTP server metrics.
- The app adds no response compression, output caching or CORS. When a deployment adds them: leave the workbook out of compression (an `.xlsx` is already a zip, and compressing a response that holds secrets over HTTPS invites BREACH) and out of output caching, which caches whatever it is told regardless of `no-store`; a browser client on another origin reads the file name only if CORS exposes `Content-Disposition`. The endpoint needs an authorization policy once authentication is wired; limits per user and alerts on bulk downloads of personal data wait for it ([open questions](../plan/open-questions.md#case-and-execution)).

## Pagination

`GET /api/cases` needs `subjectRef`, `triggerEventId` or both. It returns Cases ordered by creation time, then id, in pages of `pageSize` (default 20, at most 100), with `nextPageToken` only when more follow. The token is the sort key of the page's last Case, base64url-encoded; it is passed back unchanged as `pageToken`, and one this API did not issue is `INVALID_REQUEST`. Paging by key, not offset, never repeats a Case and skips none that existed when paging started; a Case created meanwhile appears on a later page if its creation time sorts after the current position.

`GET /api/batches/{batchId}` pages its items the same way, by creation time, then job id, with `nextPageToken` null on the last page: `view=summary` (the default) lists the items not yet succeeded, `view=full` every item, and `status=` (`PENDING`, `RUNNING`, `SUCCEEDED`, `DEAD_LETTER`) the items in one status. A filter applies when each page is read, so an item whose status changes while paging can drop out of a filtered listing. The counts per status cover the whole batch as of that read.

## Errors

Every 4xx and 5xx response is an RFC 9457 problem (`application/problem+json`): `type`, `title`, `status`, `detail`, `errors[]` (what is wrong where) and `fix` (what to send instead). `type` is the problem's entry in the code catalog, `/api/codes/{code}`. A problem the framework raises (an unmatched route or unknown id, a parameter of the wrong type, a body that cannot be read, is over the size limit or arrives too slowly, a method the route does not allow, an unhandled exception) has the catalog description as `detail`, the request's `method` and `path` in `errors[]`, and the fix of its code:

| Code | Status | When |
|---|---|---|
| `INVALID_REQUEST` | 400 | a parameter or body the endpoint cannot use: no files, a `subjectRef` or `triggerEventId` missing, repeated, over 128 characters, with spaces at either end or holding control or format characters, a batch item that breaks any rule of `POST /api/cases`, its file rules included (`errors[]` holds `{item, error}`: the item's index and the error `POST /api/cases` would give), an unknown candidate source or view, a search without a name or filter, a `top` or page size out of range or not a number, a page token this API did not issue; `errors[]` lists the batch items by index or names the parameter, except for a value of the wrong type or a body that is not JSON, which the framework reports as above |
| `UNSUPPORTED_FILE` | 400 | a file that is not `.txt`, `.html` or `.htm`, or over 5 MB, or two files that read as one name (letter case is ignored), or a name that is not plain (a folder, a control character or one of `\ / : * ? " < > \|`, the same rule on every OS), as multipart or JSON; a multipart client's folder is dropped first and never echoed back. `errors[]` gives each file, cut to 100 characters, with its size and `problem` (`TYPE`, `SIZE`, `NAME`, `DUPLICATE`, `MISSING`) |
| `INPUT_INCOMPLETE` | 400 | a required input file is missing or not unique: exactly one `案件資訊*.txt`, and the subject's own file (`客戶-背景資料.txt` for a customer, `客戶-關聯人背景資料.txt` for a related party) |
| `INPUT_UNREADABLE` | 400 | a file that is not in the export layout; `errors[]` gives `file`, `table` and `problem` (`MISSING_SECTION`, `MISSING_COLUMN`, `MALFORMED`) |
| `NOT_FOUND` | 404 | an unknown case, revision, hit, batch, Profile, code or schema, or no such route |
| `HTTP_ERROR` | other 4xx | e.g. a method the route does not allow |
| `INTERNAL_ERROR` | 5xx | an unexpected failure |

A missing Profile file is not an input problem: that candidate is `NOT_EVALUATED` and the revision `PARTIAL`. The required-file check runs in the case reader (`TxtCaseReader.RequiredFileProblem`), which reports every unusable input as `CaseInputException`; the endpoints turn it into a problem, and a batch job into `INVALID_INPUT`.

Uploads accept `.txt`, `.html` and `.htm` files (extension in any case) up to 5 MB each: a multipart file by its own bytes, a file sent as JSON text (a batch item or a JSON `POST /api/cases`) by its UTF-8 encoding, so the same CP950 export counts larger as JSON. A JSON item that is null, lacks `subjectRef` or `triggerEventId`, has a file name with a path or characters not allowed in a file name, or has a null file content is refused, as is a form that repeats `subjectRef` or `triggerEventId`. Multipart uploads are read as raw bytes: the case reader decodes export files as strict UTF-8 (BOM dropped), else CP950 (Big5) with an `ENCODING_FALLBACK:<file>` finding, and HTML pages by their own declared encoding. The reader records each file's SHA-256 as its source hash on the case input (`SourceHashes`): of the uploaded bytes for an upload, of the UTF-8 encoding of the text for JSON input and for the re-analyses a Profile import queues (`REANALYZE_CASE`, run on the stored text); `POST …/analyses` is multipart and hashes the uploaded bytes. The hashes are stored in the revision's lineage ([lineage and quality](#lineage-and-quality)). Each revision stores its input files as decoded text for re-analysis.

- Publication has no endpoint: step 14 publishes automatically when the version and completeness checks of the [case-level result](case-management.md#case-level-result) pass.
- Imported decisions are read from the input files, stored with `independentlyVerified` and never become SystemDecisions; there is no endpoint to submit decisions ([ADR-0004](../adr/0004-identification-only.md)).
- The revision record (`GET …/revisions/{revision}`) is the explanation and audit view ([ADR-0006](../adr/0006-one-dotnet-api-application.md)).
- Authentication is OIDC/OAuth 2.0 with policy-based authorization, wired once the identity provider is known.

### Profile import

`POST /api/profiles/import` takes `名單資料_<id>.txt` or `.html` files (file pattern matched in any case), decoded as strict UTF-8, else CP950 (Big5), and returns:

```text
profiles[]          profileId, profileKey, version, changed
batchId             the batch of queued re-analyses, or null when none was queued
queuedReanalyses[]  jobId, caseId, profileId, profileKey, version
rejected[]          file names not imported
```

- The content hash is the SHA-256 of the normalized export text: an HTML page is first converted to its export text; the BOM is dropped, line endings become LF, trailing blanks on each line and trailing empty lines are removed. CRLF and LF copies, and the `.txt` and `.html` of one Profile, therefore hash the same.
- An unchanged hash keeps the current version; a different one captures a new version, and the old one is kept.
- Rejected: a name that is not a Profile file; one Profile twice in one import (for example `.txt` and `.html`); content whose `Profile_ID` is not the ID in the file name, or that the Profile reader cannot read.
- A new version queues a `REANALYZE_CASE` job for every Case whose latest input holds that Profile with different content, in the capture transaction ([re-analysis after a Profile change](case-management.md#re-analysis-after-a-profile-change)). All jobs of one import share `batchId`; track them with `GET /api/batches/{batchId}`.

### Retrieval and merges

- `GET /api/profiles/search?name=` (`INDEX_SEARCH`) and `GET /api/cases/{caseId}/candidates` (`source=table`, the default: the Case's own candidate table, `CONTROLLED_SET`; `source=previous`: the Profiles of its latest input, `PREVIOUS_LINK`) return candidates with `profileId`, `profileKey` (`DJ:<id>`), `matchedName`, `route`, `score`, `mode` and `truncated`, at most `top` (1 to 100, default 10). `truncated` is true on every candidate when the result holds fewer Profiles than matched, or more than `top` because exact alias matches are never cut. The `INDEX_SEARCH` index is built once per list database version and rebuilt on the first search after a capture.
- `GET /api/profiles/{profileId}` (`DIRECT_REFERENCE`) returns one captured Profile, at its latest version unless `?version=` names another, with `profileId`, `profileKey`, `version`, `latestVersion`, `contentHash` and `capturedAt`. `view=full` (the default) adds the Profile as read. `view=summary` adds the record type, gender, names, citizenship, categories, keywords, roles, official-list entry and findings, and `full`, the link to the full view; like the Case summaries it holds no field marked `IDENTIFIER` and, of the `PERSONAL` fields, only names, so dates of birth and death, documents, other identifiers, addresses, relationships and sources are left out. Any other view is `INVALID_REQUEST`. Routes take the Profile ID with or without the `DJ:` prefix. `GET …/versions` lists every version with its content hash.
- `GET /api/profiles/merges` returns `{ groups, rejected }`. Each group has `profileIds`, `profileKeys`, `edges` (each accepted merge with `a`, `b`, `basis` and `documentKey`), `findings` and `basis`; `rejected` lists the merge edges refused because of a DOB or gender conflict, with `conflict`.

## Explanation contents

The revision record's `explanation` holds, per pair:

- hit reason: hit number, screened subject (`customer` or `related party` with name), `profileId`, `profileKey` and main name (`profileName`), the names the candidate table matched (`matchedNames`), `isRca` (the table's `Is RCA?`), the list categories (`sourceCategories`), whether news is attached (`attachedNews`), and the source conditions below, which say why the flags or category were used;
- comparison table in fixed order: Chinese name (with aliases, former and maiden names), English name / romanization / order, nationality, DOB or age, gender, document, address, occupation or employer, other identifiers; each row has subject value, Profile value, comparisonStatus, `difference` (codes, joined with `；`) and `note` (reviewer text, such as how a stated age was reckoned);
- related parties named by the bank data (`SUBJECT`) and by the Profile (`PROFILE`);
- evidence, conflicts, unknown fields and reason codes;
- `wouldChangeIf`: the stage 3 row and code the identity decision would move to if a document of the same type, or a full DOB, were comparable and agreed or conflicted ([contrastive explanation](identity-adjudication.md#contrastive-explanation)); it never states an external result;
- source conditions, DispositionRecommendation and the pending verification checks;
- a five-part summary: internal data, external data, agreements and differences, key evidence or gaps, conclusion.

Per pair the response also carries `reviewPriority` (HIGH for PROBABLE_MATCH or Sanction, NORMAL for stage 3 row 5, LOW otherwise) and `pairReasonCodes` (e.g. `RELEASE_CONTROL_HUMAN_REVIEW`, `SOURCE_CONDITION_UNKNOWN`); per Case, `notEvaluatedProfiles` and `changesFromPreviousRevision`, compared against the last effective revision (`ADDED`, `REMOVED`, `RESULT_CHANGED`, `IDENTITY_CHANGED`, `REASONS_CHANGED`, each with `profileId`, `profileKey`, from and to).

The summary only restates recorded facts; it adds none. Sources are listed with `ref` (`S1`, `S2`, …), id, name, URL, retrieval date, where used and whether attached to the Profile; the summary's external-data part cites them as `[S1]`. Nothing is dropped to shorten the response.

## Lineage and quality

Each revision is stored with:

- **lineage**: every input file with its SHA-256; every Profile the analysis used, with the list database version holding the same content (`LIST_DATABASE`) or `CASE_FILES` when it came from the Case's files only, and its content hash; the versions of the source policy, the code catalog, the stored schema and the application;
- **quality**: comparison cells in total and by missing, uncertain and conflict, with their rates; Profiles not evaluated; data quality findings counted by code, once per hit. The Golden metrics compute their cell rates with the same function (`AnalysisQuality.Of`).

## Durable batch execution

A database job table ([ADR-0010](../adr/0010-persistence-and-batch.md)) with:

```text
jobId, jobType, caseId, analysisRevision, status, priority,
attemptCount, nextAttemptAt, leaseOwner, leaseExpiry,
createdAt, completedAt, errorCode
```

Job types:

| Type | Queued by | Payload | Runs |
|---|---|---|---|
| `ANALYZE_CASE` | `POST /api/batches` | subjectRef, triggerEventId, files | Case creation and first analysis, as `POST /api/cases` |
| `REANALYZE_CASE` | `POST /api/profiles/import`, in the capture transaction | jobId, caseId, profileId, profileKey, version | re-analysis of the Case's latest input with the current list versions of its Profiles |

Jobs are claimed optimistically ([job claim](persistence.md#job-claim)). Controls:

| Control | How |
|---|---|
| bounded workers | `JobWorker` keeps at most `Batch:MaxWorkers` (32) jobs running; a finished job's slot is refilled at once, and an empty queue is polled every `Batch:PollInterval` (2 s) |
| timeout and cancellation | `Batch:Timeout` (2 min) per job; the analysis itself is not interrupted, but a job past its deadline does not write its revision and fails with `TIMEOUT`. On host shutdown the running analyses are cancelled and the worker waits for every job to settle, up to `Batch:ShutdownTimeout` (2.5 min, which sets the host's shutdown timeout): an analysis already stored records its outcome, any other job goes back to `PENDING` at once without spending an attempt (at most `Batch:MaxReleases`, 5, times per job; past that each interruption counts, and a job whose attempts are spent is dead-lettered with `RELEASE_LIMIT`), and its Case, if stored, is reused when it runs again. A job whose worker died is reclaimed when its lease expires and is due again after `Batch:RetryDelay`. The settings are checked at startup: `Batch:Timeout` must be below `Batch:Lease`, and `Batch:ShutdownTimeout` above `Batch:Timeout` |
| lease | renewed just before the revision is written; a job that lost its lease stops without writing. Complete and fail change the job only for its lease owner on the attempt it claimed, while it is RUNNING; otherwise a warning is logged and the state is left to the owner |
| retry budget | `Batch:MaxAttempts` (3); backoff `Batch:RetryDelay` (10 s), doubling per attempt |
| idempotency key | unique across the job table. `ANALYZE_CASE:{length of triggerEventId}:{triggerEventId}:{subjectRef}` (the length keeps a `:` inside a ref from making two alerts one key): a repeat is reported as a duplicate, not queued. A job queued under the earlier key form (without the length) is not matched; resubmitting its alert queues a job that finds the alert's existing Case. `REANALYZE:{caseId}:{profileId}:{version}`: a repeat is skipped in the outbox |
| checkpoint | Case creation is idempotent on trigger and subject, so a retry after a crash reuses the stored revision; a re-analysis whose input already matches the list database creates no revision |
| durability | the submit is durable; a job's own writes commit without a log sync ([ADR-0010](../adr/0010-persistence-and-batch.md)). After a power loss the newest of them may be gone: the job reads PENDING, or RUNNING until its lease expires, and runs again; a job on its last attempt is dead-lettered (`LEASE_EXPIRED`) and needs a re-run. A batch item's new Case takes the job's id, so the re-run finds or stores the same Case |
| failed/dead-letter | `DEAD_LETTER` when the budget is spent, or at once without retry when the payload is unreadable (`INVALID_PAYLOAD`: unknown job type, bad JSON or a missing field) or the files are unusable (`INVALID_INPUT`, after which the fixed files go to `POST /api/cases`, since the idempotency key refuses a second batch item); `TIMEOUT` past the deadline; `EXCEPTION:<type>` for an unexpected exception; `LEASE_EXPIRED` for abandoned jobs; `RELEASE_LIMIT` for a job interrupted by host stops more than `Batch:MaxReleases` times |
| outcome not recorded | a database error while recording the result is logged as a warning; the job returns to the queue when its lease expires |
| operator re-run | `POST /api/batches/{batchId}/rerun` gives dead-lettered items a fresh budget |

Job statuses: `PENDING`, `RUNNING`, `SUCCEEDED` (with `caseId` and `analysisRevision`), `DEAD_LETTER`. The payload is stored in the job row so a restart loses nothing. `GET /api/batches/{batchId}` reports counts per status and, a page at a time ([pagination](#pagination)), the items with their idempotency key, attempts, `caseId`, `analysisRevision` and `errorCode`, for submitted batches and re-analysis batches alike.

## Idempotency and late results

Keys:

- Case creation: triggerEventId + subjectRef + scope.
- Stage result: caseId + revision + stage + subject/profile + input/policy versions.
- Candidate Profile: source namespace + Profile ID.

Late revision results remain historical and cannot republish over a later effective revision.
