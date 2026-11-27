---
name: prune-rules
description: Try deleting or changing a legal-stage rule (src/Aml.Core/Screening/Stage) and decide by measurement whether it may go, with tools/stage-rules/prune.py and tools/Aml.StageEval; then delete it with its code, catalog entry, tests and docs. Use when trying to delete, simplify or change a stage rule, or when asked which stage rules the vote still needs.
---

# Prune or change a stage rule

Read first: `docs/plan/stage-model-training.md` ("What is judged", "Measured sets and their boundaries", "Decisions in force"), `docs/feature/watchlist-and-adverse-information.md` ("Stage from attached news", "Why the rules are few", "Three readers"), `tools/stage-rules/README.md`. The `measure-stage` skill covers reading the evaluator's output.

## Locations

Nothing here is machine-specific; find the locations on this host before running anything:

- the language model answers folder to replay (the two positional arguments of `prune.py` and `Aml.StageEval`): the deployed model's cached answers named in `docs/plan/stage-model-training.md`, one `<set>.jsonl` per set;
- `onnx=<dir>`: the deployed classifier folder (`model.onnx`, `vocab.txt`, `labels.json`); `sets=<dir>`: the folder with `report/` and `court/` measured sets; `out=<dir>` (evaluator only): where the per-person rows of the development sets go.

Check the environment variables of `tools/stage-model/paths.py` (`AML_TRAIN`, `AML_DATA`), the paths `Aml.StageEval` prints first, and the READMEs. If a location is missing or the printed paths do not hold what is described, stop and ask the user; never guess and never point the tools at a held-back set's rows.

## The rule

A rule may go when, on each of the six development sets (dev, dev2, v9, accept, accept3, accept4), the unanimous precision RIGHT / DECIDED is not below the current one. Coverage (DECIDED / n) and the rules-alone count are reported, not the criterion. A changed rule follows the same measure and must also raise coverage on the sets it is meant for. The held-back sets (v11, accept5) are computed once, at the end, as a check; they never choose between variants. A rule a Golden Case needs stays whatever the measure says (`OrderUnderHeading`: AUG-02-22).

## Steps

1. `python -I -X utf8 tools/stage-rules/prune.py <answers> <answers> [only=<Unit,...>] [onnx=<dir>] [sets=<dir>]` from the repo root. It measures a temporary copy of this checkout unchanged, then each unit switched off, and prints per set the DECIDED/RIGHT delta, `!` where precision falls, and a verdict per unit (`no effect`: no DECIDED, RIGHT or rules count moves on any set; `may go`: something moves but no set's precision falls; `precision falls`). Both `no effect` and `may go` meet the rule above. It takes no `out=` (its rows stay in the temporary copy). A unit "patch does not apply" means `EXTRA` in `prune.py` is out of date with the code: update it first. To try several units together, delete them in a working copy and measure that with `Aml.StageEval`.
2. Delete what may go: the rule function and its list entry, its constant in `StageBasis`, its `LEGAL_STAGE_BASIS` entry in `Core/Codes/CodeCatalog.cs`, the count in `tests/Aml.Tests/GoldenCases/CodeCatalogTests.cs`, its rows in `tests/Aml.Tests/Unit/Screening/ScreeningPolicyTests.cs` (`legal_stage_from_news`, `legal_stage_basis_from_news`, the ruling trace) and any test naming its code (e.g. `StageVoteTests`), and patterns and glossary words nothing else uses. Keep or rewrite counter-example rows that still hold.
3. Build with 0 warnings, `dotnet format --verify-no-changes`, the whole suite (commands in `AGENTS.md`).
4. Measure the changed checkout with `Aml.StageEval` (its own `out=` folder) and compare with the baseline; then, once and only for the final change, add `--heldback` and record the aggregate counts.
5. Update the docs in the same change: the code table and "Why the rules are few" in `docs/feature/watchlist-and-adverse-information.md`, the counts in `docs/plan/stage-model-training.md` ("Decisions in force"), and ADR-0012's line on the stage rules with its date line.

## Privacy

- Report counts only. No names, quotes or document text in code, docs, test names, commit messages or logs; CJK literals in tests are generic words or made-up names.
- Held-back sets: aggregate counts only; never open their texts, answers or rows (the tools write none).
- When looking at which persons a change moves, use the development `.tsv` rows (ids and stages), not the texts.
