"""The data the repo tools read, as a bundle that moves to another machine: every dataset, label, raw text and cached
language-model answer, the evaluator's documents, the baseline stage classifier and the retained news model pair (MODELS);
never base-model weights or training caches. Prints counts and hashes only, never text.

usage: bundle.py build <dest>          copy the closure into <dest>/aml-data, aml-train and aml-eval (dest must not exist)
       bundle.py verify <root>         every file against <root>/MANIFEST.json; exit 1 on a mismatch, missing or extra file
       bundle.py remove <doc id> <root>  delete a document and its rows and labels everywhere in a bundle, update the manifest

Sources: AML_DATA, AML_TRAIN and AML_EVAL (tools/stage-model/paths.py); a host that sets them to <dest>/aml-data,
<dest>/aml-train and a working copy of <dest>/aml-eval runs the tools unchanged. The closure is CLOSURE below: what
tools/stage-model, llm-stage, news-model, labels, judgments, Aml.StageEval and Aml.NewsEval read (traced and rebuilt
sha-identical, see tools/data/README.md). Held-back sets are in the bundle (StageEval --heldback, score_rows.py at the
end) and marked heldback in the manifest: aggregate counts only. ORIGINAL_ONLY files (the labelling guideline, checker
and prompts) never enter the pseudonymised copy; MODELS folders enter it byte for byte.
"""
import collections, hashlib, json, os, pathlib, re, shutil, sys

sys.dont_write_bytecode = True
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1] / "stage-model"))
import paths  # noqa: E402

ROOTS = {"aml-data": "AML_DATA", "aml-train": "AML_TRAIN", "aml-eval": "AML_EVAL"}
META = {"MANIFEST.json", "README.txt", "PSEUDONYM-MAP.json"}  # top-level files outside the manifest
WEIGHTS = re.compile(r".*\.(safetensors|onnx|bin|gguf|pt|pth|ckpt|npz|pyc)$")
MODELS = ("aml-train/downloads/aml47/onnx/", "aml-train/measurements/aml43-pair/")  # weights taken here only; copied unchanged
ORIGINAL_ONLY = re.compile(r"aml-data/labels/tools/.*")  # may quote documents and no pseudonymisation reads it
TEXT = {".txt", ".json", ".jsonl", ".tsv", ".md", ".csv", ".py"}  # files remove() reads as text
SETS8 = "(dev|dev2|v9|v11|accept|accept3|accept4|accept5)"
HELD_ROWS = {"heldout", "test"}  # held-out rows and test splits; the held-back measured sets are paths.HELD


def is_held(name):
    """A held-back set or held-out rows, compared in lower case (paths.held: Windows opens Accept5.jsonl as accept5.jsonl)."""
    return bool(paths.held([name])) or name.lower() in HELD_ROWS or name.lower().startswith("heldout")

# (root, folder, file pattern relative to the folder). Only these folders are walked.
CLOSURE = [
    ("aml-data", "labels", r"(?!tools/)(?!.*__pycache__/).*\.(json|jsonl|tsv|md|txt)"),  # every round, review and pass
    ("aml-data", "court/raw", r"[^/.]+\.(main|para)\.txt|index\.jsonl"),                # court releases (add17, merge, jids)
    ("aml-data", "court", r"list\.jsonl"),                                              # jids.py
    ("aml-data", "judgments", r"[^/.]+\.main\.txt"),                                    # news-model prep.py raw_text
    ("aml-data", "press", r"[^/.]+\.(main|para)\.txt"),
    ("aml-data", "news", r"[^/.]+\.main\.txt"),
    ("aml-train", "data-r11", r"[^/]+\.(jsonl|json|txt)"),                              # event43's round
    ("aml-train", "data-r12", r"[^/]+\.(jsonl|json|txt)"),                              # prep_stage, prep.py r12
    ("aml-train", "data-dev2", r"[^/]+\.(jsonl|json)"),                                 # second dev set (gold_merge)
    ("aml-train", "data-synth", r"(train-[abc]|heldout)\.jsonl"),                       # build_job --add, score_rows
    ("aml-train", "data-court", r"(train|heldout|v12-rows)\.jsonl|heldout-docs\.json"),
    ("aml-train", "measurements/report", r"[^/]+\.jsonl"),                              # evaluator reference documents
    ("aml-train", "measurements/court", r"[^/]+\.jsonl"),
    ("aml-train", "measurements/ner-ascii-validation", r"token-ids-dev\.jsonl"),         # news-model e2e.sh idsref=
    ("aml-train", "stage-clf", r"data(17v?)?/[^/]+\.(jsonl|txt)"),                     # classifier rows
    ("aml-train", "gemma-ft", r"data/train\.jsonl"),                                    # sft.py output
    ("aml-train", "downloads", r"aml[^/]+/out/[^/]+/" + SETS8 + r"\.jsonl|aml56/out/.+|answers-20261123/.+"),  # language-model answers (the evaluator's: answers-20261123)
    ("aml-train", "snapshots/before-review24-corrections-20261114", r"data/[^/]+\.(jsonl|json)"),  # NER20's rows
    ("aml-train", "models/bert-base-chinese", r"(config|tokenizer|tokenizer_config|special_tokens_map)\.json|vocab\.txt"),
    ("aml-train", "downloads/aml47/onnx", r".+"),                                       # the baseline stage classifier (AML_STAGE_MODEL)
    ("aml-train", "measurements/aml43-pair", r"(ner|ev)/.+"),                          # the retained news pair (AML_NEWS_MODELS), no caches
    ("aml-data", "labels/tools", r"(guideline\.md|check\.py|[^/]+-prompt\.md)"),        # news-event guideline, checker, prompts: original only
    ("aml-eval", "", r"(dev|train|evids-dev)\.jsonl|sg-[^/]+-r17\.tsv"),                # Aml.NewsEval documents, score.py rows
]


