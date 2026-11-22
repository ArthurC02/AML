"""Asserts on the pure parts with made-up input; no data, no GPU, no network: common (cut, forms, data_line, user_turn),
jobs/infer/infer.py read_doc (prompt rendering, answer parsing) against a fake Ollama and its SETS, build_job's job.env
and --sets. Prints
'selfcheck OK'."""
import contextlib, io, json, os, pathlib, shutil, sys, tempfile, types
sys.dont_write_bytecode = True  # no __pycache__ in the repo folders it imports from
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import common, build_job

# common: cut as the runtime cuts, forms, data_line keeps suspects only
assert common.cut("案" * 16000) == "案" * 16000
long = "甲" * 10000 + "乙" * 1000 + "丙" * 6000
assert common.cut(long) == "甲" * 10000 + "\n（中間省略）\n" + "丙" * 6000
assert common.forms({"mentions": ["陳志明", "陳", "志明", "陳志明"]}) == ["陳志明", "志明"]
assert common.forms({"mentions": ["陳", "李"]}) == ["陳", "李"]
doc = {"doc": "d1", "text": "陳志明涉嫌詐欺，李小四為證人。", "events": [{"mention": "陳志明", "labels": ["Indictment"]}],
       "people": [{"id": "P1", "role": "PER-Suspect", "party": "Suspect", "mentions": ["陳志明"]},
                  {"id": "P2", "role": "PER-Witness", "party": "Other", "mentions": ["李小四"]}]}
line = json.loads(common.data_line(doc))
assert line == {"doc": "d1", "text": doc["text"], "persons": [{"id": "P1", "forms": ["陳志明"]}]}, line  # no stage, no event
assert common.data_line({**doc, "people": doc["people"][1:]}) == ""

# job.env: values the shell would expand are refused
assert build_job.env_text({"KIND": "infer", "MODELS": "gemma4:12b-it-q4_K_M a/b"}) == "KIND='infer'\nMODELS='gemma4:12b-it-q4_K_M a/b'\n"
for bad in ["$(x)", "a;b", "a'b", "", "a  b"]:
    try: build_job.env_text({"MODEL": bad}); raise SystemExit("accepted " + repr(bad))
    except AssertionError: pass

# --sets: SETS order, unknown names refused, held-back ones only with a warning line; job.env SETS read back
assert build_job.pick_sets(None) == common.SETS and build_job.pick_sets("accept") == ["accept"]
for bad in ["acept", "accept,x", ",", ""]:
    try: build_job.pick_sets(bad); raise AssertionError("accepted " + repr(bad))
    except SystemExit: pass
out = io.StringIO()
with contextlib.redirect_stdout(out): got = build_job.pick_sets("accept5,dev,v11,dev")
assert got == ["dev", "v11", "accept5"] and out.getvalue().startswith("WARNING held-back sets accept5,v11 "), (got, out.getvalue())
with contextlib.redirect_stdout(out := io.StringIO()): build_job.pick_sets("dev,accept")
assert out.getvalue() == ""
assert build_job.job_sets({"KIND": "infer", "MODELS": "m", "SETS": "accept v11"}) == ["accept", "v11"]
assert build_job.job_sets({"KIND": "infer", "MODELS": "m"}) == common.SETS  # older jobs and every finetune job

# infer.py against a fake requests: the prompt it sends and how it reads answers (SETS from the environment, as run.sh exports it)
os.environ["SETS"] = "accept v11"
prompt = "人：{{PERSONS}}\n文：{{TEXT}}\n"
sent, answers = [], []
class Resp:
    def __init__(self, content): self.status_code, self.content = 200, content
    def json(self): return {"message": {"content": self.content}, "prompt_eval_count": 1, "prompt_eval_duration": 1, "eval_count": 1, "eval_duration": 1, "done_reason": "stop"}
def post(url, json=None, timeout=None):
    sent.append(json); return Resp(answers.pop(0))
sys.modules["requests"] = types.SimpleNamespace(post=post, get=None, RequestException=OSError)
tmp = tempfile.mkdtemp(); cwd = os.getcwd()
try:
    shutil.copyfile(common.JOBS / "infer" / "infer.py", pathlib.Path(tmp) / "infer.py")
    pathlib.Path(tmp, "prompt.txt").write_text(prompt, encoding="utf-8")
    os.chdir(tmp); sys.path.insert(0, tmp)
    import infer
    assert infer.SETS == ["accept", "v11"] and infer.ALL == common.SETS
    d = {"doc": "d1", "text": "陳志明經起訴。李小四到案。", "persons": [{"id": "P1", "forms": ["陳志明", "志明"]}, {"id": "P2", "forms": ["李小四"]}]}
    answers += [json.dumps({"persons": [{"id": "P1", "stage": "INDICTED", "quote": "陳志明 經起訴"}, {"id": "P9", "stage": "CONVICTED", "quote": ""}]}),
                json.dumps({"persons": [{"id": "P2", "stage": "GUILTY", "quote": ""}]})]
    r = infer.read_doc("m", d)
    assert len(sent) == 2 and sent[0]["messages"][0]["content"] == common.user_turn(prompt, "P1", ["陳志明", "志明"], d["text"]) == "人：P1：陳志明、志明\n文：" + d["text"] + "\n"
    assert sent[0]["think"] is False and sent[0]["options"]["temperature"] == 0 and sent[0]["format"]["required"] == ["persons"]
    assert r["error"] is None and not r["cut"]
    assert r["persons"] == [{"id": "P1", "stage": "INDICTED", "quote": "陳志明 經起訴", "quote_ok": True},  # quote checked without whitespace
                            {"id": "P2", "stage": "MISSING", "quote": "", "quote_ok": False}]          # a stage outside the list is no answer
    assert infer.bad(r)
    sent.clear(); answers.append("not json")
    r = infer.read_doc("m", {"doc": "d2", "text": long, "persons": [{"id": "1", "forms": ["陳志明"]}]})
    assert r["cut"] and r["error"] == "JSONDecodeError" and r["persons"][0]["stage"] == "MISSING"  # the error names no text
    assert sent[0]["messages"][0]["content"] == common.user_turn(prompt, "1", ["陳志明"], common.cut(long))  # = sft.py's user turn
finally:
    os.chdir(cwd); shutil.rmtree(tmp); del os.environ["SETS"]
print("selfcheck OK")
