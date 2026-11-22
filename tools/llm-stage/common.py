"""Locations and the pure helpers shared by build_job.py, sft.py and selfcheck.py. Data locations come from
tools/stage-model/paths.py (AML_DATA, AML_TRAIN, AML_STAGE_WORK, ...) plus

  AML_COLAB_HOME  where job folders and packages go (mounted at /home/colab in the colab container)   default C:/tmp/colab/home
  AML_LLM_WORK    where sft.py writes data/train.jsonl                                               default AML_TRAIN/gemma-ft
"""
import json, os, pathlib, sys

HERE = pathlib.Path(__file__).resolve().parent
REPO = HERE.parents[1]
sys.path.insert(0, str(REPO / "tools" / "stage-model"))  # python -I leaves script folders off the path
import gold, paths  # noqa: E402

HOME = pathlib.Path(os.environ.get("AML_COLAB_HOME", "C:/tmp/colab/home"))
SFT = pathlib.Path(os.environ.get("AML_LLM_WORK", paths.TRAIN + "gemma-ft")) / "data" / "train.jsonl"
PROMPT_FILE = REPO / "src" / "Aml.Infra" / "Models" / "stage-prompt.txt"  # the runtime prompt, as measured
ROWS = paths.WORK + "data17v/train.jsonl"  # the classifier's train rows; sft.py makes one example per row
JOBS = HERE / "jobs"
# infer.py's order: the six development sets, then the held-back v11 and accept5 (computed, never looked at)
SETS = ["dev", "dev2", "v9", "accept", "accept3", "accept4", "v11", "accept5"]
LONG, HEAD, TAIL, CUT_MARK = 16000, 10000, 6000, "\n（中間省略）\n"  # as OllamaStageReader and infer.py cut


def source(s):
    """The measured file of set s (the evaluator's reference documents)."""
    return pathlib.Path(gold.COURT + s + ".jsonl" if s in gold.COURT_SETS else gold.REPORT + s + "-stage.jsonl")


def cut(text): return text[:HEAD] + CUT_MARK + text[-TAIL:] if len(text) > LONG else text


def forms(p):
    """A suspect's distinct mentions of 2+ characters in first-occurrence order (all mentions if none is that long)."""
    ms = list(dict.fromkeys(p["mentions"])); long = [m for m in ms if len(m) >= 2]
    return long or ms


def user_turn(prompt, pid, fs, text):
    """The runtime prompt for one person (infer.py builds the same string per call)."""
    return prompt.replace("{{PERSONS}}", "%s：%s" % (pid, "、".join(fs))).replace("{{TEXT}}", text)


def data_line(d):
    """One infer.py input line for a measured document: {doc, text, persons: [{id, forms}]}; '' without a suspect.
    No stage, label or event goes in."""
    persons = [{"id": str(p["id"]), "forms": forms(p)} for p in gold.suspects(d)]
    return json.dumps({"doc": d["doc"], "text": d["text"], "persons": persons}, ensure_ascii=False) + "\n" if persons else ""


def eval_bytes(s):
    return "".join(data_line(json.loads(l)) for l in open(source(s), encoding="utf-8")).encode("utf-8")
