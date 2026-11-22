"""Build or check a language-model stage reader job folder for tools/colab. Prints counts and hashes only.

usage: build_job.py infer <name> [--models TAG[,TAG...]] [--sets SET[,SET...]]
                                     Ollama tags; default gemma4:12b-it-q4_K_M (the deployed model); sets default all eight
       build_job.py finetune <name> [--size 12b|e4b] [--maxlen N] [--steps N] [--transformers latest|pin]
       build_job.py check <name>

build writes AML_COLAB_HOME/job<name>/ (refuses if it exists):
  run.sh        jobs/<kind>/run.sh
  infer.py      jobs/infer/infer.py (both kinds)
  train.py      jobs/finetune/train.py (finetune)
  prompt.txt    src/Aml.Infra/Models/stage-prompt.txt, the runtime prompt (copied, asserted byte-identical)
  job.env       the parameters run.sh exports (KIND, MODELS, SETS | MODEL, BASE, TRANSFORMERS, MAXLEN, MAX_STEPS)
  data/<set>.jsonl   the measured sets as infer.py reads them (common.data_line): infer the --sets (SETS in job.env,
                read by infer.py), finetune all eight
--sets: known names only, kept in the common.SETS order; a held-back set (paths.HELD) only when listed, with a WARNING
line (its answers are for the evaluator's cache, not to be opened or scored before the end).
  data/train.jsonl   sft.py's output (finetune)
--size picks MODEL, BASE and the default --transformers: 12b = unsloth/gemma-4-12b-it, gemma4:12b-it-q4_K_M, latest;
e4b = unsloth/gemma-4-E4B-it, gemma4:e4b-it-q4_K_M, pin (transformers 5.5.0, the Unsloth E4B notebook's pin).
--steps -1 (default) trains one epoch. --maxlen default 16384.

check: the scripts and prompt byte-identical to their sources; the folder holds exactly the expected files; job.env
well formed; the data files exactly those of SETS (all eight without it), each byte-identical to a fresh build from the measured rows, keys doc/text/persons/id/forms
only, unique documents, non-empty persons; finetune: train.jsonl byte-identical to sft.py's output, each example a user
turn wrapping the current prompt plus a one-person answer with a known stage, no train text equal to an evaluation
text (cut as the runtime cuts) and no train source document id in an evaluation set; run.sh without CR and passing
bash -n; scripts compile; sha256 of every file. Ends with ALL OK, exit 1 otherwise."""
import argparse, hashlib, json, pathlib, re, shutil, subprocess, sys
sys.dont_write_bytecode = True  # no __pycache__ in the repo folders it imports from
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import common
from common import gold, SETS, JOBS, PROMPT_FILE
sys.path.insert(0, str(common.REPO / "tools" / "colab"))
from colabjob import bash

SIZES = {"12b": ("unsloth/gemma-4-12b-it", "gemma4:12b-it-q4_K_M", "latest"),
         "e4b": ("unsloth/gemma-4-E4B-it", "gemma4:e4b-it-q4_K_M", "pin")}
KEYS = {"infer": ["KIND", "MODELS"], "finetune": ["KIND", "MODEL", "BASE", "TRANSFORMERS", "MAXLEN", "MAX_STEPS"]}
VALUE = re.compile(r"[A-Za-z0-9._:/-]+( [A-Za-z0-9._:/-]+)*")  # nothing the shell would expand
sha = lambda b: hashlib.sha256(b).hexdigest()
md5 = lambda t: hashlib.md5(t.encode()).hexdigest()


def files(kind):
    f = {"run.sh": JOBS / kind / "run.sh", "infer.py": JOBS / "infer" / "infer.py", "prompt.txt": PROMPT_FILE}
    if kind == "finetune": f["train.py"] = JOBS / "finetune" / "train.py"
    return f


def env_text(env):
    for k, v in env.items(): assert VALUE.fullmatch(v), "%s: letters, digits and ._:/- only" % k
    return "".join("%s='%s'\n" % kv for kv in env.items())


def read_env(path):
    return dict(re.fullmatch(r"([A-Z_]+)='([^']*)'", l).groups() for l in path.read_text(encoding="utf-8").splitlines())


