"""Person-level legal-stage classifier: bert-base-chinese, 7-way head on [CLS].
Input: [CLS] name [SEP] evidence [SEP] (token types 0 / 1). The markers [unused1]/[unused2] in the evidence text are put
in as their vocabulary ids (1, 2) between separately encoded text pieces, as pilot_pairs.py did, so the tokenizer never
splits them. Runs from one folder (data/ + this file); needs internet only to download bert-base-chinese.
After each epoch, per development set (--sets, default dev,dev2,v9): out/pred-<set>-e<epoch>.jsonl {doc, person, kind,
gold, pred, probs}; accuracy, macro-F1, accuracy per kind. The held-back set v11 is never in --sets; --heldback
evaluates it once after the last epoch and prints the same aggregate counts only, no per-person file.
usage: train_stage.py [--epochs 4 --lr 3e-5 --batch 16 --max-len 512 --seed 1 --class-weights --model bert-base-chinese --save DIR --heldback]"""
import argparse, json, os, random, re, time
from collections import Counter

STAGES = ["UNKNOWN", "ALLEGED", "WANTED", "INDICTED", "DISMISSED", "CONVICTED", "ACQUITTED"]
MARK = re.compile(r"(\[unused[12]\])")


def enc(tok, s):
    """Token ids of a text holding [unused1]/[unused2] markers, each marker one id."""
    out = []
    for piece in MARK.split(s):
        if piece in ("[unused1]", "[unused2]"): out.append(tok.convert_tokens_to_ids(piece))
        elif piece: out += tok.encode(piece, add_special_tokens=False)
    return out


def ids(tok, r, max_len):
    name = enc(tok, r["name"])[:32]
    ev = enc(tok, r["evidence"])[:max_len - 3 - len(name)]  # prep crops to 512 already; this is the hard limit
    return [tok.cls_token_id] + name + [tok.sep_token_id] + ev + [tok.sep_token_id], len(name) + 2


def macro_f1(gold, pred):
    labels = set(gold) | set(pred); f = []
    for c in labels:
        tp = sum(g == p == c for g, p in zip(gold, pred)); fp = sum(p == c != g for g, p in zip(gold, pred)); fn = sum(g == c != p for g, p in zip(gold, pred))
        f.append(2 * tp / max(1, 2 * tp + fp + fn))
    return sum(f) / max(1, len(f))