def is_model(path): return path.startswith(MODELS)


def sha(p): return hashlib.file_digest(open(p, "rb"), "sha256").hexdigest()


def sources():
    return {"aml-data": paths.DATA, "aml-train": paths.TRAIN, "aml-eval": paths.EVAL}


def closure(src):
    """{(root, rel path): source file} of the closure under the source roots."""
    out = {}
    for root, folder, pat in CLOSURE:
        rx = re.compile(pat); base = pathlib.Path(src[root]) / folder
        for dp, dn, fn in os.walk(base):
            dn[:] = [d for d in dn if d != "__pycache__" and ".bak-" not in d]  # backups of superseded data never travel
            for f in fn:
                p = pathlib.Path(dp) / f; rel = (folder + "/" + p.relative_to(base).as_posix()).lstrip("/")
                if rx.fullmatch(p.relative_to(base).as_posix()) and (is_model(root + "/" + rel) or not WEIGHTS.fullmatch(f)) and ".bak-" not in rel:
                    out[(root, rel)] = p
    return out


def classify(root, rel):
    """(role, set, heldback) of a bundle file."""
    parts = rel.split("/"); name = parts[-1]; stem = name.split(".")[0].lower()
    if is_model(root + "/" + rel): role, s = "model", parts[1] if parts[0] == "measurements" else parts[1] + "/" + parts[2]
    elif root == "aml-eval":
        role, s = ("heldback" if is_held(stem.split("-")[1] if stem.startswith("sg-") else stem) else "dev"), "eval:" + stem
    elif root == "aml-data":
        if parts[0] == "labels" and parts[1] == "tools": role, s = "guideline", "labels/tools"
        elif parts[0] == "labels": role, s = "labels", parts[1] if len(parts) > 2 else "labels"
        else: role, s = "raw", parts[0]
    elif parts[0] == "models": role, s = "tokenizer", "bert-base-chinese"
    elif parts[0] == "downloads": role, s = "answers", "%s/%s:%s" % (parts[1], parts[-2], stem)
    elif parts[0] in ("data-r11", "data-r12", "data-dev2", "snapshots"):
        rnd = {"data-r11": "r11", "data-r12": "r12", "data-dev2": "dev2", "snapshots": "ner20"}[parts[0]]
        split = stem.split("-")[-1] if "-" in stem else stem
        if parts[0] == "data-dev2" and name != "split.json": role, s = "dev", "dev2:" + stem
        else: role, s = ("split" if stem == "split" else "stats" if name.endswith(".txt") else
                         {"train": "train", "dev": "dev", "test": "heldback"}.get(split, "train")), rnd + ":" + stem
    elif parts[0] in ("data-synth", "data-court"):
        role, s = ("heldback" if is_held(stem) else "train"), parts[0] + ":" + stem
    elif parts[0] == "measurements":
        st = stem.split("-")[0]
        role = "heldback" if is_held(st) else "raw" if stem == "unseen" else "dev"
        s = parts[1] + ":" + stem
    elif parts[0] == "stage-clf":
        role = "stats" if name.endswith(".txt") else "train" if stem == "train" else "heldback" if is_held(stem) else "dev"
        s = parts[1] + ":" + stem
    elif parts[0] == "gemma-ft": role, s = "train", "gemma-ft:sft"
    else: role, s = "other", parts[0]
    held = role == "heldback" or (role == "answers" and is_held(stem))
    return role, s, held


