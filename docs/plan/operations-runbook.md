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

1. `GET /api/batches/{batchId}`: counts per status and each item with attempts and `errorCode`. Re-analyses queued by a Profile import are batch jobs too (`REANALYZE_CASE`); their `batchId` is in the import response.
2. `INVALID_INPUT`: the files are incomplete or not in the export layout. Fix the files and submit a new batch; re-running the same files fails the same way.
3. `INVALID_PAYLOAD`: the job row has an unknown type, unreadable JSON or a missing field. It is dead-lettered at once; re-running it fails the same way.
4. `TIMEOUT`, `SqliteException` or other transient codes: fix the cause, then `POST /api/batches/{batchId}/rerun`.
5. `LEASE_EXPIRED`: a worker stopped mid-job. The job returns to the queue by itself while budget remains; otherwise re-run.
6. Scale-out: several instances may share one database; the claim is optimistic, so a job runs on one worker at a time.

## Audit

- Every analysis revision is kept (`case_analysis`); nothing is overwritten. A late revision never replaces a newer effective one.
- Each revision stores the full result and the explanation as JSON: evidence, source conditions with basis and policy version, identity decision, reason codes, review priority and the changes from the last effective revision. With it are its lineage (input file hashes, the list version of each Profile, rule-set and application versions) and its data quality counts.
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
