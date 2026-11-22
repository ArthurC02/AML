# NER document-role head on the frozen encoder models/ner-start (train_ner_document.py), @EPOCHS@ epochs at lr @LR@;
# the dev-selected head and the unchanged encoder in out/ner, logs-ner.txt and job.json beside it. Counts only in logs.
log() { echo "$(date +%H:%M:%S) $*" >> log.txt; }
fail() { log "FAIL $1"; echo "$1" > fail.txt; exit 1; }
cd /content/job@NAME@ || fail "cd /content/job@NAME@ failed"
set -o pipefail
log "job@NAME@ ner start"
pip -q install "transformers==5.22.2" > pip.log 2>&1 || fail "pip install failed"
log "versions $(python -I -c 'import torch, transformers; print(torch.__version__, transformers.__version__)' 2>&1 | tail -1)"
nvidia-smi > gpu.log 2>&1 || fail "nvidia-smi failed"
python -I train_ner_document.py @EPOCHS@ models/ner-start @LR@ 2>&1 | tee logs-ner.txt >> log.txt || fail "train_ner_document.py failed"
mkdir -p out && cp -r models/ner out/ner && cp logs-ner.txt job.json out/ || fail "copy to out failed"
log "saved out/ner"
log "JOB_DONE"; echo DONE > done.txt
