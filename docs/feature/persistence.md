# Persistence

> Cross-cutting, lives in Aml.Infra.

SQLite for the PoC, replaced later by Microsoft SQL Server or Oracle; Dapper + DbUp, no EF Core ([ADR-0010](../adr/0010-persistence-and-batch.md)).

## Structure

```text
Aml.Infra/Persistence/
├── SqliteDatabase.cs         # connections, DbUp upgrade, the only place aware of the provider
├── SqlCatalog.cs             # loads Sql.<provider>.xml
├── Repositories/             # CaseStore, ListStore, PageStore (JobStore lives in Aml.Infra/Jobs)
├── Sql/                      # Sql.xsd, Sql.sqlite.xml
└── Scripts/Sqlite/           # DbUp DDL: 0001_schema.sql, 0002_web_page.sql, 0003_web_page_published.sql
```

When a second provider is chosen, the provider-specific parts of `SqliteDatabase` move behind `IDbConnectionFactory` and an `ISqlDialect` (parameter prefix rewrite, unique-violation detection).

| Table | Holds |
|---|---|
| `aml_case` | one row per Case; unique on `(trigger_event_id, subject_ref)` (`uq_case_trigger`), indexed by `subject_ref` |
| `case_analysis` | every analysis revision with its result and explanation as JSON, read back into their Core types |
| `case_input` | the input files (decoded text) of each revision, for re-analysis after a source update; a received page the analysis named is one of them, `網頁_<pageId>.json`, the page record as read, so the Case's link to a page lives here, never on `web_page` ([external sources](external-sources.md#matched-against-the-customer)) |
| `analysis_lineage` | what a revision was computed from: input file names and SHA-256, each Profile with its list version or `CASE_FILES`, the source-policy, code-catalog, schema and application versions, the analysis date (`as_of`, UTC) the time-dependent rules ran on, the news model version when the [news models](watchlist-and-adverse-information.md#news-models) read an article (`news_model_version`, else null), and each received page used with its URL, fetch time, body hash and text hash (`pages_json`, else null) |
| `analysis_quality` | data quality of a revision: comparison cells by status, Profiles not evaluated, findings counted by code |
| `case_hit`, `hit_evidence`, `hit_reason` | queryable facts of each hit (identity decision, stage 3 row, hard conflict, pair result, priority; each evidence group's direction, status, strength and detail code; identity, pair and disposition reason codes in order); `hit_reason` indexed by `reason_code` |
| `job` | batch jobs (`ANALYZE_CASE`, `REANALYZE_CASE`, `FETCH_PAGE`) and their payloads; unique on `idempotency_key` (`uq_job_idempotency`), indexed by `(batch_id, status)` and, for claiming, `(status, priority DESC, created_at, next_attempt_at)`; `payload` is the last column |
| `list_profile` | every captured Profile version with the SHA-256 of its normalized export text ([ADR-0008](../adr/0008-validation-and-deferred-capabilities.md)); rows are never updated or deleted |
| `web_page` | one row per page received by URL, keyed by its page id: canonical URL, status (`PENDING`, `READ`, `REFUSED`, `FAILED`) and problem, the `FETCH_PAGE` job and batch it is queued under, and its latest fetch (final URL, media type, title, time, body SHA-256, main text, the publication date at its precision and its source `METADATA`, `TEXT`, `UPDATE_DATE` or `MISSING` (both NULL until read), the reading as JSON, [external sources](external-sources.md#received-url-source-2)); a fetch overwrites the previous one; the long columns are last, as in `job` |

The schema is `0001_schema.sql` plus add-only scripts, each a new table or a new nullable column (`0002_web_page.sql`, `0003_web_page_published.sql`), run in name order. The PoC database is disposable ([ADR-0023](../adr/0023-business-api-data-and-workbook.md)): when an existing column or the stored JSON format changes, `CaseJson.SchemaVersion` is raised and `0001` with it. `0001` records the format as `PRAGMA user_version`, and startup refuses an existing database whose version is not `CaseJson.SchemaVersion`. An older development database is not migrated: stop the service, delete the database file with its `-wal` and `-shm` files, and start; the schema is created afresh. A script that only adds a table or a nullable column (read as null on rows stored before it) leaves the format as it is, and DbUp applies it to an existing database at startup. Old formats are never read; a stored record that does not read as its type fails. Before a production database exists, an ADR switches every change to add-only migrations starting from this schema.

The JSON in `case_analysis` is the full record; the fact tables are an index of it for calibration and monitoring queries, written in the same transaction, so they never disagree with it.

Configuration: `"Database": { "ConnectionString": "..." }`. SQLite-specific settings stay in `SqliteDatabase`: foreign keys, busy timeout (5 s) and in-memory temporary storage (`temp_store = MEMORY`) on every connection, and write-ahead logging (`journal_mode = WAL`), set once after the upgrade and kept in the database file, so readers do not wait for the writer.

## Writing rules

| Rule | Reason |
|---|---|
| ids generated by the application with `Guid.CreateVersion7()` | no identity/sequence differences; time-ordered |
| timestamps supplied by `TimeProvider`, always UTC | no database clock functions; testable |
| enums stored as strings | portable and readable |
| JSON columns are never filtered, joined or sorted on in SQL | queried values get their own columns; the one JSON function, `json_array_length` in `Case.Revisions` (the change count of a revision listing), has an equivalent in each target dialect (`OPENJSON` with `COUNT` in SQL Server, `JSON_VALUE` with the `size()` item method in Oracle) |
| parameters written as `@name` | SQLite and SQL Server accept it; the Oracle dialect rewrites to `:name` |
| no `AS` on table aliases | Oracle rejects it |
| unquoted snake_case identifiers, max 30 characters | Oracle upper-cases and limits length; Dapper `MatchNamesWithUnderscores` maps to PascalCase |
| avoid reserved words (`CASE`, `GROUP`, `LEVEL`, `SIZE`, `USER`, `COMMENT`, `DATE`, `NUMBER`) | e.g. table `aml_case`, column `evidence_group` |
| never use `''` to mean missing | Oracle stores `''` as NULL; use `valueState` |
| idempotent writes: plain `INSERT`, treat unique violation as "already exists" | avoids `MERGE` / `ON CONFLICT` differences |
| one repository write call is one atomic unit; repositories run it through `SqliteDatabase.WriteAsync` ([group commit](#group-commit)) | the units below commit whole or not at all on every provider |

## Type mapping

| C# | SQLite | SQL Server | Oracle |
|---|---|---|---|
| `Guid` (v7) | `TEXT` | `char(36)` | `CHAR(36)` |
| UTC time | `TEXT` (ISO-8601) | `datetime2(3)` | `TIMESTAMP(3)` |
| multilingual string | `TEXT` | `nvarchar(n)` | `NVARCHAR2(n)` or `VARCHAR2(n CHAR)` on AL32UTF8 |
| enum | `TEXT` | `varchar(40)` | `VARCHAR2(40)` |
| bool | `INTEGER` | `bit` | `NUMBER(1)` |
| JSON / long text | `TEXT` | `nvarchar(max)` | `CLOB` |

Multilingual names on SQL Server require `nvarchar`; `varchar` without a UTF-8 collation corrupts Chinese and Vietnamese text. Guids are stored as text because `uniqueidentifier` sorts unusually and `RAW(16)` has byte-order issues; v7 text sorts by time.

## Atomic writes

| Repository call | One transaction | Statements |
|---|---|---|
| `CaseStore.AcceptAnalysisAsync` | the analysis revision with its lineage, quality and hit facts, its input files and the move of the Case to it; a revision is never stored without the input it was run on | `Case.InsertAnalysis`, `Case.InsertLineage`, `Case.InsertQuality`, `Case.InsertHit`, `Case.InsertEvidence`, `Case.InsertReason`, `Case.InsertInput`, `Case.AcceptAnalysis` |
| `ListStore.CaptureAsync` | a new Profile version and the `REANALYZE_CASE` jobs it causes (transactional outbox) | `Extraction.GetLatestProfile`, `Extraction.InsertProfile`, `Batch.Enqueue` |
| `JobStore.EnqueueAsync` (list) | every item of one `POST /api/batches`; an item whose idempotency key is already queued is skipped | `Batch.Enqueue` |
| `PageStore.QueueAsync` | the `FETCH_PAGE` job and the page marked `PENDING` under it (transactional outbox); when the job's key is already queued nothing changes and the stored page is returned | `Batch.Enqueue`, `Extraction.QueuePage` or `Extraction.InsertPage`, `Extraction.GetPage` |
| `PageStore.SaveAsync` | a fetch outcome, only while the page is queued under that job (`job_id`), so a late earlier job never overwrites a newer fetch | `Extraction.SavePage` |
| `JobStore.ClaimAsync` | the claim of every job taken in one round | `Batch.ListClaimable`, `Batch.Claim`, `Batch.GetClaimed` |

## Group commit

Every write goes through `SqliteDatabase.WriteAsync`. Each write runs as its own savepoint inside a shared transaction, and its caller resumes only after that transaction commits. Writes that arrive while a batch commits wait, then commit together. Concurrent jobs therefore share one sync of the write-ahead log instead of paying one each. The batching follows the LMAX Disruptor's smart batching under a single writer: whoever holds the writer slot drains every pending write, so batches grow with load and add no delay when idle.

- Writes run in the order they were queued, and each sees the writes before it, as if run one by one.
- A write that throws is rolled back to its savepoint alone, and only its caller gets the exception.
- An error after which SQLite rolls back the whole transaction (a full disk, an I/O error), or a failed commit, fails every write of its batch with that error; nothing of the batch is stored.
- A batch that holds a durable write commits with `synchronous = FULL`, so that write survives a power loss once its call returns. A batch of replayable writes only commits with `NORMAL` and may lose its newest writes on power loss. Replayable writes are the writes a batch job makes, which the job table replays ([ADR-0010](../adr/0010-persistence-and-batch.md)). Every other write is durable.
- A write must not call `WriteAsync` again; reads use their own connections.

Measured on golden cases with a scratch database: submitting 500 jobs takes about 0.15 s. The analysis itself is about 0.6 ms of CPU per case, so throughput is set by the single writer: its statements plus the batch commits. More workers make larger batches, so each job pays less of a commit: with every commit synced, about 90 jobs/s with 4 workers, 230 with 16, 390 with 32 (the default) and 470 with 64. Batch jobs commit without a sync ([ADR-0010](../adr/0010-persistence-and-batch.md)), which gives about 230 jobs/s with one worker and about 480 with 32 or more; the writer's statements are then the limit. This document holds the throughput figures; others link here.

Every write runs inside a savepoint, which keeps a sub-journal of the pages it changes. By default SQLite holds that sub-journal in memory up to 64 KiB, then spills it to a temporary file. `temp_store = MEMORY` keeps it in memory for the whole write; storing one analysis (`AcceptAnalysisAsync`) takes about 1.3 ms without the commit. The sub-journal only undoes a statement or a savepoint and holds nothing durable. It is freed when the write's savepoint is released, so memory peaks at the largest single write. A re-run or lease reclaim of very many jobs is the largest, since every row it updates carries its payload.

In the job table the payload, which holds the case files, is the last column. SQLite reads a row's columns in order, so the claim and status queries never read the payload; updates of a job still rewrite the whole row. The claim index follows the claim order and holds `next_attempt_at`, so a claim neither sorts the queue nor reads the rows of jobs still waiting out a retry delay. A claim takes about 2.7 ms with one worker, which is one commit.

Revision numbers are allocated optimistically: `CaseService` takes the Case's current revision + 1, and the primary key of `case_analysis` rejects a number another analysis already took. The repository then stores nothing and returns false; `CaseService` re-reads the Case, recomputes the number, status and changes, and tries again, at most `CaseService.MaxRevisionAttempts` (3) times before failing. `Case.AcceptAnalysis` moves the Case only while `current_revision < @revision`, so a late revision is kept as history and never overwrites a newer one.

In the outbox, a job whose idempotency key is already in the job table is skipped (unique violation on that statement only); the version and the other jobs still commit.

Other statements used by these flows: `Case.GetAnalysis`, `Case.GetLineage` and `Case.GetQuality` read one revision back; `Case.LatestInput` and `Case.LatestInputs` read the latest stored input of one Case or of every Case; `Extraction.ListVersion` counts `list_profile` rows, which, because the table is append-only, changes with every capture and keys the cached retrieval index; `Extraction.GetPage` reads one page. A job that writes no Case (`FETCH_PAGE`) completes with null `case_id` and `analysis_revision`.

## Job claim

Jobs are claimed optimistically with SQL identical on all providers:

```sql
UPDATE job SET status = 'RUNNING', lease_owner = @leaseOwner, lease_expiry = @leaseExpiry,
    attempt_count = attempt_count + 1
WHERE job_id = @jobId AND status = 'PENDING'
```

One affected row means the worker owns the job. The worker gives a new `lease_owner` to every claim round (machine name and a GUID), so an attempt whose lease expired and that the same process claims again is shut out like any other worker. Every later write requires `status = 'RUNNING'` and that lease owner:

- `Batch.RenewLease` extends `lease_expiry` just before the job writes its analysis revision; zero rows (the lease expired and was reclaimed) stops the job before it writes;
- `Batch.Complete` and `Batch.Fail` change only that row; zero rows means the lease was reclaimed or the job already finished, and the worker logs a warning and leaves the job's state alone;
- `Batch.Release`, when the host stops during a job, puts it back to `PENDING`, due at once, and takes back the interrupted attempt (`attempt_count - 1`) while `release_count` is below `Batch:MaxReleases`; past that the attempt counts, and a job whose attempts are spent is dead-lettered (`RELEASE_LIMIT`). `release_count` rises on every release, and a re-run resets it with the attempts.

`Batch.ReclaimExpired` returns a RUNNING job past its lease (its worker died) to `PENDING`, due after `Batch:RetryDelay`, or dead-letters it when its attempts are spent.

Ceiling: many concurrent workers cause wasted attempts; upgrade to `UPDLOCK, READPAST` (SQL Server) or `FOR UPDATE SKIP LOCKED` (Oracle) when measured.

## SQL catalog

All DML lives in XML catalogs, one complete file per provider; the file name selects the provider ([ADR-0010](../adr/0010-persistence-and-batch.md)):

```text
Aml.Infra/Persistence/Sql/
├── Sql.xsd
├── Sql.sqlite.xml
├── Sql.sqlserver.xml   # when SQL Server is adopted
└── Sql.oracle.xml      # when Oracle is adopted
```

```xml
<sqlCatalog xmlns="urn:aml:sql:v1" version="1">
  <module name="Batch">
    <statement id="Batch.Claim" kind="Update">
      <description>Optimistic claim.</description>
      <sql><![CDATA[ UPDATE job ... ]]></sql>
    </statement>
  </module>
</sqlCatalog>
```

Schema rules (`Sql.xsd`):

- modules: `System`, `Case`, `Batch`, `Extraction`, `Identity`, `Screening`, `Explanation`; each at most once;
- statement id format `Module.PascalName`, unique within the file;
- `kind`: `Select | Insert | Update | Delete`;
- every statement has `description` and `sql`; SQL is wrapped in CDATA.

Startup checks in `SqlCatalog` (beyond XSD 1.0):

- the id prefix equals its module name;
- every `Sql.*.xml` defines the same set of statement ids.

Any failure stops the application at startup. The catalog is an embedded resource, validated with the built-in `XmlReaderSettings` schema validation, and cached per id. The database health check executes `System.Ping`.

DDL is not in the catalog; it stays in the DbUp script under `Scripts/<Provider>/`.

## Repository contract tests

`tests/Aml.Tests/Integration/` verifies repository behaviour: round-trip, idempotency keys, exclusive job claim, late revisions not overwriting a newer effective result, fact tables that rebuild every decision in the stored JSON of each Golden Case, and lineage that follows a Profile into a new list version. It runs on SQLite. Replacing the database means running the same suite against SQL Server or Oracle (Testcontainers); passing it is the acceptance criterion.