def counts(p):
    """rows (lines of a .jsonl), persons and suspects of a data file; {} for a file that holds no rows."""
    if p.suffix not in (".jsonl", ".json"): return {}
    c = collections.Counter()
    try:
        items = [json.loads(l) for l in open(p, encoding="utf-8-sig") if l.strip()] if p.suffix == ".jsonl" else [json.load(open(p, encoding="utf-8-sig"))]
    except (ValueError, UnicodeDecodeError): return {"unparsable": 1}
    for r in items:
        if not isinstance(r, dict): continue
        if p.suffix == ".jsonl": c["rows"] += 1
        if isinstance(r.get("people"), list):
            per = [x for x in r["people"] if isinstance(x, dict) and str(x.get("role", "")).startswith("PER-")]
            c["persons"] += len(per); c["suspects"] += sum(x.get("party") == "Suspect" for x in per)
        elif isinstance(r.get("persons"), list): c["persons"] += len(r["persons"])
        elif "person" in r and "stage" in r: c["persons"] += 1
    return dict(c)


def manifest(dest, pseudonymised=False, note=""):
    files, sets = [], collections.defaultdict(collections.Counter)
    for root in ROOTS:
        for p in sorted((dest / root).rglob("*")):
            if not p.is_file(): continue
            rel = p.relative_to(dest / root).as_posix(); role, s, held = classify(root, rel); c = counts(p)
            files.append({"path": root + "/" + rel, "size": p.stat().st_size, "sha256": sha(p), "role": role, "set": s,
                          **({"heldback": True} if held else {})})
            k = sets[role + " " + s]; k["files"] += 1; k["bytes"] += p.stat().st_size; k.update(c)
    m = {"format": 1, "pseudonymised": pseudonymised, "roots": ROOTS, "note": note, "files": files,
         "sets": {k: dict(v) for k, v in sorted(sets.items())}}
    (dest / "MANIFEST.json").write_text(json.dumps(m, ensure_ascii=False, indent=1) + "\n", encoding="utf-8")
    return m


README = """AML screening PoC: data bundle ({kind})

What it is: the datasets the repository's tools read (labels, raw texts of the reports, press releases, court releases
and judgments, the train / development / held-back measured sets and rows, the language-model answers that
tools/Aml.StageEval replays, the news evaluator's documents in aml-eval), the baseline stage classifier
(aml-train/downloads/aml47/onnx) and the retained news model pair (aml-train/measurements/aml43-pair, ner/ and ev/),
without the bert-base-chinese weights (only its tokenizer files; see tools/data/README.md for the download).
The models were trained on these real texts and are as sensitive as they are.
MANIFEST.json lists every file with size, sha256, role and set; check it with
    python -I -X utf8 tools/data/bundle.py verify <this folder>

Use: set AML_DATA=<this folder>/aml-data, AML_TRAIN=<this folder>/aml-train and AML_EVAL=<a copy of this folder's
aml-eval> (the news evaluator writes there); the tools then run unchanged.

Held-back sets (manifest "heldback": true: v11, accept5, the held-out rows, the test splits and their answers) are
for aggregate counts computed once at the end of a change; never open their texts or per-person rows.

{warning}
"""
SENSITIVE = """SENSITIVE PERSONAL DATA. The texts and labels hold the real names of people in criminal proceedings. Keep this
folder only inside an encrypted archive (tools/data/README.md); do not copy it unencrypted, do not upload it, record every
copy in the copies register, and delete a removed judgment from every copy (bundle.py remove)."""


def build(dest):
    dest = pathlib.Path(dest)
    if dest.exists(): sys.exit("%s exists" % dest)
    files = closure(sources())
    for need in MODELS + ("aml-eval/",):  # a bundle without them is built, but not complete
        if not any((r + "/" + rel).startswith(need) for r, rel in files): print("MISSING (not in this bundle): %s" % need)
    for (root, rel), p in sorted(files.items()):
        out = dest / root / rel; out.parent.mkdir(parents=True, exist_ok=True); shutil.copy2(p, out)
    m = manifest(dest, note="built from AML_DATA / AML_TRAIN")
    (dest / "README.txt").write_text(README.format(kind="original", warning=SENSITIVE), encoding="utf-8")
    summary(m)


