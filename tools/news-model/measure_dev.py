"""Measure saved models on dev, or final test locked to a passing dev record. Prints counts and scores only."""
import argparse
from collections import Counter
import hashlib
import json
from pathlib import Path
import sys

import numpy as np
import torch
from transformers import AutoModelForSequenceClassification, AutoModelForTokenClassification

sys.path.insert(0, str(Path(__file__).parent))
import train_ev as te
import train_ner as tn


def metrics(tp, fp, fn):
    return {'tp': int(tp), 'fp': int(fp), 'fn': int(fn), 'P': float(tp / max(tp + fp, 1)),
            'R': float(tp / max(tp + fn, 1)), 'F1': float(2 * tp / max(2 * tp + fp + fn, 1))}


def passes_target(score):
    return 2 * score['tp'] > 9 * (score['fp'] + score['fn'])


def sha256(path):
    with path.open('rb') as source:
        return hashlib.file_digest(source, 'sha256').hexdigest()


def evaluation_sources(model, directory, split):
    sources = {'model_sha256': directory / 'model.safetensors', 'config_sha256': directory / 'config.json',
            split + '_data_sha256': Path('data') / (model + '-' + split + '.jsonl'),
            'split_sha256': Path('data/split.json'), 'scorer_sha256': Path(__file__),
            'reader_sha256': Path(tn.__file__ if model == 'ner' else te.__file__),
            'tokenizer_sha256': Path('models/bert-base-chinese/tokenizer.json'),
            'tokenizer_config_sha256': Path('models/bert-base-chinese/tokenizer_config.json'),
            'vocab_sha256': Path('models/bert-base-chinese/vocab.txt')}
    if model == 'ev':
        sources['pooling_reader_sha256'] = Path(__file__).with_name('event_pooling.py')
        config = json.loads((directory/'config.json').read_text())
        # The experimental event modes (query predicates, caption observation, document context) were not retained;
        # their readers stay in the training workspace (C:/tmp/aml-train measure_dev.py).
        if config.get('aml_pooling') == 'query-predicates-v1' or config.get('aml_event_caption_version', 0) or config.get('aml_event_document_context', False):
            raise ValueError('Unsupported experimental event mode')
    elif json.loads((directory/'config.json').read_text()).get('aml_ner_document_roles', False):
        sources['document_head_sha256'] = directory/'document-roles.safetensors'
        sources['document_reader_sha256'] = Path(__file__).with_name('ner_document.py')
    return sources


def locked_dev_threshold(record, fingerprints, model):
    if record.get('evaluation_split') != 'dev' or record.get('model_type') != model:
        raise ValueError('Final test needs a dev record for the same model type')
    score = record if model == 'ner' else record['best']
    if any(type(score.get(key)) is not int or score[key] < 0 for key in ('tp', 'fp', 'fn')):
        raise ValueError('Invalid dev confusion counts')
    if not passes_target(score) or not record.get('meets_strict_f1_target'):
        raise ValueError('Final test requires exact dev F1 strictly greater than 0.90')
    if any(record['provenance'].get(key) != value for key, value in fingerprints.items()):
        raise ValueError('Model, scorer or dev inputs differ from the selected dev record')
    if model == 'ev':
        threshold = float(score['threshold'])
        if not 0 <= threshold <= 1:
            raise ValueError('Invalid frozen event threshold')
        return threshold
    return None


def decode_bio(probabilities, labels):
    """Mirror OnnxNewsModel.Decode; report alongside greedy F1 without changing the acceptance metric."""
    p = np.asarray(probabilities, dtype=np.float64)
    assert p.ndim == 2 and p.shape[1] == len(labels)
    if not len(p):
        return []
    transition = np.zeros((len(labels), len(labels)))
    initial = np.zeros(len(labels))
    for to, label in enumerate(labels):
        if label.startswith('I-'):
            initial[to] = -np.inf
            transition[:, to] = [0 if previous != 'O' and previous[2:] == label[2:] else -np.inf for previous in labels]
    logp = np.log(np.maximum(p, 1e-9))
    score = logp[0] + initial
    back = np.zeros(p.shape, dtype=np.int32)
    for index in range(1, len(p)):
        choices = score[:, None] + transition
        back[index] = choices.argmax(axis=0)
        score = choices[back[index], np.arange(len(labels))] + logp[index]
    last = int(score.argmax())
    path = []
    for index in range(len(p) - 1, -1, -1):
        path.append(labels[last])
        last = int(back[index, last])
    return path[::-1]


