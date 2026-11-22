"""End-to-end score of an evaluator dump against every labelled (doc, party, label) of a dev set, not only the asked
ones. Counts only. Prints the strict score, the fragment score and the working measure (persons written identically
are one name), then the misses by label.
Usage: gold_merge.py dev|dev2 <dump.tsv> [threshold] [fixdir]
  dev:  the 161 dev documents of the round's split.json, labels v6/ver, v6/npa/fin, v7/ver (+ fixdir, the dev corrections)
  dev2: the second dev set (data-dev2/split.json), labels v10/ver
(The former measurements/audit-e2e31/full_gold_merge.py and measurements/select10/full_gold2_merge.py.)"""
import collections, glob, hashlib, json, os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import paths, prep
which, dump = sys.argv[1], sys.argv[2]; thr = float(sys.argv[3]) if len(sys.argv) > 3 else 0.3; fixdir = sys.argv[4] if len(sys.argv) > 4 else None
assert which in ("dev", "dev2"), "first argument: dev or dev2"
if which == "dev":
    split = json.load(open(paths.round_dir(paths.ROUND) + "split.json")); dirs = [paths.LABELS + d for d in ("v6/ver/", "v6/npa/fin/", "v7/ver/")] + ([fixdir] if fixdir else [])
else:
    split = {"dev": json.load(open(paths.DEV2 + "split.json"))["dev"]}; dirs = [paths.LABELS + "v10/ver/"]
path = {}
for d in dirs:
    for f in glob.glob(d + "*.json"): path[os.path.basename(f)[:-5]] = f
pred = collections.defaultdict(set)
for l in open(dump, encoding="utf-8"):
    d, k, lab, p, g, _ = l.rstrip("\n").split("\t")
    if float(p) >= thr: pred[d].add((k, lab))
for mode in ("strict", "fragment", "fragment + persons written identically are one name"):
    tp = fp = fn = 0; per = collections.defaultdict(lambda: [0, 0, 0]); xleft = 0
    for doc in split["dev"]:
        if doc not in path: continue
        lab = json.load(open(path[doc], encoding="utf-8-sig"))
        canon = {}; seen = {}
        for x in lab.get("persons", []) + lab.get("orgs", []):
            for m in x["mentions"]: canon.setdefault(x["id"], seen.setdefault(m, x["id"]))
        cz = (lambda i: canon.get(i, i)) if "identically" in mode else (lambda i: i)
        gold = set()
        for ev in lab.get("events", []):
            pid = cz((ev["persons"] + ev["orgs"])[0]); gold.add((pid, prep.event_label(ev)))
            if ev.get("prior"): gold.add((pid, "prior"))
        got = set()
        if any(k.startswith("x:") for k, _ in pred[doc]): raw = prep.raw_text(doc)
        for k, l in pred[doc]:
            if k.startswith("x:") and mode != "strict":
                name = next((raw[i:i + n] for n in range(1, 13) for i in range(len(raw) - n + 1)
                             if hashlib.sha256((doc + "\0x:" + raw[i:i + n]).encode()).hexdigest() == k[2:]), None)
                owners = {x["id"] for kk in ("persons", "orgs") for x in lab.get(kk, []) if name and any(name in m for m in x["mentions"])}
                owners = {cz(o) for o in owners}
                if len(owners) == 1: k = owners.pop()
            got.add((k, l))
        xleft += sum(k.startswith("x:") for k, _ in got)
        for c in got | gold:
            i = 0 if c in got and c in gold else 1 if c in got else 2
            per[c[1]][i] += 1; tp += i == 0; fp += i == 1; fn += i == 2
    print(f"{mode}: gold {tp + fn} P {tp/(tp+fp):.3f} R {tp/(tp+fn):.3f} F1 {2*tp/(2*tp+fp+fn):.3f} (tp {tp} fp {fp} of which unlabelled names {xleft}, fn {fn})")
print("fn by label:", sorted(((l, v[2]) for l, v in per.items() if v[2]), key=lambda x: -x[1])[:12])
