"""SFT examples for fine-tuning Gemma 4 as the legal-stage reader: one chat example per row of AML_STAGE_WORK/data17v/train.jsonl
(the classifier's train rows, tools/stage-model add17.py --verdicts).

Documents are rebuilt as the rows were made: prep_stage.train_docs (minus evaluation ids and texts, as prep_stage.main)
followed by the v17 court releases as add17.py --verdicts builds them; every rebuilt (document, suspect) is checked
against its row (row() output equal on every field the row holds), so the text, the person and its mentions are the
row's. Person forms = common.forms (as the inference data).
user      = the runtime prompt (src/Aml.Infra/Models/stage-prompt.txt) for person "1" with the text cut as the runtime
            cuts it (OllamaStageReader: over 16,000 chars -> first 10,000 + "\n（中間省略）\n" + last 6,000).
assistant = {"persons":[{"id":"1","stage":STAGE,"quote":""}]}.
Asserts no train document id or text equals any document of the eight evaluation sets. Prints counts only.
Writes AML_LLM_WORK/data/train.jsonl (common.SFT)."""
import collections, glob, hashlib, json, os, sys
sys.dont_write_bytecode = True  # no __pycache__ in the repo folders it imports from
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import common
from common import gold, paths, cut, forms, LONG
from prep_stage import row, train_docs

PROMPT = common.PROMPT_FILE.read_text(encoding="utf-8")
OUT = str(common.SFT)
EVAL_SETS = gold.SETS + gold.COURT_SETS
h = lambda t: hashlib.md5(t.encode()).hexdigest()


def eval_docs():
    out = []
    for s in gold.SETS: out += [json.loads(l) for l in open(gold.REPORT + s + "-stage.jsonl", encoding="utf-8")]
    for s in gold.COURT_SETS: out += [json.loads(l) for l in open(gold.COURT + s + ".jsonl", encoding="utf-8")]
    return out


def v17_docs(evdocs):
    """add17.py --verdicts, document part (same filters, same people and events)."""
    import add17
    R = add17.R
    load = lambda d: {os.path.basename(f)[:-5]: json.load(open(f, encoding="utf-8-sig")) for f in glob.glob(R + d + "/jy-*.json")}
    A, B = load("a"), load("b")
    evids = {d["doc"] for d in evdocs}; evg = [add17.grams(d["text"]) for d in evdocs]
    for doc in sorted(set(A) & set(B)):
        if not (A[doc]["kind"] in ("verdict", "ruling") and B[doc]["kind"] == A[doc]["kind"]): continue
        t = open(paths.COURT_RAW + doc + ".main.txt", encoding="utf-8-sig").read()
        g = add17.grams(t)
        if doc in evids or any(len(g & e) / max(1, min(len(g), len(e))) >= 0.2 for e in evg): continue
        people, events = [], []
        for p in A[doc]["persons"]:
            fs = [f for f in p["forms"] if f and f in t]
            if not fs: continue
            q = [x for x in B[doc]["persons"] if set(x["forms"]) & set(p["forms"])]
            if not q or q[0]["stage"] != p["stage"]: continue
            pid = "P%d" % (len(people) + 1)
            people.append({"id": pid, "role": "PER-Suspect", "party": "Suspect", "mentions": fs, "descriptors": []})
            if add17.EV[p["stage"]]: events.append({"mention": fs[0], "sentence": "", "labels": add17.EV[p["stage"]]})
        if people: yield {"doc": doc, "old": False, "text": t, "ids": [], "people": people, "events": events}


def pct(xs, q): return sorted(xs)[min(len(xs) - 1, int(q * len(xs)))]


def main():
    rows = [json.loads(l) for l in open(common.ROWS, encoding="utf-8")]
    evdocs = eval_docs()
    rep = [json.loads(l) for s in gold.SETS for l in open(gold.REPORT + s + "-stage.jsonl", encoding="utf-8")]
    exclude = {d["doc"] for d in rep}; rep_texts = {h(d["text"]) for d in rep}
    base = [d for d in train_docs(exclude) if h(d["text"]) not in rep_texts]; v17 = list(v17_docs(evdocs))
    docs = base + v17
    pairs = [(d, p) for d in docs for p in gold.suspects(d)]
    assert len(pairs) == len(rows), (len(pairs), len(rows))
    for (d, p), r in zip(pairs, rows):
        got = row(d, p)
        assert all(got[k] == r[k] for k in r), "rebuilt row differs from data17v row"
        assert gold.person_stage(d, p) == r["stage"]
    ev_ids = {d["doc"] for d in evdocs}; ev_texts = {h(d["text"]) for d in evdocs}
    for d in docs: assert d["doc"] not in ev_ids and h(d["text"]) not in ev_texts, "train document equals an evaluation document"
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    with open(OUT, "w", encoding="utf-8", newline="\n") as w:
        for d, p in pairs:
            user = common.user_turn(PROMPT, "1", forms(p), cut(d["text"]))
            ans = json.dumps({"persons": [{"id": "1", "stage": gold.person_stage(d, p), "quote": ""}]}, ensure_ascii=False)
            w.write(json.dumps({"messages": [{"role": "user", "content": user}, {"role": "assistant", "content": ans}]}, ensure_ascii=False) + "\n")
    L = [len(d["text"]) for d, _ in pairs]; U = [len(cut(d["text"])) for d, _ in pairs]
    n17 = sum(len(gold.suspects(d)) for d in v17)
    print("examples", len(pairs), "| documents rebuilt", len(docs), "with a suspect", len({d["doc"] for d, _ in pairs}),
          "| report-train examples", len(pairs) - n17, "| v17 examples", n17, "docs", len(v17))
    print("per stage", dict(collections.Counter(r["stage"] for r in rows).most_common()))
    print("eval sets checked", len(EVAL_SETS), "documents", len(evdocs), "| shared ids 0, identical texts 0")
    print("text chars p50 %d p90 %d p95 %d p99 %d max %d | cut (>16000) %d" % (pct(L, .5), pct(L, .9), pct(L, .95), pct(L, .99), max(L), sum(x > LONG for x in L)))
    print("text chars after cut p50 %d p90 %d p95 %d p99 %d max %d | prompt chars without text %d" % (pct(U, .5), pct(U, .9), pct(U, .95), pct(U, .99), max(U), len(PROMPT) - len("{{PERSONS}}{{TEXT}}")))
    print("wrote", OUT, "sha256", hashlib.sha256(open(OUT, "rb").read()).hexdigest())


if __name__ == "__main__":
    main()
