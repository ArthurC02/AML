# AGENTS.md

AML screening PoC: given a customer (or related party) and candidate list Profiles (Sanction, PEP/RCA, official lists, adverse news and court rulings), it decides whether they are the same person and whether the source condition holds, and explains every decision. Rules and algorithms only, no LLM in any decision. API only, no UI. C# / .NET 10, SQLite in the PoC.

## Mental model in five facts

1. **Two independent questions per candidate.** Identity (`NO_MATCH`, `REVIEW`, internal `PROBABLE_MATCH`; never an automatic MATCH) and source condition (`HIT`, `NOT_HIT`, `UNKNOWN`). They are decided separately and combined only by the combination matrix (`Screening/Assessment.cs`).
2. **Evidence first.** Readers turn inputs into fields with raw value, normalized value, precision and location; comparisons become evidence groups; `HardConflictGate` can only say "different person" on validated rules; `IdentityDecisionPolicy` decides the rest. Missing or unparseable data becomes a finding (`MISSING`, `AMBIGUOUS` …), never an invented value.
3. **Every emitted code is in `Codes/CodeCatalog.cs`** (served by `GET /api/codes`). A new reason code without a catalog entry is a bug.
4. **Case state is append-only.** A re-analysis creates a new revision; `CaseService` is the only writer of Case state (through `ICaseStore`). Other modules return typed results.
5. **The system identifies; humans decide.** No reviewer workflow, approvals or final disposition (ADR-0020). Article 22 output is advisory.

## Projects and dependency rules

```text
Api ──► Services ──► Core
 │                    ▲
 └────► Infra ────────┘
```

- `Aml.Core`: all decision logic, pure, zero package dependencies; ports (`Ports/ICaseStore`, `IJobStore`, `IListStore`) live here.
- `Aml.Services`: orchestration (`CaseService`, `BatchService`, `ListService`).
- `Aml.Infra`: implements the ports and readers: `Persistence/` (Dapper + DbUp, SQL only in `Sql/Sql.sqlite.xml`, schema `Scripts/Sqlite/0001_schema.sql`), `Txt/`, `Html/`, `Web/`, `Excel/`, `Jobs/`. Depends on Core only.
- `Aml.Api`: composition root and minimal-API endpoints; no business logic.

## Read first, by task

| Task | Start with |
|---|---|
| Whole flow of one Case | `Core/Cases/CaseAnalyzer.cs`, then `Services/CaseService.cs` |
| Identity decision | `Core/Identity/EvidenceBuilder.cs`, `HardConflictGate.cs`, `IdentityDecisionPolicy.cs`; spec `docs/feature/identity-adjudication.md` |
| Source condition / list types | `Core/Screening/SourceConditions.cs`; spec `docs/feature/watchlist-and-adverse-information.md` |
| Names, dates, addresses | `Core/Normalization/`; spec `docs/feature/extraction-and-normalization.md` |
| Explanation and news digest | `Core/Explanation/ExplanationBuilder.cs`, `NewsDigest.cs` |
| REST, batch, job table | `Api/Endpoints/`, `Services/BatchService.cs`, `Infra/Jobs/JobStore.cs`; spec `docs/feature/pipeline-api-and-batch.md` |
| Storage | `Infra/Persistence/`; spec `docs/feature/persistence.md` |
| Web text (crawler, PDF) | `Infra/Web/`; spec `docs/feature/web-text.md` |
| Why something is the way it is | `docs/adr/README.md` (index with status) |
| Terms (Profile, revision, evidence group …) | `docs/feature/glossary.md` |

`docs/plan/implementation-baseline.md` has the full folder tree, testing rules and package list; read the section you need, not the whole file.

## Commands

```bash
dotnet build Aml.slnx --no-incremental          # must be 0 warnings (TreatWarningsAsErrors)
dotnet format Aml.slnx --verify-no-changes
dotnet test --solution Aml.slnx                 # whole suite, a few seconds
tests/Aml.Tests/bin/Debug/net10.0/Aml.Tests.exe -namespace "Aml.Tests.Unit.*"   # inner loop
AML_UPDATE_SNAPSHOTS=1 dotnet test --solution Aml.slnx   # only after an intended contract/example change; review the diff
dotnet tool restore && dotnet stryker -f stryker-new-logic.json -m "**/Identity/IdentityDecisionPolicy.cs"   # mutation test one file
```

CI (`.github/workflows/ci.yml`) restores with `--locked-mode`, so a package change must update `packages.lock.json`. Versions live only in `Directory.Packages.props`.

## Rules for changes

- **Tests:** lowest layer that can show the behaviour; `TestPyramidTests` fails if Integration < 2 × E2E or in-process < 3 × Integration. Expected values come from the feature docs, not from running the code; assert exact values. Decision tables are one `[Theory]` with a row per cell. Time via `ManualTimeProvider`, never sleeps.
- **Contracts:** `E2E/Contracts` snapshots guard the OpenAPI document and JSON Schemas; every operation needs an `operationId`, tag and description.
- **Docs describe the current state.** Update the feature/plan doc in the same change as the code. ADRs are never edited after acceptance except the status line; a changed decision is a new ADR that supersedes the old one.
- **Licences:** no commercially licensed packages (ADR-0015); models and training text only under clean licences allowing commercial use (ADR-0029). Embedded third-party data needs an entry in `THIRD-PARTY-NOTICES.md` and `licenses/`.
- **Personal data:** `samples/` is supplied material, read-only; do not open its files unless the task needs them, and never copy names, ids or quotes from it into code, docs, logs, commit messages or test names. Customer data stays in the Runtime Query half (`docs/feature/security-and-privacy.md`).
- **Not in Phase 1:** LLM adjudication or AI match probabilities, automatic MATCH, any UI, OCR, unapproved risk weights.
