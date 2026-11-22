"""Language-model stage reader on the Colab VM (tools/llm-stage, one person per call, all eight sets): Gemma 4 through Ollama reads each listed person's legal stage. Standard library + requests.
usage: infer.py <model> [<model> ...]   (run from the job folder: data/<set>.jsonl, prompt.txt; writes out/, log.txt)
One /api/chat call per (document, person), the prompt listing that person only (stream false, JSON-schema format, temperature 0, seed 1). Output per model:
out/<model made filename-safe>/<set>.jsonl, one line per document, appended and flushed; a restart skips documents already
written. Before each model's full run the first 3 documents of the first set are checked (the sanity pass): 2 or more without parseable
JSON holding every listed id -> fail.txt, exit 3. log.txt carries counts, document ids, stage names and server status only."""
import json, os, re, sys, time
import requests

URL = "http://127.0.0.1:11434"
SETS = ["dev", "dev2", "v9", "accept", "accept3", "accept4", "v11", "accept5"]
STAGES = ["CONVICTED", "ACQUITTED", "DISMISSED", "INDICTED", "WANTED", "ALLEGED", "UNKNOWN"]
OPTIONS = {"temperature": 0, "seed": 1, "num_ctx": 24576, "num_predict": 4096}
CHUNK = 1
LONG, HEAD, TAIL = 16000, 10000, 6000
CUT_MARK = "\n（中間省略）\n"
SCHEMA = {"type": "object", "required": ["persons"], "properties": {"persons": {"type": "array", "items": {
    "type": "object", "required": ["id", "stage", "quote"],
    "properties": {"id": {"type": "string"}, "stage": {"type": "string", "enum": STAGES}, "quote": {"type": "string"}}}}}}
PROMPT = open("prompt.txt", encoding="utf-8").read()
assert "{{PERSONS}}" in PROMPT and "{{TEXT}}" in PROMPT
think = {"send": True, "known": False}  # whether to send think:false; known once the server has answered a call
WS = re.compile(r"\s+")


def log(msg):
    with open("log.txt", "a", encoding="utf-8") as f: f.write(time.strftime("%H:%M:%S ") + msg + "\n")


def fail(msg, code):
    log("FAIL " + msg)
    with open("fail.txt", "w", encoding="utf-8") as f: f.write(msg + "\n")
    sys.exit(code)


def server_error(r):
    try: return str(r.json().get("error", ""))[:200]
    except ValueError: return r.text[:200]


def chat(model, prompt):
    body = {"model": model, "messages": [{"role": "user", "content": prompt}], "stream": False, "format": SCHEMA, "options": OPTIONS}
    if think["send"]: body["think"] = False
    r = requests.post(URL + "/api/chat", json=body, timeout=1800)
    if r.status_code != 200 and "think" in body and not think["known"]:
        first = "%d %s" % (r.status_code, server_error(r))
        del body["think"]
        r = requests.post(URL + "/api/chat", json=body, timeout=1800)
        if r.status_code == 200:
            think.update(send=False, known=True); log("think: the server rejected think:false (%s); sent without it from now on" % first)
    elif r.status_code == 200 and not think["known"]:
        think["known"] = True; log("think: the server accepted think:false")
    if r.status_code != 200: raise RuntimeError("http %d: %s" % (r.status_code, server_error(r)))
    return r.json()


def squash(s): return WS.sub("", s)


def read_doc(model, d):
    text, cut = d["text"], len(d["text"]) > LONG
    if cut: text = text[:HEAD] + CUT_MARK + text[-TAIL:]
    groups = [d["persons"][k:k + CHUNK] for k in range(0, len(d["persons"]), CHUNK)]
    ids = [p["id"] for p in d["persons"]]
    rec = {"doc": d["doc"], "cut": cut, "seconds": 0.0, "persons": [], "error": None, "tokens": None}
    t0 = time.time(); got = {}
    try:
        for group in groups:  # at most CHUNK persons per call: an answer for 80 persons outruns num_predict
            persons = "\n".join("%s：%s" % (p["id"], "、".join(p["forms"])) for p in group)
            prompt = PROMPT.replace("{{PERSONS}}", persons).replace("{{TEXT}}", text)
            res = chat(model, prompt)
            rec["tokens"] = {k: res.get(k) for k in ("prompt_eval_count", "prompt_eval_duration", "eval_count", "eval_duration", "done_reason")}
            ans = json.loads(res["message"]["content"])
            mine = {q["id"] for q in group}
            for e in ans["persons"]:
                i = str(e.get("id", ""))
                if i in mine and i not in got and e.get("stage") in STAGES: got[i] = (e["stage"], str(e.get("quote", "")))
    except Exception as e:  # recorded per document; the message never carries document text
        rec["error"] = type(e).__name__ + (": " + str(e)[:200] if isinstance(e, (RuntimeError, requests.RequestException)) else "")
    rec["seconds"] = round(time.time() - t0, 1)
    whole = squash(d["text"])
    for i in ids:
        stage, quote = got.get(i, ("MISSING", ""))
        q = squash(quote)
        rec["persons"].append({"id": i, "stage": stage, "quote": quote, "quote_ok": bool(q) and q in whole})
    return rec


