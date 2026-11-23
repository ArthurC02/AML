"""Rows for the person-level legal-stage classifier: one per (document, annotated suspect). Prints counts only.
usage: prep_stage.py [--out DIR] [--nonsuspects]   writes DIR/data/ (default AML_STAGE_WORK, see paths.py)
  --nonsuspects: train.jsonl also gets one UNKNOWN row per other named person (Victim, Official, Witness, Other) of the
          train documents, built by row() as a suspect's is; left out: a person sharing a form with a suspect of the
          document, a Witness with event rows, a person with no form in the text. Capped at the suspect row count,
          Victim and Other kept first, Witness and Official drawn together (seeded). Every other file is unchanged.
  --officials N (with --nonsuspects): no overall cap; every Victim, Other and Witness, and a seeded sample of N Officials.

Documents
  train : prep_r12's train split (data-r12/split.json) minus every document of dev-, dev2-, v9-, v11-stage.jsonl (and
          prep_r12's dev/test, asserted disjoint), minus train documents whose text equals an evaluation text. Each is built as an evaluator document file is: text from
          data-r12/ner-train.jsonl, people from the label file, events = its data-r12/ev-train.jsonl rows (evaldocs.py)
          plus every label-file event of each person (stagegold.py; label file = last of paths.LABEL_DIRS + v10/ver).
  eval  : measurements/report/<set>-stage.jsonl as they are (the evaluator's reference documents).
  court : measurements/court/accept, accept3, accept4, accept5.jsonl as they are; evaluation only, written after the
          rows above (which they do not change); asserted to share no document id or text with train (stats-court.txt).
  Both go through row(): stage = gold.person_stage (the evaluator's reference derivation).
kind     : "judgment" if a line is 主文 once whitespace is removed; else "court-release" if the id starts "jy-" (the
           Judicial Yuan releases; every other press prefix is a prosecutor / MJIB / MOJ / FSC site); else "report".
Input (run-time computable from the text and the person's forms only)
  name     : the person's first form.
  evidence : in document order, (a) the first 60 characters, (b) every sentence holding one of the person's forms,
             (c) after each, the next sentence when it holds no form of any annotated person (PER-*). Forms shorter
             than 2 characters are ignored (label guideline rule 42). Each occurrence of a form (longest first, no
             overlaps) is wrapped in [unused1] [unused2]. Pieces are joined directly when adjacent, with one space when
             only whitespace lies between, and with "…" where text was skipped. Sentences end on 。！？； (and their NFKC
             forms !?;) and newlines.
  crop     : if over the token budget (512 - 3 - name tokens): the head (a), then pieces in order up to 60% of the
             budget, then the LAST pieces that fit the rest (the end often carries the outcome).
"""
import collections, glob, hashlib, json, os, random, re, statistics, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import paths, textutil, gold
from train_stage import enc

R12, REPORT = paths.R12, paths.REPORT
SETS = ["dev", "dev2", "v9", "v11"]
COURT, COURT_SETS = gold.COURT, gold.COURT_SETS  # court press-release evaluation sets: data/<set>.jsonl + stats-court.txt
HEAD, BUDGET = 60, 512
SENT = re.compile(r"[^。！？；!?;\n]*[。！？；!?;]?")
OUT = paths.out_dir(sys.argv) + "data/"
NONSUSPECTS = "--nonsuspects" in sys.argv
OFFICIALS = int(sys.argv[sys.argv.index("--officials") + 1]) if "--officials" in sys.argv else None
NS_RANK = {"Victim": 0, "Other": 0, "Witness": 1, "Official": 1}  # lower rank kept first up to the cap; a seeded draw within a rank
NS_SEED = 1  # ponytail: cap = 1 non-suspect row per suspect row, by role rank; tune the ratio if the classifier over-answers UNKNOWN
tok = None  # the bert tokenizer, loaded by the first row() (selfcheck.py imports this file without it)


def kind(doc, text):
    if any("".join(l.split()) == "主文" for l in text.split("\n")): return "judgment"
    return "court-release" if doc.startswith("jy-") else "report"


