"""Trainable document context for NER: group predicted mentions by source form, then adjust final BIO logits."""
from collections import defaultdict
import json
from pathlib import Path
import torch
from torch import nn
from safetensors.torch import load_file
import train_ner as ner

HEAD = 'document-roles.safetensors'


class DocumentRoles(nn.Module):
    def __init__(self, hidden_size):
        super().__init__()
        size = hidden_size+len(ner.ROLES)+1
        self.dropout = nn.Dropout(0.1)
        self.attention = nn.Linear(size, 1, bias=False)
        self.output = nn.Linear(size, len(ner.ROLES)+1)
        for parameter in self.parameters():
            nn.init.zeros_(parameter)

    def forward(self, features, mask):
        features = self.dropout(features)
        weights = (self.attention(features).squeeze(-1)+(mask-1)*10000).softmax(1)
        return self.output((features*weights.unsqueeze(-1)).sum(1))


class NerExport(nn.Module):
    def __init__(self, model):
        super().__init__()
        self.model = model

    def forward(self, input_ids, attention_mask, token_type_ids):
        result = self.model(input_ids=input_ids, attention_mask=attention_mask,
                            token_type_ids=token_type_ids, output_hidden_states=True)
        return result.logits, result.hidden_states[-1]


def load_head(path, device='cpu'):
    config = json.loads((Path(path)/'config.json').read_text(encoding='utf-8'))
    assert config['aml_ner_document_roles'] is True
    assert config['aml_ner_document_role_labels'] == ['O']+ner.ROLES
    head = DocumentRoles(config['hidden_size']).to(device)
    head.load_state_dict(load_file(str(Path(path)/HEAD), device=str(device)))
    return head.eval()


def groups(text, offsets, probabilities, hidden):
    """No gold labels: use first-pass greedy predicted spans and original, case-preserved source forms."""
    grouped = defaultdict(list)
    for a, b, _ in sorted(ner.spans([ner.LABELS[i] for i in probabilities.argmax(-1).tolist()])):
        grouped[text[offsets[a][0]:offsets[b-1][1]]].append((a, b))
    result = []
    for spans in grouped.values():
        features = []
        for a, b in spans:
            p = probabilities[a:b].mean(0)
            roles = torch.cat((p[:1], p[1:].reshape(len(ner.ROLES), 2).sum(1)))
            features.append(torch.cat((hidden[a:b].mean(0), roles)))
        result.append({'spans': spans, 'features': torch.stack(features)})
    return result


def padded(groups, device):
    assert groups and all(len(group['features']) for group in groups)
    length = max(len(group['features']) for group in groups)
    features = torch.zeros(len(groups), length, groups[0]['features'].shape[1], device=device)
    mask = torch.zeros(len(groups), length, device=device)
    for i, group in enumerate(groups):
        n = len(group['features'])
        features[i, :n] = group['features'].to(device)
        mask[i, :n] = 1
    return features, mask


def expanded(bias):
    return torch.cat((bias[..., :1], bias[..., 1:].repeat_interleave(2, dim=-1)), dim=-1)


@torch.no_grad()
def apply(document, head):
    probabilities = document['probabilities']
    grouped = document['groups']
    if not grouped:
        return probabilities
    bias = expanded(head(*padded(grouped, next(head.parameters()).device))).cpu()
    output = probabilities.clone()
    for group, values in zip(grouped, bias):
        for a, b in group['spans']:
            output[a:b] = (probabilities[a:b].clamp_min(1e-9).log()+values).softmax(-1)
    return output


def group_loss(group, bias):
    values = expanded(bias)
    loss = torch.nn.functional.cross_entropy(group['log_probabilities'].to(bias.device)+values,
                                              group['labels'].to(bias.device), reduction='none')
    loss = (loss*group['token_weights'].to(bias.device)).sum()
    if group['role_target'] >= 0:
        loss = loss+len(group['spans'])*torch.nn.functional.cross_entropy(bias[None], torch.tensor([group['role_target']], device=bias.device))
    return loss


def training_groups(documents):
    output = []
    for document in documents:
        gold = ner.spans([ner.LABELS[i] for i in document['labels'].tolist()])
        for group in document['groups']:
            spans = group['spans']
            item = dict(group)
            item['log_probabilities'] = torch.cat([document['probabilities'][a:b].clamp_min(1e-9).log() for a, b in spans])
            item['labels'] = torch.cat([document['labels'][a:b] for a, b in spans])
            item['token_weights'] = torch.cat([torch.full((b-a,), 1/(b-a)) for a, b in spans])
            matched = {ner.ROLES.index(role)+1 for a, b, role in gold if (a, b) in spans}
            overlaps = any(max(a, c) < min(b, d) for a, b in spans for c, d, _ in gold)
            item['role_target'] = next(iter(matched)) if len(matched) == 1 else 0 if not overlaps else -100
            output.append(item)
    return output


@torch.no_grad()
def extract(model, documents, source):
    """Same windows/batch layout as the primary scorer; only unfinished documents retain full hidden states."""
    model.eval()
    active, output = {}, []
    positions = model.config.max_position_embeddings
    batch_size = max(1, 16*512**2//positions**2)
    device = next(model.parameters()).device
    for chunk, ids, attention, _ in ner.batches(documents, batch_size, False, positions):
        prediction = model(input_ids=ids.to(device), attention_mask=attention.to(device), output_hidden_states=True)
        probabilities, hidden = prediction.logits.softmax(-1).cpu(), prediction.hidden_states[-1].cpu()
        for j, (d, a, b) in enumerate(chunk):
            if d not in active:
                n = len(documents[d][0])
                active[d] = (torch.zeros(n, len(ner.LABELS)), torch.zeros(n, model.config.hidden_size), torch.zeros(n))
            p, h, count = active[d]
            p[a:b] += probabilities[j, 1:b-a+1]
            h[a:b] += hidden[j, 1:b-a+1]
            count[a:b] += 1
            if b != len(documents[d][0]):
                continue
            assert (count > 0).all()
            p, h = p/count[:, None], h/count[:, None]
            raw = source[d]
            mode = getattr(model.config, 'aml_ner_ascii_lower', False)
            encoded = ner.tok(ner.feature_text(raw['text'], mode), add_special_tokens=False, return_offsets_mapping=True)
            assert encoded['input_ids'] == documents[d][0]
            grouped = groups(raw['text'], encoded['offset_mapping'], p, h)
            assert all(torch.isfinite(g['features']).all() for g in grouped)
            output.append({'doc': raw['doc'], 'probabilities': p, 'labels': torch.tensor(documents[d][1]), 'groups': grouped})
            del active[d]
        if len(output) and len(output) % 100 == 0:
            print('feature_documents', len(output), flush=True)
    assert not active and [row['doc'] for row in output] == [row['doc'] for row in source]
    return output


@torch.no_grad()
def score(documents, head):
    tp = fp = fn = 0
    head.eval()
    for document in documents:
        gold = ner.spans([ner.LABELS[i] for i in document['labels'].tolist()])
        predicted = ner.spans([ner.LABELS[i] for i in apply(document, head).argmax(-1).tolist()])
        tp += len(gold & predicted)
        fp += len(predicted-gold)
        fn += len(gold-predicted)
    return {'tp': tp, 'fp': fp, 'fn': fn, 'P': tp/max(1, tp+fp), 'R': tp/max(1, tp+fn), 'F1': 2*tp/max(1, 2*tp+fp+fn)}