def summary(m):
    by = collections.defaultdict(collections.Counter)
    for f in m["files"]:
        r = f["path"].split("/")[0]; top = "/".join(f["path"].split("/")[1:3 if f["path"].split("/")[1] in ("measurements", "court", "stage-clf", "snapshots", "models") else 2])
        by[r + "/" + top]["files"] += 1; by[r + "/" + top]["bytes"] += f["size"]
    for k, v in sorted(by.items()): print("%-58s files %6d  %8.1f MB" % (k, v["files"], v["bytes"] / 1e6))
    for r in ROOTS:
        fs = [f for f in m["files"] if f["path"].startswith(r + "/")]
        print("%s: files %d, %.1f MB" % (r, len(fs), sum(f["size"] for f in fs) / 1e6))
    print("heldback files %d" % sum(f.get("heldback", False) for f in m["files"]))


def verify(root):
    root = pathlib.Path(root); m = json.load(open(root / "MANIFEST.json", encoding="utf-8"))
    want = {f["path"]: f for f in m["files"]}; have = set(); bad = 0
    for r in ROOTS:
        for p in (root / r).rglob("*"):
            if p.is_file(): have.add(p.relative_to(root).as_posix())
    extra = have - set(want); missing = set(want) - have
    top = {p.name for p in root.iterdir() if p.is_file()}
    stray = (top - META - {"MANIFEST.json"}) | ({p.name for p in root.iterdir() if p.is_dir()} - set(ROOTS))
    mapped = "PSEUDONYM-MAP.json" in top
    for k in sorted(set(want) & have):
        p = root / k
        if p.stat().st_size != want[k]["size"] or sha(p) != want[k]["sha256"]: bad += 1
    print("files %d | sha mismatches %d | missing %d | extra %d | stray top-level entries %d | pseudonymised %s | map file present %s"
          % (len(want), bad, len(missing), len(extra), len(stray), m["pseudonymised"], mapped))
    leak = m["pseudonymised"] and mapped
    if leak: print("FAIL: a pseudonymised root must not hold the map")
    ok = not (bad or missing or extra or stray or leak)
    print("VERIFY OK" if ok else "VERIFY FAILED")
    return ok


def _drop(x, doc):
    """x without the document: list items equal to it or dicts of it, dict keys equal to it. (new value, changed)."""
    if isinstance(x, list):
        out, ch = [], False
        for v in x:
            if v == doc or (isinstance(v, dict) and v.get("doc") == doc): ch = True; continue
            v2, c2 = _drop(v, doc); out.append(v2); ch |= c2
        return out, ch
    if isinstance(x, dict):
        out, ch = {}, False
        for k, v in x.items():
            if k == doc: ch = True; continue
            v2, c2 = _drop(v, doc); out[k] = v2; ch |= c2
        return out, ch
    return x, False