def main():
    a = argparse.ArgumentParser()
    a.add_argument("--data", default="data"); a.add_argument("--out", default="out"); a.add_argument("--model", default="bert-base-chinese")
    a.add_argument("--epochs", type=int, default=4); a.add_argument("--lr", type=float, default=3e-5); a.add_argument("--batch", type=int, default=16)
    a.add_argument("--accum", type=int, default=1, help="gradient accumulation: micro-batch = batch / accum (OOM fallback)")
    a.add_argument("--max-len", type=int, default=512); a.add_argument("--seed", type=int, default=1); a.add_argument("--class-weights", action="store_true")
    a.add_argument("--sets", default="dev,dev2,v9"); a.add_argument("--heldback", action="store_true", help="evaluate v11 once after the last epoch, aggregate counts only")
    a.add_argument("--save", default="", help="folder for the final weights and tokenizer (none by default)"); a.add_argument("--train-limit", type=int, default=0); a.add_argument("--eval-limit", type=int, default=0)
    args = a.parse_args()
    sets = args.sets.split(",")
    if "v11" in sets: a.error("v11 is held back: use --heldback, not --sets")
    import torch
    from transformers import AutoTokenizer, AutoModelForSequenceClassification
    random.seed(args.seed); torch.manual_seed(args.seed)
    dev = "cuda" if torch.cuda.is_available() else "cpu"
    tok = AutoTokenizer.from_pretrained(args.model)
    assert tok.convert_tokens_to_ids("[unused1]") == 1 and tok.convert_tokens_to_ids("[unused2]") == 2
    load = lambda n, k: [json.loads(l) for l in open(os.path.join(args.data, n + ".jsonl"), encoding="utf-8")][:k or None]
    data = {"train": load("train", args.train_limit), **{s: load(s, args.eval_limit) for s in sets + ["v11"] * args.heldback}}
    for rows in data.values():
        for r in rows: r["ids"], r["seg"] = ids(tok, r, args.max_len); r["y"] = STAGES.index(r["stage"])
    print("rows", {k: len(v) for k, v in data.items()}, "at max length", {k: sum(len(r["ids"]) >= args.max_len for r in v) for k, v in data.items()}, "train labels", dict(Counter(r["stage"] for r in data["train"])), flush=True)

    def batches(rows, bs, shuffle):
        order = list(range(len(rows)))
        if shuffle: random.shuffle(order)
        else: order.sort(key=lambda i: len(rows[i]["ids"]))
        for k in range(0, len(order), bs):
            idx = order[k:k + bs]; L = max(len(rows[i]["ids"]) for i in idx)
            x = torch.tensor([rows[i]["ids"] + [0] * (L - len(rows[i]["ids"])) for i in idx])
            t = torch.tensor([[0] * rows[i]["seg"] + [1] * (L - rows[i]["seg"]) for i in idx]) * (x != 0)
            yield idx, x.to(dev), (x != 0).long().to(dev), t.to(dev), torch.tensor([rows[i]["y"] for i in idx]).to(dev)

    def predict(rows):
        probs = [None] * len(rows)
        with torch.no_grad():
            for idx, x, m, t, _ in batches(rows, 32, False):
                with torch.autocast(dev, dtype=torch.float16, enabled=amp):
                    q = model(input_ids=x, attention_mask=m, token_type_ids=t).logits.float().softmax(-1).tolist()
                for i, v in zip(idx, q): probs[i] = v
        return [STAGES[max(range(len(STAGES)), key=p.__getitem__)] for p in probs], probs

    def counts(tag, rows, pred):
        gold = [r["stage"] for r in rows]
        kinds = sorted({r["kind"] for r in rows})
        per_kind = " ".join("%s %d/%d" % (k, sum(g == p for r, g, p in zip(rows, gold, pred) if r["kind"] == k), sum(r["kind"] == k for r in rows)) for k in kinds)
        print("%s: acc %.3f (%d/%d) macro-F1 %.3f | %s" % (tag, sum(g == p for g, p in zip(gold, pred)) / max(1, len(rows)), sum(g == p for g, p in zip(gold, pred)), len(rows), macro_f1(gold, pred), per_kind), flush=True)

    model = AutoModelForSequenceClassification.from_pretrained(args.model, num_labels=len(STAGES)).to(dev)
    cnt = Counter(r["y"] for r in data["train"])
    w = torch.tensor([len(data["train"]) / (len(STAGES) * cnt[c]) if cnt[c] else 0.0 for c in range(len(STAGES))]).to(dev) if args.class_weights else None
    loss_fn = torch.nn.CrossEntropyLoss(weight=w)
    opt = torch.optim.AdamW(model.parameters(), lr=args.lr, weight_decay=0.01)
    steps = args.epochs * ((len(data["train"]) + args.batch - 1) // args.batch)
    sched = torch.optim.lr_scheduler.LambdaLR(opt, lambda s: min(1, (s + 1) / max(1, 0.06 * steps)) * max(0.0, (steps - s) / steps))
    amp = dev == "cuda"; scaler = torch.amp.GradScaler("cuda", enabled=amp); micro = args.batch // args.accum
    os.makedirs(args.out, exist_ok=True); t0 = time.time(); step = 0
    for ep in range(1, args.epochs + 1):
        model.train()
        for _, x, m, t, y in batches(data["train"], args.batch, True):
            for k in range(0, len(y), micro):
                with torch.autocast(dev, dtype=torch.float16, enabled=amp):
                    loss = loss_fn(model(input_ids=x[k:k + micro], attention_mask=m[k:k + micro], token_type_ids=t[k:k + micro]).logits.float(), y[k:k + micro]) * len(y[k:k + micro]) / len(y)
                scaler.scale(loss).backward()
            scaler.unscale_(opt); torch.nn.utils.clip_grad_norm_(model.parameters(), 1.0); scaler.step(opt); scaler.update(); sched.step(); opt.zero_grad(); step += 1
            if step % 50 == 0: print("step %d/%d loss %.4f %ds" % (step, steps, loss.item() * args.accum, time.time() - t0), flush=True)
        model.eval()
        for s in sets:
            rows = data[s]; pred, probs = predict(rows)
            with open(os.path.join(args.out, "pred-%s-e%d.jsonl" % (s, ep)), "w", encoding="utf-8") as f:
                for r, p, q in zip(rows, pred, probs):
                    f.write(json.dumps({"doc": r["doc"], "person": r["person"], "kind": r["kind"], "gold": r["stage"], "pred": p, "probs": [round(v, 4) for v in q]}) + "\n")
            counts("epoch %d %s" % (ep, s), rows, pred)
    if args.heldback: counts("heldback v11 after epoch %d" % args.epochs, data["v11"], predict(data["v11"])[0])  # counts only, no file
    if args.save: model.save_pretrained(args.save); tok.save_pretrained(args.save); print("saved final model to", args.save, flush=True)
    print("DONE %ds" % (time.time() - t0), flush=True)


if __name__ == "__main__":
    main()
