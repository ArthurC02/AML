"""Self-check of the pure helpers of prep.py, build_job.py and paths.py on made-up inputs. Needs no data, model or GPU.
usage: python -I selfcheck.py   -> "selfcheck OK" or an AssertionError"""
import json, os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import build_job, paths, prep

# prep.normalise: NFKC per character, one to one (a character whose NFKC form is longer stays as it is)
assert prep.normalise("ＡＢ１２！？；") == "AB12!?;"
assert prep.normalise("㍿") == "㍿" and len(prep.normalise("陳志明㍿")) == 4
# prep.occurrences: every start, overlaps included; an empty needle yields nothing
assert list(prep.occurrences("陳志明與陳志明", "陳志明")) == [(0, 3), (4, 7)]
assert list(prep.occurrences("aaa", "aa")) == [(0, 2), (1, 3)] and list(prep.occurrences("abc", "")) == []
# prep.sentences: split after 。！？； and at line ends, blank pieces dropped
t = "陳志明被起訴。李小四被逮捕！\n\n共犯？"
assert [t[s:e] for s, e in prep.sentences(t)] == ["陳志明被起訴。", "李小四被逮捕！", "共犯？"]
# prep.event_label: type, with "-certainty" unless Stated
assert prep.event_label({"type": "Indictment", "certainty": "Stated"}) == "Indictment"
assert prep.event_label({"type": "Conviction", "certainty": "Negated"}) == "Conviction-Negated"
# prep.GROUP (NewsModelReading.Group): 等N人 / 等人 names a group; 平等, 相等 do not
assert prep.GROUP.search("陳志明等3人") and prep.GROUP.search("李小四等人") and prep.GROUP.search("陳志明等共五名被告")
assert not prep.GROUP.search("平等人權") and not prep.GROUP.search("相等人數")
# prep.event_rows: the rows of the party whose sentence span holds the trigger, at every verbatim repeat of the sentence
raw = "甲被起訴。乙被起訴。甲被起訴。"
rows = {("P1", 0): {"span": (0, 5)}, ("P1", 10): {"span": (10, 15)}, ("P2", 5): {"span": (5, 10)}}
assert prep.event_rows(raw, "甲被起訴。", "起訴", "P1", rows) == {("P1", 0), ("P1", 10)}
assert prep.event_rows(raw, "甲被起訴。", None, "P2", rows) == set()

# build_job.base_rows: case-only rows out, labels cut to the start model's set, other fields kept
lines = [json.dumps(r, ensure_ascii=False) for r in (
    {"doc": "d1", "mention": "陳志明", "sentence": "陳志明被起訴。", "labels": ["Indictment", "Unknown"]},
    {"doc": "d1", "mention": "李小四", "sentence": "陳志明被起訴。", "labels": [], "caseonly": True},
    {"doc": "d2", "mention": "李小四", "sentence": "李小四被逮捕。", "labels": ["prior"]})]
got = build_job.base_rows(lines, {"Indictment", "Arrest"})
assert [r["labels"] for r in got] == [["Indictment"], []] and [r["doc"] for r in got] == ["d1", "d2"]
# build_job.check_added: exactly the four keys, the mention inside the sentence, known labels only
ok = {"doc": "s1", "mention": "陳志明", "sentence": "陳志明被判有罪。", "labels": ["Conviction"]}
assert build_job.check_added(dict(ok), {"Conviction"}) == ok
for bad in ({**ok, "mention": "李小四"}, {**ok, "labels": ["Acquittal"]}, {**ok, "caseonly": True}):
    try: build_job.check_added(bad, {"Conviction"}); raise SystemExit("check_added accepted a bad row")
    except AssertionError: pass

# paths: r12 is r11 plus v12/ver before the dev corrections, which rank last in both
r11, r12 = paths.ROUND_LABELS["r11"], paths.ROUND_LABELS["r12"]
assert r12[:-2] == r11[:-1] and r12[-2].endswith("v12/ver/") and r11[-1] == r12[-1] and r11[-1].endswith("review-current/fix/")
assert paths.round_dir("r11").endswith("data-r11/") and all(d.endswith("/") for d in r12)
print("selfcheck OK")
