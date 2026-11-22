"""Person/org role tagger: BIO token classification on bert-base-chinese. Prints scores only, never text.
Usage: python -I train_ner.py [epochs] [starting model] [learning rate] [dropout] [max positions] [BIO penalty] [entity role loss] [ascii-lower]. Dev only; test is reserved for final validation."""
import json, math, random, sys, time
import torch
from transformers import AutoTokenizer, AutoModelForTokenClassification, get_linear_schedule_with_warmup

BASE, OUT = "models/bert-base-chinese", "models/ner"
ROLES = ["PER-Suspect", "PER-Victim", "PER-Official", "PER-Other", "ORG-Suspect", "ORG-Victim", "ORG-Other"]
MERGE = {"PER-Witness": "PER-Other"}  # one training span: too rare to learn alone
LABELS = ["O"] + [f"{p}-{r}" for r in ROLES for p in "BI"]
L2I = {l: i for i, l in enumerate(LABELS)}
torch.manual_seed(0); random.seed(0); torch.set_num_threads(6)
DEV = "cuda" if torch.cuda.is_available() else "cpu"
tok = AutoTokenizer.from_pretrained(BASE)


def feature_text(text, ascii_lower=False):
    return text.translate(str.maketrans('ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz')) if ascii_lower else text


def load(split, ascii_lower=False):
    docs = []
    for line in open(f"data/ner-{split}.jsonl", encoding="utf-8"):
        d = json.loads(line)
        enc = tok(feature_text(d["text"], ascii_lower), add_special_tokens=False, return_offsets_mapping=True)
        ids, offs = enc["input_ids"], enc["offset_mapping"]
        lab = [L2I["O"]] * len(ids)
        for s, e, l in d["spans"]:
            l = MERGE.get(l, l); first = True
            for i, (a, b) in enumerate(offs):
                if a >= s and b <= e and b > a:
                    lab[i] = L2I[("B-" if first else "I-") + l]; first = False
        docs.append((ids, lab))
    return docs


def windows(n, positions=512):
    assert positions in (512, 1024), 'Profile memory on T4 before supporting larger windows'
    length, step = positions - 2, positions * 3 // 4
    starts = list(range(0, max(n - length, 0) + 1, step))
    if starts[-1] + length < n: starts.append(n - length)
    return [(s, min(s + length, n)) for s in starts]


def batches(docs, bs, shuffle, positions=512):
    items = [(d, s, e) for d, (ids, _) in enumerate(docs) for s, e in windows(len(ids), positions)]
    if shuffle: random.shuffle(items)
    for k in range(0, len(items), bs):
        chunk = items[k:k + bs]; L = max(e - s for _, s, e in chunk) + 2
        ids = torch.zeros(len(chunk), L, dtype=torch.long); att = torch.zeros_like(ids); lab = torch.full_like(ids, -100)
        for j, (d, s, e) in enumerate(chunk):
            x, y = docs[d]
            ids[j, :e - s + 2] = torch.tensor([tok.cls_token_id] + x[s:e] + [tok.sep_token_id]); att[j, :e - s + 2] = 1
            lab[j, 1:e - s + 1] = torch.tensor(y[s:e])
        yield chunk, ids, att, lab


def spans(tags):
    out, cur = set(), None
    for i, t in enumerate(tags + ["O"]):
        if cur and not (t.startswith("I-") and t[2:] == cur[2]):
            out.add(tuple(cur)); cur = None
        if t.startswith("B-") or (t.startswith("I-") and cur is None):
            cur = [i, i + 1, t[2:]]
        elif cur: cur[1] = i + 1
    return out


