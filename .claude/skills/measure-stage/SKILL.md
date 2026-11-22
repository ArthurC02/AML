---
name: measure-stage
description: Measure a change to the legal-stage rules (Core/Screening/Stage), the stage vote, the stage classifier or the language model stage reader through SourceConditions.Determine with tools/Aml.StageEval, and judge whether to adopt it. Use before adopting any such change or when asked how a stage change scores.
---

# Measure a stage change

Why and what is judged: `docs/plan/stage-model-training.md` (sections "What is judged", "Measured sets and their boundaries", "Decisions in force and their evidence"). Read them first.

## Run

From the repo root, with the language model answers to replay (default: the deployed model's cached answers named in that doc):

```bash
A=C:/tmp/aml-train/downloads/aml49/out/gemma4_12b-it-q4_K_M
rtk proxy dotnet run -c Release --project tools/Aml.StageEval -- $A $A out=C:/tmp/eval-sys/out-<change>
```

- Arguments: `<answers for report sets> <answers for court sets> [--heldback] [onnx=<classifier dir>] [out=<dir>] [judgments=<dir>] [sets=<dir with report/ and court/>]`. The tool prints the paths it uses first; check them.
- Measure the baseline and the change with the same answers, classifier and sets; write each to its own `out=` folder.
- A new classifier: `onnx=<its dir>`. A new language model or prompt: its answers folder as the two positional arguments.
- Held-back check, only once the change is final and its development numbers pass: add `--heldback`. Never run it to choose between variants.

## Read

Per set: `n` persons; `RULES`, `CLASSIF`, `LLM`, `VOTE` = persons each reader (or the vote) gets right (diagnostics only); `SPLIT` = readers disagree; `llm-none` / `clf-none` = reader gave no answer; `DECIDED` = all three alike (the automatic stage); `RIGHT` = of those, how many match the reference stage.

- Precision = RIGHT / DECIDED. Coverage = DECIDED / n.
- Per-person rows (`out/<set>.tsv`: doc id, person id, reference and each reader's stage, basis) exist for development sets only; use them to find where a change moves.

## Adopt only if

1. On each of the six development sets (dev, dev2, v9, accept, accept3, accept4) the precision is not below the current one in "Decisions in force".
2. Coverage rises on the sets the change is meant for.
3. Then the held-back sets (v11, accept5) are computed once, as a check; their counts are reported, not used for a choice.

Removing a rule follows the same measure: it may go when no set's precision falls. Record adopted numbers by updating the table "Decisions in force and their evidence" in `docs/plan/stage-model-training.md` in the same change.

## Privacy

- Report counts only. Never put names, quotes or document text in reports, code, docs, commit messages or logs.
- Held-back sets: aggregate counts only; never open their texts or answers, never write or read per-person rows for them (the tool writes none).
- Do not print document text or answer quotes while debugging; look at ids and stages in the development `.tsv` rows.
