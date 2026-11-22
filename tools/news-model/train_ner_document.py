"""Train document-role attention on a frozen approved NER encoder. Fixed dev selects checkpoints; test is never loaded."""
import hashlib
import json
from pathlib import Path
import random
import shutil
import sys
import time
import torch
from safetensors.torch import save_file
from transformers import AutoModelForTokenClassification

sys.path.insert(0, str(Path(__file__).parent))
import train_ner as ner
import ner_document as document


def digest(path):
    return hashlib.file_digest(Path(path).open('rb'), 'sha256').hexdigest()


def main():
    epochs, start, lr = int(sys.argv[1]), Path(sys.argv[2]), float(sys.argv[3])
    assert 1 <= epochs <= 100 and 0 < lr <= 0.01
    config = json.loads((start/'config.json').read_text())
    assert not config.get('aml_ner_document_roles', False), 'Start from a fixed plain NER encoder'
    assert config['label2id'] == ner.L2I
    assert config['max_position_embeddings'] == 512
    torch.manual_seed(0)
    random.seed(0)
    model = AutoModelForTokenClassification.from_pretrained(start).to(ner.DEV).eval()
    records = {}
    print('training_setup', json.dumps({'stage': 'extracting frozen encoder features', 'epochs': epochs,
          'start': str(start), 'frozen_encoder': True, 'model_training': False,
          'head_optimizer_started': False, 'data_splits': ['train', 'dev']}), flush=True)
    for split in ('train', 'dev'):
        source = [json.loads(line) for line in Path('data/ner-'+split+'.jsonl').read_text(encoding='utf-8').splitlines()]
        docs = ner.load(split, config.get('aml_ner_ascii_lower', False))
        records[split] = document.extract(model, docs, source)
        torch.save({'model_sha256': digest(start/'model.safetensors'), 'data_sha256': digest('data/ner-'+split+'.jsonl'),
                    'reader_sha256': digest(document.__file__), 'documents': records[split]}, 'features-ner-'+split+'.pt')
        print('features_complete', json.dumps({'split': split, 'documents': len(source),
              'groups': sum(len(d['groups']) for d in records[split]),
              'predicted_spans': sum(len(g['spans']) for d in records[split] for g in d['groups'])}), flush=True)
    hidden_size = model.config.hidden_size
    del model
    if ner.DEV == 'cuda':
        torch.cuda.empty_cache()
    head = document.DocumentRoles(hidden_size).to(ner.DEV)
    initial = document.score(records['dev'], head)
    job = json.loads(Path('job.json').read_text()) if Path('job.json').exists() else {}
    if 'document_parent_dev_counts' in job:
        assert [initial[k] for k in ('tp', 'fp', 'fn')] == job['document_parent_dev_counts'], 'Zero head changed the parent dev predictions'
    print('initial_document_dev', json.dumps(initial), flush=True)
    output = Path(ner.OUT)
    shutil.copytree(start, output, dirs_exist_ok=True)
    config['aml_ner_document_roles'] = True
    config['aml_ner_document_role_labels'] = ['O']+ner.ROLES
    (output/'config.json').write_text(json.dumps(config, indent=2), encoding='utf-8')
    save_file(head.state_dict(), str(output/document.HEAD))
    best = initial['F1']
    groups = document.training_groups(records['train'])
    assert groups
    optimizer = torch.optim.AdamW(head.parameters(), lr=lr, weight_decay=0.01)
    steps, step, t0 = epochs*((len(groups)+31)//32), 0, time.time()
    head.train()
    print('training_setup', json.dumps({'epochs': epochs, 'lr': lr, 'stage': 'document role head',
          'frozen_encoder': True, 'groups': len(groups), 'head_parameters': sum(p.numel() for p in head.parameters()),
          'initial_dev_counts': [initial[k] for k in ('tp', 'fp', 'fn')], 'zero_head_parent_verified': 'document_parent_dev_counts' in job,
          'model_training': head.training, 'head_dropout': head.dropout.p, 'head_dropout_training': head.dropout.training,
          'feature_grouping': 'source form of first-pass greedy predictions, within document'}), flush=True)
    print('global_role_targets', json.dumps({'supervised_groups': sum(g['role_target'] >= 0 for g in groups),
          'excluded_ambiguous_or_partial_groups': sum(g['role_target'] < 0 for g in groups),
          'loss': 'equal-span BIO cross entropy plus global role cross entropy; primary BIO supervision retains every predicted mention'}), flush=True)
    for epoch in range(epochs):
        head.train()
        random.shuffle(groups)
        seen = 0
        for at in range(0, len(groups), 32):
            batch = groups[at:at+32]
            assert head.training and head.dropout.training
            bias = head(*document.padded(batch, ner.DEV))
            count = sum(len(g['spans']) for g in batch)
            loss = sum(document.group_loss(g, b) for g, b in zip(batch, bias))/count
            assert torch.isfinite(loss)
            loss.backward()
            torch.nn.utils.clip_grad_norm_(head.parameters(), 1)
            optimizer.step()
            optimizer.zero_grad()
            step += 1
            seen += len(batch)
            if step % 20 == 0:
                print(f'step {step}/{steps} loss {loss.item():.4f} {time.time()-t0:.0f}s', flush=True)
        assert seen == len(groups)
        score = document.score(records['dev'], head)
        print('epoch', epoch+1, 'dev', json.dumps(score), flush=True)
        if score['F1'] > best:
            best = score['F1']
            save_file(head.state_dict(), str(output/document.HEAD))
    assert digest(output/'model.safetensors') == digest(start/'model.safetensors'), 'Frozen encoder changed'
    print('TRAINING_COMPLETE', json.dumps({'best_dev_F1': best, 'frozen_encoder_sha256': digest(output/'model.safetensors')}), flush=True)


if __name__ == '__main__':
    main()
