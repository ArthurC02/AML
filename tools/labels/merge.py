"""Two blind label passes of court releases (labels/<round>/a, b; brief stage-brief.md) -> the evaluator's documents
for the persons both passes give the same stage (a person of pass a is matched to the first of pass b sharing a form).
--fin: the adjudicated labels labels/<round>/fin instead, one pass, no agreement needed.
check: the format of a pass folder (keys, enums, forms present in the text). Counts only.

merge refuses a round whose passes check finds format errors in (a person's forms, defendant or stage; a file's doc or
kind; a person with no form in the text is only left out), and an existing <out.jsonl> unless --force. merge and check
exit 1 on a pass folder holding no jy-*.json (a mistyped round).

usage: merge.py <round> <out.jsonl> [--fin] [--force]   e.g. merge.py v14 <AML_TRAIN>/measurements/court/accept3.jsonl
       merge.py check <round>/<pass>                    e.g. merge.py check v17/a
Rounds so far: v13 -> accept, v14 -> accept3, v15 -> accept4, v16 -> accept5 (held back); v17 is train data (add17.py).
"""
import collections, glob, json, os, sys
sys.path.insert(0, os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "stage-model"))
import paths

EV = {"CONVICTED": ["Conviction"], "ACQUITTED": ["Acquittal"], "DISMISSED": ["NonIndictment"], "INDICTED": ["Indictment"],
      "WANTED": ["WantedNotice"], "ALLEGED": ["Investigation"], "UNKNOWN": []}
KINDS = {"verdict", "ruling", "other"}


def load(d): return {os.path.basename(f)[:-5]: json.load(open(f, encoding="utf-8-sig")) for f in glob.glob(paths.LABELS + d + "/jy-*.json")}
def text(doc): return open(paths.COURT_RAW + doc + ".main.txt", encoding="utf-8-sig").read()


def merge(A, B, raw):
    """(evaluator rows, counts). B None: A is adjudicated and taken whole."""
    c = collections.Counter(); rows = []
    for doc in sorted(A):
        t = raw(doc); people = []; events = []
        for p in A[doc]["persons"]:
            forms = [f for f in p["forms"] if f and f in t]
            if not forms: continue
            if B is not None:
                q = [x for x in B.get(doc, {"persons": []})["persons"] if set(x["forms"]) & set(p["forms"])]
                if not q: c["listed by one pass only"] += 1; continue
                if q[0]["stage"] != p["stage"]: c["stage differs"] += 1; c[p["stage"] + "/" + q[0]["stage"]] += 1; continue
                c["same"] += 1
            pid = "P%d" % (len(people) + 1)
            people.append({"id": pid, "role": "PER-Suspect", "party": "Suspect", "mentions": forms, "descriptors": []})
            if EV[p["stage"]]: events.append({"mention": forms[0], "sentence": "", "labels": EV[p["stage"]]})
        if B is not None:
            c["listed by one pass only"] += sum(1 for x in B.get(doc, {"persons": []})["persons"] if not any(set(x["forms"]) & set(p["forms"]) for p in A[doc]["persons"]))
        if people: c["docs"] += 1; rows.append(json.dumps({"doc": doc, "old": False, "text": t, "ids": [], "people": people, "events": events}, ensure_ascii=False))
    return rows, c


def check(labels, raw):
    """Counter of format errors over {doc: label} (empty when all is well)."""
    e = collections.Counter()
    for doc, lab in labels.items():
        if lab.get("doc") != doc: e["doc differs from file name"] += 1
        if lab.get("kind") not in KINDS: e["kind"] += 1
        t = raw(doc)
        for p in lab.get("persons", []):
            if not isinstance(p.get("forms"), list) or not p["forms"] or not all(isinstance(f, str) and f for f in p["forms"]): errs = ["forms"]
            else: errs = [k for k, bad in (("no form in the text", not any(f in t for f in p["forms"])),
                                           ("defendant", not isinstance(p.get("defendant"), bool)), ("stage", p.get("stage") not in EV)) if bad]
            e.update(errs)
            if set(errs) - {"no form in the text"}: e[BAD] += 1
    return e