def pick_sets(arg):
    """--sets a,b as known set names in the SETS order; None: all eight. Unknown names exit; held-back ones warn."""
    if arg is None: return list(SETS)
    names = [n for n in arg.split(",") if n]
    bad = sorted(set(names) - set(SETS))
    if bad or not names: sys.exit("--sets: unknown %s (known: %s)" % (",".join(bad) or "(none given)", ",".join(SETS)))
    held = common.paths.held(names)
    if held: print("WARNING held-back sets %s in the job: answers for the evaluator's cache only, not opened or scored before the end" % ",".join(held))
    return [s for s in SETS if s in names]


def job_sets(env):
    """The sets a job answers: SETS in job.env, all eight when absent (older infer jobs, every finetune job)."""
    return env["SETS"].split() if "SETS" in env else list(SETS)


def build(kind, name, env):
    J = common.HOME / ("job" + name)
    if J.exists(): sys.exit("%s exists: pick another name or delete it" % J)
    text = env_text(env); src = PROMPT_FILE.read_text(encoding="utf-8")
    assert "{{PERSONS}}" in src and "{{TEXT}}" in src, "stage-prompt.txt lacks a placeholder"
    if kind == "finetune": assert common.SFT.is_file(), "no %s: run sft.py first" % common.SFT
    (J / "data").mkdir(parents=True)
    for f, s in files(kind).items(): shutil.copyfile(s, J / f)
    assert (J / "prompt.txt").read_bytes() == PROMPT_FILE.read_bytes()
    (J / "job.env").write_text(text, encoding="utf-8", newline="\n")
    for s in job_sets(env):
        b = common.eval_bytes(s); (J / "data" / (s + ".jsonl")).write_bytes(b)
        docs = [json.loads(l) for l in b.decode("utf-8").splitlines()]
        print("%s: documents %d persons %d" % (s, len(docs), sum(len(d["persons"]) for d in docs)))
    if kind == "finetune":
        shutil.copyfile(common.SFT, J / "data" / "train.jsonl")
        print("train: examples %d" % sum(1 for _ in open(J / "data" / "train.jsonl", encoding="utf-8")))
    print("built %s (%s)\nnext: python -I tools/llm-stage/build_job.py check %s" % (J, " ".join("%s=%s" % kv for kv in env.items()), name))
    print("      python -I tools/colab/colabjob.py package %s %s %s" % (J, name, "--alive infer.py --milestones 'DONE|FAIL |MODEL_START| sanity | think: | pulled | tps '" if kind == "infer" else "--extra lora,Modelfile --alive 'train.py|infer.py'"))
    print("      python -I tools/colab/colabjob.py check %s && bash tools/colab/run_job.sh %s L4 <download dir>" % (name, name))


