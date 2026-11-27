"""Every data location of the news-model scripts. Set the environment variables to move them; the defaults are the
paths on the machine the scripts were written on. Directory values end in '/' (the scripts concatenate file names).

  AML_DATA        labelled material (labels/, judgments/, press/, news/)            default C:/tmp/aml-data
  AML_TRAIN       training workspace (data-r*/, data-dev2/, data-synth/, models/,
                  downloads/, measurements/)                                         default C:/tmp/aml-train
  AML_EVAL        tools/Aml.NewsEval's working folder outside the repo: its documents
                  in, dumps out (e2e.sh)                                             default C:/tmp/aml-eval-r4
  AML_COLAB_HOME  where build_job.py writes job<name>/ (tools/colab reads it there) default C:/tmp/colab/home
"""
import os


def _dir(var, default):
    return os.environ.get(var, default).replace("\\", "/").rstrip("/") + "/"


DATA = _dir("AML_DATA", "C:/tmp/aml-data")
TRAIN = _dir("AML_TRAIN", "C:/tmp/aml-train")
EVAL = _dir("AML_EVAL", "C:/tmp/aml-eval-r4")
COLAB_HOME = _dir("AML_COLAB_HOME", "C:/tmp/colab/home")

LABELS = DATA + "labels/"
JD, PR, NEWS = DATA + "judgments/", DATA + "press/", DATA + "news/"   # <doc>.main.txt (press: .para.txt first)
BERT = TRAIN + "models/bert-base-chinese/"   # base model and tokenizer copied into every job
DEV2 = TRAIN + "data-dev2/"                  # second dev set: split.json (labels v10/ver)

# Label folders of each data round, in rank order (a later folder's label file wins). r11 built the rows of the
# retained event model (job43); r12 adds the v12 train-only documents (their ids were appended to the train list of
# split.json before the build). Train only from v7 on; dev corrections in review-current/fix.
_R11 = ["v6/ver/", "v6/npa/fin/", "v7/ver/", "v8/ver/", "v9/ver/", "v11/ver/", "review-current/fix/"]
ROUND_LABELS = {"r11": [LABELS + d for d in _R11],
                "r12": [LABELS + d for d in _R11[:-1] + ["v12/ver/", _R11[-1]]]}
ROUND = "r12"                                # the latest round; gold_merge.py reads its split.json


def round_dir(name):
    return TRAIN + "data-" + name + "/"
