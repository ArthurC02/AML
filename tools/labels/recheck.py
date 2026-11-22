"""Recheck of the hardest persons of a development court set by two further blind passes (brief recheck-brief.md).
pick:    persons where at least two of the three readers miss the reference stage (Aml.StageEval's per-person rows)
         -> <dir>/persons.tsv (doc, person id, the set's forms joined by |), the passes' only input besides the texts.
compare: <dir>/a.tsv and b.tsv (doc, person id, stage) against the set's reference: confirmed (both give it), label
         error (both give the same other stage: correct the label, rebuild the set), split (the passes differ).
Held-back sets are never rechecked (their per-person rows are never made). Counts only.

usage: recheck.py pick <set> <eval out dir> <dir>     e.g. pick accept3 <Aml.StageEval out=> <AML_DATA>/labels/v14r
       recheck.py compare <set> <dir>
"""
import collections, json, os, sys
sys.path.insert(0, os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "stage-model"))
import paths, gold

def tsv(p): return [l.rstrip("\n").split("\t") for l in open(p, encoding="utf-8") if l.strip()]


def pick(rows):
    """Keys (doc, person) of Aml.StageEval rows (header first: doc person reference rules classifier llm ...) where
    at least two readers miss the reference."""
    return [(r[0], r[1]) for r in rows[1:] if sum(x != r[2] for x in r[3:6]) >= 2]


def compare(ref, a, b):
    c = collections.Counter()
    for k, s in ref.items():
        x, y = a.get(k), b.get(k)
        c["confirmed" if x == y == s else "label error" if x == y else "split"] += 1
        if x != y and s in (x, y): c["split, one pass gives the reference"] += 1
    return c


def selfcheck():
    rows = [["doc", "person", "reference", "rules", "classifier", "llm"], ["d", "P1", "CONVICTED", "CONVICTED", "INDICTED", "UNKNOWN"],
            ["d", "P2", "CONVICTED", "CONVICTED", "CONVICTED", "INDICTED"]]
    assert pick(rows) == [("d", "P1")]
    ref = {("d", "P1"): "CONVICTED", ("d", "P2"): "INDICTED", ("d", "P3"): "ALLEGED"}
    c = compare(ref, {("d", "P1"): "CONVICTED", ("d", "P2"): "WANTED", ("d", "P3"): "ALLEGED"}, {("d", "P1"): "CONVICTED", ("d", "P2"): "WANTED", ("d", "P3"): "UNKNOWN"})
    assert c == {"confirmed": 1, "label error": 1, "split": 1, "split, one pass gives the reference": 1}, c
    for s in ("Accept5", "V11"):
        try: main(["pick", s, ".", "."]); raise AssertionError(s)
        except SystemExit as e: assert e.code == __doc__, s  # refused before any file is opened
    print("selfcheck OK")


def main(argv):
    if argv == ["--selfcheck"]: return selfcheck()
    if len(argv) < 3 or paths.held([argv[1]]) or argv[0] not in ("pick", "compare"): sys.exit(__doc__)
    docs = {d["doc"]: d for d in map(json.loads, open(paths.COURT + argv[1] + ".jsonl", encoding="utf-8"))}
    if argv[0] == "pick" and len(argv) == 4:
        keys = pick(tsv(os.path.join(argv[2], argv[1] + ".tsv")))
        forms = {(d, p["id"]): "|".join(p["mentions"]) for d, doc in docs.items() for p in gold.suspects(doc)}
        os.makedirs(argv[3], exist_ok=True)
        with open(os.path.join(argv[3], "persons.tsv"), "w", encoding="utf-8", newline="\n") as w:
            for k in keys: w.write("%s\t%s\t%s\n" % (*k, forms[k]))
        print("persons", len(keys), "documents", len({d for d, _ in keys}))
    elif argv[0] == "compare" and len(argv) == 3:
        keys = [tuple(r[:2]) for r in tsv(os.path.join(argv[2], "persons.tsv"))]
        stage = {(d, p["id"]): gold.person_stage(doc, p) for d, doc in docs.items() for p in gold.suspects(doc)}
        a, b = ({tuple(r[:2]): r[2] for r in tsv(os.path.join(argv[2], x + ".tsv"))} for x in "ab")
        print("persons", len(keys), dict(compare({k: stage[k] for k in keys}, a, b)))
    else: sys.exit(__doc__)


if __name__ == "__main__": main(sys.argv[1:])
