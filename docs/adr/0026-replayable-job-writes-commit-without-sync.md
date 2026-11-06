# ADR-0026: Replayable job writes commit without a log sync

- Status: Accepted
- Date: 2026-11-07

## Context

All SQLite writes go through one group-committing writer ([persistence](../feature/persistence.md#group-commit)). With `synchronous = FULL` every commit syncs the write-ahead log, and that sync dominates batch throughput: a case takes about 0.6 ms of CPU, while one worker ran 65 jobs/s and 32 workers 390 jobs/s. With `synchronous = NORMAL` the log is synced only at checkpoints. SQLite's documentation says WAL with NORMAL stays consistent but may roll back the newest transactions after a power loss or OS crash; transactions survive an application crash in any mode.

Asynchronous commit is established practice. PostgreSQL's `synchronous_commit` can be set per transaction, so durable and non-durable commits run side by side. Recovery replays the log in commit order, so a later transaction never survives without an earlier one. The PostgreSQL manual warns against asynchronous commit when "the client will take external actions relying on the assumption that the transaction will be remembered".

The batch job table already compensates for a lost write. A job is queued once, by a durable submit. Its state after the claim lives under a lease that expires and is reclaimed. Case creation is idempotent on trigger and subject.

## Decision

- A write is durable unless it is made inside `WriteDurability.Replayable()`. The writer commits a batch with `synchronous = FULL` when it holds a durable write, and with `NORMAL` when every write in it is replayable. A FULL commit syncs the whole log, so every earlier commit becomes durable as well.
- Every write a job makes is replayable: claim, lease reclaim and renewal, Case creation and analysis, re-analysis, complete and fail. `BatchService` marks them.
- Every write the API makes is durable: `POST /api/cases`, the re-analysis endpoint, `POST /api/batches`, re-runs and Profile imports.
- A batch item's new Case takes its job's id, so a re-run after a lost write finds or stores the same Case under that id.

## Consequences

- Measured on golden cases: about 230 jobs/s with one worker (was 65), and about 480 jobs/s with 32 workers (was 390).
- After a power loss the newest job writes may be gone, oldest kept first. Such a job reads PENDING, or RUNNING until its lease expires (`Batch:Lease`, 5 min), and then runs again. A job that was on its last attempt is dead-lettered (`LEASE_EXPIRED`) instead, even when its Case survived, and needs a re-run, as after an application crash.
- A client that polled `SUCCEEDED` before the loss finds the same Case id after the replay. One exception: if `POST /api/cases` stores a Case with the same trigger and subject before the job runs again, the job adopts that Case, and the polled id no longer exists.
- The replayed analysis has the same inputs and therefore the same result. Its lineage records the list versions current at the replay, and its Case and revision times are those of the replay.
- A durable write commits after everything it read, and the log is synced in commit order, so it also makes those earlier job writes durable. A reviewer's action recorded through the API therefore never refers to a lost Case.
- A request that only reads job-written data writes nothing, so nothing is synced, and it can return a Case that a power loss then takes away. This includes `POST /api/cases` returning an existing Case. So does an action taken outside the system on a Case read in the seconds before the loss. The replay brings that Case back, normally under the same id.