def spans(text, forms):
    """Non-overlapping occurrences of the forms, longest first."""
    c = sorted(((s, e) for f in forms if len(f) >= 2 for s, e in textutil.occurrences(text, f)), key=lambda x: (x[0] - x[1], x[0]))
    taken, out = set(), []
    for s, e in c:
        if not taken & set(range(s, e)): taken |= set(range(s, e)); out.append((s, e))
    return sorted(out)


def row(d, p):
    global tok
    if tok is None:
        from transformers import AutoTokenizer
        tok = AutoTokenizer.from_pretrained(paths.BERT)
    text, forms = d["text"], [m for m in p["mentions"] if len(m) >= 2]
    others = {m for q in d["people"] if q["role"].startswith("PER-") for m in q["mentions"] if len(m) >= 2}
    sents = [(m.start(), m.end()) for m in SENT.finditer(text) if m.group().strip()]
    own = [any(f in text[s:e] for f in forms) for s, e in sents]
    keep = set()
    for i, o in enumerate(own):
        if o:
            keep.add(i)
            if i + 1 < len(sents) and not any(f in text[slice(*sents[i + 1])] for f in others): keep.add(i + 1)
    head = (0, min(HEAD, len(text)))
    units = [head] + [(max(s, HEAD), e) for i, (s, e) in enumerate(sents) if i in sorted(keep) and e > HEAD]
    marks = spans(text, forms)

    def render(u):
        s, e = u; out, at = [], s
        for a, b in marks:
            if s <= a and b <= e: out += [text[at:a], "[unused1]", text[a:b], "[unused2]"]; at = b
        return "".join(out) + text[at:e]

    pieces = [render(u) for u in units]; lens = [len(enc(tok, x)) + 1 for x in pieces]
    budget = BUDGET - 3 - min(32, len(enc(tok, (forms or p["mentions"] or [""])[0]))); full = sum(lens)
    chosen = list(range(len(units)))
    if full > budget:
        chosen, used = [0], lens[0]
        for i in range(1, len(units)):
            if used + lens[i] > 0.6 * budget: break
            chosen.append(i); used += lens[i]
        tail = []
        for i in range(len(units) - 1, chosen[-1], -1):
            if used + lens[i] > budget: break
            tail.insert(0, i); used += lens[i]
        chosen += tail
    ev, prev = "", None
    for i in chosen:
        if prev is not None:
            gap = text[units[prev][1]:units[i][0]]
            ev += "" if not gap else " " if not gap.strip() else "…"
        ev += pieces[i]; prev = i
    return {"doc": d["doc"], "kind": kind(d["doc"], text), "person": p["id"], "stage": gold.person_stage(d, p),
            "name": (forms or p["mentions"] or [""])[0], "evidence": ev, "cropped": full > budget,
            "no_form": not any(own), "tokens_full": full}


def train_docs(exclude):
    split = json.load(open(R12 + "split.json"))
    dirs = paths.LABEL_DIRS + [paths.LABELS + "v10/ver/"]
    files = {}
    for rank, dd in enumerate(dirs):
        for f in glob.glob(dd + "*.json"): files.setdefault(os.path.basename(f)[:-5], []).append((rank, f))
    ids = set(split["train"]) - exclude
    assert not ids & (set(split["dev"]) | set(split["test"]))
    ev = collections.defaultdict(list)
    for l in open(R12 + "ev-train.jsonl", encoding="utf-8"):
        r = json.loads(l)
        if r["doc"] in ids: ev[r["doc"]].append({"mention": r["mention"], "sentence": r["sentence"], "labels": r["labels"]})
    for l in open(R12 + "ner-train.jsonl", encoding="utf-8"):
        r = json.loads(l)
        if r["doc"] not in ids: continue
        lab = json.load(open(max(files[r["doc"]])[1], encoding="utf-8-sig"))
        people = [{"id": p["id"], "role": ("PER-" if k == "persons" else "ORG-") + ("Other" if p["party"] == "Witness" else p["party"]), "party": p["party"],
                   "mentions": [textutil.normalise(m) for m in p["mentions"]]} for k in ("persons", "orgs") for p in lab.get(k, [])]
        events = list(ev[r["doc"]])
        first = {p["id"]: p["mentions"][0] for p in people if p["mentions"]}
        for e in lab.get("events", []):
            for pid in e.get("persons", []):
                if pid in first: events.append({"mention": first[pid], "sentence": e.get("sentence") or "", "labels": [textutil.event_label(e)] + (["prior"] if e.get("prior") else [])})
        yield {"doc": r["doc"], "text": r["text"], "people": people, "events": events}


