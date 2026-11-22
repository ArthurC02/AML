"""Every data location of the stage-model scripts. Set the environment variables to move them; the defaults are the
paths on the machine the scripts were written on. Directory values end in '/' (the scripts concatenate file names).

  AML_DATA        labelled material (labels/, court/raw/)                  default C:/tmp/aml-data
  AML_TRAIN       training workspace (data-r12/, measurements/, models/)    default C:/tmp/aml-train
  AML_EVAL        optional .NET evaluator aml-eval-r4 (gold.py verify, sg-*.tsv)   default C:/tmp/aml-eval-r4
  AML_STAGE_WORK  where data/, data17/, data17v/ are written               default AML_TRAIN/stage-clf
                  (prep_stage.py and add17.py also take --out DIR)
"""
import os


def _dir(var, default):
    return os.environ.get(var, default).replace("\\", "/").rstrip("/") + "/"


DATA = _dir("AML_DATA", "C:/tmp/aml-data")
TRAIN = _dir("AML_TRAIN", "C:/tmp/aml-train")
EVAL = _dir("AML_EVAL", "C:/tmp/aml-eval-r4")
WORK = _dir("AML_STAGE_WORK", TRAIN + "stage-clf")

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
