# Implementation Baseline

> Agreed for PoC Phase 1. Subject to bank technology governance.

## PoC implementation decisions

| Topic | Decision | ADR |
|---|---|---|
| Runtime | C#, .NET 10 LTS, ASP.NET Core | [0007](../adr/0007-dotnet-technology-stack.md) |
| Delivery | API + OpenAPI / Swagger UI, no UI; a case workbook (.xlsx) renders the API | [0009](../adr/0009-api-only.md), [0027](../adr/0027-case-workbook-renders-the-api.md) |
| Input | supplied `.txt` exports and HTML pages; API or crawler later behind ports | [0012](../adr/0012-source-acquisition-behind-ports.md) |
| Database | SQLite in the PoC; SQL Server or Oracle for production | [0010](../adr/0010-replaceable-persistence.md) |
| Data access | Dapper + DbUp; SQL in `Sql.<provider>.xml` | [0010](../adr/0010-replaceable-persistence.md), [0014](../adr/0014-per-provider-xml-sql-catalog.md) |
| Retrieval | case-table candidates (CONTROLLED_SET) feed Cases; the list database and index serve the other modes on request | [0021](../adr/0021-deferred-capabilities-built-behind-controls.md) |
| Batch | DB job table + `BackgroundService` | [0011](../adr/0011-database-job-table-for-batch.md) |
| Structure | Core / Services / Infra / Api | [0013](../adr/0013-four-project-solution.md) |
| Libraries | no commercially licensed packages | [0015](../adr/0015-no-commercially-licensed-libraries.md) |

## Solution structure

```text
Api ──► Services ──► Core
 │                    ▲
 └────► Infra ────────┘
```

```text
AML/
├── docs/
│   ├── adr/
│   ├── feature/
│   └── plan/
├── licenses/                     # license texts for the embedded third-party data
├── samples/                      # supplied samples, read-only
│   └── golden/GC-01..03, 11, 12/ # anonymized inputs + expected.yaml per Golden Case
├── THIRD-PARTY-NOTICES.md        # attribution for the embedded third-party data
├── src/
│   ├── Aml.Core/                 # zero dependencies
│   │   ├── Shared/               # Evidence, reason codes, evidence details, findings, sensitivity marks, enums
│   │   ├── Codes/                # CodeCatalog: every emitted code with its meaning
│   │   ├── Subjects/             # screened subject, source Profile, documents, roles
│   │   ├── Normalization/        # names, romanization, similarity, dates, Taiwan addresses
│   │   │   ├── Countries.cs      # ISO 3166-1 codes and common English / Chinese names, Taiwan aliases
│   │   │   └── Data/*.txt        # embedded reference tables (see Third-party data)
│   │   ├── Identity/             # evidence groups, HardConflictGate, IdentityDecisionPolicy (with Contrast), Fellegi-Sunter calibration
│   │   ├── Screening/            # source-condition policies, combination matrix, DispositionPolicy, risk scoring
│   │   ├── Retrieval/            # ProfileIndex (alias, BM25, n-gram, reading-key routes), ProfileMerge
│   │   ├── Cases/                # CaseAnalyzer, Case records, revision changes, lineage, quality, CaseViews and page tokens, CaseJson (JSON form and schemas)
│   │   ├── Explanation/          # ExplanationBuilder
│   │   └── Ports/                # ICaseStore, IJobStore, IListStore
│   ├── Aml.Services/             # orchestration
│   │   ├── CaseService.cs        # sole writer of Case state; builds each revision's lineage and quality
│   │   ├── ProfileFiles.cs       # Profile file names and the list content hash
│   │   ├── BatchService.cs       # job-table batches
│   │   ├── AmlTelemetry.cs       # traces and metrics: analyses, durations, job attempts, workbooks
│   │   └── ListService.cs        # list database, retrieval, merges, REANALYZE_CASE jobs on Profile change
│   ├── Aml.Infra/                # implements Core/Ports
│   │   ├── Persistence/          # see feature/persistence.md
│   │   ├── Txt/                  # .txt export reader (UTF-8, CP950 fallback), CaseInputException
│   │   ├── Html/                 # HTML page to export layout (AngleSharp)
│   │   ├── Excel/                # Xlsx (SpreadsheetML with the standard library), CaseWorkbook (seven sheets)
│   │   └── Jobs/                 # JobStore
│   └── Aml.Api/                  # composition root
│       ├── Program.cs            # DI, OpenAPI, OpenTelemetry, health check, endpoint registration
│       ├── Endpoints/            # minimal API endpoints per resource (cases, batches, profiles, codes), upload reading, problems
│       └── Workers/JobWorker.cs  # BackgroundService
└── tests/
    └── Aml.Tests/
        ├── Unit/                 # in process, no database: Cases, Extraction, Identity, Names, Retrieval, Screening
        ├── GoldenCases/          # Golden Cases through the analyzer, augmented cases, explanation, views, metrics (in process)
        ├── Integration/          # services, repositories and the job table on SQLite
        ├── E2E/                  # critical API flows through WebApplicationFactory
        ├── TestSupport.cs        # TestDatabase, ApiHost, ManualTimeProvider, Wait
        └── TestPyramidTests.cs   # keeps each layer well below the one under it
```

