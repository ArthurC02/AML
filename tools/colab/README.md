# colab

One generic runner for Colab jobs (training and inference). A job is a folder with a `run.sh`; `colabjob.py` packages
it, `run_job.sh` uploads, starts, watches, downloads and stops it. Data and results stay outside the repo.

Prerequisite: the `colab` docker container (Google's `google-colab-cli`) is running, already signed in, with
`AML_COLAB_HOME` (default `C:/tmp/colab/home`) mounted at `/home/colab`. The credentials live only in the container's
keyring: never print, copy or move them, and never put them in a job folder.

## 1. Make a job folder

`run.sh` runs on the VM as `bash /content/job<name>/run.sh` with that folder as working directory, detached; its
stdout/stderr go to `run.log`. It must

- define `log`/`fail` first, then `cd /content/job<name> || fail ...` (the folder name is fixed by the package name; a
  failing `cd` or settings file is a failure like any other);
- append progress lines to `log.txt`, at least one every few minutes (the runner stops a job whose last `log.txt` line
  has not changed for the stall limit, default 45 min). Lines with `step N/M` are shown as progress; lines matching the
  milestone regex (`DONE|FAIL |ONNX_|epoch |saved |versions ` unless `--milestones` says otherwise) are printed by peek;
- on any failure write the reason to `fail.txt` (and a `FAIL ...` line to `log.txt`) and exit non-zero;
- on success write `done.txt` last;
- put results in `out/`; everything else (model weights, base models) stays on the VM unless listed with `--extra`;
- never write document text, names or quotes to `log.txt` or any `*.log`: peek prints milestone lines and, for a job
  that is neither alive nor done, the tails of `log.txt` and every `*.log`, and the runner echoes them.

Keep it LF-only. Example:

```bash
log() { echo "$(date +%H:%M:%S) $*" >> log.txt; }
fail() { log "FAIL $1"; echo "$1" > fail.txt; exit 1; }
cd /content/job61 || fail "cd /content/job61 failed"
nvidia-smi > gpu.log 2>&1 || fail "nvidia-smi failed"
python3 -I train.py > train.log 2>&1 || fail "train.py exited $?"
log "JOB_DONE"; echo DONE > done.txt
```

## 2. Package and check

```bash
python -I tools/colab/colabjob.py package <job dir> <name> [--extra onnx,lora] [--alive 'train.py|infer.py'] [--milestones REGEX]
python -I tools/colab/colabjob.py check <name>      # must end with ALL OK
```

`package` writes `job<name>.tgz`, `j<name>.part.NN` (40 MiB) and `start<name>.py`, `peek<name>.py`, `pack<name>.py`
(from the `*.py.tmpl` here; the archive sha is embedded in start) into `AML_COLAB_HOME`. `--alive` adds process
patterns that count as alive besides `run.sh`; `--extra` adds files or folders to the download.

## 3. Run

GPU: small models (BERT-size classifiers) on **T4**; Gemma 4 on **L4**.

```bash
bash tools/colab/run_job.sh <name> <T4|L4> <download dir> [stall seconds] [max seconds] [--reuse]   # defaults 2700, 43200
bash tools/colab/run_job.sh <name> L4 C:/tmp/x --dry-run   # prints every docker command, simulated answers, no Colab
AML_DRY_SIM=existing bash tools/colab/run_job.sh <name> L4 C:/tmp/x --dry-run   # session already open: exit 5
AML_DRY_SIM=nolist bash tools/colab/run_job.sh <name> L4 C:/tmp/x --dry-run     # stop not confirmed: exit 7
```

Session `aml<name>` is opened; if it is already open the runner refuses (exit 5) and leaves it alone, since an old
`done.txt` or `out/` there could pass as new results (`--reuse` uses it anyway). Sessions are matched by their exact
`[aml<name>]` field, so `aml1` never matches `aml10`. Parts are uploaded (5 tries each), the job started
(`STARTED`/`ALREADY`), peeked every `AML_COLAB_PEEK` seconds (default 180); done, alive and fail are read from peek's
`PEEK` status line only. The runner stops the session on `fail.txt`, on a dead job, on a stalled log, at the time limit
or after five failed peeks, downloading what exists first while the VM answers. The download is packed on the VM,
fetched in parts (md5 per part, sha256 of the join) and extracted to the download dir. An EXIT trap stops the session
if still open and ends with `colab sessions`. A session counts as gone only when `colab sessions` succeeds with its
normal listing without `[aml<name>]`; when five stop attempts cannot confirm that, the runner prints `SESSION MAY STILL
BE RUNNING` and exits 7, whatever happened before. A start that hangs or fails is stopped; run the same command again
to restart. Exit codes are listed at the top of `run_job.sh`.

A hard kill of the runner (SIGKILL, `kill -9`, Task Manager ending the process tree, the machine going down) skips the EXIT
trap, so nothing stops the session: stop it by hand (`colab stop -s aml<name>` in the container) and check `colab
sessions`.

## 4. Confirm

After every run, read the final `colab sessions` lines yourself: it must list no `aml` session. If one is listed, stop
it (`colab stop -s aml<name>` in the container) and check again.
