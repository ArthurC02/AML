# ADR-0011: Database job table for batch

- Status: Accepted
- Date: 2026-11-02

## Context

Batch needs durability, retry, dead-letter, idempotency, lineage to Case and revision, and progress queries. Schedulers such as Quartz.NET handle *when* to run but not per-item business state; all triggers in scope are events, not times.

## Decision

Use a database job table and a hosted `BackgroundService` with optimistic claiming. Add a scheduler only if timed triggers (for example nightly re-screening) are required, and then only to enqueue into the same table.

## Consequences

- Job fields and controls are in [pipeline, API and batch](../feature/pipeline-api-and-batch.md#durable-batch-execution); the claim SQL is identical on all providers ([persistence](../feature/persistence.md#job-claim)).
- Ceiling: many concurrent workers waste claim attempts; upgrade to `UPDLOCK, READPAST` or `FOR UPDATE SKIP LOCKED` when measured.
- `IJobQueue` lives in Core, so a library-backed queue would only change Infra.
