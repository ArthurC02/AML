# Implementation Baseline

> Agreed for PoC Phase 1, 2026-11-02. Subject to bank technology governance.

## PoC implementation decisions

| Topic | Decision | ADR |
|---|---|---|
| Runtime | C#, .NET 10 LTS, ASP.NET Core | [0007](../adr/0007-dotnet-technology-stack.md) |
| Delivery | API + OpenAPI / Swagger UI, no UI | [0009](../adr/0009-api-only.md) |
| Input | supplied HTML files; API or crawler later behind ports | [0012](../adr/0012-source-acquisition-behind-ports.md) |
| Database | SQLite now; SQL Server or Oracle later | [0010](../adr/0010-replaceable-persistence.md) |
| Data access | Dapper + DbUp; SQL in `Sql.<provider>.xml` | [0010](../adr/0010-replaceable-persistence.md), [0014](../adr/0014-per-provider-xml-sql-catalog.md) |
| Volume | small; no search index, CONTROLLED_SET retrieval | |
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
├── samples/                      # supplied samples, read-only
│   └── golden/GC-01..03, 11, 12/ # anonymized inputs + expected.yaml per Golden Case
├── src/
│   ├── Aml.Core/                 # zero dependencies
│   │   ├── Shared/               # FieldValue, Evidence, ReasonCode, enums
│   │   ├── Cases/                # Case, AnalysisRevision, decisions, transition rules
│   │   ├── Subjects/             # CustomerSnapshot, SourceProfileSnapshot
│   │   ├── Normalization/        # names, date precision, country roles, documents, Taiwan addresses
│   │   ├── Identity/             # EvidenceBuilder, HardConflictGate, IdentityDecisionPolicy
│   │   ├── Screening/            # Sanction/Pep/OfficialList/AdverseInformation policies, list-type mapping, combination matrix
│   │   ├── Disposition/          # DispositionPolicy (Article 22), verification record rules
│   │   └── Ports/                # IProfileSource, ICustomerSource, ICaseRepository, IJobQueue
│   ├── Aml.Services/             # orchestration
│   │   ├── CaseService.cs        # sole writer of Case state
│   │   ├── AnalyzeCaseHandler.cs # AnalyzeCase flow
│   │   ├── CandidateService.cs, ReviewService.cs, DispositionService.cs
│   │   └── ExplanationService.cs, BatchService.cs
│   ├── Aml.Infra/                # implements Core/Ports
│   │   ├── Persistence/          # see feature/persistence.md
│   │   ├── Txt/                  # .txt export reader (Golden Case samples)
│   │   ├── Html/                 # AngleSharp sources and page-template parsers
│   │   └── Jobs/                 # job-table IJobQueue
│   └── Aml.Api/                  # composition root
│       ├── Program.cs            # DI, OpenAPI, OpenTelemetry, health checks
│       ├── Controllers/
│       └── Workers/JobWorker.cs  # BackgroundService
└── tests/
    └── Aml.Tests/
        ├── Core/                 # policies, matrix, normalization; no database
        ├── Infra/                # HTML extraction against samples/
        ├── Persistence/          # repository contract tests
        └── GoldenCases/          # GC-01..03, 11, 12 end to end
```

Rules:

- All decision logic lives in Core as pure code, so Golden Case tests need no database or HTML.
- Ports live in Core; Infra depends on Core only, never on Services.
- Case state mutators are `internal` with `InternalsVisibleTo("Aml.Services")`; within Services, `CaseService` remains the only writer by review.
- Api holds no business logic.

Built so far (2026-11-02):

- Api uses minimal APIs in `Program.cs` instead of `Controllers/`; endpoints: `POST /api/cases` (multipart `.txt` upload), `POST /api/cases/{caseId}/analyses`, `GET /api/cases/{caseId}`, `GET /api/cases/{caseId}/analyses/{revision}`.
- Analysis runs synchronously in `CaseService`; the job table exists but `BatchService` and `JobWorker` are not built.
- Persistence is SQLite-only (`SqliteDatabase`, `SqlCatalog`, `CaseStore`); `IDbConnectionFactory` and `ISqlDialect` are extracted when a second provider is chosen.
- OpenTelemetry, health checks and authentication are not wired yet.

## Build settings

| File | Purpose |
|---|---|
| `Directory.Packages.props` | Central Package Management; csproj files carry no versions |
| `Directory.Build.props` | `net10.0`, Nullable, TreatWarningsAsErrors, `AnalysisLevel=latest-recommended`, `RestorePackagesWithLockFile` |
| `nuget.config` | `packageSourceMapping` to approved feeds only |
| `global.json` | pin SDK 10.0.x; `test.runner: Microsoft.Testing.Platform` (required by xunit.v3 on .NET 10) |

NuGet Audit and the built-in .NET analyzers are enabled without extra packages.

## Packages

Versions as of 2026-11-02.

| Project | Package | Version | Purpose |
|---|---|---|---|
| Core | none | | |
| Services | Microsoft.Extensions.Logging.Abstractions | 10.0.14 | `ILogger` |
| Infra | Dapper | 2.1.89 | data access |
| Infra | Microsoft.Data.Sqlite | 10.0.14 | SQLite driver |
| Infra | dbup-sqlite | 6.0.4 | schema scripts |
| Infra | AngleSharp | 1.9.0 | HTML parsing |
| Api | Microsoft.AspNetCore.OpenApi | 10.0.14 | OpenAPI document |
| Api | Swashbuckle.AspNetCore.SwaggerUI | 10.3.1 | Swagger UI only |
| Api | OpenTelemetry.Extensions.Hosting | 1.20.1 | telemetry host |
| Api | OpenTelemetry.Instrumentation.AspNetCore | 1.20.1 | HTTP traces/metrics |
| Api | OpenTelemetry.Exporter.OpenTelemetryProtocol | 1.20.1 | OTLP, enabled when an endpoint is configured |
| Tests | xunit.v3 | 4.0.2 | test framework |
| Tests | xunit.runner.visualstudio | 4.0.1 | runner |
| Tests | Microsoft.NET.Test.Sdk | 18.11.0 | test host |
| Tests | Microsoft.AspNetCore.Mvc.Testing | 10.0.14 | `WebApplicationFactory` |
| Tests | coverlet.collector | 10.1.0 | coverage |

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
