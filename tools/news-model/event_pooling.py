"""Configuration-aware event logits; native weights remain ordinary BERT sequence-classification weights."""
import torch
from transformers.modeling_outputs import SequenceClassifierOutput


def event_logits(model, encoded):
    mode = getattr(getattr(model, 'config', None), 'aml_pooling', 'cls')
    if mode in (None, 'cls'):
        return model(**encoded).logits
    if mode != 'markers':
        raise ValueError('Unknown event pooling mode')
    hidden = model.bert(**encoded, return_dict=True).last_hidden_state
    marked = encoded['input_ids'].eq(1) & encoded['attention_mask'].bool() & encoded['token_type_ids'].eq(1)
    count = marked.sum(1, keepdim=True)
    entity = (hidden * marked.unsqueeze(-1)).sum(1) / count.clamp_min(1)
    representation = torch.where(count.gt(0), entity, hidden[:, 0])
    pooled = model.bert.pooler.dense(representation).tanh()
    return model.classifier(model.dropout(pooled))


class EventExport(torch.nn.Module):
    def __init__(self, model):
        super().__init__()
        self.model = model

    def forward(self, input_ids, attention_mask, token_type_ids):
        return SequenceClassifierOutput(logits=event_logits(self.model, {
            'input_ids': input_ids, 'attention_mask': attention_mask, 'token_type_ids': token_type_ids}))
