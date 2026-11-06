# Operations Runbook (Gate D draft)

> Draft for [Gate D](poc-delivery-plan.md#gate-d-before-production). It describes the PoC as built; items marked **Pending** wait for bank decisions ([open questions](open-questions.md)).

## Service

| Item | Value |
|---|---|
| Process | `Aml.Api` (ASP.NET Core); hosts the REST API and `JobWorker` |
| Database | SQLite file from `Database:ConnectionString`, in write-ahead-log mode: `<file>-wal` and `<file>-shm` sit next to it while it is in use and belong to it. **Pending:** target database (SQL Server or Oracle) |
| Schema | One DbUp script (`0001_schema.sql`) runs at startup; a failed upgrade or an invalid SQL catalog stops the process |
| Health | `GET /health` returns `Healthy` when the database answers `System.Ping` |
| Telemetry | OpenTelemetry; set `OTEL_EXPORTER_OTLP_ENDPOINT` to export traces, metrics and logs |
| Authentication | **Pending:** OIDC identity provider. Until then, run only on a closed network |

## Routine checks

- `GET /health` every minute; alert after three failures.
- `aml.job.attempts{outcome="DEAD_LETTER"}` above zero: inspect the batch (below).
- `aml.analyses{execution_status="Partial"}`: a Profile file was missing; the Case keeps its previous effective result until a complete re-analysis.
- `aml.analysis.duration` p95 trend. **Pending:** latency target.
- Job worker warnings in the log: a job that lost its lease, or whose outcome could not be recorded. Repeated warnings point to jobs outrunning `Batch:Lease` or to database errors.

## Batch operations

1. `GET /api/batches/{batchId}`: counts per status and the items not yet succeeded, with attempts and `errorCode` (`?view=full` lists every item, `?status=DEAD_LETTER` one status; follow `nextPageToken` for more). Re-analyses queued by a Profile import are batch jobs too (`REANALYZE_CASE`); their `batchId` is in the import response.
2. `INVALID_INPUT`: the files are incomplete or not in the export layout. Re-running fails the same way, and a new batch with the same `triggerEventId` and `subjectRef` is reported as a duplicate; create the Case from the fixed files with `POST /api/cases`. The batch item stays `DEAD_LETTER` and is not linked to that Case; a later re-run of the batch fails it again.
3. `INVALID_PAYLOAD`: the job row has an unknown type, unreadable JSON or a missing field. It is dead-lettered at once; re-running it fails the same way.
4. `TIMEOUT` or `EXCEPTION:<type>` (for example `EXCEPTION:SqliteException`): fix the cause, then `POST /api/batches/{batchId}/rerun`.
5. `LEASE_EXPIRED`: a worker stopped mid-job without settling it (killed, crashed, or stopped past `Batch:ShutdownTimeout`). The job returns to the queue after `Batch:RetryDelay` while budget remains; otherwise re-run. Every job running in a process that crashed is charged an attempt, the job that caused the crash and its neighbours alike: a crash repeated `Batch:MaxAttempts` times dead-letters them all, and the re-run brings the innocent ones back. The warning "job(s) whose worker stopped without a word were reclaimed" and `aml.job.attempts{outcome="RECLAIMED"}` count them.
6. Stopping: the worker cancels running analyses and waits for each job to settle, up to `Batch:ShutdownTimeout` (2.5 min, above `Batch:Timeout`); the log shows "waiting for running jobs" and then "settled in N s". A container's grace period (Kubernetes `terminationGracePeriodSeconds`, 30 s by default) must be longer, or the process is killed first. A job interrupted by a stop goes back to the queue without spending an attempt, at most `Batch:MaxReleases` (5) times; past that each interruption counts, so a job that keeps the host from stopping cleanly dead-letters with `RELEASE_LIMIT` (`aml.job.attempts{outcome="RELEASED"}` counts releases); find why it outlasts a stop, then re-run.
7. Scale-out: several instances may share one SQLite database only on one host with a local disk (WAL over a network share, NFS, SMB or a shared volume, is unsafe). Group commit is per process, so instances on one file contend for the write lock (`busy_timeout` 5 s) and a write that waits longer fails. More than one host needs a server database; the claim is optimistic, so a job still runs on one worker at a time.

## Looking up a Case

- `GET /api/cases?subjectRef=` or `?triggerEventId=` finds the Case; `GET /api/cases/{caseId}` gives the Case with the full record of its effective revision; `?view=summary` gives its result, conclusion and hits, with links to each hit summary and to the full revision.
- `GET /api/cases/{caseId}/revisions` shows every revision and which one is effective; a PARTIAL latest revision means a Profile file was missing (`notEvaluatedProfiles` in that revision), and the Case keeps its previous result.
- `GET /api/cases/{caseId}/revisions/{revision}` is the full record: result, explanation, lineage (input hashes, Profile list versions, rule-set versions) and data quality.

## Audit

- Every analysis revision is kept (`case_analysis`); nothing is overwritten. A late revision never replaces a newer effective one.
- Each revision stores the full result and the explanation as JSON: evidence, source conditions with basis and policy version, identity decision, reason codes, review priority and the changes from the last effective revision. With it are its lineage (input file hashes, the list version of each Profile, rule-set and application versions, the analysis date) and its data quality counts.
- Document and reference numbers are masked in the explanation (first 3 and last 2 characters). The input files of every revision are stored (`case_input`) for re-analysis, as are batch job payloads; they hold unmasked personal data, so database access is restricted accordingly.
- Every captured Profile version is kept with the SHA-256 of its normalized export text (`list_profile`); a changed Profile queues, in the same transaction, a re-analysis job for every Case whose latest input used it, and each job produces a new revision.
- Imported legacy decisions are kept as `ImportedDecision` and never used as evidence.
- **Pending:** retention period for revisions and job payloads; who may read the audit view once roles exist.

## Profile imports and re-analysis

- A re-analysis runs on the stored decoded text, so an `ENCODING_FALLBACK` finding of the original upload is not repeated in the new revision.
- A Case created while an import is running is not queued by that import; it carries its own Profile files.

## Rebuilding the PoC database

The PoC database is disposable: a release that changes the schema or the stored JSON format (`CaseJson.SchemaVersion`) does not read an older database: the schema script records the format (`PRAGMA user_version`, equal to `CaseJson.SchemaVersion`; a schema change raises both), and startup refuses a database of another format with a message to delete it. Stop the service, delete the database file with its `-wal` and `-shm` files, and start; the schema is created from `0001_schema.sql`. Re-import the Profiles and re-submit the Cases that are still needed.

## Backup and disaster recovery

- SQLite: back up online with `sqlite3 aml.db ".backup aml-YYYYMMDD.db"`, or copy the file together with its `-wal` and `-shm` files while the service is stopped.
- Restore: stop the service, replace the file and remove any `-wal` and `-shm` files left from the old one, start. A backup taken under an earlier schema version cannot be restored into this one ([rebuilding](#rebuilding-the-poc-database)).
- After a restore, RUNNING jobs return to the queue when their lease expires; SUCCEEDED jobs are not repeated, and a repeated item returns the existing Case.
- **Pending:** RPO and RTO; backup tooling and replication for the target database.

## Release

- CI (`.github/workflows/ci.yml`) runs a locked restore, a Release build with warnings as errors, and all tests including the Golden Cases.
- A Golden Case failure blocks the release: the policy changes, not the expectation.
- **Pending:** security, privacy, AML, legal/compliance and model-risk approvals.
