# news-model

The pipeline of the two news models that `OnnxNewsModel` reads from `NewsModels:Directory` (tests: `AML_NEWS_MODELS`):
the NER role tagger and the event (EV) classifier. It builds the rows from the labels, builds a Colab job, trains,
exports ONNX and measures native dev and end-to-end F1. What the models learn, the three acceptance gates and the
current candidate: [docs/plan/news-model-training.md](../../docs/plan/news-model-training.md).

The data holds real names. It stays outside the repo; the scripts print counts and hashes only. Run Python isolated
(`python -I -X utf8 ...`).

## Scripts

| Script | Does | Reads | Writes |
|---|---|---|---|
| `prep.py [--round r12\|r11] [--out DIR] [--train-only]` | labels to rows, mirroring `NewsModelReading.Asked`; the split is fixed (`DIR/split.json` must exist) | label dirs of the round, source texts | `DIR/{ner,ev}-{train,dev,test}.jsonl`, `split.json`; stdout = `stats.txt` |
| `build_job.py ev\|ner <name> ...` | a job folder for `tools/colab`, checked (label set, parent rows, every row through the job's reader) | a round dir, start model, `--parent` job, `--add` rows | `AML_COLAB_HOME/job<name>/` |
| `train_ev_reviewed.py` (+ `train_ev.py`, `event_pooling.py`, `gpu_smoke_reviewed.py`) | EV: continue a CLS checkpoint, asked name masked in half the rows; per-epoch dev arrays, last epoch kept | job `data/`, `models/` | job `models/ev/` |
| `train_ner_document.py` (+ `train_ner.py`, `ner_document.py`) | NER: document-role head on a frozen encoder (`train_ner.py` trains the encoder itself) | job `data/`, `models/` | job `models/ner/` |
| `export_one.py <model dir> <out> [aml_input]` | ONNX opset 17 (+ `document-roles.onnx` for NER), config, vocab; checks ORT against torch | native checkpoint | `<out>/` |
| `measure_dev.py ner\|ev <model dir> <out> [--split test --dev-metrics F]` | primary NER / event gate on native weights; test only behind a passing dev record | `data/`, `models/bert-base-chinese` of the cwd | `<out>/<model>-<split>-metrics.json` |
| `score_rows.py <model dir> <rows.jsonl> [thr]` | row-level F1 of an event model on a rows file (made-up or held-out rows) | | stdout |
| `e2e.sh <tag> [pair]` | the working end-to-end measure on both dev sets at 0.30 (builds and runs the .NET evaluator) | `AML_EVAL`, the pair | dumps in `AML_EVAL`, stdout |
| `gold_merge.py dev\|dev2 <dump> [thr] [fixdir]` | scores an evaluator dump against every labelled event (strict, fragment, written-name measure) | labels, split | stdout |
| `selfcheck.py` | asserts on `prep`, `build_job`, `paths` helpers with made-up inputs; no data, no GPU | | `selfcheck OK` |

`jobs/ev/` and `jobs/ner/` hold the `run.sh` and `job.json` templates `build_job.py` fills (`@NAME@`, `@EPOCHS@`,
`@LR@`; job.json gets epochs, lr, initial, sha256 of every file, note). The trainers, `train_ev.py`, `train_ner.py` and
the scorers load the tokenizer from `models/bert-base-chinese` and rows from `data/` **relative to the working
directory**: run them in a job folder (Colab) or in `AML_TRAIN` (export, scoring). They are the files job43 and job20
executed, byte for byte. `measure_dev.py` also needs `data/split.json` in that folder; its experimental event modes
(query predicates, caption, document context) were not retained and are refused.

## Environment

| Variable | Default | Holds |
|---|---|---|
| `AML_DATA` | `C:/tmp/aml-data` | `labels/` (v6 ... v12, `review-current/fix`, v10 = second dev set), `judgments/`, `press/`, `news/` |
| `AML_TRAIN` | `C:/tmp/aml-train` | `data-r11/`, `data-r12/`, `data-dev2/`, `data-synth/`, `models/bert-base-chinese/`, `downloads/`, `measurements/`, `.venv/` |
| `AML_COLAB_HOME` | `C:/tmp/colab/home` | job folders and packages (mounted at `/home/colab` in the colab container) |
| `AML_EVAL` | `C:/tmp/aml-eval-r4` | the .NET evaluator (outside the repo; see below) |
| `AML_PY` | `AML_TRAIN/.venv/Scripts/python.exe` | interpreter `e2e.sh` uses |

Packages: `requirements.txt` (pinned).

## Rebuild what the retained pair was trained on

The retained pair is NER20 / event43 (`AML_TRAIN/measurements/aml43-pair`).

```bash
python -I -X utf8 tools/news-model/selfcheck.py
mkdir -p $OUT && cp $AML_TRAIN/data-r11/split.json $OUT/          # the split is fixed
python -I -X utf8 tools/news-model/prep.py --round r11 --out $OUT > $OUT/stats.txt   # event43's round (r12: + v12)
python -I -X utf8 tools/news-model/build_job.py ev 43 --start $AML_TRAIN/downloads/aml37/models/ev --rows $AML_TRAIN/data-r11 \
  --parent $AML_COLAB_HOME/job37 --add $AML_TRAIN/data-synth/train-{a,b,c}.jsonl --repeat 4      # job43: 1 epoch, lr 1e-5
python -I -X utf8 tools/news-model/build_job.py ner 20 --start <NER19 encoder> --rows <round dir> --parent-counts 2738 395 293
```

job43's `data/ev-train.jsonl` must hash `8b886560...` and its dev rows `1d720141...` (job37's, byte for byte). A label
edit after a round was built changes a rebuild; compare `stats.txt` and the hashes before training on it. NER20's rows
are the 2026-11-14 round (`AML_TRAIN/snapshots/before-review24-corrections-20261114/data`); the current labels no
longer give them. New train-only documents: append their ids to the train list of `split.json` (as for v12), add the
label folder to `paths.ROUND_LABELS` under a new round name.

