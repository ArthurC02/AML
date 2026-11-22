"""Classifier against the rules on the same persons. Counts only.
usage: score.py out/pred-<set>-e<k>.jsonl <AML_EVAL>/sg-<set>-r17.tsv
sg-*.tsv lists exactly the suspects where the rules' stage differed from the reference (evaluator stageout), so a
person absent from it had the rules right. Columns used: doc, person id, reference, rule stage."""
import collections, json, sys

pred = [json.loads(l) for l in open(sys.argv[1], encoding="utf-8")]
wrong = {}
for l in open(sys.argv[2], encoding="utf-8"):
    f = l.rstrip("\n").split("\t"); wrong[(f[0], f[1])] = (f[2].upper(), f[3].upper())
keys = {(r["doc"], r["person"]) for r in pred}
missing = set(wrong) - keys
assert all(wrong[(r["doc"], r["person"])][0] == r["gold"] for r in pred if (r["doc"], r["person"]) in wrong), "reference differs from the tsv"
c = collections.Counter()
for r in pred:
    clf, rule = r["pred"] == r["gold"], (r["doc"], r["person"]) not in wrong
    for k in ("all", r["kind"]):
        c[(k, "n")] += 1; c[(k, "clf")] += clf; c[(k, "rule")] += rule
        c[(k, "both" if clf and rule else "only rules" if rule else "only classifier" if clf else "neither")] += 1
print("persons %d | tsv rows not in the prediction file %d" % (len(pred), len(missing)))
for k in ["all"] + sorted({r["kind"] for r in pred}):
    print("%-14s n %4d | classifier right %4d | rules right %4d | both %d, only rules %d, only classifier %d, neither %d"
          % (k, c[(k, "n")], c[(k, "clf")], c[(k, "rule")], c[(k, "both")], c[(k, "only rules")], c[(k, "only classifier")], c[(k, "neither")]))
