# Implementation Baseline

> Agreed for PoC Phase 1. Subject to bank technology governance.

## PoC implementation decisions

| Topic | Decision | ADR |
|---|---|---|
| Runtime | C#, .NET 10 LTS, ASP.NET Core | [0007](../adr/0007-dotnet-technology-stack.md) |
| Delivery | API + OpenAPI / Swagger UI, no UI | [0009](../adr/0009-api-only.md) |
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
│   │   ├── Shared/               # Evidence, reason codes, findings, enums
│   │   ├── Subjects/             # screened subject, source Profile, documents, roles
│   │   ├── Normalization/        # names, romanization, similarity, dates, Taiwan addresses
│   │   │   ├── Countries.cs      # ISO 3166-1 codes and common English / Chinese names, Taiwan aliases
│   │   │   └── Data/*.txt        # embedded reference tables (see Third-party data)
│   │   ├── Identity/             # evidence groups, HardConflictGate, IdentityDecisionPolicy, Fellegi-Sunter shadow
│   │   ├── Screening/            # source-condition policies, combination matrix, DispositionPolicy, risk scoring
│   │   ├── Retrieval/            # ProfileIndex (alias, BM25, n-gram, reading-key routes), ProfileMerge
│   │   ├── Cases/                # CaseAnalyzer, Case records, revision changes
│   │   ├── Explanation/          # ExplanationBuilder
│   │   └── Ports/                # ICaseStore, IJobStore, IListStore
│   ├── Aml.Services/             # orchestration
│   │   ├── CaseService.cs        # sole writer of Case state
│   │   ├── BatchService.cs       # job-table batches
│   │   └── ListService.cs        # list database, retrieval, merges, REANALYZE_CASE jobs on Profile change
│   ├── Aml.Infra/                # implements Core/Ports
│   │   ├── Persistence/          # see feature/persistence.md
│   │   ├── Txt/                  # .txt export reader (UTF-8, CP950 fallback)
│   │   ├── Html/                 # HTML page to export layout (AngleSharp)
│   │   └── Jobs/                 # JobStore
│   └── Aml.Api/                  # composition root
│       ├── Program.cs            # minimal API endpoints, DI, OpenAPI, OpenTelemetry, health check
│       └── Workers/JobWorker.cs  # BackgroundService
└── tests/
    └── Aml.Tests/
        ├── Core/                 # extraction, identity, screening, retrieval and scoring rules; no database
        ├── Api/                  # endpoints through WebApplicationFactory
        ├── Persistence/          # repository and batch tests
        ├── Fixes/                # regression tests for parsing, names, identity, retrieval, screening, persistence and integration
        └── GoldenCases/          # Golden Cases end to end, augmented cases, explanation, metrics
```

Rules:

- All decision logic lives in Core as pure code, so Golden Case tests need no database or HTML.
- Ports live in Core; Infra depends on Core only, never on Services.
- Case state mutators are `internal` with `InternalsVisibleTo("Aml.Services")`; within Services, `CaseService` remains the only writer by review.
- Api holds no business logic.

Implementation notes:

- Api uses minimal APIs in `Program.cs`; the endpoints are listed in [REST resources](../feature/pipeline-api-and-batch.md#rest-resources), plus `GET /health`. Uploads are multipart `.txt` or `.html` files, read as raw bytes: an export is strict UTF-8, else CP950 (Big5) with an `ENCODING_FALLBACK` finding; an HTML page's encoding is decided by AngleSharp (BOM, then meta charset). A Case keeps the SHA-256 of each uploaded file's bytes as its source hash.
- The explanation is built in Core (`Explanation/ExplanationBuilder`) with each revision and stored beside the result; document and reference numbers are masked (first 3 and last 2 characters). The five-part summary is in Traditional Chinese for reviewers. Each hit lists its sources: Profile citations (`PROFILE_RECORD`, with publication and retrieval dates and URL) and attached news (`LEGAL_STAGE` when it set the legal stage of an ADVERSE_MEDIA condition).
- No human workflow in the PoC ([ADR-0020](../adr/0020-identification-only.md)).
- `POST /api/cases` analyzes synchronously; `POST /api/batches` queues items in the job table and `Workers/JobWorker` runs them through `BatchService` ([durable batch execution](../feature/pipeline-api-and-batch.md#durable-batch-execution)). The job store is `Infra/Jobs/JobStore` behind `Core/Ports/IJobStore` (named "Store" because CA1711 rejects a "Queue" suffix).
- Each revision records source condition status and basis, pair result, review priority, pair reason codes and the changes from the previous revision; a missing Profile makes the revision PARTIAL and keeps the previous effective result.
- Persistence is SQLite-only (`SqliteDatabase`, `SqlCatalog`, `CaseStore`); `IDbConnectionFactory` and `ISqlDialect` are extracted when a second provider is chosen.
- `GET /health` runs `System.Ping`. OpenTelemetry traces, metrics and logs are on (ASP.NET Core plus the `Aml` source and meter: `aml.analyses`, `aml.analysis.duration`, `aml.job.attempts`); OTLP export starts only when `OTEL_EXPORTER_OTLP_ENDPOINT` is set.
- The list database (`list_profile`) and each revision's input files (`case_input`, decoded text) are stored for retrieval and re-analysis ([ADR-0021](../adr/0021-deferred-capabilities-built-behind-controls.md)). A Profile version is hashed on its export text, so an HTML page and its `.txt` export are one version. `POST /api/lists/profiles` stores a changed Profile as a new version and, in the same transaction, queues a `REANALYZE_CASE` job for every Case whose latest input holds a different version of it; the jobs run through `BatchService` and are tracked as one batch (`GET /api/batches/{batchId}`).
- Authentication is not wired (identity provider pending).
- Golden Case metrics are computed by `tests/Aml.Tests/GoldenCases/GoldenMetricsTests` ([results](poc-delivery-plan.md#golden-case-results)).

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
| Identity | OpenID Connect / OAuth 2.0 |
| Authorization | Policy-based authorization |
| Observability | OpenTelemetry |
| Deployment | Linux container on approved Kubernetes/OpenShift/server platform |
| Testing | xUnit, Golden Case regression, repository contract tests |
| Supply chain | Central Package Management, lock files, package source mapping, NuGet Audit, SBOM, SAST/SCA, container scanning |
