# stage-model

The person-stage classifier pipeline: build the rows, train bert-base-chinese on Colab, export ONNX, score against the
rules and the LLM runs. Why and how it is judged: [docs/plan/stage-model-training.md](../../docs/plan/stage-model-training.md).

The data holds real names. It stays outside the repo; the scripts print counts only. Run Python isolated
(`python -I -X utf8 ...`).

## Scripts

| Script | Does | Reads | Writes |
|---|---|---|---|
| `prep_stage.py [--out DIR] [--nonsuspects [--officials N]]` | one row per (document, suspect): name, evidence with the person's forms marked, reference stage | `data-r12/`, label dirs, `measurements/report`, `measurements/court`, tokenizer | `DIR/data/<set>.jsonl`, `stats.txt`, `stats-court.txt` |
| `add17.py [--verdicts] [--out DIR]` | `data/` plus the v17 double-labelled court releases (agreed persons only, overlap with evaluation dropped) | `DIR/data/`, `labels/v17/a,b`, `court/raw/` | `DIR/data17/` (`--verdicts`: `DIR/data17v/`, verdicts and rulings only) |
| `train_stage.py [--heldback]` | fine-tune, predictions per epoch on the development sets (`--sets`, default `dev,dev2,v9`); standalone (runs on Colab from a folder holding it and `data/`). The held-back v11 is never in `--sets`: `--heldback` evaluates it once after the last epoch, aggregate counts only, no file | `data/` | `out/pred-<set>-e<k>.jsonl` (development sets only), `--save DIR` weights |
| `export_onnx.py <saved> <out> [<data> <set>]` | ONNX opset 17 + tokenizer + `labels.json`; checks 20 rows against PyTorch, last line `ONNX_OK` | saved model | `<out>/model.onnx` ... |
| `gold.py verify [court] [out=<dir>]` | agreement of the reference port with `tools/Aml.StageEval`'s reference column (`ReferenceStage.Of`, the same derivation), every suspect and every row; the held-back v11 and accept5 are skipped. Run the current evaluator first (its header ends in the `judgment` column; rows from an older evaluator fail the header check on purpose, so re-run it); `out=` is its `out=` folder | development sets, `<out>/<set>.tsv` | stdout |
| `score.py <pred> <sg.tsv>` | classifier against the rules on the same persons | | stdout |
| `score_gemma.py <out dir> [--heldback]` / `score_one.py <out dir> [--heldback]` | LLM runs (all persons / one person per call) against reference and rules, development sets only; `--heldback` adds the held-back v11 (and accept5), aggregate counts only, once the design is fixed | downloaded Colab output | stdout |
| `selfcheck.py` | asserts on `textutil` and `gold` with made-up inputs; no data, no GPU | | `selfcheck OK` |

`--nonsuspects` adds to `train.jsonl` one `UNKNOWN` (no stage) row per other named person of the train documents
(Victim, Official, Witness, Other), built as a suspect's row is. Left out: a person sharing a form with a suspect of the
document, a Witness with a labelled event, a person with no form in the text. The rows are capped at the suspect row
count (Victim and Other first, Witness and Official drawn with a fixed seed); `--officials N` replaces that cap with
every Victim, Other and Witness plus a seeded sample of N Officials. The development and court files are unchanged, and
without the options the output is byte-identical to the rows aml47 was trained on. Outcome: job58 (`--nonsuspects`) and
job59 (`--officials 1500`) were trained and measured; both lower unanimous precision on suspects, so neither is adopted
([non-suspects](../../docs/plan/stage-model-training.md#persons-who-are-not-suspects)).

`paths.py` holds every location (also imported by `tools/labels` and `tools/judgments`), `textutil.py` the three helpers vendored from the label-to-training-data step.

## Environment

| Variable | Default | Holds |
|---|---|---|
| `AML_DATA` | `C:/tmp/aml-data` | `labels/`, `court/raw/` |
| `AML_TRAIN` | `C:/tmp/aml-train` | `data-r12/`, `measurements/{report,court}/`, `models/bert-base-chinese`, `downloads/` |
| `AML_EVAL` | `C:/tmp/aml-eval-r4` | the outside evaluator's `sg-<set>-r17.tsv` rows (`score.py`, `score_gemma.py`) |
| `AML_STAGE_EVAL_OUT` | `C:/tmp/eval-sys/out` | `tools/Aml.StageEval`'s `out=` folder (`gold.py verify` without `out=`) |
| `AML_JUDGMENTS` | `C:/tmp/jud` | `tools/judgments`: `jids.jsonl` and the fetched `docs/` |
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
