"""Every data location of the stage-model scripts. Set the environment variables to move them; the defaults are the
paths on the machine the scripts were written on. Directory values end in '/' (the scripts concatenate file names).

  AML_DATA        labelled material (labels/, court/raw/)                  default C:/tmp/aml-data
  AML_TRAIN       training workspace (data-r12/, measurements/, models/)    default C:/tmp/aml-train
  AML_EVAL        outside .NET evaluator aml-eval-r4's folder: its sg-<set>-r17.tsv rows (score.py, score_gemma.py)
                  default C:/tmp/aml-eval-r4
  AML_STAGE_EVAL_OUT  tools/Aml.StageEval's out= folder, <set>.tsv per development set (gold.py verify)
                  default C:/tmp/eval-sys/out (the evaluator's own default)
  AML_STAGE_WORK  where data/, data17/, data17v/ are written               default AML_TRAIN/stage-clf
                  (prep_stage.py and add17.py also take --out DIR)
  AML_JUDGMENTS   tools/judgments: jids.jsonl and the fetched docs/          default C:/tmp/jud
Also imported by tools/labels and tools/judgments.
"""
import os


def _dir(var, default):
    return os.environ.get(var, default).replace("\\", "/").rstrip("/") + "/"


DATA = _dir("AML_DATA", "C:/tmp/aml-data")
TRAIN = _dir("AML_TRAIN", "C:/tmp/aml-train")
EVAL = _dir("AML_EVAL", "C:/tmp/aml-eval-r4")
WORK = _dir("AML_STAGE_WORK", TRAIN + "stage-clf")
JUDGMENTS = _dir("AML_JUDGMENTS", "C:/tmp/jud")
STAGE_EVAL_OUT = _dir("AML_STAGE_EVAL_OUT", "C:/tmp/eval-sys/out")  # tools/Aml.StageEval's out= (gold.py verify)
HELD = {"v11", "accept5"}  # held-back sets: no tool verifies, relabels or looks them up (gold.py, recheck.py, jids.py)


def held(names):
    """The held-back sets among names, compared in lower case (Windows opens Accept5.jsonl as accept5.jsonl)."""
    return sorted({n.lower() for n in names} & HELD)


assert held(["Accept5", "V11", "dev"]) == ["accept5", "v11"]

REPORT = TRAIN + "measurements/report/"   # <set>-stage.jsonl: the evaluator's reference documents
COURT = TRAIN + "measurements/court/"     # court press-release evaluation sets <set>.jsonl
R12 = TRAIN + "data-r12/"                 # prep_r12 output: split.json, ner-train.jsonl, ev-train.jsonl
DOWNLOADS = TRAIN + "downloads/"          # Colab job downloads (score_one.py)
BERT = TRAIN + "models/bert-base-chinese"  # tokenizer used for the token budget
LABELS = DATA + "labels/"
COURT_RAW = DATA + "court/raw/"           # <doc>.main.txt of gathered court releases (add17.py)
V17 = LABELS + "v17/"                     # two blind label passes a/, b/ (add17.py)
# prep_r12.LABEL_DIRS, in rank order (a later folder's label file wins); prep_stage.py appends v10/ver.
LABEL_DIRS = [LABELS + d for d in ("v6/ver/", "v6/npa/fin/", "v7/ver/", "v8/ver/", "v9/ver/", "v11/ver/", "v12/ver/", "review-current/fix/")]


def out_dir(argv):
    """The folder after --out on the command line, else AML_STAGE_WORK."""
    return argv[argv.index("--out") + 1].replace("\\", "/").rstrip("/") + "/" if "--out" in argv else WORK
