"""Per-(person, sentence) event classifier: multi-label (type, type-Negated/-Requested/-Hedged, prior) on bert-base-chinese.
Input pair: (mention, sentence cropped around the mention). Prints scores only, never text.
Usage: python -I train_ev.py [epochs] [starting model] [learning rate] [entity-mask] [marker-pool] [target-contrast]. Dev only; test is reserved for final validation."""
import json, math, random, sys, time
from collections import Counter
from pathlib import Path
import torch
from transformers import AutoTokenizer, AutoModelForSequenceClassification, get_linear_schedule_with_warmup

sys.path.insert(0, str(Path(__file__).parent))
from event_pooling import event_logits

BASE, OUT, MAXLEN, CROP = "models/bert-base-chinese", "models/ev", 512, 460
M_OPEN, M_CLOSE = 1, 2  # [unused1], [unused2]: mark every occurrence of the mention inside the sentence
O_OPEN, O_CLOSE = 6, 7  # [unused6], [unused7]: mark the other persons named in the sentence
torch.manual_seed(0); random.seed(0); torch.set_num_threads(6)
DEV = "cuda" if torch.cuda.is_available() else "cpu"
tok = AutoTokenizer.from_pretrained(BASE)
ENC = lambda t: tok(t, add_special_tokens=False)["input_ids"] if t else []


