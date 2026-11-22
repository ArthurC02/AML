"""Asserts on bundle.py and pseudonymise.py with a made-up tiny bundle (made-up names, no real data); prints selfcheck OK.
Covers build -> manifest -> verify (and a tampered file failing it), pseudonymise (offsets kept, masked forms consistent
with the full name, the map only inside the original), remove (a document gone from files, rows, index rows and the map,
BOM and line ends kept) and a stray top-level directory failing verify."""
import json, os, pathlib, shutil, subprocess, sys, tempfile

HERE = pathlib.Path(__file__).resolve().parent
T = pathlib.Path(tempfile.mkdtemp(prefix="aml-bundle-selfcheck-"))
os.environ["AML_DATA"], os.environ["AML_TRAIN"] = str(T / "src-data"), str(T / "src-train")
sys.dont_write_bytecode = True
sys.path.insert(0, str(HERE))
import bundle  # noqa: E402

FULL, OTHER = "王大明", "李小華"      # made up
T1 = "被告王大明涉嫌詐欺，王○○到案。大明表示不知情。\n證人李小華作證。本案由地方檢察署偵辦，並已移送法院審理，相關人員均已到庭說明。"
T2 = "被告李小華經起訴。本案另有其他關係人。"


def w(p, s):
    p = pathlib.Path(p); p.parent.mkdir(parents=True, exist_ok=True); p.write_text(s, encoding="utf-8")


def jl(rows): return "".join(json.dumps(r, ensure_ascii=False) + "\n" for r in rows)


def py(*a):
    return subprocess.run([sys.executable, "-I", "-X", "utf8", "-B", *map(str, a)], capture_output=True, text=True, encoding="utf-8")


D, R = T / "src-data", T / "src-train"
w(D / "labels/v6/ver/jd-0001.json", json.dumps({"doc": "jd-0001", "persons": [
    {"id": "P1", "party": "Suspect", "mentions": [FULL, "大明", "王○○"], "descriptors": []},
    {"id": "P2", "party": "Witness", "mentions": [OTHER], "descriptors": []}], "orgs": [],
    # a trigger that starts inside the name: replaced on its own it would no longer be part of its sentence
    "events": [{"sentence": "被告王大明涉嫌詐欺，王○○到案。", "trigger": "明涉嫌", "persons": ["P1"], "orgs": [], "also": ["大明表示不知情。"]}]}, ensure_ascii=True))
w(D / "labels/v6/ver/jd-0002.json", json.dumps({"doc": "jd-0002", "persons": [{"id": "P1", "party": "Suspect", "mentions": [OTHER]}], "orgs": [], "events": []}, ensure_ascii=False))
w(D / "judgments/jd-0001.main.txt", T1); w(D / "judgments/jd-0002.main.txt", T2)
w(D / "judgments/jd-0001.html", "<html>not in the closure</html>")
w(D / "court/raw/index.jsonl", jl([{"id": "jd-0001", "url": "u1", "title": "t1"}, {"id": "jd-0002", "url": "u2", "title": "t2"}]))  # id, no doc
s, e = T1.index(FULL), T1.index(FULL) + 3; s2 = T1.index(OTHER)
w(R / "data-r12/ner-train.jsonl", jl([{"doc": "jd-0001", "text": T1, "spans": [[s, e, "PER-Suspect"], [s2, s2 + 3, "PER-Other"]]},
                                      {"doc": "jd-0002", "text": T2, "spans": []}]))
w(R / "data-r12/split.json", json.dumps({"train": ["jd-0001", "jd-0002"], "dev": [], "test": []}))
w(R / "measurements/report/dev-stage.jsonl", jl([{"doc": "jd-0002", "text": T2, "people": [{"id": "P1", "role": "PER-Suspect", "party": "Suspect", "mentions": [OTHER]}], "events": []}]))
w(R / "gemma-ft/data/train.jsonl", jl([{"messages": [{"role": "user", "content": "x " + T1}, {"role": "assistant", "content": "{}"}]}]))
w(R / "models/bert-base-chinese/model.safetensors", "weights, never bundled")

# build, manifest, verify
O, P = T / "bundle/original", T / "bundle/pseudonymised"
bundle.build(O)
m = json.load(open(O / "MANIFEST.json", encoding="utf-8"))
paths_ = {f["path"] for f in m["files"]}
assert "aml-data/judgments/jd-0001.html" not in paths_ and not any("safetensors" in p for p in paths_), "closure took a file it must not"
assert {f["role"] for f in m["files"]} >= {"labels", "raw", "train", "dev", "split"}, "roles"
assert m["sets"]["dev report:dev-stage"]["suspects"] == 1
assert bundle.verify(O)
f = O / "aml-data/judgments/jd-0002.main.txt"; keep = f.read_bytes(); f.write_bytes(keep + b"x")
assert not bundle.verify(O); f.write_bytes(keep); assert bundle.verify(O)

