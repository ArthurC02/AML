#!/bin/bash
# Language-model stage reader inference on the Colab VM (built by tools/llm-stage/build_job.py; settings in job.env):
# install Ollama (official script, no account), serve it, pull each model in MODELS, run infer.py on the sets in SETS
# (all eight when job.env has none), one person per call. Any failing step writes fail.txt and exits non-zero. Progress lines go to log.txt (read by peek).
log() { echo "$(date +%H:%M:%S) $*" >> log.txt; }
fail() { log "FAIL $1"; echo "$1" > fail.txt; exit 1; }
cd "$(dirname "$0")" || fail "cd to the job folder failed"
set -a; . ./job.env || fail "job.env failed"; set +a
log "run.sh start"
if ! command -v zstd > /dev/null; then
  (apt-get update -qq && apt-get install -y -qq zstd) > apt.log 2>&1 || fail "apt-get install zstd failed"
fi
command -v curl > /dev/null || fail "curl missing"
timeout 300 curl -fsSL https://ollama.com/install.sh -o ollama-install.sh || fail "download of the Ollama install script failed"
timeout 1200 sh ollama-install.sh > install.log 2>&1 || fail "Ollama install script failed"
command -v ollama > /dev/null || fail "ollama not on PATH after install"
log "ollama $(ollama --version 2>&1 | tail -1)"
nohup ollama serve > ollama.log 2>&1 < /dev/null &
up=0
for n in $(seq 1 90); do
  curl -sf http://127.0.0.1:11434/api/version > /dev/null && { up=1; break; }
  sleep 2
done
[ $up = 1 ] || fail "Ollama API did not answer within 3 minutes"
log "ollama api $(curl -s http://127.0.0.1:11434/api/version)"
for m in $MODELS; do
  t=$(date +%s)
  timeout 3600 ollama pull $m > pull.log 2>&1 || fail "ollama pull $m failed"
  log "pulled $m in $(( $(date +%s) - t ))s: $(ollama list | grep -F "$m" | head -1)"
done
nvidia-smi >> log.txt 2>&1 || fail "nvidia-smi failed"
python3 -c "import requests" > /dev/null 2>&1 || pip -q install requests > pip.log 2>&1 || fail "requests missing and pip install failed"
python3 -I infer.py $MODELS > infer.log 2>&1
rc=$?
if [ $rc != 0 ]; then
  [ -f fail.txt ] || { log "FAIL infer.py exited $rc"; echo "infer.py exited $rc" > fail.txt; }
  exit 1
fi
[ -f done.txt ] || fail "infer.py ended without done.txt"
log "run.sh end"