@torch.no_grad()
def evaluate(model, docs):
    model.eval()
    sums = [torch.zeros(len(ids), len(LABELS)) for ids, _ in docs]
    positions = model.config.max_position_embeddings
    for chunk, ids, att, _ in batches(docs, max(1, 16 * 512 ** 2 // positions ** 2), False, positions):
        logits = model(input_ids=ids.to(DEV), attention_mask=att.to(DEV)).logits.softmax(-1).cpu()
        for j, (d, s, e) in enumerate(chunk): sums[d][s:e] += logits[j, 1:e - s + 1]
    tp = fp = fn = tp_span = 0; per = {}
    for (ids, lab), sm in zip(docs, sums):
        gold = spans([LABELS[i] for i in lab]); pred = spans([LABELS[i] for i in sm.argmax(-1).tolist()])
        tp += len(gold & pred); fp += len(pred - gold); fn += len(gold - pred)
        tp_span += len({g[:2] for g in gold} & {p[:2] for p in pred})
        for r in ROLES:
            g = {x for x in gold if x[2] == r}; p = {x for x in pred if x[2] == r}
            a = per.setdefault(r, [0, 0, 0]); a[0] += len(g & p); a[1] += len(p); a[2] += len(g)
    f = lambda t, p, g: 2 * t / (p + g) if p + g else 0
    model.train()
    return {"P": tp / max(tp + fp, 1), "R": tp / max(tp + fn, 1), "F1": f(tp, tp + fp, tp + fn),
            "span_F1(no role)": f(tp_span, tp + fp, tp + fn), "per_role_F1": {r: round(f(*v), 3) for r, v in per.items() if v[2]}}


def bio_penalty(logits, labels):
    assert logits.shape[:-1] == labels.shape and logits.shape[-1] == len(LABELS)
    probabilities = logits.softmax(-1)
    preceding_role = probabilities[:, :-1, 1::2] + probabilities[:, :-1, 2::2]
    invalid = (probabilities[:, 1:, 2::2] * (1 - preceding_role)).sum(-1)
    # A window may begin inside a real entity; constrain only adjacent supervised tokens.
    valid = (labels[:, :-1] != -100) & (labels[:, 1:] != -100)
    return invalid.masked_fill(~valid, 0).sum() / valid.sum().clamp_min(1)


def entity_role_loss(logits, labels):
    assert logits.shape[:-1] == labels.shape and logits.shape[-1] == len(LABELS)
    entity = labels > 0
    if not entity.any():
        return logits.sum() * 0
    roles = (labels - 1).clamp_min(0) // 2
    starts = entity.clone()
    starts[:, 1:] &= (labels[:, 1:] % 2 == 1) | ~entity[:, :-1] | (roles[:, 1:] != roles[:, :-1])
    groups = starts.flatten().long().cumsum(0)[entity.flatten()] - 1
    targets = roles[starts]
    # Give each supervised entity fragment one vote, without rewarding extra spans.
    role_logits = logits[:, :, 1:].reshape(*labels.shape, len(ROLES), 2).logsumexp(-1)
    pooled = logits.new_zeros((len(targets), len(ROLES))).index_add(0, groups, role_logits[entity])
    pooled = pooled / torch.bincount(groups, minlength=len(targets)).unsqueeze(1)
    return torch.nn.functional.cross_entropy(pooled, targets)


def resize_positions(model, positions):
    old = model.config.max_position_embeddings
    assert old in (512, 1024) and positions in (512, 1024)
    if positions == old:
        return
    embedding = model.bert.embeddings
    weight = embedding.position_embeddings.weight
    resized = torch.nn.Embedding(positions, weight.shape[1], device=weight.device, dtype=weight.dtype)
    with torch.no_grad():
        resized.weight.copy_(weight.repeat(positions // old, 1) if positions > old else weight[:positions])
    embedding.position_embeddings = resized
    embedding.register_buffer('position_ids', torch.arange(positions, device=weight.device).expand((1, -1)), persistent=False)
    embedding.register_buffer('token_type_ids', torch.zeros((1, positions), dtype=torch.long, device=weight.device), persistent=False)
    model.config.max_position_embeddings = positions


def main():
    epochs = int(sys.argv[1]) if len(sys.argv) > 1 else 3
    start = sys.argv[2] if len(sys.argv) > 2 else BASE
    lr = float(sys.argv[3]) if len(sys.argv) > 3 else 5e-5
    dropout = float(sys.argv[4]) if len(sys.argv) > 4 else None
    positions = int(sys.argv[5]) if len(sys.argv) > 5 else None
    bio_weight = float(sys.argv[6]) if len(sys.argv) > 6 else 0.0
    role_weight = float(sys.argv[7]) if len(sys.argv) > 7 else 0.0
    ascii_requested = len(sys.argv) > 8
    assert len(sys.argv) <= 9 and (not ascii_requested or sys.argv[8] == 'ascii-lower'), 'Unknown NER input option'
    start_config = json.load(open(start + '/config.json', encoding='utf-8'))
    ascii_lower = start_config.get('aml_ner_ascii_lower', False)
    assert type(ascii_lower) is bool, 'NER ASCII input flag must be boolean'
    ascii_lower = ascii_requested or ascii_lower
    assert math.isfinite(bio_weight) and 0 <= bio_weight <= 1, 'BIO penalty weight must be in [0, 1]'
    assert math.isfinite(role_weight) and 0 <= role_weight <= 1, 'Entity role loss weight must be in [0, 1]'
    if dropout is not None:
        assert 0 <= dropout < 1, 'Dropout must be in [0, 1)'
    regularization = {} if dropout is None else {'hidden_dropout_prob': dropout, 'attention_probs_dropout_prob': dropout}
    train, dev = (load("train", True), load("dev", True)) if ascii_lower else (load("train"), load("dev"))
    if start != BASE:
        assert start_config['label2id'] == L2I, 'Starting checkpoint has different labels'
    model = AutoModelForTokenClassification.from_pretrained(start, num_labels=len(LABELS), id2label=dict(enumerate(LABELS)), label2id=L2I, **regularization).to(DEV).train()
    positions = model.config.max_position_embeddings if positions is None else positions
    resize_positions(model, positions)
    model.config.training_bio_penalty_weight = bio_weight
    model.config.training_entity_role_loss_weight = role_weight
    model.config.aml_ner_ascii_lower = ascii_lower
    tok.model_max_length = positions
    batch_size = 8 * 512 // positions
    if dropout is not None:
        assert model.bert.embeddings.dropout.p == dropout, 'Requested dropout was not applied'
    print('training_setup', json.dumps({'epochs': epochs, 'start': start, 'lr': lr, 'dropout': model.config.hidden_dropout_prob,
                                      'max_position_embeddings': positions, 'batch_size': batch_size, 'bio_penalty_weight': bio_weight,
                                      'entity_role_loss_weight': role_weight,
                                      'ner_ascii_lower': ascii_lower,
                                      'model_training': model.training,
                                      'embedding_dropout_training': model.bert.embeddings.dropout.training}), flush=True)
    n_steps = epochs * math.ceil(sum(len(windows(len(i), positions)) for i, _ in train) / batch_size)
    opt = torch.optim.AdamW(model.parameters(), lr=lr, weight_decay=0.01)
    sch = get_linear_schedule_with_warmup(opt, int(0.1 * n_steps), n_steps)
    best, step, t0 = -1, 0, time.time()
    for ep in range(epochs):
        assert model.training, 'Optimizer batches require training mode'
        for _, ids, att, lab in batches(train, batch_size, True, positions):
            labels = lab.to(DEV)
            output = model(input_ids=ids.to(DEV), attention_mask=att.to(DEV), labels=labels)
            loss = output.loss
            if bio_weight:
                loss = loss + bio_weight * bio_penalty(output.logits, labels)
            if role_weight:
                loss = loss + role_weight * entity_role_loss(output.logits, labels)
            loss.backward(); torch.nn.utils.clip_grad_norm_(model.parameters(), 1.0); opt.step(); sch.step(); opt.zero_grad(); step += 1
            if step == 1 and DEV == 'cuda':
                print('first_batch_gpu', json.dumps({'shape': list(ids.shape), 'peak_allocated_bytes': torch.cuda.max_memory_allocated(),
                                                     'model_training': model.training, 'bio_penalty_weight': bio_weight,
                                                     'entity_role_loss_weight': role_weight}), flush=True)
            if step % 20 == 0: print(f"step {step}/{n_steps} loss {loss.item():.4f} {time.time() - t0:.0f}s", flush=True)
        r = evaluate(model, dev); print(f"epoch {ep + 1} dev", json.dumps(r), flush=True)
        if r["F1"] > best: best = r["F1"]; model.save_pretrained(OUT); tok.save_pretrained(OUT)
    print('TRAINING_COMPLETE', json.dumps({'best_dev_F1': best}), flush=True)


if __name__ == "__main__":
    main()
