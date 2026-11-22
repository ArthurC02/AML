"""Validate two real CUDA updates in isolated output before the full reviewed-label run."""
import hashlib
import json
from pathlib import Path
import sys
sys.path.insert(0,str(Path(__file__).parent))
import train_ev_reviewed as trainer

ev=trainer.ev;torch=trainer.torch
assert ev.DEV=='cuda' and torch.cuda.is_available()
sha=lambda p:hashlib.file_digest(Path(p).open('rb'),'sha256').hexdigest()
sources={name:sha(name) for name in ('data/ev-train.jsonl','data/ev-dev.jsonl','models/ev-start/model.safetensors','train_ev_reviewed.py')}
ev.OUT='models/ev-gpu-smoke'
original_shuffle=trainer.random.shuffle;original_random=trainer.random.random
trainer.random.shuffle=lambda values:None
mask_calls=0;steps=0
def masks():
    global mask_calls
    mask_calls+=1
    return .9 if mask_calls<=16 or mask_calls%2 else .1
trainer.random.random=masks
original_step=torch.optim.AdamW.step
class SmokeComplete(Exception):pass
def step(optimizer,*args,**kwargs):
    global steps
    assert all(p.device.type=='cuda' for group in optimizer.param_groups for p in group['params'])
    assert all(p.grad is None or torch.isfinite(p.grad).all().item() for group in optimizer.param_groups for p in group['params'])
    assert any(p.grad is not None and p.grad.abs().sum().item()>0 for group in optimizer.param_groups for p in group['params'])
    value=original_step(optimizer,*args,**kwargs);steps+=1
    assert all(torch.isfinite(p).all().item() for group in optimizer.param_groups for p in group['params'])
    print('GPU_SMOKE_OPTIMIZER_STEP',steps,flush=True)
    if steps==2:raise SmokeComplete
    return value
torch.optim.AdamW.step=step
try:
    try:trainer.train(3,'models/ev-start',1e-5)
    except SmokeComplete:pass
    else:raise AssertionError('GPU smoke did not stop after two optimizer calls')
    assert steps==2 and mask_calls==32 and all(sha(name)==digest for name,digest in sources.items())
    print('GPU_SMOKE_PASSED '+json.dumps({'optimizer_calls':steps,'device':'cuda','finite_model_gradients_and_parameters':True,
        'unmasked_and_mixed_mask_batches':True,'peak_allocated_bytes':torch.cuda.max_memory_allocated(),
        'job_inputs_and_parent_unchanged':True,'not_a_trained_candidate':True,'trainer_sha256':sources['train_ev_reviewed.py']}),flush=True)
finally:
    torch.optim.AdamW.step=original_step;trainer.random.shuffle=original_shuffle;trainer.random.random=original_random
