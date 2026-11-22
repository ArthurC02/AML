"""Builds a Colab job folder AML_COLAB_HOME/job<name>/ for tools/colab from a data round and a start model, and checks
it the way the former chk/mk scripts did. Prints counts and hashes only.

usage: build_job.py ev <name> --start DIR --rows DIR [--parent JOBDIR] [--add FILE ...] [--repeat N] [--epochs N] [--lr X] [--note TEXT]
       build_job.py ner <name> --start DIR --rows DIR [--parent-counts TP FP FN] [--epochs N] [--lr X] [--note TEXT]

ev:  data/ev-train.jsonl = the round's ev-train rows without the case-only rows, each row's labels cut to the start
     model's label set, then every --add file (rows {doc,mention,sentence,labels}) --repeat times (default 1).
     data/ev-dev.jsonl = the --parent job's dev rows byte for byte (keeps the row-level dev number comparable), else the
     round's ev-dev rows made the same way. With --parent the base train rows are compared with the parent's rows
     (same doc/mention/sentence and keys; label edits counted). Defaults: 1 epoch, lr 1e-5 (job43).
ner: data/ner-{train,dev}.jsonl copied from the round; trains the document-role head on the frozen --start encoder.
     --parent-counts are the start model's dev TP FP FN: the trainer asserts the zero head reproduces them.
     Defaults: 30 epochs, lr 0.001 (job20).
Both: code from this folder, models/bert-base-chinese and models/<model>-start, run.sh and job.json from jobs/<model>/,
job.json with the sha256 of every file. Then load every row through the job's own reader (needs transformers).
"""
import argparse, hashlib, json, os, pathlib, shutil, sys
from collections import Counter

HERE = pathlib.Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import paths

CODE = {"ev": ["train_ev.py", "train_ev_reviewed.py", "event_pooling.py", "gpu_smoke_reviewed.py"],
        "ner": ["train_ner.py", "train_ner_document.py", "ner_document.py"]}
MODEL_FILES = ("config.json", "vocab.txt", "tokenizer.json", "tokenizer_config.json", "special_tokens_map.json")
sha = lambda p: hashlib.file_digest(open(p, "rb"), "sha256").hexdigest()
dump = lambda r: json.dumps(r, ensure_ascii=False) + "\n"


def base_rows(lines, known):
    """Rows of a data round for a job: case-only rows left out, labels the start model has no output for dropped."""
    rows = []
    for line in lines:
        r = json.loads(line)
        if r.get("caseonly"): continue
        r["labels"] = [l for l in r["labels"] if l in known]; rows.append(r)
    return rows


def check_added(r, known):
    assert set(r) == {"doc", "mention", "sentence", "labels"} and r["mention"] in r["sentence"] and set(r["labels"]) <= known, r["doc"]
    return r


def copy_model(src, dst, weights):
    dst.mkdir(parents=True)
    for n in MODEL_FILES:
        if (src / n).is_file(): shutil.copyfile(src / n, dst / n)
    if weights: shutil.copyfile(src / "model.safetensors", dst / "model.safetensors")


def write(path, rows):
    with open(path, "w", encoding="utf-8", newline="\n") as out:
        for r in rows: out.write(dump(r))