def nonsuspects(docs):
    """--nonsuspects: (party, row with stage UNKNOWN) per eligible other named person, and the exclusion counts."""
    out, c = [], collections.Counter()
    for d in docs:
        sus = {m for p in gold.suspects(d) for m in p["mentions"]}
        for p in d["people"]:
            if not p["role"].startswith("PER-") or p["party"] == "Suspect": continue
            ms = set(p["mentions"])
            if ms & sus: c["shares a form with a suspect"] += 1; continue
            if p["party"] == "Witness" and any(e["mention"] in ms and e["labels"] for e in d["events"]): c["Witness with events"] += 1; continue
            r = row(d, p)
            if r["no_form"]: c["no form in text"] += 1; continue
            c["kept, own events give a stage"] += r["stage"] != "UNKNOWN"
            out.append((p["party"], dict(r, stage="UNKNOWN")))
    return out, c


def cap(ns, n):
    """At most n of the (party, row) pairs: by NS_RANK, a seeded draw within a rank; document order kept."""
    rng = random.Random(NS_SEED)
    keep = sorted(range(len(ns)), key=lambda i: (NS_RANK[ns[i][0]], rng.random()))[:n]
    return [ns[i] for i in sorted(keep)]


def officials(ns, n):
    """--officials n: every non-Official pair, n Officials by a seeded draw; document order kept."""
    o = [i for i, (k, _) in enumerate(ns) if k == "Official"]
    keep = set(random.Random(NS_SEED).sample(o, min(n, len(o))))
    return [x for i, x in enumerate(ns) if x[0] != "Official" or i in keep]


def pct(xs, q): return sorted(xs)[min(len(xs) - 1, int(q * len(xs)))] if xs else 0