def pair_ids(m, s, mask_entity=False, others=()):
    """[CLS] mention [SEP] sentence [SEP]; the sentence cut to CROP characters around the mention, each occurrence of the
    mention wrapped in the two marker ids and each occurrence of another person's form (longest first, never inside an
    already wrapped span) in O_OPEN/O_CLOSE, the whole cut to MAXLEN tokens."""
    i = max(s.find(m), 0); a = max(0, min(i - CROP // 2, len(s) - CROP)); s = s[a:a + CROP]
    first = [tok.mask_token_id] if mask_entity and m else ENC(m)
    parts = [(s, None)]
    for form, mark in [(m, (M_OPEN, M_CLOSE))] + [(x, (O_OPEN, O_CLOSE)) for x in others]:
        if not form: continue
        nxt = []
        for text, done in parts:
            if done: nxt.append((text, done)); continue
            for k, piece in enumerate(text.split(form)):
                if k: nxt.append((form, mark))
                if piece: nxt.append((piece, None))
        parts = nxt
    second = []
    for text, mark in parts:
        second += ENC(text) if not mark else [mark[0]] + (first if mark[0] == M_OPEN else ENC(text)) + [mark[1]]
    ids = [tok.cls_token_id] + first + [tok.sep_token_id]; seg = len(ids)
    ids += second[:max(0, MAXLEN - 1 - seg)] + [tok.sep_token_id]
    return ids, seg


def load(split, mask_entity=False):
    rows = []
    for line in open(f"data/ev-{split}.jsonl", encoding="utf-8"):
        r = json.loads(line); ids, seg = pair_ids(r["mention"], r["sentence"], mask_entity, r.get("others", ()))
        rows.append((ids, seg, r["labels"]))
    return rows


def encode(rows, labels):
    L = max(len(i) for i, _, _ in rows)
    ids = torch.zeros(len(rows), L, dtype=torch.long); att = torch.zeros_like(ids); typ = torch.zeros_like(ids)
    for j, (i, seg, _) in enumerate(rows):
        ids[j, :len(i)] = torch.tensor(i); att[j, :len(i)] = 1; typ[j, seg:len(i)] = 1
    y = torch.tensor([[float(l in ls) for l in labels] for _, _, ls in rows])
    return {"input_ids": ids.to(DEV), "attention_mask": att.to(DEV), "token_type_ids": typ.to(DEV)}, y


@torch.no_grad()
def predict(model, rows, labels):
    if not rows:
        return torch.empty((0, len(labels))), torch.empty((0, len(labels)))
    training = model.training
    model.eval(); probabilities, gold = [], []
    for k in range(0, len(rows), 64):
        enc, y = encode(rows[k:k + 64], labels)
        probabilities.append(event_logits(model, enc).sigmoid().cpu()); gold.append(y)
    model.train(training)
    return torch.cat(probabilities), torch.cat(gold)


def score_probabilities(probabilities, gold, labels, thr=0.5, missing=0):
    assert probabilities.shape == gold.shape and probabilities.ndim == 2 and probabilities.shape[1] == len(labels)
    p, g = probabilities > thr, gold.bool()
    tp = (p & g).sum(0).tolist(); fp = (p & ~g).sum(0).tolist(); fn = (~p & g).sum(0).tolist()
    f = lambda t, p, n: 2 * t / (2 * t + p + n) if t + p + n else 0
    T, P, N = sum(tp), sum(fp), sum(fn) + missing
    return {"P": T / max(T + P, 1), "R": T / max(T + N, 1), "F1": f(T, P, N), "tp": T, "fp": P, "fn": N,
            "per_label_F1": {label: round(f(tp[i], fp[i], fn[i]), 2) for i, label in enumerate(labels) if tp[i] + fn[i]}}


def evaluate(model, rows, labels, thr=0.5):
    probabilities, gold = predict(model, rows, labels)
    missing = sum(label not in labels for _, _, ls in rows for label in ls)
    return score_probabilities(probabilities, gold, labels, thr, missing)


def target_metadata(train):
    raw = [json.loads(line) for line in open('data/ev-train.jsonl', encoding='utf-8')]
    assert len(raw) == len(train)
    lookup, forms, groups, contexts, form_ids = {}, {}, [], [], []
    for index, (source, row) in enumerate(zip(raw, train)):
        assert row == (*pair_ids(source['mention'], source['sentence']), source['labels'])
        key = source['doc'], source['sentence']
        if key not in lookup:
            lookup[key] = len(groups)
            groups.append([])
        group = lookup[key]
        groups[group].append(index)
        # Contrast only fully retained sentences; incomplete rows still receive ordinary BCE.
        complete = len(source['sentence']) <= CROP and len(row[0]) < MAXLEN
        contexts.append(group if complete and source['mention'] and source['mention'] in source['sentence'] else -index-1)
        form_ids.append(forms.setdefault(source['mention'], len(forms)))
    return groups, contexts, form_ids


def grouped_training_order(groups):
    shuffled = [list(group) for group in groups]
    random.shuffle(shuffled)
    order = []
    for group in shuffled:
        random.shuffle(group)
        order.extend(group)
    assert len(order) == len(set(order)) == sum(map(len, groups))
    return order


def target_contrast_loss(logits, gold, contexts, forms, features):
    assert logits.shape == gold.shape and logits.ndim == 2
    assert contexts.shape == forms.shape == features.shape == (len(gold),)
    different = (contexts[:, None] == contexts[None, :]) & (forms[:, None] != forms[None, :])
    different &= features[:, None] != features[None, :]
    pairs = different[:, :, None] & gold[:, None, :].bool() & ~gold[None, :, :].bool()
    number = int(pairs.sum().item())
    if not number:
        return logits.sum() * 0, 0
    # Positive and negative actors share the sentence: require the right actor's logit to be larger.
    return torch.nn.functional.softplus(logits[None, :, :] - logits[:, None, :])[pairs].mean(), number


def main():
    epochs = int(sys.argv[1]) if len(sys.argv) > 1 else 4
    start = sys.argv[2] if len(sys.argv) > 2 else BASE
    lr = float(sys.argv[3]) if len(sys.argv) > 3 else 5e-5
    flags = set(sys.argv[4:])
    assert len(flags) == len(sys.argv[4:]) and flags <= {'entity-mask', 'marker-pool', 'target-contrast'}, 'Unknown or repeated training option'
    entity_mask = 'entity-mask' in flags
    target_contrast = 'target-contrast' in flags
    train, dev = load("train"), load("dev")
    masked = load('train', mask_entity=True) if entity_mask else train
    assert len(train) == len(masked) and all(a[2] == b[2] for a, b in zip(train, masked))
    training_pairs = list(zip(train, masked))
    groups, contexts, forms = target_metadata(train) if target_contrast else (None, None, None)
    counts = Counter(l for _, _, ls in train for l in ls)
    labels = sorted(l for l, c in counts.items() if c >= 5)
    pos = [r for r in train if r[2]]; neg = [r for r in train if not r[2]]
    per_epoch = len(train)  # every row each epoch: 2:1 negative sampling left too many no-event sentences unseen
    if start != BASE:
        assert json.load(open(start + '/config.json'))['label2id'] == dict(zip(labels, range(len(labels)))), 'Starting checkpoint has different labels'
    model = AutoModelForSequenceClassification.from_pretrained(start, num_labels=len(labels), problem_type="multi_label_classification",
                                                               id2label=dict(enumerate(labels)), label2id={l: i for i, l in enumerate(labels)}).to(DEV).train()
    model.config.training_entity_mask_probability = 0.5 if entity_mask else 0.0
    model.config.training_target_contrast_weight = 0.1 if target_contrast else 0.0
    model.config.aml_pooling = 'markers' if 'marker-pool' in flags else getattr(model.config, 'aml_pooling', 'cls')
    contrast_columns = [i for i, label in enumerate(labels) if label != 'prior']
    print('training_setup', json.dumps({'epochs': epochs, 'start': start, 'lr': lr,
          'training_entity_mask_probability': model.config.training_entity_mask_probability, 'pooling': model.config.aml_pooling,
          'target_contrast_weight': model.config.training_target_contrast_weight,
          'target_context_groups': len(groups) if groups is not None else None,
          'target_eligible_rows': sum(value >= 0 for value in contexts) if contexts is not None else None,
          'inference_input': 'unchanged mention-marker input', 'model_training': model.training,
          'embedding_dropout_training': model.bert.embeddings.dropout.training}), flush=True)
    n_steps = epochs * math.ceil(per_epoch / 16)
    opt = torch.optim.AdamW(model.parameters(), lr=lr, weight_decay=0.01)
    sch = get_linear_schedule_with_warmup(opt, int(0.1 * n_steps), n_steps)
    best, step, t0, first_contrast_logged = -1, 0, time.time(), False
    for ep in range(epochs):
        assert model.training, 'Optimizer batches require training mode'
        if target_contrast:
            order = grouped_training_order(groups)
        else:
            order = list(range(per_epoch)); random.shuffle(order)
        assert len(order) == per_epoch
        pair_count = contrast_batches = 0
        for k in range(0, len(order), 16):
            indexes = order[k:k+16]
            chunk = [training_pairs[i][1] if entity_mask and random.random() < 0.5 else training_pairs[i][0] for i in indexes]
            enc, y = encode(chunk, labels)
            logits, targets = event_logits(model, enc), y.to(DEV)
            loss = torch.nn.functional.binary_cross_entropy_with_logits(logits, targets)
            if target_contrast:
                feature_lookup = {}
                feature_ids = [feature_lookup.setdefault((tuple(row[0]), row[1]), len(feature_lookup)) for row in chunk]
                contrast, number = target_contrast_loss(logits[:, contrast_columns], targets[:, contrast_columns],
                    torch.tensor([contexts[i] for i in indexes], device=DEV), torch.tensor([forms[i] for i in indexes], device=DEV),
                    torch.tensor(feature_ids, device=DEV))
                loss = loss + model.config.training_target_contrast_weight * contrast
                pair_count += number
                contrast_batches += number > 0
                if number and not first_contrast_logged:
                    print('first_target_contrast', json.dumps({'pairs': number, 'loss': contrast.item(),
                          'weight': model.config.training_target_contrast_weight, 'step': step+1}), flush=True)
                    first_contrast_logged = True
            loss.backward(); torch.nn.utils.clip_grad_norm_(model.parameters(), 1.0); opt.step(); sch.step(); opt.zero_grad(); step += 1
            if step == 1 and DEV == 'cuda':
                print('first_batch_gpu', json.dumps({'shape': list(enc['input_ids'].shape),
                      'peak_allocated_bytes': torch.cuda.max_memory_allocated(), 'model_training': model.training}), flush=True)
            if step % 20 == 0: print(f"step {step}/{n_steps} loss {loss.item():.4f} {time.time() - t0:.0f}s", flush=True)
        if target_contrast:
            assert pair_count > 0, 'Target contrast requires actual positive/negative actor pairs'
            print('target_contrast_epoch', json.dumps({'epoch': ep+1, 'rows': len(order),
                  'pairs': pair_count, 'batches_with_pairs': contrast_batches}), flush=True)
        probabilities, gold = predict(model, dev, labels)
        missing = sum(label not in labels for _, _, ls in dev for label in ls)
        r = score_probabilities(probabilities, gold, labels, missing=missing); print(f"epoch {ep + 1} dev", json.dumps(r), flush=True)
        best_thr = max((score_probabilities(probabilities, gold, labels, t, missing)["F1"], t) for t in (0.3, 0.4, 0.5, 0.6, 0.7)); print("  best threshold", best_thr, flush=True)
        if best_thr[0] > best: best = best_thr[0]; model.save_pretrained(OUT); tok.save_pretrained(OUT)
    print('TRAINING_COMPLETE', json.dumps({'best_dev_F1': best}), flush=True)


if __name__ == "__main__":
    main()
