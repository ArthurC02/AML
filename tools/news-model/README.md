# news-model

Start with [docs/plan/model-training.md](../../docs/plan/model-training.md) if you are new to the model work.

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
| `e2e.sh <tag> [pair]` | the working end-to-end measure on both dev sets at 0.40 (builds and runs `tools/Aml.NewsEval`) | documents in `AML_EVAL`, the pair | dumps in `AML_EVAL`, stdout |
| `gold_merge.py dev\|dev2 <dump> [thr] [fixdir]` | scores an evaluator dump against every labelled event (strict, fragment, written-name measure) | labels, split | stdout |
| `selfcheck.py` | asserts on `prep`, `build_job`, `paths` helpers with made-up inputs; no data, no GPU | | `selfcheck OK` |

`jobs/ev/` and `jobs/ner/` hold the `run.sh` and `job.json` templates `build_job.py` fills (`@NAME@`, `@EPOCHS@`,
`@LR@`; job.json gets epochs, lr, initial, sha256 of every file, note). The trainers, `train_ev.py`, `train_ner.py` and
the scorers load the tokenizer from `models/bert-base-chinese` and rows from `data/` **relative to the working
directory**: run them in a job folder (Colab) or in `AML_TRAIN` (export, scoring). They are the exact files the
retained pair's jobs (job43, job20) ran. `measure_dev.py` also needs `data/split.json` in that folder; its experimental event modes
(query predicates, caption, document context) were not retained and are refused.

## Environment

| Variable | Default | Holds |
|---|---|---|
| `AML_DATA` | `C:/tmp/aml-data` | `labels/` (v6 ... v12, `review-current/fix`, v10 = second dev set), `judgments/`, `press/`, `news/` |
| `AML_TRAIN` | `C:/tmp/aml-train` | `data-r11/`, `data-r12/`, `data-dev2/`, `data-synth/`, `models/bert-base-chinese/`, `downloads/`, `measurements/`, `.venv/` |
| `AML_COLAB_HOME` | `C:/tmp/colab/home` | job folders and packages (mounted at `/home/colab` in the colab container) |
| `AML_EVAL` | `C:/tmp/aml-eval-r4` | the working folder of `tools/Aml.NewsEval`, outside the repo: its documents (`dev.jsonl`, `train.jsonl`, `evids-dev.jsonl`) and the dumps it writes |
| `AML_PY` | `AML_TRAIN/.venv/Scripts/python.exe` | interpreter `e2e.sh` uses |

Packages: `requirements.txt` (pinned), installed in the venv `AML_PY` points to. Jobs run through
[tools/colab](../colab/README.md) (its prerequisites: the `colab` container signed in with your own account), on a T4.
`e2e.sh` needs `bash`, the .NET SDK of `global.json` and the evaluator's documents in `AML_EVAL`. The evaluator itself
is `tools/Aml.NewsEval` (built by CI with the solution); `e2e.sh` builds it from this checkout.

## The evaluator, tools/Aml.NewsEval

`dotnet tools/Aml.NewsEval/bin/Release/net10.0/Aml.NewsEval.dll <pair dir> <split> <mode> [key=value ...]` runs the
repository's `OnnxNewsModel` and `NewsModelReading` over annotated documents and prints counts and rates only. Its working
folder is `out=<dir>`, else `AML_EVAL`, outside the repo; it refuses to start without one. The split (`dev`, `train` = the
second dev set, default `test`) selects `<split>.jsonl` in that folder unless `docs=<file>` is given; every relative
path argument (`dump=`, `stageout=`, `rowdump=`, `fpout=`, `fnout=`) and `net-ids.json` are written there, so pass the
pair and caches as absolute paths. Modes: `e2e` (what `e2e.sh` runs), `profile` (each labelled suspect in turn as the
Profile person), `stage` (the rules' legal stage against the annotated one; `stageout=sg-<set>-r17.tsv` writes the rows
`tools/stage-model/score.py` reads), `idents`, `stated`, `frag`, `tagcache`, `ids`, `evids`, `rowdump=`, `bench`, `read`;
without a mode it reports the tagger and the events per row. `cached` reads tagger spans and event answers through
`cache=<dir>` (`tags-<split>.jsonl`, `evcache-<split>.tsv`; new answers are appended).

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
are the 2026-11-14 round (`AML_TRAIN/snapshots/before-review24-corrections-20261114/data`), which today's labels do not
rebuild. New train-only documents: append their ids to the train list of `split.json` (as for v12), add the
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

