# AGENTS.md

AML screening PoC: given a customer (or related party) and candidate list Profiles (Sanction, PEP/RCA, official lists, adverse news and court rulings), it decides whether they are the same person and whether the source condition holds, and explains every decision. Rules and algorithms decide; with both optional stage models configured, the legal stage of attached news is given only when the rules, a classifier and a large language model all read the same stage, else it is UNKNOWN for a reviewer; with one model or none configured the rules' stage stands; a configured model whose answer is missing gives UNKNOWN with `STAGE_READER_NO_ANSWER`, never the rules' stage, and a batch job retries it before writing (`Screening/Stage/StageVote.cs`, ADR-0012); a model enters a decision only on measured evidence. API only, no UI. C# / .NET 10, SQLite in the PoC.

## Mental model in five facts

1. **Two independent questions per candidate.** Identity (`NO_MATCH`, `REVIEW`, internal `PROBABLE_MATCH`; never an automatic MATCH) and source condition (`HIT`, `NOT_HIT`, `UNKNOWN`). They are decided separately and combined only by the combination matrix (`Screening/Assessment.cs`).
2. **Evidence first.** Readers turn inputs into fields with raw value, normalized value, precision and location; comparisons become evidence groups; `HardConflictGate` can only say "different person" on validated rules; `IdentityDecisionPolicy` decides the rest. Missing or unparseable data becomes a finding (`MISSING`, `AMBIGUOUS` …), never an invented value.
3. **Every emitted code is in `Codes/CodeCatalog.cs`** (served by `GET /api/codes`). A new reason code without a catalog entry is a bug.
4. **Case state is append-only.** A re-analysis creates a new revision; `CaseService` is the only writer of Case state (through `ICaseStore`). Other modules return typed results.
5. **The system identifies; humans decide.** No reviewer workflow, approvals or final disposition (ADR-0004). Article 22 output is advisory.

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
- `tools/`: working tools for the models, not part of the product: `Aml.StageEval` (console, uses Core and Infra, built by CI), `stage-model/`, `llm-stage/`, `news-model/`, `stage-rules/`, `labels/`, `judgments/`, `data/` (Python) and `colab/`. Data, labels and models stay outside the repo, found through `AML_*` environment variables or arguments; the tools print counts only. The data is sensitive personal data: `tools/data` packs it for another machine as an encrypted original and a pseudonymised copy (the user types the password). Each tool has a README; the skills in `.claude/skills/` (`measure-stage`, `prune-rules`, `label-data`, `gemma-stage`, `news-models`, `colab-job`, `data-bundle`) walk through them.

## Read first, by task

