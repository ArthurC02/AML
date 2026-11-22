#!/bin/bash
# Fine-tune job on the Colab VM (L4, bf16; built by tools/llm-stage/build_job.py; settings in job.env): fine-tune Gemma 4
# for the legal-stage reader and measure it as the inference job measures the base models. (a) pip install per Unsloth's
# Gemma 4 Colab recipe, versions logged; (b) train.py: LoRA SFT on data/train.jsonl, lora/ saved, Q8_0 GGUF exported
# (path in gguf.txt); (c) Ollama installed and served as in the inference job, the same-size base model BASE pulled only
# for its non-FROM Modelfile lines (TEMPLATE, PARAMETER ...), model `ft` created from the GGUF with those lines,
# infer.py ft on all eight sets (writes out/ft/).
# Any failing step writes fail.txt and exits non-zero. Progress lines go to log.txt (read by peek).
log() { echo "$(date +%H:%M:%S) $*" >> log.txt; }
fail() { log "FAIL $1"; echo "$1" > fail.txt; exit 1; }
cd "$(dirname "$0")" || fail "cd to the job folder failed"
set -a; . ./job.env || fail "job.env failed"; set +a
log "run.sh start $MODEL base $BASE"
nvidia-smi >> log.txt 2>&1 || fail "nvidia-smi failed"

# (a) Unsloth's Gemma 4 Colab install recipe
pi() { log "pip $*"; timeout 1800 pip install -q "$@" >> pip.log 2>&1 || fail "pip install $* failed"; }
pi sentencepiece protobuf "datasets==4.3.0" "huggingface_hub>=0.34.0" hf_transfer
pi --no-deps unsloth_zoo bitsandbytes accelerate peft trl triton unsloth
timeout 1800 unsloth install-kernels >> pip.log 2>&1 || log "unsloth install-kernels failed with $? (tolerated)"
pi --no-deps --upgrade "torchao>=0.16.0"
if [ "$TRANSFORMERS" = latest ]; then
  # transformers 5.5.0 (the E4B notebook's pin) does not support Gemma 4 12B: the newest release instead.
  pi --upgrade transformers tokenizers
else
  pi --no-deps transformers==5.5.0 "tokenizers>=0.22.0,<=0.23.0"
fi
pi "huggingface_hub>=1.5.0,<2.0"
log "versions $(python3 -c "import importlib.metadata as m, sys
def v(p):
    try: return m.version(p)
    except Exception: return 'missing'
print('python', sys.version.split()[0], ' '.join('%s=%s' % (p, v(p)) for p in ['torch', 'transformers', 'tokenizers', 'trl', 'peft', 'accelerate', 'bitsandbytes', 'unsloth', 'unsloth_zoo', 'torchao', 'triton', 'datasets', 'huggingface_hub']))" 2>&1 | tail -1)"

# (b) fine-tune and export (not -I: Unsloth imports its compiled cache from the working folder)
python3 train.py $MODEL > train.log 2>&1
rc=$?
if [ $rc != 0 ]; then
  [ -f fail.txt ] || { log "FAIL train.py exited $rc"; echo "train.py exited $rc" > fail.txt; }
  exit 1
fi
[ -f lora/adapter_config.json ] || fail "train.py ended without lora/adapter_config.json"
GGUF=$(head -1 gguf.txt 2> /dev/null)
[ -n "$GGUF" ] && [ -f "$GGUF" ] || fail "train.py ended without a GGUF named in gguf.txt"

# (c) Ollama as in the inference job
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
t=$(date +%s)
timeout 3600 ollama pull $BASE > pull.log 2>&1 || fail "ollama pull $BASE failed"
log "pulled $BASE in $(( $(date +%s) - t ))s (template source only)"
ollama show --modelfile $BASE > base.Modelfile 2>> ollama.log || fail "ollama show --modelfile $BASE failed"
python3 -I - "$GGUF" >> log.txt 2>&1 <<'EOF' || fail "Modelfile build failed"
import re, sys, time
out, inq, n_from, kinds = ["FROM " + sys.argv[1]], False, 0, {}
for line in open("base.Modelfile", encoding="utf-8").read().split("\n"):
    if not inq:
        if re.match(r"FROM\s", line, re.I): n_from += 1; continue
        if line.startswith("#"): continue
        k = line.split(" ", 1)[0].upper()
        if k: kinds[k] = kinds.get(k, 0) + 1
    out.append(line)
    if line.count('"""') % 2: inq = not inq
assert not inq, "unbalanced triple quotes"
assert n_from >= 1 and ("TEMPLATE" in kinds or "RENDERER" in kinds), "base Modelfile has no FROM or no TEMPLATE/RENDERER"
open("Modelfile", "w", encoding="utf-8").write("\n".join(out).rstrip("\n") + "\n")
print(time.strftime("%H:%M:%S"), "Modelfile: FROM the fine-tuned gguf; base FROM lines dropped", n_from, "| kept", " ".join("%s %d" % kv for kv in sorted(kinds.items())))
EOF
t=$(date +%s)
timeout 3600 ollama create ft -f Modelfile > create.log 2>&1 || fail "ollama create ft failed"
log "created ft in $(( $(date +%s) - t ))s: $(ollama list | grep -E '^ft[: ]' | head -1)"
ollama rm $BASE >> ollama.log 2>&1 || log "ollama rm $BASE failed (tolerated)"
python3 -c "import requests" > /dev/null 2>&1 || pip -q install requests >> pip.log 2>&1 || fail "requests missing and pip install failed"
python3 -I infer.py ft > infer.log 2>&1
rc=$?
if [ $rc != 0 ]; then
  [ -f fail.txt ] || { log "FAIL infer.py exited $rc"; echo "infer.py exited $rc" > fail.txt; }
  exit 1
fi
[ -f done.txt ] || fail "infer.py ended without done.txt"
log "run.sh end"
