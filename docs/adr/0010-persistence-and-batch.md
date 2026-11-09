# ADR-0010: Replaceable persistence and a database job table

- Status: Accepted
- Date: 2026-11-02; updated 2026-11-09 (merged former ADR-0011, 0014, 0026)

## Context

The PoC needs a database that runs anywhere with no setup; production will use SQL Server or Oracle. Most records are append-only, the job queue needs precise atomic SQL, and bank reviewers prefer SQL they can read. Batch needs durability, retry, dead-letter, idempotency, lineage and progress queries; schedulers such as Quartz.NET handle *when*, not per-item business state, and every trigger in scope is an event.

All SQLite writes go through one group-committing writer. With `synchronous = FULL` the log sync dominates batch throughput (65 jobs/s with one worker). With `NORMAL`, WAL stays consistent but may roll back the newest transactions after a power loss; PostgreSQL's asynchronous commit is the same practice, safe when the client takes no external action on the lost write.

## Decision

- **Database.** SQLite for the PoC, designed for SQL Server or Oracle from day one. Dapper for data access, DbUp for schema scripts, no EF Core. Portability rules, type mapping and the dialect seam are in [persistence](../feature/persistence.md).
- **SQL catalog.** All DML is in one XML catalog per database (`Sql.sqlite.xml`, later `Sql.sqlserver.xml`, `Sql.oracle.xml`), validated at startup against a shared `Sql.xsd`; startup fails if a catalog lacks an id another defines. DDL stays in DbUp scripts ([SQL catalog](../feature/persistence.md#sql-catalog)).
- **Job table.** Batch runs from a database job table and a hosted `BackgroundService` with optimistic claiming; the claim SQL is identical on all providers. A scheduler, if timed triggers are ever needed, only enqueues into the same table ([durable batch execution](../feature/pipeline-api-and-batch.md#durable-batch-execution)).
- **Replayable job writes.** A write is durable unless made inside `WriteDurability.Replayable()`. The writer commits a batch with `FULL` when it holds a durable write, else `NORMAL`. Every job write (claim, lease, Case creation, analysis, complete, fail) is replayable; every API write is durable. A batch item's new Case takes its job's id.

## Consequences

- Switching databases means a dialect class, a catalog and DDL scripts; Core and Services do not change. The repository contract tests are the acceptance criterion. Oracle pitfalls (`''` as NULL, `:` parameters, identifier limits) still surface only on Oracle.
- Mapping is hand-written; no automatic migrations.
- Ceiling: many concurrent workers waste claim attempts; move to `UPDLOCK, READPAST` or `FOR UPDATE SKIP LOCKED` when measured.
- Throughput rose to about 230 jobs/s with one worker and 480 with 32. After a power loss the newest job writes may be lost, oldest kept first; the job runs again after its lease (`Batch:Lease`, 5 min), or is dead-lettered (`LEASE_EXPIRED`) on its last attempt. A replay has the same inputs and result under the same Case id; its lineage and times are the replay's. A durable write also makes earlier job writes durable, so an API action never refers to a lost Case.