def remove(doc, root):
    """Delete document <doc> from a bundle: its own files (<doc>.json, <doc>.main.txt ...), its lines in every .jsonl
    (by "doc"; a row without one by "id", as in court/raw/index.jsonl, or by holding a 200-character piece of its text),
    its entries in every .json (lists, keys) and its lines in every .tsv (first column); then the manifest is rebuilt and, in an original, map entries no
    remaining file holds are dropped. Logs and stats that merely name the id are counted, not edited. Rewritten files keep
    their BOM and line ends. Counts only."""
    if not re.fullmatch(r"[A-Za-z0-9_.-]+", doc): sys.exit("not a document id")
    root = pathlib.Path(root); m = json.load(open(root / "MANIFEST.json", encoding="utf-8")); c = collections.Counter()
    like_manifest = (root / "MANIFEST.json").read_bytes()
    files = [root / f["path"] for f in m["files"]]
    own = [p for p in files if p.name.split(".")[0] == doc]
    texts = [p.read_text(encoding="utf-8-sig") for p in own if p.name.endswith(".txt")]
    texts += [r["text"] for p in files if p.suffix == ".jsonl" for r in (json.loads(l) for l in open(p, encoding="utf-8") if '"%s"' % doc in l)
              if r.get("doc") == doc and isinstance(r.get("text"), str)]
    # pieces of its text that a row without a doc id (sft examples) would hold: one near the head (kept by the 16,000
    # character cut), one from the middle; a short text whole
    pieces = {x for t in texts for x in ([t[100:300], t[len(t) // 2 - 100:len(t) // 2 + 100]] if len(t) >= 400 else [t] if len(t) >= 50 else [])}
    for p in files:  # a piece another document also holds (a shared header) identifies nothing
        if p.suffix == ".jsonl" and pieces:
            for l in open(p, encoding="utf-8"):
                r = json.loads(l) if l.strip() else {}
                if isinstance(r, dict) and r.get("doc") not in (None, doc): pieces = {x for x in pieces if x not in _strings(r)}
    for p in own: p.unlink(); c["files deleted"] += 1
    for p in files:
        if p in own: continue
        if p.suffix == ".jsonl":
            keep, n = [], 0
            for l in open(p, encoding="utf-8", newline=""):
                r = json.loads(l) if l.strip() else {}
                hit = r.get("doc") == doc if isinstance(r, dict) and "doc" in r else (isinstance(r, dict) and r.get("id") == doc) or bool(pieces) and any(x in _strings(r) for x in pieces)
                if hit: n += 1
                else: keep.append(l)
            if n: open(p, "w", encoding="utf-8", newline="").write("".join(keep)); c["jsonl lines dropped"] += n; c["jsonl files changed"] += 1
        elif p.suffix == ".json" and doc in p.read_text(encoding="utf-8-sig"):
            raw = p.read_text(encoding="utf-8-sig"); j, ch = _drop(json.loads(raw), doc)
            if ch:
                _write_like(p, json.dumps(j, ensure_ascii=False, indent=1 if "\n " in raw else None), p.read_bytes()); c["json files changed"] += 1
        elif p.suffix == ".tsv":
            ls = open(p, encoding="utf-8", newline="").readlines(); keep = [l for l in ls if l.split("\t")[0] != doc]
            if len(keep) != len(ls): open(p, "w", encoding="utf-8", newline="").write("".join(keep)); c["tsv lines dropped"] += len(ls) - len(keep)
    # after the pass: every text file that still names the id (logs, stats, the guideline, or a row keyed another way)
    left = [p for p in files if p.exists() and p.suffix in TEXT and doc in p.read_text(encoding="utf-8-sig", errors="replace")]
    if left: c["files still naming the id (not edited)"] = len(left)
    mp = root / "PSEUDONYM-MAP.json"
    if mp.exists():
        mm = json.load(open(mp, encoding="utf-8")); body = "".join(_all_text(root / f["path"]) for f in m["files"] if (root / f["path"]).exists() and (root / f["path"]).suffix in TEXT)
        gone = [k for k in mm["map"] if k not in body]
        for k in gone: del mm["map"][k]
        _write_like(mp, json.dumps(mm, ensure_ascii=False, indent=1), mp.read_bytes()); c["map entries dropped"] += len(gone)
    m2 = manifest(root, m["pseudonymised"], m.get("note", ""))
    rem = m.get("removed", []) + [hashlib.sha256(doc.encode()).hexdigest()]
    m2["removed"] = rem; _write_like(root / "MANIFEST.json", json.dumps(m2, ensure_ascii=False, indent=1) + "\n", like_manifest)
    print("removed 1 document:", dict(c))
    if left: print("NOT FULLY REMOVED: %d file(s) still name the id; check them by hand (tools/data/README.md, Remove)" % len(left))
    return c


def _write_like(p, s, like):
    """Write s (LF newlines) to p with the BOM, CRLF and final newline of the bytes `like` the file had."""
    if like.endswith(b"\n") and not s.endswith("\n"): s += "\n"
    if b"\r\n" in like: s = s.replace("\n", "\r\n")
    p.write_text(s, encoding="utf-8-sig" if like.startswith(b"\xef\xbb\xbf") else "utf-8", newline="")


def _strings(x):
    if isinstance(x, str): return x
    if isinstance(x, list): return "\n".join(_strings(v) for v in x)
    if isinstance(x, dict): return "\n".join(_strings(v) for v in x.values())
    return ""


def _all_text(p):
    t = p.read_text(encoding="utf-8-sig", errors="replace")
    if p.suffix == ".json":
        try: return _strings(json.loads(t))
        except ValueError: return t
    if p.suffix == ".jsonl":
        try: return "\n".join(_strings(json.loads(l)) for l in t.splitlines() if l.strip())
        except ValueError: return t
    return t


if __name__ == "__main__":
    a = sys.argv[1:]
    if len(a) == 2 and a[0] == "build": build(a[1])
    elif len(a) == 2 and a[0] == "verify": sys.exit(0 if verify(a[1]) else 1)
    elif len(a) == 3 and a[0] == "remove": remove(a[1], a[2])
    else: sys.exit(__doc__)
