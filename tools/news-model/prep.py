"""Turns the gold labels into training data. Prints counts only, never text.
Usage: python -I -X utf8 prep.py [--round r12|r11] [--out DIR] [--train-only] > DIR/stats.txt

The split is fixed: DIR/split.json must already exist (copy it from the round you continue; append new train-only
documents to its train list by hand). Without --out, DIR is AML_TRAIN/data-<round>/. --train-only rebuilds the train
files only and asserts the split is unchanged. `--round r11` is the former prep_r11.py, the default r12 prep_r12.py.

Outputs (DIR):
  split.json            doc ids per split (by document, judgments and press stratified)
  ner-<split>.jsonl     {"doc","text","spans":[[start,end,label],...]}  person/org mentions with their role
  ev-<split>.jsonl      {"doc","mention","sentence","labels":[...]}     one row per (person, sentence)
"""
import glob, json, os, random, re, sys, unicodedata
from collections import Counter

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import paths

# Each round is split on its own, so adding a round leaves the earlier splits (and their test set) unchanged.
ROUNDS = [("v3", "v4"), ("v5",)]
CARRY = 1  # NewsModelReading.CarryOver


def raw_text(doc):
    if doc.startswith("jd-"): return open(paths.JD + doc + ".main.txt", encoding="utf-8-sig").read()
    if doc.startswith("npa-"): return open(paths.NEWS + doc + ".main.txt", encoding="utf-8-sig").read()
    p = paths.PR + doc + ".para.txt"
    return open(p if os.path.exists(p) else paths.PR + doc + ".main.txt", encoding="utf-8-sig").read()


def normalise(s):
    """NFKC per character, kept one to one so offsets stay valid (CJK compatibility ideographs, full-width forms)."""
    out = []
    for c in s:
        n = unicodedata.normalize("NFKC", c)
        out.append(n if len(n) == 1 else c)
    return "".join(out)


def occurrences(text, s):
    i = text.find(s)
    while i >= 0 and s:
        yield i, i + len(s)
        i = text.find(s, i + 1)


def event_rows(raw, sentence, trigger, pid, rows):
    # Repeated verbatim sentences state the same party's step at every occurrence, not just the first.
    offset = sentence.find(trigger) if trigger else 0
    assert offset >= 0
    positions = [start + offset for start, _ in occurrences(raw, sentence)]
    return {key for key, row in rows.items() if key[0] == pid
            and any(row['span'][0] <= at < row['span'][1] for at in positions)}


GROUP = re.compile(r"(?<![平同相對])等共?[0-9０-９一二兩三四五六七八九十百餘多數]*(?:人|名|位|嫌|被告)")  # NewsModelReading.Group
SENT = re.compile(r"[^。！？；!?;\n]*[。！？；!?;]?")


def sentences(text):
    for m in SENT.finditer(text):
        if m.group().strip(): yield m.start(), m.end()


def event_label(e):
    lab = e["type"] if e["certainty"] == "Stated" else f'{e["type"]}-{e["certainty"]}'
    return lab


