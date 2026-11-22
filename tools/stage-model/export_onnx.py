"""Export the person-stage classifier saved by train_stage.py --save to ONNX and check it against PyTorch.
usage: export_onnx.py <saved model dir> <out dir> [<data dir> <check set>]   (defaults: hf onnx data dev)
Writes <out>/model.onnx (opset 17; inputs input_ids, attention_mask, token_type_ids, int64 [batch, sequence], both
axes dynamic; output logits float32 [batch, 7]), the tokenizer files (vocab.txt ...) and <out>/labels.json (label order,
max length, input layout). Check: the first 20 rows of <data>/<set>.jsonl, encoded as train_stage.py does, as ONE padded
batch through PyTorch (fp32, CPU) and ONNX Runtime (CPU): max |logit diff| < 1e-3 and the same argmax on every row,
else exit 1. Prints counts and numbers only; last line "ONNX_OK ..." on success."""
import json, os, sys
import numpy as np
import torch
from transformers import AutoTokenizer, AutoModelForSequenceClassification
import onnxruntime as ort
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # python -I leaves the script folder off the path
from train_stage import STAGES, ids

MAX_LEN, N_CHECK = 512, 20
src, out, data_dir, check_set = (sys.argv[1:] + ["hf", "onnx", "data", "dev"][len(sys.argv[1:]):])[:4]

tok = AutoTokenizer.from_pretrained(src)
model = AutoModelForSequenceClassification.from_pretrained(src, attn_implementation="eager").float().eval()
assert model.config.num_labels == len(STAGES)


class Logits(torch.nn.Module):
    def __init__(self, m): super().__init__(); self.m = m
    def forward(self, input_ids, attention_mask, token_type_ids):
        return self.m(input_ids=input_ids, attention_mask=attention_mask, token_type_ids=token_type_ids).logits


os.makedirs(out, exist_ok=True)
path = os.path.join(out, "model.onnx")
# The dummy holds padding and both token types: traced with an all-ones mask, transformers drops the mask from the graph.
dm = torch.ones(2, 16, dtype=torch.long); dm[1, 9:] = 0
dt = torch.zeros(2, 16, dtype=torch.long); dt[:, 5:] = 1; dt *= dm
dummy = ((torch.arange(32).view(2, 16) + 100) * dm, dm, dt)
names = ["input_ids", "attention_mask", "token_type_ids"]
kw = dict(input_names=names, output_names=["logits"], opset_version=17, do_constant_folding=True,
          dynamic_axes={**{n: {0: "batch", 1: "sequence"} for n in names}, "logits": {0: "batch"}})
with torch.no_grad():
    w = Logits(model).eval()  # a new Module starts in train mode, and export restores that mode afterwards (dropout on)
    try: torch.onnx.export(w, dummy, path, dynamo=False, **kw)  # the TorchScript exporter: dynamic_axes as given
    except TypeError: torch.onnx.export(w, dummy, path, **kw)  # older torch without the dynamo argument
model.eval()
assert not any(m.training for m in model.modules())
tok.save_pretrained(out)
if not os.path.exists(os.path.join(out, "vocab.txt")):  # transformers 5 saves tokenizer.json only: write the WordPiece list by id
    v = tok.get_vocab(); assert sorted(v.values()) == list(range(len(v)))
    with open(os.path.join(out, "vocab.txt"), "w", encoding="utf-8", newline="\n") as f:
        f.writelines(w + "\n" for w, _ in sorted(v.items(), key=lambda x: x[1]))
json.dump({"labels": STAGES, "max_length": MAX_LEN, "inputs": names, "output": "logits",
           "layout": "[CLS] name [SEP] evidence [SEP]; token_type_ids 0 up to and including the first [SEP], 1 after; "
                     "[unused1]/[unused2] mark the person's form in the evidence (ids 1, 2); name at most 32 tokens"},
          open(os.path.join(out, "labels.json"), "w", encoding="utf-8"), indent=1)
assert os.path.exists(os.path.join(out, "vocab.txt"))

rows = [json.loads(l) for l in open(os.path.join(data_dir, check_set + ".jsonl"), encoding="utf-8")][:N_CHECK]
enc = [ids(tok, r, MAX_LEN) for r in rows]
L = max(len(x) for x, _ in enc)
x = np.array([a + [0] * (L - len(a)) for a, _ in enc], dtype=np.int64)
m = (x != 0).astype(np.int64)
t = np.array([[0] * seg + [1] * (L - seg) for _, seg in enc], dtype=np.int64) * m
with torch.no_grad():
    pt = model(input_ids=torch.from_numpy(x), attention_mask=torch.from_numpy(m), token_type_ids=torch.from_numpy(t)).logits.numpy()
sess = ort.InferenceSession(path, providers=["CPUExecutionProvider"])
ox = sess.run(["logits"], {"input_ids": x, "attention_mask": m, "token_type_ids": t})[0]
diff = float(np.abs(pt - ox).max()); same = int((pt.argmax(-1) == ox.argmax(-1)).sum())
size = sum(os.path.getsize(os.path.join(out, f)) for f in os.listdir(out))
print("onnx check %s rows %d padded length %d max abs diff %.2e same argmax %d/%d | folder %d bytes, files %s"
      % (check_set, len(rows), L, diff, same, len(rows), size, " ".join(sorted(os.listdir(out)))), flush=True)
if not (len(rows) == N_CHECK and diff < 1e-3 and same == len(rows)):
    print("ONNX_FAIL", flush=True); sys.exit(1)
print("ONNX_OK diff %.2e argmax %d/%d" % (diff, same, len(rows)), flush=True)