@torch.no_grad()
def ner(path, output, split='dev'):
    config = json.loads((path / 'config.json').read_text(encoding='utf-8'))
    ascii_lower = config.get('aml_ner_ascii_lower', False)
    assert type(ascii_lower) is bool, 'NER ASCII input flag must be boolean'
    docs = tn.load(split, True) if ascii_lower else tn.load(split)
    source = [json.loads(line) for line in Path('data/ner-' + split + '.jsonl').read_text(encoding='utf-8').splitlines()]
    model = AutoModelForTokenClassification.from_pretrained(path).to(tn.DEV).eval()
    assert model.config.label2id == tn.L2I, 'Checkpoint labels do not match the NER scorer'
    if config.get('aml_ner_document_roles', False):
        import ner_document
        head = ner_document.load_head(path, tn.DEV)
        records = ner_document.extract(model, docs, source)
        sums = [ner_document.apply(record, head) for record in records]
        windows = [torch.ones(len(ids)) for ids, _ in docs]
    else:
        sums = [torch.zeros(len(ids), len(tn.LABELS)) for ids, _ in docs]
        windows = [torch.zeros(len(ids)) for ids, _ in docs]
        positions = model.config.max_position_embeddings
        for chunk, ids, att, _ in tn.batches(docs, max(1, 16 * 512 ** 2 // positions ** 2), False, positions):
            probabilities = model(input_ids=ids.to(tn.DEV), attention_mask=att.to(tn.DEV)).logits.softmax(-1).cpu()
            for j, (d, start, end) in enumerate(chunk):
                sums[d][start:end] += probabilities[j, 1:end-start+1]
                windows[d][start:end] += 1
    errors, confusion, kinds, per_role = [], Counter(), Counter(), {}
    tp = fp = fn = tp_boundary = 0
    constrained_counts = [0, 0, 0]
    for row, (_, labels), scores, count in zip(source, docs, sums, windows):
        gold = tn.spans([tn.LABELS[i] for i in labels])
        pred = tn.spans([tn.LABELS[i] for i in scores.argmax(-1).tolist()])
        constrained = tn.spans(decode_bio((scores / count[:, None]).numpy(), tn.LABELS))
        for i, value in enumerate((len(gold & constrained), len(constrained - gold), len(gold - constrained))):
            constrained_counts[i] += value
        tp += len(gold & pred)
        fp += len(pred - gold)
        fn += len(gold - pred)
        tp_boundary += len({g[:2] for g in gold} & {p[:2] for p in pred})
        for role in tn.ROLES:
            gs, ps = {g for g in gold if g[2] == role}, {p for p in pred if p[2] == role}
            counts = per_role.setdefault(role, [0, 0, 0])
            for i, value in enumerate((len(gs & ps), len(ps - gs), len(gs - ps))):
                counts[i] += value
        for span in gold - pred:
            same = [p for p in pred if p[:2] == span[:2]]
            overlap = [p for p in pred if max(p[0], span[0]) < min(p[1], span[1])]
            kind = 'wrong_role' if same else 'boundary' if overlap else 'missed'
            kinds[kind] += 1
            if same:
                confusion[span[2] + ' -> ' + same[0][2]] += 1
            errors.append({'doc': row['doc'], 'kind': kind, 'gold_token_span': span, 'pred_token_spans': same or overlap})
        kinds['extra'] += sum(not any(max(p[0], g[0]) < min(p[1], g[1]) for g in gold) for p in pred - gold)
    (output / ('ner-errors.json' if split == 'dev' else 'ner-test-errors.json')).write_text(json.dumps(errors), encoding='utf-8')
    result = {**metrics(tp, fp, fn), 'input_ascii_lower': ascii_lower,
              'document_roles': config.get('aml_ner_document_roles', False),
              'span_F1(no role)': 2 * tp_boundary / max(2 * tp + fp + fn, 1),
              'per_role': {role: metrics(*counts) for role, counts in per_role.items()},
              'error_kinds': dict(kinds), 'role_confusion': dict(confusion),
              'bio_constrained_diagnostic': metrics(*constrained_counts)}
    return result


@torch.no_grad()
def event(path, output, split='dev', fixed_threshold=None):
    if split == 'test' and fixed_threshold is None:
        raise ValueError('Test evaluation needs a frozen dev threshold')
    rows = te.load(split)
    model = AutoModelForSequenceClassification.from_pretrained(path).to(te.DEV).eval()
    labels = [model.config.id2label[i] for i in range(model.config.num_labels)]
    probabilities, gold = te.predict(model, rows, labels)
    p, g = probabilities.numpy(), gold.bool().numpy()
    ignored = Counter(label for _, _, ls in rows for label in ls if label not in labels)
    np.savez_compressed(output / ('ev-' + split + '-probabilities.npz'), probabilities=p, gold=g, labels=np.array(labels))
    thresholds = []
    grid = [fixed_threshold] if fixed_threshold is not None else [round(float(t), 3) for t in np.arange(0.05, 0.951, 0.025)]
    for threshold in grid:
        pred = p > threshold
        score = metrics((pred & g).sum(), (pred & ~g).sum(), (~pred & g).sum() + sum(ignored.values()))
        thresholds.append({'threshold': threshold, **score})
    best = max(thresholds, key=lambda score: score['F1'])
    pred = p > best['threshold']
    per_label = {label: metrics((pred[:, i] & g[:, i]).sum(), (pred[:, i] & ~g[:, i]).sum(), (~pred[:, i] & g[:, i]).sum()) for i, label in enumerate(labels)}
    per_label.update({label: metrics(0, 0, count) for label, count in ignored.items()})
    return {'best': best, 'per_label': per_label, 'thresholds': thresholds, 'unsupported_gold_labels': dict(ignored), 'rows': len(rows),
            'threshold_selection': 'frozen_dev_threshold' if fixed_threshold is not None else 'dev_grid_search'}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('model', choices=('ner', 'ev'))
    parser.add_argument('directory', type=Path)
    parser.add_argument('output', type=Path)
    parser.add_argument('--split', choices=('dev', 'test'), default='dev')
    parser.add_argument('--dev-metrics', type=Path)
    args = parser.parse_args()
    if (args.split == 'test') != (args.dev_metrics is not None):
        parser.error('--split test requires --dev-metrics; dev runs do not take that argument')
    dev_fingerprints = None
    dev_selection_digest = None
    threshold = None
    if args.split == 'test':
        dev_sources = evaluation_sources(args.model, args.directory, 'dev')
        dev_fingerprints = {name: sha256(path) for name, path in dev_sources.items()}
        try:
            selection_bytes = args.dev_metrics.read_bytes()
            dev_selection_digest = hashlib.sha256(selection_bytes).hexdigest()
            threshold = locked_dev_threshold(json.loads(selection_bytes), dev_fingerprints, args.model)
        except (ValueError, KeyError) as error:
            parser.error(str(error))
    args.output.mkdir(parents=True, exist_ok=True)
    sources = evaluation_sources(args.model, args.directory, args.split)
    if args.split == 'test':
        sources.update(dev_sources)
        sources['dev_metrics_sha256'] = args.dev_metrics
    fingerprints = {name: sha256(path) for name, path in sources.items()}
    if dev_fingerprints is not None:
        assert all(fingerprints[key] == value for key, value in dev_fingerprints.items()), 'Dev selection inputs changed before final test'
        assert fingerprints['dev_metrics_sha256'] == dev_selection_digest, 'Dev selection changed before final test'
    result = ner(args.directory, args.output, args.split) if args.model == 'ner' else event(args.directory, args.output, args.split, threshold)
    assert fingerprints == {name: sha256(path) for name, path in sources.items()}, 'Evaluation inputs changed during measurement'
    result['meets_strict_f1_target'] = passes_target(result if args.model == 'ner' else result['best'])
    result['evaluation_split'] = args.split
    result['model_type'] = args.model
    result['provenance'] = {**fingerprints,
                            'scoring': 'exact BIO spans including roles, greedy decoding' if args.model == 'ner'
                                       else 'all-label row micro F1, unsupported gold counted as misses'}
    (args.output / (args.model + '-' + args.split + '-metrics.json')).write_text(json.dumps(result, indent=2), encoding='utf-8')
    print(json.dumps({'model': args.model, 'directory': str(args.directory), args.split: result}, ensure_ascii=True), flush=True)


if __name__ == '__main__':
    if '--self-test' in sys.argv:
        assert metrics(8, 2, 4)['F1'] == 16 / 22
        assert metrics(0, 0, 0)['F1'] == 0
        assert not passes_target(metrics(9, 2, 0))
        assert passes_target(metrics(10, 2, 0))
        assert not passes_target(metrics(0, 0, 0))
        assert decode_bio(np.empty((0, 3)), ['O', 'B-A', 'I-A']) == []
        assert decode_bio([[.1, .2, .7], [.1, .2, .7]], ['O', 'B-A', 'I-A']) == ['B-A', 'I-A']
        assert decode_bio([[.05, .30, .01, .25, .39], [.01, .02, .1, .02, .85]], ['O', 'B-A', 'I-A', 'B-B', 'I-B']) == ['B-B', 'I-B']
        print('F1 boundary, invalid BIO starts, and global BIO path checks passed')
    else:
        main()