def main():
    os.makedirs(OUT, exist_ok=True)
    evdocs = {s: [json.loads(l) for l in open(REPORT + s + "-stage.jsonl", encoding="utf-8")] for s in SETS}
    exclude = {d["doc"] for v in evdocs.values() for d in v}
    split = json.load(open(R12 + "split.json"))
    h = lambda d: hashlib.md5(d["text"].encode()).hexdigest()
    evtexts = {h(d) for v in evdocs.values() for d in v}
    cand = list(train_docs(exclude)); same_text = [d for d in cand if h(d) in evtexts]  # same text under another id: a leak too
    docs = {"train": [d for d in cand if h(d) not in evtexts], **{s: evdocs[s] for s in SETS}}
    rows = {k: [row(d, p) for d in v for p in gold.suspects(d)] for k, v in docs.items()}
    out = []
    P = lambda *a: out.append(" ".join(str(x) for x in a))
    P("prep_r12 train docs", len(split["train"]), "| dropped as evaluation docs", len(set(split["train"]) & exclude),
      "| dropped for a text identical to an evaluation doc", len(same_text), "| prep_r12 dev/test docs", len(split["dev"]) + len(split["test"]), "| train docs kept", len(docs["train"]))
    for s in SETS: P("  eval", s, "docs", len(evdocs[s]), "also in prep_r12 train", len({d['doc'] for d in evdocs[s]} & set(split["train"])),
                     "in prep_r12 dev", len({d['doc'] for d in evdocs[s]} & set(split["dev"])), "in prep_r12 test", len({d['doc'] for d in evdocs[s]} & set(split["test"])))
    train_ids = {d["doc"] for d in docs["train"]}
    for s in SETS:
        shared = train_ids & {d["doc"] for d in evdocs[s]}; assert not shared, s
        th = {hashlib.md5(d["text"].encode()).hexdigest() for d in docs["train"]}
        P("check: train /", s, "shared doc ids", len(shared), "| identical texts", sum(hashlib.md5(d["text"].encode()).hexdigest() in th for d in evdocs[s]))
    if NONSUSPECTS:
        ns, c = nonsuspects(docs["train"])
        kept = cap(ns, len(rows["train"])) if OFFICIALS is None else officials(ns, OFFICIALS)
        P("\nnon-suspects (train docs only): suspect rows", len(rows["train"]), dict(collections.Counter(r["stage"] for r in rows["train"]).most_common()))
        P("  left out", dict(c)); P("  rows per party before cap", dict(collections.Counter(k for k, _ in ns)), "| after cap", dict(collections.Counter(k for k, _ in kept)))
        rows["train"] = rows["train"] + [r for _, r in kept]
    for k, rs in rows.items(): write(k, docs[k], rs, P)
    tr = rows["train"]
    P("\ntrain UNKNOWN %d of %d (%.3f)" % (sum(r["stage"] == "UNKNOWN" for r in tr), len(tr), sum(r["stage"] == "UNKNOWN" for r in tr) / len(tr)))
    random.seed(1); ok = bad = 0
    people = {(d["doc"], p["id"]): set(p["mentions"]) for d in docs["train"] for p in d["people"]}
    for r in random.sample(tr, 20):
        for m in re.findall(r"\[unused1\](.*?)\[unused2\]", r["evidence"]):
            if m in people[(r["doc"], r["person"])]: ok += 1
            else: bad += 1
    P("check: 20 random train rows, marked spans equal to a form of the person %d, not %d" % (ok, bad))
    assert bad == 0
    open(OUT + "stats.txt", "w", encoding="utf-8").write("\n".join(out) + "\n")
    print("\n".join(out))
    # Court press-release evaluation sets: added after the train/eval rows above, which they do not change (train is
    # not filtered against them; instead it is asserted that none shares a document id or a text with train).
    cout = []
    C = lambda *a: cout.append(" ".join(str(x) for x in a))
    th = {h(d) for d in docs["train"]}
    for s in COURT_SETS:
        cd = [json.loads(l) for l in open(COURT + s + ".jsonl", encoding="utf-8")]
        shared, same = train_ids & {d["doc"] for d in cd}, sum(h(d) in th for d in cd)
        C("check: train /", s, "shared doc ids", len(shared), "| identical texts", same)
        assert not shared and not same, s
        write(s, cd, [row(d, p) for d in cd for p in gold.suspects(d)], C)
    open(OUT + "stats-court.txt", "w", encoding="utf-8").write("\n".join(cout) + "\n")
    print("\n".join(cout))


def write(k, kdocs, rs, P):
    """data/<k>.jsonl and its statistics lines."""
    with open(OUT + "%s.jsonl" % k, "w", encoding="utf-8") as w:
        for r in rs: w.write(json.dumps({x: r[x] for x in ("doc", "kind", "person", "stage", "name", "evidence", "cropped")}, ensure_ascii=False) + "\n")
    L = [r["tokens_full"] for r in rs]
    P("\n[%s] docs %d rows %d | kinds(docs) %s" % (k, len(kdocs), len(rs), dict(collections.Counter(kind(d["doc"], d["text"]) for d in kdocs))))
    P("  stages", dict(collections.Counter(r["stage"] for r in rs).most_common()))
    for kd in sorted({r["kind"] for r in rs}): P("  stages", kd, sum(r["kind"] == kd for r in rs), dict(collections.Counter(r["stage"] for r in rs if r["kind"] == kd).most_common()))
    P("  cropped %d (%.3f) | person has no form in text %d | evidence tokens before crop p50 %d p90 %d p99 %d max %d"
      % (sum(r["cropped"] for r in rs), sum(r["cropped"] for r in rs) / max(1, len(rs)), sum(r["no_form"] for r in rs), pct(L, .5), pct(L, .9), pct(L, .99), max(L)))
    P("  duplicates (doc, person)", len(rs) - len({(r["doc"], r["person"]) for r in rs}))
    P("  judgment-prefix (jd-) docs without a 主文 line", sum(d["doc"].startswith("jd-") and kind(d["doc"], d["text"]) != "judgment" for d in kdocs))


if __name__ == "__main__":
    main()
