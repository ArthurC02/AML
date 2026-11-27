---
name: news-models
description: Use when changing, retraining or measuring the news models (NER/EV), the two ONNX models OnnxNewsModel reads (NewsModels:Directory, AML_NEWS_MODELS).
---

# News models

Pipeline: `tools/news-model/README.md`. What is learned, the gates and the current candidate:
`docs/plan/news-model-training.md`. Colab runner: `tools/colab/README.md`.

## Rules

- Data first: fix confirmed label errors before training; stop a run as soon as an input or label error is found.
- Training needs the user's yes for each run. No GPU here: train only on Colab, small models on T4.
- After every run, confirm yourself that `colab sessions` lists no `aml` session; stop any that remains and check again.
- The measure is end-to-end F1 on Case outputs (`e2e.sh`, the written-name measure, both dev sets at 0.40). A row-level
  or native score alone never decides. All three gates must pass for the same pair (`2TP > 9(FP+FN)`).
- Held-back sets (test split, `data-synth/heldout.jsonl`, `data-court/heldout.jsonl`) only at the end, once fixed.
- Privacy: the data holds real names. Nothing in the repo, logs, reports or commit messages may hold names, quotes,
  document text, tokens or credentials; print counts and anonymous ids only. CJK literals in code are generic words or
  made-up names only.

## Locations

No machine paths are written here. Every location comes from environment variables, each defaulting to the value in
`tools/news-model/paths.py`:

- `AML_DATA`: labelled material (`labels/` with the label round folders, `judgments/`, `press/`, `news/`).
- `AML_TRAIN`: training workspace (`data-r*/` rounds, `data-dev2/`, `data-synth/`, `models/bert-base-chinese/`,
  `downloads/`, `measurements/`, the Python venv).
- `AML_COLAB_HOME`: the folder mounted at `/home/colab` in the colab container (job folders, packages).
- `AML_EVAL`: the working folder of the .NET evaluator `tools/Aml.NewsEval` (which `e2e.sh` builds from the repo):
  its documents `dev.jsonl` / `train.jsonl` in, dumps out; outside the repo and not in the data bundle (without them `e2e.sh`
  cannot run: stop and ask the user).
- Arguments: `--out <dir>` (rows), `--rows <round dir>`, `--start <native model dir>`, `--parent <job dir>`, the
  download dir of `run_job.sh`, the pair folder of `e2e.sh`.

Find them on this host first: check the environment variables, the defaults the scripts print or assert on
(`paths.py`), and the README. If a location is missing or does not hold what is listed above, stop and ask the user.

## Steps

1. `python -I -X utf8 tools/news-model/selfcheck.py` (must print `selfcheck OK`).
2. Rows: copy the fixed `split.json` of the round you continue into `<out>`, then `prep.py --round <r> --out <out>
   > <out>/stats.txt`. Compare `stats.txt` and the file hashes with the round you continue; explain every difference
   (a label edit) before going on.
3. Job: `build_job.py ev|ner <name> --start ... --rows ...` (ev: `--parent` the previous job keeps dev rows
   comparable; `--add`/`--repeat` for extra rows). It must end with `ok job<name>`; read its counts (label edits vs
   parent, rows loaded, over 512).
4. Ask the user for the run. Then `colabjob.py package` + `check` (`ALL OK`), `run_job.sh <name> T4 <download dir>`,
   and confirm `colab sessions` shows no `aml` session.
5. From `AML_TRAIN`: `export_one.py <download>/out/ev <pair>/ev` (must pass its ORT check), assemble the pair with the
   NER, fresh caches for a new event model, then `bash tools/news-model/e2e.sh <tag> <pair>` (prefix with `rtk proxy`).
6. Decide on the end-to-end counts against the retained pair (same measure, same dev revision); update
   `docs/plan/news-model-training.md` with conclusions, not a run log.
