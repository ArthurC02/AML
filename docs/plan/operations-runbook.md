# Operations Runbook (Gate D draft)

> Draft for [Gate D](poc-delivery-plan.md#gate-d-before-production), 2026-11-03. It describes the PoC as built; items marked **Pending** wait for bank decisions ([open questions](open-questions.md)).

## Service

| Item | Value |
|---|---|
| Process | `Aml.Api` (ASP.NET Core); hosts the REST API and `JobWorker` |
| Database | SQLite file from `Database:ConnectionString`. **Pending:** target database (SQL Server or Oracle) |
| Schema | DbUp scripts run at startup; a failed upgrade or an invalid SQL catalog stops the process |
| Health | `GET /health` returns `Healthy` when the database answers `System.Ping` |
| Telemetry | OpenTelemetry; set `OTEL_EXPORTER_OTLP_ENDPOINT` to export traces, metrics and logs |
| Authentication | **Pending:** OIDC identity provider. Until then, run only on a closed network |

## Routine checks

- `GET /health` every minute; alert after three failures.
- `aml.job.attempts{outcome="DEAD_LETTER"}` above zero: inspect the batch (below).
- `aml.analyses{execution_status="Partial"}`: a Profile file was missing; the Case keeps its previous effective result until a complete re-analysis.
- `aml.analysis.duration` p95 trend. **Pending:** latency target.

## Batch operations

1. `GET /api/batches/{batchId}`: counts per status and each item with attempts and `errorCode`.
2. `INVALID_INPUT`: the files are incomplete or not in the export layout. Fix the files and submit a new batch; re-running the same files fails the same way.
3. `TimeoutException`, `SqliteException` or other transient codes: fix the cause, then `POST /api/batches/{batchId}/rerun`.
4. `LEASE_EXPIRED`: a worker stopped mid-job. The job returns to the queue by itself while budget remains; otherwise re-run.
5. Scale-out: several instances may share one database; the claim is optimistic, so a job runs on one worker at a time.

## Audit

- Every analysis revision is kept (`case_analysis`); nothing is overwritten. A late revision never replaces a newer effective one.
- Each revision stores the full result and the explanation as JSON: evidence, source conditions with basis and policy version, identity decision, reason codes, review priority and the changes from the previous revision.
- Document and reference numbers are masked in the explanation (first 3 and last 2 characters). Raw input files are not stored, except as batch job payloads.
- Imported legacy decisions are kept as `ImportedDecision` and never used as evidence.
- **Pending:** retention period for revisions and job payloads; who may read the audit view once roles exist.

## Backup and disaster recovery

- SQLite: back up online with `sqlite3 aml.db ".backup aml-YYYYMMDD.db"`, or copy the file while the service is stopped.
- Restore: stop the service, replace the file, start; DbUp applies only the scripts the backup lacks.
- After a restore, RUNNING jobs return to the queue when their lease expires; SUCCEEDED jobs are not repeated, and a repeated item returns the existing Case.
- **Pending:** RPO and RTO; backup tooling and replication for the target database.

## Release

- CI (`.github/workflows/ci.yml`) runs a locked restore, a Release build with warnings as errors, and all tests including the Golden Cases.
- A Golden Case failure blocks the release: the policy changes, not the expectation.
- **Pending:** security, privacy, AML, legal/compliance and model-risk approvals.
