"""Continue ordinary CLS on reviewed train labels; keep epoch zero, per-epoch predictions and last trained weights."""
from collections import Counter
import hashlib
import json
import math
from pathlib import Path
import random
import sys
import time
import numpy as np
import torch
from transformers import AutoModelForSequenceClassification,get_linear_schedule_with_warmup
sys.path.insert(0,str(Path(__file__).parent))
import train_ev as ev

def sha(path):return hashlib.file_digest(Path(path).open('rb'),'sha256').hexdigest()

def evaluate(model,rows,labels):
    probabilities,gold=ev.predict(model,rows,labels)
    missing=sum(label not in labels for _,_,values in rows for label in values)
    scores=[dict(threshold=round(.05+i*.025,3),**ev.score_probabilities(probabilities,gold,labels,round(.05+i*.025,3),missing)) for i in range(37)]
    return max(scores,key=lambda r:r['F1']),probabilities,gold

def train(epochs,start,lr):
    assert 1<=epochs<=20 and 0<lr<=1e-3
    random.seed(0);torch.manual_seed(0)
    original=ev.load('train');masked=ev.load('train',mask_entity=True);dev=ev.load('dev')
    assert len(original)==len(masked) and all(a[2]==b[2] for a,b in zip(original,masked))
    config=json.loads((Path(start)/'config.json').read_text())
    labels=[config['id2label'][str(i)] for i in range(len(config['id2label']))]
    support=Counter(label for _,_,values in original for label in values)
    assert labels==sorted(label for label,count in support.items() if count>=5)
    assert config.get('aml_pooling','cls') in (None,'cls') and not config.get('aml_event_document_context',False)
    metadata=json.loads(Path('job.json').read_text()) if Path('job.json').exists() else {}
    if metadata.get('event_reviewed_gold'):
        proof=json.loads(Path('review-data-impact.json').read_text())
        assert sha('data/ev-train.jsonl')==proof['training_data']['ev']['after_sha256']==metadata['sha256']['data/ev-train.jsonl']
        assert sha('data/ev-dev.jsonl')==metadata['sha256']['data/ev-dev.jsonl']
        assert proof['held_out_and_split_bytes_and_mtimes_unchanged'] and not proof['performance_claimed']
    model=AutoModelForSequenceClassification.from_pretrained(start).to(ev.DEV).train()
    output=Path(ev.OUT);diagnostics=output/'diagnostics';diagnostics.mkdir(parents=True,exist_ok=True)
    model.config.training_entity_mask_probability=.5
    model.config.training_target_contrast_weight=0.
    model.config.training_reviewed_gold=bool(metadata.get('event_reviewed_gold'))
    model.config.training_data_sha256=sha('data/ev-train.jsonl')
    def save(directory,epoch):
        model.config.training_selected_epoch=epoch
        model.save_pretrained(directory);ev.tok.save_pretrained(directory)
    def measure(epoch):
        score,probabilities,gold=evaluate(model,dev,labels)
        np.savez_compressed(diagnostics/('epoch-%02d.npz'%epoch),probabilities=probabilities.numpy(),gold=gold.numpy(),labels=np.array(labels))
        (diagnostics/('epoch-%02d.json'%epoch)).write_text(json.dumps({'epoch':epoch,'dev':score,'dev_data_sha256':sha('data/ev-dev.jsonl')}))
        return score
    initial=measure(0)
    if metadata.get('reviewed_gold_parent_counts') is not None:
        assert [initial[k] for k in ('tp','fp','fn')]==metadata['reviewed_gold_parent_counts']
        assert initial['threshold']==metadata['reviewed_gold_parent_threshold']
    print('initial_reviewed_gold_dev',json.dumps(initial),flush=True)
    save(output,0);best=initial['F1'];best_epoch=0
    steps=epochs*math.ceil(len(original)/16)
    optimizer=torch.optim.AdamW(model.parameters(),lr=lr,weight_decay=.01)
    schedule=get_linear_schedule_with_warmup(optimizer,int(.1*steps),steps)
    print('training_setup',json.dumps({'stage':'reviewed train labels, ordinary CLS','epochs':epochs,'lr':lr,'rows':len(original),
        'training_entity_mask_probability':.5,'auxiliary_weight':0.,'target_contrast_weight':0.,'pooling':'cls',
        'model_training':model.training,'embedding_dropout_training':model.bert.embeddings.dropout.training,
        'per_epoch_dev_predictions_saved':True,'last_trained_checkpoint_saved':True,'inference_input':'unchanged CLS mention-marker pairs'}),flush=True)
    step=0;t0=time.time();total_masked=0
    for epoch in range(epochs):
        model.train();order=list(range(len(original)));random.shuffle(order)
        seen=masked_count=0;loss_sum=0.
        for at in range(0,len(order),16):
            indexes=order[at:at+16];mask=[random.random()<.5 for _ in indexes]
            chunk=[masked[i] if m else original[i] for i,m in zip(indexes,mask)]
            assert model.training and model.bert.embeddings.dropout.training
            features,gold=ev.encode(chunk,labels);logits=ev.event_logits(model,features);targets=gold.to(logits.device)
            assert targets.device==logits.device
            loss=torch.nn.functional.binary_cross_entropy_with_logits(logits,targets)
            assert torch.isfinite(loss)
            loss.backward();torch.nn.utils.clip_grad_norm_(model.parameters(),1.,error_if_nonfinite=True)
            optimizer.step();schedule.step();optimizer.zero_grad();step+=1
            seen+=len(indexes);masked_count+=sum(mask);loss_sum+=loss.item()*len(indexes)
            if step==1 and ev.DEV=='cuda':
                print('first_batch_gpu',json.dumps({'shape':list(features['input_ids'].shape),'peak_allocated_bytes':torch.cuda.max_memory_allocated(),'model_training':model.training}),flush=True)
            if step%20==0:print(f'step {step}/{steps} loss {loss.item():.4f} {time.time()-t0:.0f}s',flush=True)
        assert seen==len(original)
        total_masked+=masked_count
        print('reviewed_gold_epoch',json.dumps({'epoch':epoch+1,'rows':seen,'masked_rows':masked_count,'mean_training_BCE':loss_sum/seen}),flush=True)
        score=measure(epoch+1)
        print('epoch',epoch+1,'dev',json.dumps(score),flush=True)
        save(output/'last-epoch',epoch+1)
        if score['F1']>best:best=score['F1'];best_epoch=epoch+1;save(output,epoch+1)
    summary={'best_dev_F1':best,'best_epoch':best_epoch,'last_epoch':epochs,'masked_rows':total_masked,
        'unchanged_inference_input':True,'dev_only_selection':True,'per_epoch_dev_predictions_saved':True,'last_trained_checkpoint_saved':True}
    print('TRAINING_COMPLETE',json.dumps(summary),flush=True)
    return summary

if __name__=='__main__':
    assert len(sys.argv)==5 and sys.argv[4]=='entity-mask'
    train(int(sys.argv[1]),sys.argv[2],float(sys.argv[3]))
