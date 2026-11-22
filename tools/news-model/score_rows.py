"""Row-level scores of an event model on a rows file. Prints counts only, never text.
Usage: python -I score_rows.py <model dir> <rows.jsonl> [threshold]"""
import json, sys, re
from collections import Counter
from pathlib import Path
import torch
from transformers import AutoModelForSequenceClassification
sys.path.insert(0, str(Path(__file__).parent))
import train_ev as T
from event_pooling import event_logits
mdir, f = sys.argv[1], sys.argv[2]; thr = float(sys.argv[3]) if len(sys.argv) > 3 else 0.3
model = AutoModelForSequenceClassification.from_pretrained(mdir).eval()
labels = [model.config.id2label[i] for i in range(model.config.num_labels)]
rows = [json.loads(l) for l in open(f, encoding='utf-8')]
A = re.compile('[可得]上訴|[可得]抗告'); M = re.compile('(?<![可得])上訴|(?<![可得])抗告|再審|更審|發回')
tp = Counter(); fp = Counter(); fn = Counter(); app_only = [0, 0]; prior_neg = [0, 0]
with torch.no_grad():
    for k in range(0, len(rows), 32):
        b = rows[k:k + 32]; enc = [T.pair_ids(r['mention'], r['sentence']) for r in b]
        e, _ = T.encode([(i, s, []) for i, s in enc], labels)
        p = event_logits(model, e).sigmoid()
        for r, pr in zip(b, p):
            pred = {labels[i] for i in range(len(labels)) if pr[i] >= thr}; gold = set(r['labels'])
            for l in pred & gold: tp[l] += 1
            for l in pred - gold: fp[l] += 1
            for l in gold - pred: fn[l] += 1
            if A.search(r['sentence']) and not M.search(r['sentence']):
                app_only[0] += 1; app_only[1] += bool(pred & {'Appeal', 'Appeal-Negated'})
            if 'prior' not in gold: prior_neg[0] += 1; prior_neg[1] += 'prior' in pred
T_, F_, N_ = sum(tp.values()), sum(fp.values()), sum(fn.values())
f1 = lambda a, b, c: 2 * a / (2 * a + b + c) if a else 0.0
print(f'rows {len(rows)} thr {thr} micro tp {T_} fp {F_} fn {N_} F1 {f1(T_, F_, N_):.3f}')
for l in sorted(set(tp) | set(fp) | set(fn)): print(f'  {l:24s} tp {tp[l]:3d} fp {fp[l]:3d} fn {fn[l]:3d} F1 {f1(tp[l], fp[l], fn[l]):.2f}')
print(f'appealable-only rows {app_only[0]}: model says Appeal on {app_only[1]}')
print(f'rows without gold prior {prior_neg[0]}: model says prior on {prior_neg[1]}')
