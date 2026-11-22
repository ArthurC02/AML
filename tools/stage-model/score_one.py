"""Gemma 12B with one person per call (job49) against the reference and against the earlier all-persons runs on the same
persons. Counts only, never names or quotes.
usage: score_one.py <out dir>   (e.g. AML_TRAIN/downloads/aml49/out; the model folder inside is read, or the dir
        itself when it holds the <set>.jsonl files)
Persons = every suspect (gold.suspects) of every document of the set's source that has one (report sets:
measurements/report/<set>-stage.jsonl, court sets: measurements/court/<set>.jsonl); reference = gold.person_stage.
All-persons run = downloads/aml45 (report sets) or downloads/aml48 (court sets), same model. A person absent from an
output counts as ABSENT (wrong). A set without one-person output is skipped."""
import collections, json, os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import gold, paths

SETS = ["dev", "dev2", "v9", "accept", "accept3", "accept4", "v11", "accept5"]
TAG = "gemma4_12b-it-q4_K_M"
DL = paths.DOWNLOADS


def source(s): return gold.COURT + s + ".jsonl" if s in gold.COURT_SETS else gold.REPORT + s + "-stage.jsonl"
def earlier(s): return DL + ("aml48" if s in gold.COURT_SETS else "aml45") + "/out/" + TAG + "/" + s + ".jsonl"


def load(path):
    out = {}
    if os.path.exists(path):
        for l in open(path, encoding="utf-8"):
            try: r = json.loads(l)
            except ValueError: continue  # torn last line of a partial download
            out[r["doc"]] = r
    return out


def stages(recs): return {(r["doc"], str(p["id"])): p["stage"] for r in recs.values() for p in r["persons"]}


def score(s, one_path, all_path):
    docs = [d for d in (json.loads(l) for l in open(source(s), encoding="utf-8")) if gold.suspects(d)]
    ref = {(d["doc"], str(p["id"])): gold.person_stage(d, p) for d in docs for p in gold.suspects(d)}
    one_recs, all_recs = load(one_path), load(all_path)
    one, al = stages(one_recs), stages(all_recs)
    c = collections.Counter()
    for k, g in ref.items():
        a, b = one.get(k, "ABSENT") == g, al.get(k, "ABSENT") == g
        c["one"] += a; c["all"] += b
        c["both" if a and b else "only one-person" if a else "only all-persons" if b else "neither"] += 1
        c["one MISSING"] += one.get(k) == "MISSING"; c["one ABSENT"] += k not in one
        c["all ABSENT"] += k not in al
    print("[%s] documents %d, in one-person output %d (with error %d), in all-persons output %d | persons %d | one-person MISSING %d ABSENT %d | all-persons ABSENT %d | not in reference %d"
          % (s, len(docs), sum(d["doc"] in one_recs for d in docs), sum(r["error"] is not None for r in one_recs.values()), sum(d["doc"] in all_recs for d in docs),
             len(ref), c["one MISSING"], c["one ABSENT"], c["all ABSENT"], len(set(one) - set(ref))))
    print("    right: one-person %d/%d, all-persons %d/%d | both %d, only one-person %d, only all-persons %d, neither %d"
          % (c["one"], len(ref), c["all"], len(ref), c["both"], c["only one-person"], c["only all-persons"], c["neither"]))
    return c, len(ref)


def main():
    root = sys.argv[1]
    if os.path.isdir(os.path.join(root, TAG)): root = os.path.join(root, TAG)
    tot = collections.Counter(); n = 0
    for s in SETS:
        p = os.path.join(root, s + ".jsonl")
        if not os.path.exists(p): print("[%s] no one-person output" % s); continue
        c, k = score(s, p, earlier(s)); tot += c; n += k
    print("[scored sets] persons %d | right: one-person %d, all-persons %d | both %d, only one-person %d, only all-persons %d, neither %d"
          % (n, tot["one"], tot["all"], tot["both"], tot["only one-person"], tot["only all-persons"], tot["neither"]))


if __name__ == "__main__":
    main()