def main(LABEL_DIRS, OUT, only_train=False):
    L = paths.LABELS
    EXCLUDED = set(json.load(open(L + "v6/npa/excluded.json", encoding="utf-8"))["docs"])
    DUPS = {d for v in ("v4", "v5") for d in json.load(open(L + f"{v}/duplicates.json"))}
    NEAR = [g for v in ("v4", "v5") for g in json.load(open(L + f"{v}/neardup-groups.json"))]  # near-duplicates stay in one split
    os.makedirs(OUT, exist_ok=True)
    frozen_split = json.load(open(OUT + "split.json")) if only_train else None
    path = {os.path.basename(f)[:-5]: f.replace("\\", "/") for d in LABEL_DIRS for f in glob.glob(d + "*.json")}
    path = {d: p for d, p in path.items() if d not in EXCLUDED}
    in_group = {d: tuple(g) for g in NEAR for d in g}
    split = {"train": [], "dev": [], "test": []}
    for rnd in ROUNDS:
        docs = sorted(d for d in path if d not in DUPS and any(f"/{v}/" in path[d] for v in rnd))
        random.seed(20261108)
        for kind in (True, False):
            units = sorted({in_group.get(d, (d,)) for d in docs if d.startswith("jd-") == kind})
            random.shuffle(units)
            n = sum(map(len, units)); taken = 0
            for u in units:
                name = "train" if taken < 0.8 * n else "dev" if taken < 0.9 * n else "test"
                split[name] += [d for d in u if d in path and d not in DUPS]; taken += len(u)
    if os.path.exists(OUT + "split.json"):  # a fixed split: documents keep their side; new ones (v6 news) go 80/10/10
        fixed = json.load(open(OUT + "split.json")); known = {d for v in fixed.values() for d in v}
        new = sorted(d for d in path if d not in known and d not in DUPS and not d.startswith("jd-") and "/v6/" in path[d])
        random.seed(20261113); random.shuffle(new)
        for i, d in enumerate(new): fixed["train" if i < 0.8 * len(new) else "dev" if i < 0.9 * len(new) else "test"].append(d)
        # v7 judgments (crawled 2026-11-14, no text overlap with dev/test) go to train only, so dev and test stay comparable.
        known = {d for v in fixed.values() for d in v}
        fixed["train"] += sorted(d for d in path if d not in known and "/v7/" in path[d])
        split = {k: [d for d in v if d in path] for k, v in fixed.items()}
    if only_train:
        assert split == frozen_split, "Train-only rebuild cannot change the fixed split"
    else:
        json.dump(split, open(OUT + "split.json", "w"), indent=1)

    stats = Counter()
    for name, ids in split.items():
        if only_train and name != "train": continue
        with open(OUT + f"ner-{name}.jsonl", "w", encoding="utf-8") as ner, open(OUT + f"ev-{name}.jsonl", "w", encoding="utf-8") as ev:
            for doc in ids:
                lab = json.load(open(path[doc], encoding="utf-8-sig"))
                raw = raw_text(doc); text = normalise(raw)
                # Entity spans: every occurrence of every mention, longest first, no overlaps.
                cands = []
                for kind, tag in (("persons", "PER"), ("orgs", "ORG")):
                    for x in lab.get(kind, []):
                        for m in x["mentions"]:
                            if len(m) < 2: continue  # guideline rule 42: a one-character mention would tag every such character
                            for s, e in occurrences(raw, m): cands.append((s, e, f'{tag}-{x["party"]}', x["id"]))
                cands.sort(key=lambda c: (-(c[1] - c[0]), c[0]))
                taken = [False] * len(raw); spans = []
                for s, e, l, _ in cands:
                    if not any(taken[s:e]):
                        for i in range(s, e): taken[i] = True
                        spans.append([s, e, l])
                spans.sort()
                stats[f"{name} ner spans"] += len(spans)
                ner.write(json.dumps({"doc": doc, "text": text, "spans": spans}, ensure_ascii=False) + "\n")

                # Event rows: (party, sentence) -> event labels.
                parties = {x["id"]: x for k in ("persons", "orgs") for x in lab.get(k, [])}
                rows = {}
                persons = {x["id"] for x in lab.get("persons", [])}
                suspects = sorted(p for p in persons if parties[p]["party"] == "Suspect")
                fullname = {p: max((m for m in parties[p]["mentions"] if m in raw), key=len, default=parties[p]["mentions"][0]) for p in suspects}
                # NewsModelReading.Asked: a judgment's whole-case sentences are for the defendants listed before 主文.
                lines = raw.split(chr(10))
                order = next((sum(len(x) + 1 for x in lines[:k]) for k, x in enumerate(lines) if "".join(x.split()) == "主文"), 0)
                defendants = [p for p in suspects if any(m in raw[:order] for m in parties[p]["mentions"])]
                whole = defendants or suspects
                last, since = None, 0  # mirrors NewsModelReading.Asked: one sentence naming no person is asked for the last-named person
                for s, e in sentences(raw):
                    sent = raw[s:e]
                    named = []
                    for pid, x in parties.items():
                        m = next((m for m in x["mentions"] if m in sent), None)
                        if m:
                            rows[(pid, s)] = {"mention": m, "span": (s, e), "labels": set()}
                            if pid in persons:
                                named += [(sent.rfind(f) + len(f), len(f), pid, f) for f in x["mentions"] if f in sent]
                    if named: _, _, lp, lm = max(named); last, since = ((lp, lm) if parties[lp]["party"] == "Suspect" else None), 0
                    elif last and since < CARRY:
                        since += 1; rows[(last[0], s)] = {"mention": last[1], "span": (s, e), "labels": set()}
                    # NewsModelReading.Asked: a sentence naming no suspect is asked for each suspect, by name (any number of suspects).
                    grp = any(parties[q]["party"] == "Suspect" for _, _, q, _ in named)  # a sentence naming a suspect for a group is asked for the others too
                    co = grp and not GROUP.search(sent)  # NewsModelReading.Asked CaseOnly: asked for the suspects the sentence does not name too
                    if whole:
                        for q in whole: rows.setdefault((q, s), {"mention": fullname[q], "span": (s, e), "labels": set(), "wide": len(whole) > 5, "group": grp and not co, "caseonly": co})
                for evn in lab.get("events", []):
                    pid = (evn["persons"] + evn["orgs"])[0]
                    if evn["sentence"] not in raw: stats["event sentence not found"] += 1; continue
                    # the row of the sentence that holds the trigger, else a new row with the party's first mention
                    keys = event_rows(raw, evn["sentence"], evn["trigger"], pid, rows)
                    # A sentence that neither names the party nor carries it over gives no row: at run time the model
                    # never sees that (mention, sentence) pair (a whole-case step, 44o).
                    if not keys: stats["event sentence without the party"] += 1
                    for key in keys:
                        rows[key]["labels"].add(event_label(evn))
                        if evn.get("prior"): rows[key]["labels"].add("prior")
                    # v2.4 rule 18: every other sentence stating the same step is a positive row too
                    for extra in evn.get("also", []):
                        if extra not in raw: stats["also sentence not found"] += 1; continue
                        keys2 = event_rows(raw, extra, None, pid, rows)
                        if not keys2: stats["also sentence without the party"] += 1
                        for k2 in keys2:
                            rows[k2]["labels"].add(event_label(evn))
                            if evn.get("prior"): rows[k2]["labels"].add("prior")
                for (pid, s), r in rows.items():
                    # Large cases: of the rows without a label that only the any-number rule adds, about three per sentence are kept for training.
                    if name == "train" and r.get("wide") and not r["labels"] and __import__("zlib").crc32(f"{doc}|{s}|{pid}".encode()) % len(whole) >= 3: stats["train wide negative rows left out"] += 1; continue
                    a, b = r["span"]
                    ev.write(json.dumps({"doc": doc, "mention": normalise(r["mention"]), "sentence": text[a:b].strip(),
                                         "labels": sorted(r["labels"]), **({"caseonly": True} if r.get("caseonly") else {})}, ensure_ascii=False) + "\n")
                    stats[f"{name} ev rows"] += 1; stats[f"{name} ev positive rows"] += bool(r["labels"]); stats[f"{name} group rows"] += bool(r.get("group")); stats[f"{name} group positive rows"] += bool(r.get("group") and r["labels"]); stats[f"{name} caseonly rows"] += bool(r.get("caseonly")); stats[f"{name} caseonly positive rows"] += bool(r.get("caseonly") and r["labels"])
                    for l in (r["labels"] if r.get("caseonly") else ()): stats[f"{name} caseonly label {l}"] += 1
    for k, v in sorted(stats.items()): print(k, v)
    print({k: len(v) for k, v in split.items()})


if __name__ == "__main__":
    a = sys.argv[1:]
    rnd = a[a.index("--round") + 1] if "--round" in a else paths.ROUND
    out = a[a.index("--out") + 1].replace("\\", "/").rstrip("/") + "/" if "--out" in a else paths.round_dir(rnd)
    assert os.path.exists(out + "split.json"), "the split is fixed: copy split.json into " + out + " first"
    main(paths.ROUND_LABELS[rnd], out, "--train-only" in a)
