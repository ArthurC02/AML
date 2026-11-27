---
name: colab-job
description: Use when running anything on Colab for this project (training or inference), including Gemma 4 tests and stage-classifier training.
---

# Colab job

## Prerequisites

A running Docker container named `colab` with the `colab` CLI, signed in with the operator's own Google account,
`AML_COLAB_HOME` mounted at `/home/colab`; host `bash`, `docker`, `timeout` (details in `tools/colab/README.md`). If the
container is missing or not signed in, stop and ask the user; never sign in for them.

## Rules

- Training needs the user's yes for each run. Gemma 4 tests may open Colab without asking.
- GPU: small models (BERT-size classifiers) on T4; Gemma 4 on L4.
- Never print, copy or move credentials or tokens; they stay in the container keyring.
- After every run, personally confirm that `colab sessions` shows no session. If one remains, stop it and check again.
- A hung or failed start is stopped by the runner; rerun the same command to restart.
- Held-back sets are measured only at the end, once the design is fixed.
- Job folders, logs and downloads hold no names or quotes in the repo; data and results stay outside it.

## Steps

1. Make a job folder with `run.sh` (LF line ends) that writes `log.txt` progress lines, `fail.txt` on failure and
   `done.txt` on success, results in `out/` (see `tools/colab/README.md`).
2. `python -I tools/colab/colabjob.py package <job dir> <name> [--extra ...] [--alive ...]`
3. `python -I tools/colab/colabjob.py check <name>`: must end with `ALL OK`.
4. Optionally `bash tools/colab/run_job.sh <name> <T4|L4> <download dir> --dry-run` to see the command sequence.
5. `bash tools/colab/run_job.sh <name> <T4|L4> <download dir> [stall s] [max s]` in the background; wait for it.
6. Right after the start, confirm the job really runs on the VM: peeks show `alive=True` and a growing `LAST` line
   count, and the GPU line in its log; a returned start command alone proves nothing.
7. Read the end of its output: `VERIFIED and extracted`, then the final `colab sessions` with no session listed.
8. Score the download (for the stage models: `tools/stage-model/score*.py`) and report counts only.

## References

- `tools/colab/README.md`: job folder contract, runner behaviour, exit codes.
- `docs/plan/stage-model-training.md`: what to train and measure, and how results are judged.
- `tools/stage-model/README.md`: building the classifier data and exporting ONNX.