Rules:

- All decision logic lives in Core as pure code, so Golden Case tests need no database or HTML.
- Ports live in Core; Infra depends on Core only, never on Services.
- Case state has no mutators: `CaseRecord` and `AnalysisRecord` are immutable records, and Case state is written only through the public `ICaseStore` writes (`CreateAsync`, `AcceptAnalysisAsync`). `CaseService` is the only caller of those writes (`BatchService` and `ListService` go through it); review keeps it so, the compiler does not.
- Api holds no business logic.

Implementation notes:

- Api uses minimal APIs, one module per resource in `Endpoints/` (`MapCases`, `MapBatches`, `MapProfiles`, `MapCodes`) with the shared upload reading in `Uploads` and the RFC 9457 responses in `Problems`, registered from `Program.cs`; errors are problems ([errors](../feature/pipeline-api-and-batch.md#errors)); the endpoints are listed in [REST resources](../feature/pipeline-api-and-batch.md#rest-resources), plus `GET /health`. Uploads are `.txt`, `.html` or `.htm` files, as multipart (read as raw bytes) or, for Cases and batches, as JSON text. Multipart files: an export is strict UTF-8, else CP950 (Big5) with an `ENCODING_FALLBACK` finding; an HTML page's encoding is decided by AngleSharp (BOM, then meta charset). A Case keeps the SHA-256 of each uploaded file's bytes, or of the UTF-8 encoding of JSON text, as its source hash.
- The explanation is built in Core (`Explanation/ExplanationBuilder`) with each revision and stored beside the result; document and reference numbers are masked (first 3 and last 2 characters). The five-part summary is in Traditional Chinese for reviewers. Each hit lists its sources: Profile citations (`PROFILE_RECORD`, with publication and retrieval dates and URL) and attached news (`LEGAL_STAGE` when it set the legal stage of an ADVERSE_MEDIA condition).
- No human workflow in the PoC ([ADR-0020](../adr/0020-identification-only.md)).
- `POST /api/cases` analyzes synchronously; `POST /api/batches` queues items in the job table and `Workers/JobWorker` runs them through `BatchService` ([durable batch execution](../feature/pipeline-api-and-batch.md#durable-batch-execution)). The job store is `Infra/Jobs/JobStore` behind `Core/Ports/IJobStore` (named "Store" because CA1711 rejects a "Queue" suffix).
- Each revision records source condition status and basis, pair result, review priority, pair reason codes and the changes from the previous revision; a missing Profile makes the revision PARTIAL and keeps the previous effective result.
- Persistence is SQLite-only (`SqliteDatabase`, `SqlCatalog`, `CaseStore`) with one schema script, `0001_schema.sql`; the PoC database is rebuilt when the schema or stored format changes ([ADR-0023](../adr/0023-ai-ready-data.md)). `IDbConnectionFactory` and `ISqlDialect` are extracted when a second provider is chosen.
- Stored results are Core records, serialized with `CaseJson.Options` and read back into their types; `CaseJson.Schema` generates their JSON Schemas (`GET /api/schemas/{name}`) with field descriptions and `x-sensitivity`. Each revision also stores its lineage, data quality and hit facts ([lineage and quality](../feature/pipeline-api-and-batch.md#lineage-and-quality)).
- `GET /health` runs `System.Ping`. OpenTelemetry traces, metrics and logs are on (ASP.NET Core plus the `Aml` source and meter: `aml.analyses`, `aml.analysis.duration`, `aml.job.attempts`, `aml.workbooks`, `aml.workbook.duration`, `aml.workbook.size`); OTLP export starts only when `OTEL_EXPORTER_OTLP_ENDPOINT` is set.
- The list database (`list_profile`) and each revision's input files (`case_input`, decoded text) are stored for retrieval and re-analysis ([ADR-0021](../adr/0021-deferred-capabilities-built-behind-controls.md)). A Profile version is hashed on its export text, so an HTML page and its `.txt` export are one version. `POST /api/profiles/import` stores a changed Profile as a new version and, in the same transaction, queues a `REANALYZE_CASE` job for every Case whose latest input holds a different version of it; the jobs run through `BatchService` and are tracked as one batch (`GET /api/batches/{batchId}`).
- Authentication is not wired (identity provider pending).
- Golden Case metrics are computed by `tests/Aml.Tests/GoldenCases/GoldenMetricsTests` ([results](poc-delivery-plan.md#golden-case-results)); its comparison-cell rates come from `AnalysisQuality.Of`, the function each revision's quality is stored with.

## Testing

The purpose of the tests is to find defects. A test that only replays a known example confirms that example; it says little about the cases next to it.

### Layers

Tests follow the test pyramid: many in-process tests of Core rules and the readers, fewer service and repository tests on SQLite, and API tests only for the critical flows (upload and analysis, upload validation, refused requests and unknown ids, batch, health, Profile import with queued re-analysis, retrieval modes and the export-text wiring, the code and schema endpoints) and the contract: `ContractTests` compares the OpenAPI document and every published schema with the snapshots in `tests/Aml.Tests/E2E/Contracts` and checks that each operation has an `operationId`, a tag and a description. After an intended contract change, run the tests with `AML_UPDATE_SNAPSHOTS=1` and review the snapshot diff. A behaviour is tested at the lowest layer that can show it; an API test covers only what needs the HTTP pipeline or the DI wiring. `TestPyramidTests` counts test methods by namespace (`Aml.Tests.E2E`, `Aml.Tests.Integration`, everything else in process) and fails when integration tests fall below twice the E2E tests or in-process tests below three times the integration tests. A `[Theory]` counts as one method however many rows it has.

### Test design

Test cases are derived with the ISTQB techniques that fit each rule, and the expected values come from the feature documents, not from running the code:

| Technique | Where it applies |
|---|---|
| Decision table | stage-3 identity rows and the hard-conflict gate with its source-quality downgrade; source conditions (list flags × category × official list); the Article 22 disposition rules; the multi-row flag merge |
| Boundary values | thresholds tested on, just below and just above: minimum role age at a birthday, scattered DOBs, stated-age tolerance, risk levels, n-gram floors and Top N, the analysis date that ends a PEP role |
| Equivalence partitions | encodings (UTF-8, BOM, CP950 fallback), placeholders and dash variants, full-width digits, party types, flag spellings |
| State transitions | Case revisions (create, concurrent create, accepted, late, failed, re-analysis) and jobs (pending, running, succeeded, retry with backoff, dead letter, lease lost or expired, re-run) |
| Error guessing | duplicate files in one upload, a corrupt byte, two workers claiming one job, a duplicated Profile key |

A table is one `[Theory]` with a row per cell. Assertions name exact values: decision and row, reason codes, ranks, statuses and stored rows, never only `NotNull`, `Contains` or a status code when the exact value is known. A test that exposes behaviour contrary to the feature documents is not written to match the code; the behaviour is fixed or the document is changed first.

### Measuring the tests

- **Mutation testing** measures whether the tests find defects. Stryker.NET is a local tool (`dotnet-tools.json`, configuration in `stryker-config.json`, Microsoft.Testing.Platform runner); a surviving mutant is a fault no test detects. Run it on the files a change touches:

  ```bash
  dotnet tool restore
  dotnet stryker -f stryker-new-logic.json -m "**/Identity/IdentityDecisionPolicy.cs"
  dotnet stryker -f stryker-screening.json
  ```

  `stryker-screening.json` mutates `SourceConditions.cs`; `dotnet stryker -f stryker-screening.json -m "**/Screening/Disposition.cs"` (likewise `Assessment.cs`, `RiskScoring.cs`) runs another Screening file with the same settings. `stryker-new-logic.json` mutates `EvidenceBuilder.cs`, `StatedAgeCheck.cs`, `HardConflictGate.cs`, `IdentityDecisionPolicy.cs`, `CaseViews.cs` and `AnalysisQuality.cs`; pass `-m` with one of them per run. Use `coverage-analysis: perTest` and one file per run. A static field initializer (a vocabulary array, map or set) is built once per test run, so its mutants can be reported as survivors although a test kills them, and the count varies between runs; `off` and several files in a run make this more frequent. Such a survivor is often false: a word mutated to `""` in a `Contains` vocabulary matches every text, so any negative test catches it. Check it by hand before adding a test. In the Identity files the survivors are equivalent mutants (`at <= 0` against `at < 0` in `StatedAgeCheck.AgeFromRespectivelyList`; `<` against `<=` in the year and month comparisons of `HardConflictGate.Before`, which run only when the two differ) and static-initializer reports (`HardConflictGate.ValidatedReasonCodes`, `AdultOnlyTitles`). Its compile errors are mutants the compiler rejects: `Count()` to `Sum()` on a list's `Count` property, `or` to `and` between two constants (a pattern that can never match), and mutants that leave a pattern variable unassigned. The vstest runner does not run these tests (every mutant survives); keep `mtp`.

- **Coverage** (`Microsoft.Testing.Extensions.CodeCoverage`) shows which branches never run. It does not show that a wrong result would be noticed, so high coverage with surviving mutants still means missing assertions.

  ```bash
  dotnet test --coverage --coverage-output-format cobertura
  ```

### Speed and determinism

The whole suite runs in a few seconds; the unit tests alone (`Aml.Tests.exe -namespace "Aml.Tests.Unit.*"`, about two seconds) are the inner loop while editing Core.

- `TestDatabase` migrates the schema once into a template file and gives every test its own copy, so a test pays for a file copy rather than the migrations.
- `ApiHost` is one API host per E2E class (`IClassFixture`) on its own database; its job worker polls every 20 ms and only warnings are logged. Each test uses its own trigger and subject ids, so the tests of a class do not depend on each other.
- Time-dependent behaviour (leases, retry backoff, deadlines) is driven by `ManualTimeProvider`, never by waiting on the clock. Where an API test must wait for the job worker, `Wait.UntilAsync` polls and fails with what it was waiting for.
- Test classes are the unit of parallel execution, so a large class is split by topic (revisions, jobs, list import) rather than left to run serially.

## Third-party data

Core embeds three public reference tables (`Normalization/Data/*.txt`, embedded resources). Each file's header lines carry the required attribution, the source, the license and how the data was modified. [THIRD-PARTY-NOTICES.md](../../THIRD-PARTY-NOTICES.md) and the license texts in `licenses/` are copied into every build output that contains Core and must ship with it.

| File | Content | Source | License |
|---|---|---|---|
| `romanization.txt` | Hanyu Pinyin, Wade-Giles (passport form) and Tongyong spellings per syllable | 外交部領事事務局 護照外文姓名拼音對照表 (2026-05-28) | Open Government Data License v1.0 (OGDL-Taiwan-1.0), per the bureau's open data announcement; attribution required, no application needed |
| `hans-hant.txt` | one-to-one simplified → traditional characters (one-to-many entries left out) | OpenCC `STCharacters.txt` | Apache-2.0 |
| `hanzi-pinyin.txt` | toneless Hanyu Pinyin readings per character (CJK URO, Extensions A and B) | Unicode Unihan 18.0.0 (`kMandarin`, `kTGHZ2013`, `kXHC1983`) | Unicode License v3 |

## Build settings

| File | Purpose |
|---|---|
| `Directory.Packages.props` | Central Package Management; csproj files carry no versions |
| `Directory.Build.props` | `net10.0`, Nullable, TreatWarningsAsErrors, `AnalysisLevel=latest-recommended`, `RestorePackagesWithLockFile` |
| `nuget.config` | `packageSourceMapping` to approved feeds only |
| `global.json` | pin SDK 10.0.x; `test.runner: Microsoft.Testing.Platform` (required by xunit.v3 on .NET 10) |

NuGet Audit and the built-in .NET analyzers are enabled without extra packages.

## Packages

Versions are pinned in `Directory.Packages.props`; this table lists them for review.

| Project | Package | Version | Purpose |
|---|---|---|---|
| Core | none | | |
| Services | none | | |
| Infra | Dapper | 2.1.89 | data access |
| Infra | Microsoft.Data.Sqlite | 10.0.14 | SQLite driver |
| Infra | dbup-sqlite | 6.0.4 | schema scripts |
| Infra | AngleSharp | 1.9.0 | HTML parsing (`Infra/Html/HtmlExport`) |
| Api | Microsoft.AspNetCore.OpenApi | 10.0.14 | OpenAPI document |
| Api | Swashbuckle.AspNetCore.SwaggerUI | 10.3.1 | Swagger UI only |
| Api | OpenTelemetry.Extensions.Hosting | 1.20.1 | telemetry host |
| Api | OpenTelemetry.Instrumentation.AspNetCore | 1.20.1 | HTTP traces/metrics |
| Api | OpenTelemetry.Exporter.OpenTelemetryProtocol | 1.20.1 | OTLP, enabled when an endpoint is configured |
| Tests | xunit.v3 | 4.0.2 | test framework |
| Tests | xunit.runner.visualstudio | 4.0.1 | runner |
| Tests | Microsoft.NET.Test.Sdk | 18.11.0 | test host |
| Tests | Microsoft.AspNetCore.Mvc.Testing | 10.0.14 | `WebApplicationFactory` |
| Tests | Microsoft.Testing.Extensions.CodeCoverage | 18.12.0 | code coverage under Microsoft.Testing.Platform |
| Local tool | dotnet-stryker | 5.0.0 | mutation testing (`dotnet-tools.json`) |

Deferred:

- `Microsoft.AspNetCore.Authentication.JwtBearer` once the identity provider is known;
- SBOM tooling (CycloneDX or Microsoft.Sbom.Targets) once CI/CD is known;
- `Microsoft.Data.SqlClient` + `dbup-sqlserver`, or `Oracle.ManagedDataAccess.Core` + `dbup-oracle`, when the target database is chosen;
- Quartz.NET only if timed triggers are required.

Not used: MediatR, AutoMapper, FluentAssertions ([ADR-0015](../adr/0015-no-commercially-licensed-libraries.md)); Serilog (built-in JSON logging plus OpenTelemetry suffices); FluentValidation (explicit validation); EF Core ([ADR-0010](../adr/0010-replaceable-persistence.md)).

## Technology summary

| Area | Choice |
|---|---|
| Language | C# |
| Runtime | Bank-approved .NET LTS; .NET 10 for the PoC |
| API | ASP.NET Core Web API, OpenAPI + Swagger UI |
| Architecture | Modular monolith in four projects |
| Database | SQLite in the PoC; target Microsoft SQL Server or Oracle |
| Data access | Dapper; per-provider XML SQL catalogs; DbUp |
| HTML parser | AngleSharp |
| Validation | Explicit application validation |
| Batch | DB job table + `BackgroundService` |
| Identity | OpenID Connect / OAuth 2.0 once the identity provider is known; not built in the PoC |
| Authorization | Policy-based authorization; not built in the PoC |
| Observability | OpenTelemetry |
| Deployment | Linux container on approved Kubernetes/OpenShift/server platform |
| Testing | xUnit; test pyramid (in process, then SQLite integration, then critical API flows); ISTQB test design; mutation testing (Stryker.NET) and coverage; Golden Case regression; repository contract tests |
| Supply chain | Central Package Management, lock files, package source mapping, NuGet Audit, SBOM, SAST/SCA, container scanning |
