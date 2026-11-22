"""Train rows from v17: 73 gathered court releases labelled twice, blind (labels/v17/a, b), persons both passes give the
same stage only. Writes data17/ = data/ with train.jsonl extended by those rows (every other file copied unchanged).
A v17 document is dropped when it shares an id, a text or 10-gram overlap >= 0.2 with any evaluation document
(report and court sets). Counts only.
usage: add17.py [--verdicts] [--out DIR]   reads DIR/data/, writes DIR/data17/ (default AML_STAGE_WORK, see paths.py)"""
import collections, glob, json, os, shutil, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import paths, gold
from prep_stage import row

EV = {"CONVICTED": ["Conviction"], "ACQUITTED": ["Acquittal"], "DISMISSED": ["NonIndictment"], "INDICTED": ["Indictment"],
      "WANTED": ["WantedNotice"], "ALLEGED": ["Investigation"], "UNKNOWN": []}
R = paths.V17
# --verdicts: only documents both passes call a verdict or a ruling (no hearing notices), written to data17v/.
VERDICTS = "--verdicts" in sys.argv
BASE = paths.out_dir(sys.argv)
DATA = BASE + "data"
OUT = BASE + ("data17v" if VERDICTS else "data17")


def load(d): return {os.path.basename(f)[:-5]: json.load(open(f, encoding="utf-8-sig")) for f in glob.glob(R + d + "/jy-*.json")}
def grams(t): return {t[i:i + 10] for i in range(len(t) - 9)}


def main():
    A, B = load("a"), load("b")
    evdocs = []
    for s in gold.SETS: evdocs += [json.loads(l) for l in open(gold.REPORT + s + "-stage.jsonl", encoding="utf-8")]
    for s in gold.COURT_SETS: evdocs += [json.loads(l) for l in open(gold.COURT + s + ".jsonl", encoding="utf-8")]
    evids = {d["doc"] for d in evdocs}; evg = [grams(d["text"]) for d in evdocs]
    docs, c = [], collections.Counter()
    for doc in sorted(set(A) & set(B)):
        if VERDICTS and not (A[doc]["kind"] in ("verdict", "ruling") and B[doc]["kind"] == A[doc]["kind"]): c["dropped kind"] += 1; continue
        t = open(paths.COURT_RAW + doc + ".main.txt", encoding="utf-8-sig").read()
        g = grams(t)
        if doc in evids or any(len(g & e) / max(1, min(len(g), len(e))) >= 0.2 for e in evg): c["dropped overlap"] += 1; continue
        people, events = [], []
        for p in A[doc]["persons"]:
            forms = [f for f in p["forms"] if f and f in t]
            if not forms: continue
            q = [x for x in B[doc]["persons"] if set(x["forms"]) & set(p["forms"])]
            if not q: c["one pass only"] += 1; continue
            if q[0]["stage"] != p["stage"]: c["stage differs"] += 1; continue
            c["agreed"] += 1; c[p["stage"]] += 1
            pid = "P%d" % (len(people) + 1)
            people.append({"id": pid, "role": "PER-Suspect", "party": "Suspect", "mentions": forms, "descriptors": []})
            if EV[p["stage"]]: events.append({"mention": forms[0], "sentence": "", "labels": EV[p["stage"]]})
        if people: docs.append({"doc": doc, "old": False, "text": t, "ids": [], "people": people, "events": events})
    rows = [row(d, p) for d in docs for p in gold.suspects(d)]
    assert all(r["stage"] in EV for r in rows)
    print("v17 docs labelled by both", len(set(A) & set(B)), "| with agreed persons", len(docs), "| rows", len(rows), dict(c))
    print("row stages", dict(collections.Counter(r["stage"] for r in rows)))
    if os.path.exists(OUT): shutil.rmtree(OUT)
    shutil.copytree(DATA, OUT)
    with open(OUT + "/train.jsonl", "a", encoding="utf-8") as w:
        for r in rows: w.write(json.dumps(r, ensure_ascii=False) + "\n")
    n = sum(1 for _ in open(OUT + "/train.jsonl", encoding="utf-8")); n0 = sum(1 for _ in open(DATA + "/train.jsonl", encoding="utf-8"))
    print("train rows", n0, "->", n)


if __name__ == "__main__": main()