## Guards

Older appliers, experiment launchers in `AML_TRAIN` and generic preparation can rebuild held-out files or read the
test split on their own: inspect their side effects and pinned inputs before running one. The retained pair is
NER20 / event43; its frozen inputs (paths relative to `AML_TRAIN`) hash:

| Input | SHA-256 |
|---|---|
| `data-r8/ev-train.jsonl` | `ae2af2841d3e737afdf6144cd4bb2bb4a94b692ee22ccb8a1469fca51f93175f` |
| `data-r8/ev-dev.jsonl` | `909ba30ba48996a39c4559ec43da19dd2f29fcc3f306fc4c7d37ebf98c6664b4` |
| `data-r8/split.json` | `ca5d73ccda03f0b18149aeee76c62f04581fbefcbaeb8a2250b0348d522230a9` |
| job43 `data/ev-train.jsonl` (176,097 rows) | `8b88656042d194a305a4fa2f33356ddf7be9830ddb16b6f0e9ab843d988bff94` |
| `data-synth/heldout.jsonl` | `735067244d73e7b1b30dc968ec144acaa752aa81bd8cf79488a607eb6d53ccc8` |
| `data-court/heldout.jsonl` | `9a578359ec7caeb119b2b3f37fc96f43ee4d90d4f1a8bb05615be52d044de44e` |
| `downloads/aml37/models/ev/model.safetensors` (job43's start) | `1b5fb6d8a094c011edb6992e3ce369d53831bf75204b12927963dc5b4c9dee44` |
| `downloads/aml43/models/ev/model.safetensors` | `d14c8be982e1e48877de5ede558cd1010d9b93fbe7c972953d2fbec5d3584304` |
| `measurements/aml43-pair/ev/model.onnx` | `2a21be0c03a3db7a4b7e0cb9953299b369817a1630d997bad2bcd4697c5bd422` |
| `measurements/aml43-pair/ner/model.onnx` | `5fe102bb56cb0a5025c334888f178c0f5ab0940dff7134088affb491d90d9df1` |

The runtime side of the contract is guarded by `tests/Aml.Tests/Unit/Extraction/NewsModelTokensTests.cs` (tokens,
offsets, role adjustment) and `tests/Aml.Tests/Unit/Screening/NewsModelReadingTests.cs` (context, anchor and caption
behaviour, the same document forwarded). `INewsModel` has a document-aware overload beside the pair-only one: the
source text is an inference input for models that use document context, not a decision input, and pair-only models
keep their behaviour.

## What is not here

- The evaluator's documents (`dev.jsonl`, `train.jsonl` = the second dev set, `evids-dev.jsonl` in `AML_EVAL`). Which
  script wrote `dev.jsonl` is not recorded, so that file cannot be rebuilt from here; it travels in the data bundle
  (`tools/data`, root `aml-eval`, with the `sg-<set>-r17.tsv` rows; personal data, never git), as does the retained
  pair `aml43-pair` (`ner/`, `ev/`) without its caches `cache/` and `cache-dev2/`, which `Aml.NewsEval ... tagcache`
  rebuilds ([tools/data](../data/README.md#restore-on-another-host)).
- The evaluator folder's other wrappers and scripts (`bothc.sh`, `bothp.sh`, `stage5.sh` for older pairs, one-off
  `check_*`/`audit_*`/`review_*` analyses): no repository tool calls them; `e2e.sh` replaces `both.sh`, and the modes
  above cover the others.
- `data-dev2/` is built by `AML_TRAIN/prep_dev2.py` with older asking rules; only its `split.json` is read here.
- Download receipts (`verify_completed_run.py`), the old launcher `launch_next.py` (it starts Colab itself),
  `calibrate_e2e.py` (tied to an older metrics layout) and the one-off `analyze_`/`apply_`/`audit_` scripts stay in
  `AML_TRAIN`.
