---
name: measure-stage
description: Measure a change to the legal-stage rules (Core/Screening/Stage), the stage vote, the stage classifier or the language model stage reader through SourceConditions.Determine with tools/Aml.StageEval, and judge whether to adopt it. Use before adopting any such change or when asked how a stage change scores.
---

# Measure a stage change

Why and what is judged: `docs/plan/stage-model-training.md` (sections "What is judged", "Measured sets and their boundaries", "Decisions in force and their evidence"). Read them first.

## Run

From the repo root (prefix shell commands as AGENTS.md says, e.g. `rtk proxy` where the host has rtk):

```bash
dotnet run -c Release --project tools/Aml.StageEval -- <answers> <answers> out=<new folder for this change>
```

- Locations differ per host; find them before running. `<answers>` is the folder of the deployed language model's cached answers (one `<set>.jsonl` per measured set; the doc's "Where things live" names which model and job produced them). `sets=` is the folder with the measured sets (`report/`, `court/`), `onnx=` the deployed classifier (`model.onnx`, `vocab.txt`, `labels.json`). The built-in defaults are one machine's; on another host pass all three, from the environment, the user's notes or by asking. If you cannot find them, stop and ask; never measure against guessed or partial data.
- The tool prints the paths it uses first and exits 2 on a missing folder or file; check the printed paths before reading the table.
- Arguments: `<answers for report sets> <answers for court sets> [--heldback] [onnx=<classifier dir>] [out=<dir>] [judgments=<dir>] [sets=<dir with report/ and court/>]`.
- Measure the baseline and the change with the same answers, classifier and sets; write each to its own `out=` folder.
- A new classifier: `onnx=<its dir>`. A new language model or prompt: its answers folder as the two positional arguments.
- Persons who are not suspects (officials, victims, witnesses): a separate measurement root in the same set format, holding those persons with reference stage UNKNOWN, and the language model's answers for them; pass it as `sets=`. A set without a file is skipped, so a root may hold only the report sets. Every decided person who is not RIGHT got a wrong automatic stage; the baseline is in the doc's "Persons who are not suspects".
- Held-back check, only once the change is final and its development numbers pass: add `--heldback`. Never run it to choose between variants.

## Read

Per set: `n` persons; `RULES`, `CLASSIF`, `LLM`, `VOTE` = persons each reader (or the vote) gets right (diagnostics only); `SPLIT` = readers disagree; `llm-none` / `clf-none` = the reader's answer is missing (`-` in the `.tsv` rows, apart from an answer `Unknown`; the vote is then UNKNOWN, never decided; a replayed `MISSING` counts as missing); `DECIDED` = all three alike (the automatic stage); `RIGHT` = of those, how many match the reference stage.

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