BAD = "persons with format errors"  # what merge refuses; a person with no form in the text is only left out by merge
FILE_ERRORS = ("doc differs from file name", "kind")


def refusal(passes, raw):
    """None when every pass is well formed, else the reason merge refuses: bad persons and files per pass (counts)."""
    bad = ["pass %s: %d persons, %d files" % (n, e[BAD], sum(e[k] for k in FILE_ERRORS))
           for n, lab in passes if (e := check(lab, raw)) and (e[BAD] or any(e[k] for k in FILE_ERRORS))]
    return "refused, format errors (see merge.py check <round>/<pass>): " + "; ".join(bad) if bad else None


def selfcheck():
    raw = {"jy-1": "被告王小明判刑，李大華無罪，陳一二通緝。"}.get  # made-up names
    A = {"jy-1": {"doc": "jy-1", "kind": "verdict", "persons": [{"forms": ["王小明"], "defendant": True, "stage": "CONVICTED"},
          {"forms": ["李大華"], "defendant": True, "stage": "ACQUITTED"}, {"forms": ["陳一二"], "defendant": False, "stage": "WANTED"}]}}
    B = {"jy-1": {"doc": "jy-1", "kind": "verdict", "persons": [{"forms": ["王小明", "王男"], "defendant": True, "stage": "CONVICTED"},
          {"forms": ["李大華"], "defendant": True, "stage": "CONVICTED"}, {"forms": ["張三"], "defendant": False, "stage": "UNKNOWN"}]}}
    rows, c = merge(A, B, raw)
    d = json.loads(rows[0])
    assert [p["mentions"] for p in d["people"]] == [["王小明"]] and d["events"] == [{"mention": "王小明", "sentence": "", "labels": ["Conviction"]}], d
    assert (c["same"], c["stage differs"], c["listed by one pass only"], c["docs"]) == (1, 1, 2, 1), c
    assert len(json.loads(merge(A, None, raw)[0][0])["people"]) == 3
    assert not check(A, raw) and check(B, raw) == {"no form in the text": 1}, check(B, raw)
    S = {"jy-1": {"doc": "jy-1", "kind": "verdict", "persons": [{"forms": "王小明", "defendant": True, "stage": "CONVICTED"}]}}
    assert refusal([("a", A), ("b", B)], raw) is None and refusal([("a", S), ("b", B)], raw).endswith("pass a: 1 persons, 0 files")
    print("selfcheck OK")


def main(argv):
    if argv == ["--selfcheck"]: return selfcheck()
    if len(argv) == 2 and argv[0] == "check":
        if not (lab := load(argv[1])): sys.exit("no jy-*.json in labels/%s" % argv[1])
        e = check(lab, text); print("files", len(lab), "errors", dict(e) or "none"); return
    flags = argv[2:]
    if len(argv) < 2 or len(set(flags)) != len(flags) or set(flags) - {"--fin", "--force"}: sys.exit(__doc__)
    if os.path.exists(argv[1]) and "--force" not in flags: sys.exit("refused: %s exists (--force overwrites it)" % argv[1])
    passes = [("fin", load(argv[0] + "/fin"))] if "--fin" in flags else [("a", load(argv[0] + "/a")), ("b", load(argv[0] + "/b"))]
    if empty := [n for n, lab in passes if not lab]: sys.exit("no jy-*.json in labels/%s/%s" % (argv[0], ",".join(empty)))
    if why := refusal(passes, text): sys.exit(why)
    rows, c = merge(passes[0][1], passes[1][1] if len(passes) == 2 else None, text)
    # Text mode as the original builders wrote it (on Windows each line ends CRLF), so a rebuild is byte-identical.
    open(argv[1], "w", encoding="utf-8").write("\n".join(rows) + "\n")
    print(dict(c))


if __name__ == "__main__": main(sys.argv[1:])
