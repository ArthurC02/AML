# stage-model

The person-stage classifier pipeline: build the rows, train bert-base-chinese on Colab, export ONNX, score against the
rules and the LLM runs. Why and how it is judged: [docs/plan/stage-model-training.md](../../docs/plan/stage-model-training.md).

The data holds real names. It stays outside the repo; the scripts print counts only. Run Python isolated
(`python -I -X utf8 ...`).

## Scripts

| Script | Does | Reads | Writes |
|---|---|---|---|
| `prep_stage.py [--out DIR]` | one row per (document, suspect): name, evidence with the person's forms marked, reference stage | `data-r12/`, label dirs, `measurements/report`, `measurements/court`, tokenizer | `DIR/data/<set>.jsonl`, `stats.txt`, `stats-court.txt` |
| `add17.py [--verdicts] [--out DIR]` | `data/` plus the v17 double-labelled court releases (agreed persons only, overlap with evaluation dropped) | `DIR/data/`, `labels/v17/a,b`, `court/raw/` | `DIR/data17/` (`--verdicts`: `DIR/data17v/`, verdicts and rulings only) |
| `train_stage.py [--heldback]` | fine-tune, predictions per epoch on the development sets (`--sets`, default `dev,dev2,v9`); standalone (runs on Colab from a folder holding it and `data/`). The held-back v11 is never in `--sets`: `--heldback` evaluates it once after the last epoch, aggregate counts only, no file | `data/` | `out/pred-<set>-e<k>.jsonl` (development sets only), `--save DIR` weights |
| `export_onnx.py <saved> <out> [<data> <set>]` | ONNX opset 17 + tokenizer + `labels.json`; checks 20 rows against PyTorch, last line `ONNX_OK` | saved model | `<out>/model.onnx` ... |
| `gold.py verify [court]` | agreement of the reference port with the .NET evaluator on the development sets; the held-back v11 and accept5 are skipped (optional: needs `aml-eval-r4` built in `AML_EVAL`) | development sets | stdout (its blanked copies go to a temporary folder removed afterwards) |
| `score.py <pred> <sg.tsv>` | classifier against the rules on the same persons | | stdout |
| `score_gemma.py <out dir>` / `score_one.py <out dir>` | LLM runs (all persons / one person per call) against reference and rules | downloaded Colab output | stdout |
| `selfcheck.py` | asserts on `textutil` and `gold` with made-up inputs; no data, no GPU | | `selfcheck OK` |

`paths.py` holds every location, `textutil.py` the three helpers vendored from the label-to-training-data step.

## Environment

| Variable | Default | Holds |
|---|---|---|
| `AML_DATA` | `C:/tmp/aml-data` | `labels/`, `court/raw/` |
| `AML_TRAIN` | `C:/tmp/aml-train` | `data-r12/`, `measurements/{report,court}/`, `models/bert-base-chinese`, `downloads/` |
| `AML_EVAL` | `C:/tmp/aml-eval-r4` | optional evaluator and its `sg-<set>-r17.tsv` |
| `AML_STAGE_WORK` | `AML_TRAIN/stage-clf` | output root when `--out` is not given |

Packages: `requirements.txt` (pinned; Colab keeps its own torch).

## Rebuild the data and the model

```bash
python -I -X utf8 tools/stage-model/selfcheck.py
python -I -X utf8 tools/stage-model/prep_stage.py --out C:/tmp/stage-work      # data/
python -I -X utf8 tools/stage-model/add17.py --verdicts --out C:/tmp/stage-work # data17v/ (add17.py without the flag: data17/)
```

Train and export on Colab with `tools/colab` (a job folder holding `train_stage.py`, `export_onnx.py`, `data/` and a
`run.sh` that runs `train_stage.py --save hf`, then `export_onnx.py hf onnx data dev` and writes `done.txt` after
`ONNX_OK`); package it with `--extra onnx` so the ONNX folder is downloaded.