| Task | Start with |
|---|---|
| Whole flow of one Case | `Core/Cases/CaseAnalyzer.cs`, then `Services/CaseService.cs` |
| Identity decision | `Core/Identity/EvidenceBuilder.cs`, `HardConflictGate.cs`, `IdentityDecisionPolicy.cs`; spec `docs/feature/identity-adjudication.md` |
| Source condition / list types | `Core/Screening/SourceConditions.cs`; spec `docs/feature/watchlist-and-adverse-information.md` |
| Legal stage from attached news | `Core/Screening/Stage/`, one concern a file: `StageGlossary` (words, limits), `StagePatterns` (regexes built from them), `StageText` / `RulingForm` / `ReportForm` (reading the form), `StageTypes` (data), `RulingRules` / `ReportRules` (what a form means), `RulingReading` / `LegalStageReading` (entry points, rule order). A new wording goes into the lowest of these that can hold it |
| Names, dates, addresses | `Core/Normalization/`; spec `docs/feature/extraction-and-normalization.md` |
| Explanation and news digest | `Core/Explanation/ExplanationBuilder.cs`, `NewsDigest.cs`; news models `NewsModelReading.cs`, `Infra/Models/` |
| Stage models and the vote | `Core/Screening/Stage/StageVote.cs`, `Core/Ports/IStageReader.cs`, `Infra/Models/OnnxStageClassifier.cs`, `OllamaStageReader.cs` (prompt `stage-prompt.txt`, as measured); configuration `StageModels:*` in `Api/Program.cs` (the language model's per-analysis budget: `Services/BudgetedStageReader.cs`); codes `STAGE_VOTE_*`, `STAGE_READERS_DISAGREE`, `STAGE_READER_NO_ANSWER`; spec `docs/feature/watchlist-and-adverse-information.md#three-readers`. Both models are optional and absent in CI, so their tests skip |
| Training or measuring the news models (labels, Colab, end-to-end F1) | `docs/plan/news-model-training.md`; pipeline `tools/news-model/` (skill `news-models`); data and models live outside the repo |
| Training or measuring the stage models, or judging a stage-rule change | `docs/plan/stage-model-training.md`: judged by the unanimous vote's precision and coverage through `SourceConditions.Determine` (`tools/Aml.StageEval`, skill `measure-stage`), never by one reader's count; classifier pipeline `tools/stage-model/`; language model jobs `tools/llm-stage/` (skill `gemma-stage`); rule pruning `tools/stage-rules/` (skill `prune-rules`); labels `tools/labels/` (skill `label-data`); Colab jobs `tools/colab/` (skill `colab-job`) |
| REST, batch, job table | `Api/Endpoints/`, `Services/BatchService.cs`, `Infra/Jobs/JobStore.cs`; spec `docs/feature/pipeline-api-and-batch.md` |
| Storage | `Infra/Persistence/`; spec `docs/feature/persistence.md` |
| Web text (crawler, PDF) | `Infra/Web/`; spec `docs/feature/web-text.md` |
| Why something is the way it is | `docs/adr/README.md` (index with status) |
| Terms (Profile, revision, evidence group …) | `docs/feature/glossary.md` |

`docs/plan/implementation-baseline.md` has the full folder tree, testing rules and package list; read the section you need, not the whole file.

## Commands

Prefix agent shell commands with `rtk`; use `rtk proxy` for passthrough. `rtk dotnet test` can report "Zero tests ran"; run the suite as `rtk proxy dotnet test --solution Aml.slnx`. The local reference is `C:\Users\a8022\.codex\RTK.md`. Apply that prefix when executing the commands below.

```bash
dotnet build Aml.slnx --no-incremental          # must be 0 warnings (TreatWarningsAsErrors)
dotnet format Aml.slnx --verify-no-changes
dotnet test --solution Aml.slnx                 # whole suite, a few seconds (agents: rtk proxy dotnet test ...)
tests/Aml.Tests/bin/Debug/net10.0/Aml.Tests.exe -namespace "Aml.Tests.Unit.*"   # inner loop
AML_UPDATE_SNAPSHOTS=1 dotnet test --solution Aml.slnx   # only after an intended contract/example change; review the diff
AML_NEWS_MODELS=<dir with ner/ and ev/> dotnet test --solution Aml.slnx   # also runs the trained news models on a Case (skipped without it)
AML_STAGE_MODEL=<classifier onnx dir> dotnet test --solution Aml.slnx      # also runs the stage classifier (StageReaderCaseTests; parity rows via AML_STAGE_PARITY_ROWS / _PRED)
dotnet run -c Release --project tools/Aml.StageEval -- <llm answers> <llm answers> out=<dir>   # stage vote per measured set (skill measure-stage)
dotnet tool restore && dotnet stryker -f stryker-new-logic.json -m "**/Identity/IdentityDecisionPolicy.cs"   # mutation test one file
```

CI (`.github/workflows/ci.yml`) restores with `--locked-mode`, so a package change must update `packages.lock.json`. Versions live only in `Directory.Packages.props`.

## Rules for changes

- **Tests:** lowest layer that can show the behaviour; `TestPyramidTests` fails if Integration < 2 × E2E or in-process < 3 × Integration. Expected values come from the feature docs, not from running the code; assert exact values. Decision tables are one `[Theory]` with a row per cell. Time via `ManualTimeProvider`, never sleeps.
- **Contracts:** `E2E/Contracts` snapshots guard the OpenAPI document and JSON Schemas; every operation needs an `operationId`, tag and description.
- **Docs describe the current state and build a mental model.** Organize them around responsibilities, data flow, invariants, decisions and evidence. Keep chronological construction/training logs in experiment artifacts; update conclusions rather than appending each work session. Update the feature/plan doc in the same change as the code. ADRs hold the decisions in force, one record per subject (9 records): a changed or new decision edits the record it belongs to, with its date line updated; add a record only for a subject none covers.
- **Licences:** no commercially licensed packages; models and training text only under clean licences allowing commercial use (ADR-0015). Embedded third-party data needs an entry in `THIRD-PARTY-NOTICES.md` and `licenses/`.
- **Personal data:** `samples/` is supplied material, read-only; do not open its files unless the task needs them, and never copy names, ids or quotes from it into code, docs, logs, commit messages or test names. Customer data stays in the Runtime Query half (`docs/feature/security-and-privacy.md`).
- **Not in Phase 1:** an LLM's verdict as the identity decision, AI match probabilities, automatic MATCH, any UI, OCR, unapproved risk weights.