# pseudonymise
r = py(HERE / "pseudonymise.py", O, P, "--map", O / "PSEUDONYM-MAP.json", "--seed", "00" * 32)
assert r.returncode == 0, r.stderr[-800:]
assert FULL not in r.stdout + r.stderr and OTHER not in r.stdout + r.stderr, "a name was printed"
assert "inventory forms left unreplaced 0" in r.stdout, r.stdout
assert (O / "PSEUDONYM-MAP.json").exists() and not list(P.rglob("PSEUDONYM-MAP.json")), "map placement"
mp = json.load(open(O / "PSEUDONYM-MAP.json", encoding="utf-8"))["map"]
ps = mp[FULL]
assert len(ps) == 3 and ps != FULL and mp["大明"] == ps[1:] and mp["王○○"] == ps[0] + "○○", "masked and given forms follow the full name"
row = json.loads((P / "aml-train/data-r12/ner-train.jsonl").read_text(encoding="utf-8").splitlines()[0])
assert row["text"][s:e] == ps and row["text"][s2:s2 + 3] == mp[OTHER], "offsets kept"
lab = json.loads((P / "aml-data/labels/v6/ver/jd-0001.json").read_text(encoding="utf-8"))
assert lab["persons"][0]["mentions"] == [ps, ps[1:], ps[0] + "○○"], "an escaped label file replaced too"
ev, raw1 = lab["events"][0], (P / "aml-data/judgments/jd-0001.main.txt").read_text(encoding="utf-8")
assert ev["sentence"] in raw1 and ev["trigger"] in ev["sentence"] and ev["also"][0] in raw1 and FULL not in ev["sentence"], "event strings stay substrings"
for q in P.rglob("*"):
    if q.is_file(): assert FULL not in q.read_text(encoding="utf-8") and "大明" not in q.read_text(encoding="utf-8"), q.name
assert bundle.verify(P)
r2 = py(HERE / "pseudonymise.py", O, O / "inside", "--map", O / "PSEUDONYM-MAP.json")
assert r2.returncode != 0, "dest inside the original must be refused"
r3 = py(HERE / "pseudonymise.py", O, T / "x", "--map", T / "map.json")
assert r3.returncode != 0 and not (T / "map.json").exists(), "a map outside the original must be refused"

# remove
sp = O / "aml-train/data-r12/split.json"; sp.write_bytes(b"\xef\xbb\xbf" + sp.read_bytes().replace(b"\n", b"\r\n") + b"\r\n")  # BOM, CRLF
crlf_manifest = b"\r\n" in (O / "MANIFEST.json").read_bytes()
bundle.remove("jd-0001", O)
assert not (O / "aml-data/judgments/jd-0001.main.txt").exists() and not (O / "aml-data/labels/v6/ver/jd-0001.json").exists()
assert "jd-0001" not in (O / "aml-train/data-r12/ner-train.jsonl").read_text(encoding="utf-8")
assert "jd-0001" not in (O / "aml-data/court/raw/index.jsonl").read_text(encoding="utf-8"), "an index row keyed by id"
assert json.load(open(sp, encoding="utf-8-sig"))["train"] == ["jd-0002"]
b = sp.read_bytes(); assert b.startswith(b"\xef\xbb\xbf") and b.endswith(b"\r\n") and b.count(b"\n") == b.count(b"\r\n"), "BOM and CRLF kept"
assert (b"\r\n" in (O / "MANIFEST.json").read_bytes()) == crlf_manifest, "manifest line ends kept"
assert (O / "aml-train/gemma-ft/data/train.jsonl").read_text(encoding="utf-8") == "", "a row without a doc id holding its text"
mp2 = json.load(open(O / "PSEUDONYM-MAP.json", encoding="utf-8"))["map"]
assert FULL not in mp2 and "大明" not in mp2 and OTHER in mp2, "map entries of the removed document only are dropped"
assert bundle.verify(O)
bundle.remove("jd-0001", P); assert bundle.verify(P) and not (P / "aml-data/judgments/jd-0001.main.txt").exists()
(P / "extra").mkdir(); assert not bundle.verify(P), "a stray top-level directory"; (P / "extra").rmdir()
shutil.rmtree(T, ignore_errors=True)
print("selfcheck OK")