def done_docs(path):
    """Documents already written; a torn last line (crash mid-write) is dropped from the file."""
    if not os.path.exists(path): return {}
    keep, recs = [], {}
    for line in open(path, encoding="utf-8"):
        try: r = json.loads(line)
        except ValueError: continue
        keep.append(json.dumps(r, ensure_ascii=False) + "\n"); recs[r["doc"]] = r
    with open(path, "w", encoding="utf-8") as f: f.writelines(keep)
    return recs


def bad(r): return r["error"] is not None or any(p["stage"] == "MISSING" for p in r["persons"])


def run_model(model, data):
    tag = re.sub(r"[^A-Za-z0-9._-]", "_", model)
    os.makedirs(os.path.join("out", tag), exist_ok=True)
    total = sum(len(v) for v in data.values()); done = 0; win = []; errors_in_row = 0
    log("MODEL_START %s documents %d" % (model, total))
    for s in SETS:
        path = os.path.join("out", tag, s + ".jsonl"); have = done_docs(path)
        with open(path, "a", encoding="utf-8") as out:
            for k, d in enumerate(data[s]):
                if d["doc"] in have: done += 1
                else:
                    r = read_doc(model, d)
                    out.write(json.dumps(r, ensure_ascii=False) + "\n"); out.flush(); os.fsync(out.fileno())
                    have[d["doc"]] = r; done += 1
                    log("step %d/%d %s %s %.0fs" % (done, total, model, s, r["seconds"]))
                    if r["error"]: log("error %s %s" % (d["doc"], r["error"][:120]))
                    errors_in_row = errors_in_row + 1 if r["error"] else 0
                    if errors_in_row >= 5: fail("%s: 5 documents in a row failed, last %s" % (model, r["error"][:120]), 2)
                    if r["tokens"]: win.append(r["tokens"])
                    if len(win) == 20:
                        g = sum(t["eval_count"] or 0 for t in win) / max(1e-9, sum(t["eval_duration"] or 0 for t in win) / 1e9)
                        p = sum(t["prompt_eval_count"] or 0 for t in win) / max(1e-9, sum(t["prompt_eval_duration"] or 0 for t in win) / 1e9)
                        log("tps %s generate %.1f tok/s prompt %.1f tok/s over 20 documents" % (model, g, p)); win = []
                if s == SETS[0] and k == min(2, len(data[s]) - 1):
                    first = [have[x["doc"]] for x in data[s][:3]]; n_bad = sum(bad(r) for r in first)
                    if n_bad >= 2: fail("sanity %s: %d of %d first-set documents without parseable JSON holding the listed ids" % (model, n_bad, len(first)), 3)
                    log("sanity %s ok: %d of %d failed" % (model, n_bad, len(first)))
                    try:
                        for m in requests.get(URL + "/api/ps", timeout=30).json().get("models", []):
                            log("ps %s size %.1f GB in vram %.1f GB context %s" % (m.get("name"), m.get("size", 0) / 1e9, m.get("size_vram", 0) / 1e9, m.get("context_length")))
                    except Exception as e: log("ps failed " + type(e).__name__)
    log("MODEL_DONE " + model)
    try: requests.post(URL + "/api/generate", json={"model": model, "keep_alive": 0}, timeout=120)  # free the GPU for the next model
    except Exception as e: log("unload failed " + type(e).__name__)


def main():
    models = sys.argv[1:]; assert models, __doc__
    data = {s: [json.loads(l) for l in open(os.path.join("data", s + ".jsonl"), encoding="utf-8")] for s in SETS}
    log("data " + " ".join("%s %d docs %d persons" % (s, len(v), sum(len(d["persons"]) for d in v)) for s, v in data.items()))
    for m in models: run_model(m, data)
    log("JOB_DONE")
    with open("done.txt", "w") as f: f.write("DONE\n")


if __name__ == "__main__":
    main()