## Train, export, measure

Training needs the user's yes per run. Small models on **T4**.

```bash
python -I tools/colab/colabjob.py package $AML_COLAB_HOME/job<name> <name>
python -I tools/colab/colabjob.py check <name>                          # ALL OK
bash tools/colab/run_job.sh <name> T4 $AML_TRAIN/downloads/aml<name>    # then read `colab sessions`: no aml session
```

The download holds `out/ev` (last-epoch weights, `diagnostics/epoch-NN.npz` per-epoch dev probabilities) or `out/ner`,
with `logs-*.txt` and `job.json`. Then, from `AML_TRAIN`:

```bash
python -I -X utf8 tools/news-model/export_one.py downloads/aml<name>/out/ev measurements/aml<name>-pair/ev
cp -r measurements/aml43-pair/ner measurements/aml<name>-pair/ner     # or export a new NER the same way
mkdir measurements/aml<name>-pair/cache measurements/aml<name>-pair/cache-dev2   # fresh caches for a new event model
#   (a NER-only cache tags-*.jsonl may be reused only under the same NER, input mode and role head)
bash tools/news-model/e2e.sh <tag> aml<name>-pair
```

`e2e.sh` prints, per dev set, the fragment score and the working measure (written names; F1 = 2TP/(2TP+FP+FN), the gate
is `2TP > 9(FP+FN)`). Row-level dev: `measure_dev.py ev <model> <out>` in a folder holding the job's `data/` plus
`split.json`. Held-out sets (`data-synth/heldout.jsonl`, `data-court/heldout.jsonl`): `score_rows.py`, at the end only.
The test split only through `measure_dev.py --split test --dev-metrics` after all three dev gates pass.

## What is not here

- The .NET evaluator `aml-eval-r4` (`Program.cs`, `aml-eval.csproj`, outside the repo; `AML_EVAL`). It compiles the
  reader from a checkout, reads its documents from paths fixed in `Program.cs` (`dev.jsonl`, `train.jsonl` = the second
  dev set, `evids-dev.jsonl`), and its `profile` / `stage` modes give the Profile-person view. The script that wrote
  its current `dev.jsonl` was not identified (variants of `dump_split.py` in `C:/tmp/aml-eval`).
- `data-dev2/` is built by `AML_TRAIN/prep_dev2.py` with older asking rules; only its `split.json` is read here.
- Download receipts (`verify_completed_run.py`), the old launcher `launch_next.py` (it starts Colab itself),
  `calibrate_e2e.py` (tied to an older metrics layout) and the one-off `analyze_`/`apply_`/`audit_` scripts stay in
  `AML_TRAIN`.
