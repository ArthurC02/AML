"""Exports one token/sequence-classification model dir to <out>/model.onnx with its config and vocab; checks ORT against torch.
Usage: export_one.py <model dir> <out dir> [aml_input]"""
import json, os, shutil, sys
from pathlib import Path
import numpy as np, onnxruntime as ort, torch
from transformers import AutoTokenizer, AutoModelForSequenceClassification, AutoModelForTokenClassification
sys.path.insert(0, str(Path(__file__).parent))
from event_pooling import EventExport
from ner_document import NerExport, load_head
src, out = sys.argv[1], sys.argv[2]; os.makedirs(out, exist_ok=True)
cfg = json.load(open(f"{src}/config.json", encoding="utf-8"))
ner = any('TokenClassification' in name for name in cfg.get('architectures', []))
cls = AutoModelForTokenClassification if ner else AutoModelForSequenceClassification
tok = AutoTokenizer.from_pretrained(src); model = cls.from_pretrained(src).eval()
document_roles = ner and cfg.get('aml_ner_document_roles', False)
if document_roles:
    model = NerExport(model).eval()
if not ner and getattr(model.config, 'aml_pooling', 'cls') not in (None, 'cls'):
    model = EventExport(model).eval()
enc = tok("甲乙丙", "丁戊【甲乙丙】己庚辛壬癸", return_tensors="pt")
names = ["input_ids", "attention_mask", "token_type_ids"]; args = tuple(enc[n] for n in names)
outputs = ['logits', 'hidden'] if document_roles else ['logits']
axes = {**{n: {0: 'batch', 1: 'seq'} for n in names}, 'logits': {0: 'batch', 1: 'seq'} if ner else {0: 'batch'}}
if document_roles:
    axes['hidden'] = {0: 'batch', 1: 'seq'}
torch.onnx.export(model, args, f"{out}/model.onnx", input_names=names, output_names=outputs, dynamo=False,
                  dynamic_axes=axes, opset_version=17)
reference = model(*args)
refs = [r.detach().numpy() for r in reference] if document_roles else [reference.logits.detach().numpy()]
got = ort.InferenceSession(f"{out}/model.onnx").run(None, {n: a.numpy() for n, a in zip(names, args)})
print("max abs diff", max(float(np.abs(a-b).max()) for a, b in zip(refs, got)))
for actual, expected in zip(got, refs):
    np.testing.assert_allclose(actual, expected, atol=1e-4, rtol=1e-3)
if document_roles:
    head = load_head(src)
    features = torch.randn(2, 3, cfg['hidden_size']+8)
    mask = torch.tensor([[1., 1., 0.], [1., 1., 1.]])
    torch.onnx.export(head, (features, mask), f'{out}/document-roles.onnx', input_names=['features', 'mask'],
                      output_names=['bias'], dynamo=False, opset_version=17,
                      dynamic_axes={'features': {0: 'groups', 1: 'mentions'}, 'mask': {0: 'groups', 1: 'mentions'}, 'bias': {0: 'groups'}})
    actual = ort.InferenceSession(f'{out}/document-roles.onnx').run(None, {'features': features.numpy(), 'mask': mask.numpy()})[0]
    np.testing.assert_allclose(actual, head(features, mask).detach().numpy(), atol=1e-5, rtol=1e-4)
if len(sys.argv) > 3: cfg["aml_input"] = sys.argv[3]
json.dump(cfg, open(f"{out}/config.json", "w", encoding="utf-8"), ensure_ascii=False, indent=2)
vocab = f"{src}/vocab.txt"
shutil.copy(vocab if os.path.exists(vocab) else "models/bert-base-chinese/vocab.txt", f"{out}/vocab.txt")
