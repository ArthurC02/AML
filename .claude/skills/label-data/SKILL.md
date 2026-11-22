---
name: label-data
description: Add or recheck gold stage labels of court releases (new acceptance or train rounds, rechecks of hard persons) with tools/labels: choose documents, run two blind labelling passes, check and merge them, recheck. Use when adding, rebuilding or rechecking gold labels or a measured set.
---

# Add or recheck gold labels

Read first: `tools/labels/README.md` (the procedure end to end), `docs/plan/stage-model-training.md` ("Measured sets and their boundaries"), `tools/labels/stage-brief.md`.

## Locations

Nothing here is machine-specific; find the locations on this host first: `AML_DATA` must point to the labelled material (`labels/` with the rounds, `court/raw/` with the gathered release texts), `AML_TRAIN` to the training workspace (`measurements/court/`, `measurements/report/`, `stage-clf/`); see `tools/stage-model/paths.py`. The recheck also needs the `out=<dir>` folder of an `Aml.StageEval` run. Check the environment variables and the READMEs; if a location is missing or does not hold what is described, stop and ask the user.

## Rules

- **Data first.** Labels are fixed before anything is trained or tuned on them; a run that turns up a label error stops until the label is checked. Run `merge.py check` on every pass before merging, and fix format errors in the pass files (not in the merge).
- **Blind passes.** Each pass is a separate labelling agent per batch, given only the brief, its document ids and the folders named in its assignment. It never sees the other pass, other label folders, model answers, evaluator rows or the reference stage. A recheck pass sees the texts and its person list only.
- **Agreement keeps.** A person enters a set only when both passes give the same stage; disagreements are counted, not resolved by you. Adjudication, if the user asks for it, goes into a `fin/` folder built with `merge.py --fin`.
- **Held-back sets stay blind.** Never relabel or recheck a held-back set (now v11 and accept5) from model errors, never open its texts or per-person rows; aggregate counts only. A new round is measured once unseen before anyone looks at it.
- **Disjoint from train.** New rounds come from documents no round or set holds (`choose.py` excludes them); train rows drop any document overlapping an evaluation document (`tools/stage-model/add17.py`). Never move a document between train and evaluation.
- A rebuilt set changes every baseline measured on it: rebuild only with the user's yes, then re-measure (`measure-stage`) and update the docs' counts in the same change.

## Steps

1. `choose.py <round> <n> <seed> [candidates.txt]` -> `labels/<round>/batches.json`.
2. Two passes `a`, `b`: one agent per batch and pass with `stage-brief.md`; the assignment names `<RAW>` (the release texts), `<OUT>` (`labels/<round>/<pass>`) and the batch's ids. Agents report counts only.
3. `merge.py check <round>/a`, `merge.py check <round>/b`, then `merge.py <round> <set file>` (an evaluation set) or the train builder (train rounds). `merge.py` refuses a round with format errors and an existing set file; `--force` (overwrite) only for a rebuild the user said yes to.
4. Recheck (development sets only): `recheck.py pick <set> <eval out> <dir>`, two blind passes with `recheck-brief.md` writing `<dir>/a.tsv`, `<dir>/b.tsv`, then `recheck.py compare <set> <dir>`. Report confirmed / label error / split; a label error goes to the user before any set changes.

## Privacy

- The labels and texts hold real names: they stay outside the repo. Nothing in code, docs, briefs, logs or commit messages may hold a name, a quote or document text; examples in briefs use made-up names or masked generic forms.
- Scripts and agents report counts only; do not print forms, `persons.tsv` lines or texts while working.
