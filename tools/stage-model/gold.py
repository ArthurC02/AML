"""Reference legal stage of an annotated suspect: a port of the tools/Aml.NewsEval Program.cs `stage` mode (Gold + `mine`).

A suspect is a people entry with role PER-* and party Suspect. Its events are the document's event rows whose
`mention` is one of its forms (exact string; persons sharing a form share rows). Rows carrying "prior" are left
out whole; labels with a '-' (negated, requested, ...) are ignored; the highest step wins.

usage: gold.py verify [out=<dir>]        -> agreement with tools/Aml.StageEval on the development *-stage.jsonl files.
       gold.py verify court [out=<dir>]  -> the same on measurements/court/accept, accept3, accept4.jsonl.
Counts only. The held-back sets (v11, accept5) are never verified. verify reads the per-person rows Aml.StageEval wrote
(<out>/<set>.tsv, column reference = ReferenceStage.Of, the same derivation; default AML_STAGE_EVAL_OUT, see paths.py),
so run the evaluator first; exit 1 when a file's header is not the evaluator's, or a set agrees on fewer than all its
persons, or its row count differs (stale or partial rows). The rows must come from the current tools/Aml.StageEval (header
ending in the judgment column); rows from an older evaluator fail the header check on purpose: re-run the evaluator.
Any other command prints this and exits 1. stage() and person_stage() need nothing.
"""
import json, os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # python -I leaves the script folder off the path
import paths

STAGES = ["UNKNOWN", "ALLEGED", "WANTED", "INDICTED", "DISMISSED", "CONVICTED", "ACQUITTED"]
_ORDER = [("Conviction", "CONVICTED"), ("Acquittal", "ACQUITTED"), ("NonIndictment", "DISMISSED"),
          ("Indictment", "INDICTED"), ("WantedNotice", "WANTED")]


def stage(labels):
    l = {x for x in labels if "-" not in x and x != "prior"}
    return next((s for k, s in _ORDER if k in l), "ALLEGED" if l else "UNKNOWN")


def suspects(doc):
    return [p for p in doc["people"] if p["role"].startswith("PER-") and p["party"] == "Suspect"]


def person_stage(doc, p):
    ms = set(p["mentions"])
    return stage(x for e in doc["events"] if e["mention"] in ms and "prior" not in e["labels"] for x in e["labels"])


REPORT = paths.REPORT
SETS = ["dev", "dev2", "v9", "v11"]


COURT = paths.COURT
COURT_SETS = ["accept", "accept3", "accept4", "accept5"]
HEADER = "doc\tperson\treference\trules\tclassifier\tllm\tvote\tbasis\tjudgment"  # tools/Aml.StageEval/Program.cs


def verify(court=False, out=None):
    """The reference column of Aml.StageEval's per-person rows (<out>/<set>.tsv: doc, person, reference, ...; written for
    the development sets only) against person_stage, both ways: every suspect of the set and every row of the evaluator.
    court=True: the court press-release sets (measurements/court/<set>.jsonl). False when a file's header is not the
    evaluator's, a set agrees on fewer than all its persons or its row count differs (stale or partial rows)."""
    out = out or paths.STAGE_EVAL_OUT; ok = True
    for s in [s for s in (COURT_SETS if court else SETS) if not paths.held([s])]:  # held-back sets are skipped
        docs = [json.loads(l) for l in open(COURT + s + ".jsonl" if court else REPORT + s + "-stage.jsonl", encoding="utf-8")]
        mine = {(d["doc"], p["id"]): person_stage(d, p) for d in docs for p in suspects(d)}
        n_rows = sum(len(suspects(d)) for d in docs)
        lines = [l.rstrip("\r\n").split("\t") for l in open(os.path.join(out, s + ".tsv"), encoding="utf-8")]
        if not lines or "\t".join(lines[0]) != HEADER: print(f"{s}: FAIL header is not Aml.StageEval's"); ok = False; continue
        rows = lines[1:]
        ev = {(f[0], f[1]): f[2].upper() for f in rows}
        agree = sum(ev.get(k) == v for k, v in mine.items())
        good = agree == len(mine) and len(rows) == n_rows
        ok &= good
        print(f"{s}: documents {len(docs)} suspects {len(mine)} (rows {n_rows}) | evaluator rows {len(rows)} | agrees {agree}/{len(mine)} "
              f"| rows unknown to port {len(set(ev) - set(mine))}" + ("" if good else " | FAIL"))
    return ok


if __name__ == "__main__":
    assert stage(["Indictment", "Conviction-Negated"]) == "INDICTED" and stage(["prior"]) == "UNKNOWN" and stage(["Search"]) == "ALLEGED"
    opt = [a for a in sys.argv[1:] if a.startswith("out=")]
    args = [a for a in sys.argv[1:] if a not in opt]
    if args in (["verify"], ["verify", "court"]): sys.exit(0 if verify(court=args[1:] == ["court"], out=opt[0][4:] if opt else None) else 1)
    sys.exit(__doc__)
