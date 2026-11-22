"""Gemma (job45) against the reference and against the rules on the same persons. Counts only, never names or quotes.
usage: score_gemma.py <out dir> [--heldback]   (e.g. AML_TRAIN/downloads/aml45/out: one folder per model, <set>.jsonl inside)
Development sets only; --heldback also scores the held-back v11 (the same aggregate counts, nothing per person), only
once the design is fixed.
Persons = every suspect (gold.suspects) of every document of measurements/report/<set>-stage.jsonl that has one;
reference = gold.person_stage. Rules: sg-<set>-r17.tsv lists exactly the suspects the rules got wrong (doc, person id,
reference, rule stage); everyone else the rules got right. A person whose document is absent from the output counts as
ABSENT (wrong). Kind of document = prep_stage.kind."""
import collections, json, os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import gold, paths
from prep_stage import kind

REPORT, EVAL = paths.REPORT, paths.EVAL
SETS = ["dev", "dev2", "v9", "v11"]
ORDER = gold.STAGES + ["MISSING", "ABSENT"]


def score(path, s):
    docs = [d for d in (json.loads(l) for l in open(REPORT + s + "-stage.jsonl", encoding="utf-8")) if gold.suspects(d)]
    ref = {(d["doc"], str(p["id"])): gold.person_stage(d, p) for d in docs for p in gold.suspects(d)}
    kinds = {d["doc"]: kind(d["doc"], d["text"]) for d in docs}
    wrong = {}
    if os.path.exists(EVAL + "sg-%s-r17.tsv" % s):
        for l in open(EVAL + "sg-%s-r17.tsv" % s, encoding="utf-8"):
            f = l.rstrip("\n").split("\t"); wrong[(f[0], f[1])] = (f[2].upper(), f[3].upper())
    assert all(ref.get(k) == v[0] for k, v in wrong.items()), "tsv reference differs from gold.py"
    out = {}
    for l in open(path, encoding="utf-8"):
        try: r = json.loads(l)
        except ValueError: continue  # torn last line of a partial download
        out[r["doc"]] = r
    got = {(r["doc"], p["id"]): p for r in out.values() for p in r["persons"]}
    c = collections.Counter(); conf = collections.Counter()
    for k, g in ref.items():
        p = got.get(k); st = p["stage"] if p else "ABSENT"; kd = kinds[k[0]]
        gm, ru = st == g, k not in wrong
        conf[(g, st)] += 1
        for x in ("all", kd):
            c[x, "n"] += 1; c[x, "gemma"] += gm; c[x, "rules"] += ru
            c[x, "both" if gm and ru else "only rules" if ru else "only gemma" if gm else "neither"] += 1
        c["missing"] += st == "MISSING"; c["absent"] += st == "ABSENT"
        if p and st not in ("UNKNOWN", "MISSING"):
            c["answered"] += 1; c["quote_ok"] += p["quote_ok"]; c["right quote_ok" if p["quote_ok"] else "right quote_bad"] += gm
    extra = len(set(got) - set(ref))
    print("  [%s] documents %d in output %d (cut %d, with error %d) | persons %d | not in reference %d | MISSING %d | document absent %d"
          % (s, len(docs), sum(d["doc"] in out for d in docs), sum(r["cut"] for r in out.values()), sum(r["error"] is not None for r in out.values()),
             len(ref), extra, c["missing"], c["absent"]))
    for x in ["all"] + sorted(set(kinds.values())):
        print("    %-14s n %4d | gemma right %4d | rules right %4d | both %d, only rules %d, only gemma %d, neither %d"
              % (x, c[x, "n"], c[x, "gemma"], c[x, "rules"], c[x, "both"], c[x, "only rules"], c[x, "only gemma"], c[x, "neither"]))
    a = c["answered"]; qb = a - c["quote_ok"]
    print("    non-UNKNOWN answers %d, quote_ok %d (%.3f) | right with quote_ok %d/%d, right without %d/%d"
          % (a, c["quote_ok"], c["quote_ok"] / max(1, a), c["right quote_ok"], c["quote_ok"], c["right quote_bad"], qb))
    cols = [x for x in ORDER if any(conf[(g, x)] for g in ORDER)]
    print("    reference \\ gemma " + " ".join("%9s" % x[:9] for x in cols))
    for g in [x for x in gold.STAGES if any(conf[(x, y)] for y in cols)]:
        print("    %-18s " % g + " ".join("%9d" % conf[(g, x)] for x in cols))


def main():
    args = [a for a in sys.argv[1:] if a != "--heldback"]
    if len(args) != 1: sys.exit(__doc__)
    root, heldback = args[0], "--heldback" in sys.argv
    for m in sorted(os.listdir(root)):
        if not os.path.isdir(os.path.join(root, m)): continue
        print("model", m)
        for s in [s for s in SETS if heldback or not paths.held([s])]:
            p = os.path.join(root, m, s + ".jsonl")
            if os.path.exists(p): score(p, s)
            else: print("  [%s] no output" % s)


if __name__ == "__main__":
    main()
