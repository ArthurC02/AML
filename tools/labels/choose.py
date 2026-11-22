"""Choose the documents of a new label round: a seeded sample of gathered court releases that no label round and no
measured set holds yet, dealt into batches (one labelling agent per batch and pass) -> labels/<round>/batches.json.
Candidates: every court/raw/jy-*.main.txt, or the ids listed in a file (one a line), e.g. only verdict releases.
Counts only.

usage: choose.py <round> <n> <seed> [candidates.txt] [batches=4]     e.g. choose.py v18 40 18
"""
import glob, json, os, random, sys
sys.path.insert(0, os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "stage-model"))
import paths


def taken():
    """Every document id a label round, a recheck or a measured set already holds."""
    ids = {os.path.basename(f)[:-5] for f in glob.glob(paths.LABELS + "**/jy-*.json", recursive=True)}
    # batches.json of the stage rounds: lists of ids (older event rounds used other shapes; their files are counted above)
    ids |= {d for f in glob.glob(paths.LABELS + "**/batches.json", recursive=True) for b in json.load(open(f, encoding="utf-8"))
            if isinstance(b, list) for d in b if isinstance(d, str)}
    ids |= {l.split("\t")[0] for f in glob.glob(paths.LABELS + "**/persons.tsv", recursive=True) for l in open(f, encoding="utf-8")}
    for f in glob.glob(paths.COURT + "*.jsonl") + glob.glob(paths.REPORT + "*.jsonl"):
        ids |= {json.loads(l)["doc"] for l in open(f, encoding="utf-8")}
    return ids


def batches(docs, k):
    """Sorted, dealt round-robin: how the rounds v13 to v17 were batched."""
    s = sorted(docs)
    return [s[i::k] for i in range(k)]


def main(argv):
    if argv == ["--selfcheck"]:
        assert batches(["d3", "d1", "d2", "d5", "d4"], 2) == [["d1", "d3", "d5"], ["d2", "d4"]]
        return print("selfcheck OK")
    k = next((int(a[8:]) for a in argv if a.startswith("batches=")), 4)
    argv = [a for a in argv if not a.startswith("batches=")]
    if len(argv) not in (3, 4): sys.exit(__doc__)
    out = paths.LABELS + argv[0] + "/batches.json"
    if os.path.exists(out): sys.exit("exists: " + out)
    pool = ([l.strip() for l in open(argv[3], encoding="utf-8") if l.strip()] if len(argv) == 4
            else [os.path.basename(f)[:-9] for f in glob.glob(paths.COURT_RAW + "jy-*.main.txt")])
    free = sorted(set(pool) - taken())
    docs = random.Random(int(argv[2])).sample(free, min(int(argv[1]), len(free)))
    os.makedirs(os.path.dirname(out), exist_ok=True)
    json.dump(batches(docs, k), open(out, "w", encoding="utf-8"))
    print("candidates", len(pool), "free", len(free), "chosen", len(docs), "batches", k, "left", len(free) - len(docs))


if __name__ == "__main__": main(sys.argv[1:])