def main():
    a = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    a.add_argument("model", choices=("ev", "ner")); a.add_argument("name")
    a.add_argument("--start", required=True, type=pathlib.Path); a.add_argument("--rows", required=True, type=pathlib.Path)
    a.add_argument("--parent", type=pathlib.Path); a.add_argument("--add", nargs="*", default=[]); a.add_argument("--repeat", type=int, default=1)
    a.add_argument("--parent-counts", nargs=3, type=int); a.add_argument("--epochs", type=int); a.add_argument("--lr", type=float)
    a.add_argument("--note", default="")
    x = a.parse_args()
    m = x.model
    assert x.name.replace("_", "").replace("-", "").isalnum(), "name: letters, digits, _ and - only"
    assert m == "ev" or not (x.parent or x.add), "--parent and --add are for ev"
    assert m == "ner" or not x.parent_counts, "--parent-counts is for ner"
    J = pathlib.Path(paths.COLAB_HOME) / ("job" + x.name)
    J.mkdir(parents=True, exist_ok=False); (J / "data").mkdir()
    for n in CODE[m]: shutil.copyfile(HERE / n, J / n)
    copy_model(pathlib.Path(paths.BERT), J / "models/bert-base-chinese", False)
    copy_model(x.start, J / "models" / (m + "-start"), True)
    cfg = json.loads((x.start / "config.json").read_text(encoding="utf-8"))

    if m == "ev":
        known = set(cfg["id2label"].values())
        rows = base_rows(open(x.rows / "ev-train.jsonl", encoding="utf-8"), known)
        added = [check_added(json.loads(l), known) for f in x.add for l in open(f, encoding="utf-8")]
        write(J / "data/ev-train.jsonl", rows + added * x.repeat)
        if x.parent: shutil.copyfile(x.parent / "data/ev-dev.jsonl", J / "data/ev-dev.jsonl")
        else: write(J / "data/ev-dev.jsonl", base_rows(open(x.rows / "ev-dev.jsonl", encoding="utf-8"), known))
        print("base rows", len(rows), "added rows", len(added), "x", x.repeat, "| dev from", "parent job" if x.parent else "round")
        if x.parent:
            old = [json.loads(l) for l in open(x.parent / "data/ev-train.jsonl", encoding="utf-8")]
            assert len(old) >= len(rows), (len(old), len(rows))
            for p, q in zip(rows, old):
                assert (p["doc"], p["mention"], p["sentence"]) == (q["doc"], q["mention"], q["sentence"]) and set(p) == set(q), p["doc"]
            print("base rows equal the parent's first", len(rows), "rows | label edits vs parent", sum(p["labels"] != q["labels"] for p, q in zip(rows, old)))
        support = Counter(l for r in rows + added * x.repeat for l in r["labels"])
        labels = [cfg["id2label"][str(i)] for i in range(len(cfg["id2label"]))]
        assert labels == sorted(l for l, c in support.items() if c >= 5), sorted(set(labels) ^ {l for l, c in support.items() if c >= 5})
    else:
        assert not cfg.get("aml_ner_document_roles", False) and cfg["max_position_embeddings"] == 512, "start from a plain 512-position NER encoder"
        for s in ("train", "dev"): shutil.copyfile(x.rows / f"ner-{s}.jsonl", J / f"data/ner-{s}.jsonl")

    tmpl = HERE / "jobs" / m
    defaults = {"ev": (1, 1e-05), "ner": (30, 0.001)}[m]
    epochs, lr = x.epochs or defaults[0], x.lr if x.lr is not None else defaults[1]
    run = (tmpl / "run.sh").read_text(encoding="utf-8").replace("@NAME@", x.name).replace("@EPOCHS@", str(epochs)).replace("@LR@", str(lr))
    (J / "run.sh").write_text(run, encoding="utf-8", newline="\n")
    job = json.loads((tmpl / "job.json").read_text(encoding="utf-8"))
    job.update(epochs=epochs, lr=lr, initial=x.start.as_posix())
    if m == "ner":
        job["ner_ascii_lower"] = cfg.get("aml_ner_ascii_lower", False)
        if x.parent_counts: job["document_parent_dev_counts"] = x.parent_counts
        else: del job["document_parent_dev_counts"]  # the trainer checks the counts whenever the key is present
    job["sha256"] = {p.relative_to(J).as_posix(): sha(p) for p in sorted(J.rglob("*")) if p.is_file()}
    if x.note: job["note"] = x.note
    (J / "job.json").write_text(json.dumps(job, indent=1), encoding="utf-8")
    assert not any(k.endswith("-test.jsonl") for k in job["sha256"]), "test rows never go into a job"
    assert all(job["sha256"][k] == sha(HERE / k) for k in CODE[m])

    # Load every row through the job's own reader, as the trainer will (chk41/chk37).
    os.chdir(J); sys.path.insert(0, str(J)); sys.dont_write_bytecode = True  # no __pycache__ in the job
    if m == "ev":
        import train_ev as T
        tr, dv = T.load("train"), T.load("dev")
        print("loaded train", len(tr), "dev", len(dv), "positive train", sum(bool(r[2]) for r in tr), "longest", max(len(r[0]) for r in tr + dv), "over 512", sum(len(r[0]) > 512 for r in tr + dv))
    else:
        import train_ner as T
        flag = cfg.get("aml_ner_ascii_lower", False)
        tr, dv = T.load("train", flag), T.load("dev", flag)
        print("loaded train docs", len(tr), "dev docs", len(dv), "ascii lower", flag)
    for k in ("data/" + m + "-train.jsonl", "data/" + m + "-dev.jsonl", "models/" + m + "-start/model.safetensors"): print(k, job["sha256"][k])
    print("ok job%s: %d files in %s" % (x.name, len(job["sha256"]), J))


if __name__ == "__main__":
    main()
