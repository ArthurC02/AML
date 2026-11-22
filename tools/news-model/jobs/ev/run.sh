# Event model, ordinary CLS: continue models/ev-start for @EPOCHS@ epoch(s) at lr @LR@ with the asked name masked in half
# of the rows (train_ev_reviewed.py), after a two-step GPU smoke test. Trained weights: the last epoch, in out/ev with
# the per-epoch dev predictions (diagnostics/); logs-ev.txt, gpu-smoke.log and job.json beside it. Counts only in logs.
log() { echo "$(date +%H:%M:%S) $*" >> log.txt; }
fail() { log "FAIL $1"; echo "$1" > fail.txt; exit 1; }
cd /content/job@NAME@ || fail "cd /content/job@NAME@ failed"
set -o pipefail
log "job@NAME@ ev start"
pip -q install "transformers==5.22.2" > pip.log 2>&1 || fail "pip install failed"
log "versions $(python -I -c 'import torch, transformers; print(torch.__version__, transformers.__version__)' 2>&1 | tail -1)"
nvidia-smi > gpu.log 2>&1 || fail "nvidia-smi failed"
python -I gpu_smoke_reviewed.py > gpu-smoke.log 2>&1 || fail "gpu_smoke_reviewed.py failed"
log "gpu smoke passed"
python -I train_ev_reviewed.py @EPOCHS@ models/ev-start @LR@ entity-mask 2>&1 | tee logs-ev.txt >> log.txt || fail "train_ev_reviewed.py failed"
mkdir -p out/ev && cp models/ev/last-epoch/* out/ev/ && cp -r models/ev/diagnostics out/ev/ && cp logs-ev.txt gpu-smoke.log job.json out/ || fail "copy to out failed"
log "saved out/ev"
log "JOB_DONE"; echo DONE > done.txt