def check(name):
    ok = True
    def c(cond, msg):
        nonlocal ok; ok &= bool(cond); print(("ok   " if cond else "FAIL ") + msg)
    J = common.HOME / ("job" + name)
    env = read_env(J / "job.env"); kind = env.get("KIND"); sets = job_sets(env)
    keys = KEYS.get(kind, []) + (["SETS"] if kind == "infer" and "SETS" in env else [])
    c(kind in KEYS and list(env) == keys and all(VALUE.fullmatch(v) for v in env.values()), "job.env: " + " ".join("%s=%s" % kv for kv in env.items()))
    c(sets and sets == [s for s in SETS if s in sets], "sets %s: known, unique, in the SETS order%s"
      % (",".join(sets), "; held-back " + ",".join(common.paths.held(sets)) if common.paths.held(sets) else ""))
    if kind not in KEYS or not set(sets) <= set(SETS): print("CHECK FAILED"); return False
    want = set(files(kind)) | {"job.env"} | {"data/%s.jsonl" % s for s in sets} | ({"data/train.jsonl"} if kind == "finetune" else set())
    have = {p.relative_to(J).as_posix() for p in J.rglob("*") if p.is_file()}
    c(have == want, "folder holds exactly the expected %d files%s" % (len(want), "" if have == want else ": extra %s missing %s" % (sorted(have - want), sorted(want - have))))
    for f, s in files(kind).items():
        b = (J / f).read_bytes(); c(b == s.read_bytes(), "%s byte-identical to %s, sha256 %s" % (f, s.relative_to(common.REPO).as_posix(), sha(b)))
    run = (J / "run.sh").read_bytes(); p = subprocess.run([bash(), "-n"], input=run, capture_output=True)
    c(b"\r" not in run and p.returncode == 0, "run.sh has no CR and passes bash -n " + p.stderr.decode(errors="replace").strip()[:200])
    for f in ["infer.py"] + (["train.py"] if kind == "finetune" else []):
        try: compile((J / f).read_text(encoding="utf-8"), f, "exec"); c(True, "compiles " + f)
        except SyntaxError as e: c(False, "compiles %s: %s" % (f, e.msg))

    ev, calls = [], 0
    for s in sets:
        b = (J / "data" / (s + ".jsonl")).read_bytes(); docs = [json.loads(l) for l in b.decode("utf-8").splitlines()]; ev += docs
        c(b == common.eval_bytes(s), "data/%s.jsonl byte-identical to the measured rows, sha256 %s" % (s, sha(b)))
        ks = {k for d in docs for k in d} | {k for d in docs for p in d["persons"] for k in p}
        c(ks == {"doc", "text", "persons", "id", "forms"}, "%s keys %s" % (s, " ".join(sorted(ks))))
        c(len({d["doc"] for d in docs}) == len(docs) and all(d["persons"] and len({p["id"] for p in d["persons"]}) == len(d["persons"])
                                                            and all(p["forms"] and isinstance(p["id"], str) for p in d["persons"]) for d in docs),
          "%s: documents %d persons %d (cut by infer.py %d), unique ids, forms present"
          % (s, len(docs), sum(len(d["persons"]) for d in docs), sum(len(d["text"]) > common.LONG for d in docs)))
        calls += sum(len(d["persons"]) for d in docs)
    print("     calls (persons over the %d sets) %d, documents %d" % (len(sets), calls, len(ev)))

    if kind == "finetune":
        b = (J / "data" / "train.jsonl").read_bytes()
        c(common.SFT.is_file() and b == common.SFT.read_bytes(), "data/train.jsonl byte-identical to %s, sha256 %s" % (common.SFT, sha(b)))
        P = PROMPT_FILE.read_text(encoding="utf-8"); head, rest = P.split("{{PERSONS}}"); mid, tail = rest.split("{{TEXT}}")
        rows = [json.loads(l) for l in b.decode("utf-8").splitlines()]; texts, shape = set(), True
        for r in rows:
            m = r["messages"]; u = m[0]["content"]; a = json.loads(m[1]["content"])["persons"]
            shape &= [x["role"] for x in m] == ["user", "assistant"] and u.startswith(head) and u.endswith(tail) and mid in u \
                and len(a) == 1 and a[0]["id"] == "1" and a[0]["stage"] in gold.STAGES
            body = u[len(head):len(u) - len(tail)]; texts.add(md5(body[body.index(mid) + len(mid):]))
        c(rows and shape, "train: %d examples, each the current prompt for person 1 and a one-person answer with a known stage" % len(rows))
        c(not any(md5(common.cut(d["text"])) in texts for d in ev), "train shares no (cut) text with the %d evaluation documents" % len(ev))
        src = {json.loads(l)["doc"] for l in open(common.ROWS, encoding="utf-8")}
        c(not src & {d["doc"] for d in ev}, "train source documents (%d, %s) share no id with the evaluation sets" % (len(src), common.ROWS))
    print("ALL OK" if ok else "CHECK FAILED")
    return ok


def main():
    a = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = a.add_subparsers(dest="cmd", required=True)
    i = sub.add_parser("infer"); i.add_argument("name"); i.add_argument("--models", default="gemma4:12b-it-q4_K_M")
    i.add_argument("--sets", help="comma-separated measured sets (default all eight): " + ",".join(SETS))
    f = sub.add_parser("finetune"); f.add_argument("name"); f.add_argument("--size", choices=SIZES, default="12b")
    f.add_argument("--maxlen", type=int, default=16384); f.add_argument("--steps", type=int, default=-1)
    f.add_argument("--transformers", choices=["latest", "pin"])
    k = sub.add_parser("check"); k.add_argument("name")
    args = a.parse_args()
    if not re.fullmatch(r"[A-Za-z0-9_-]+", args.name): a.error("name: letters, digits, _ and - only")
    if args.cmd == "check": sys.exit(0 if check(args.name) else 1)
    if args.cmd == "infer": env = {"KIND": "infer", "MODELS": " ".join(m for m in args.models.split(",") if m), "SETS": " ".join(pick_sets(args.sets))}
    else:
        model, base, tf = SIZES[args.size]
        env = {"KIND": "finetune", "MODEL": model, "BASE": base, "TRANSFORMERS": args.transformers or tf,
               "MAXLEN": str(args.maxlen), "MAX_STEPS": str(args.steps)}
    build(args.cmd, args.name, env)


if __name__ == "__main__":
    main()
