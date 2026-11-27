# llm-stage

The language-model stage reader off line: Gemma 4 through Ollama reading each suspect's legal stage on the measured
sets (one person per call, the runtime prompt), and its QLoRA fine-tune with Unsloth. Jobs run on Colab through
[tools/colab](../colab/README.md); answers are measured through [tools/Aml.StageEval](../Aml.StageEval/) and judged by
[stage-model training](../../docs/plan/stage-model-training.md). The runtime reader is `src/Aml.Infra/Models/OllamaStageReader.cs`.

The data holds real names. It stays outside the repo; the scripts print counts and hashes only, and the VM side writes
counts, document ids, stage names and server status to `log.txt`, never text. Run Python isolated (`python -I -X utf8`).

## Parts

| Part | Does |
|---|---|
| `jobs/infer/infer.py` | VM side, standard library + requests: one `/api/chat` call per (document, person), the prompt listing that person only; JSON-schema answer, temperature 0, seed 1, `num_ctx` 24576; texts over 16,000 characters cut to the first 10,000 + the last 6,000 as the runtime cuts; writes `out/<model tag>/<set>.jsonl` (per person: stage or `MISSING`, quote, whether the quote is in the text), restartable; fails on 2 of the first 3 documents unreadable or 5 failures in a row |
| `jobs/infer/run.sh` | installs and serves Ollama, pulls each of `MODELS`, runs `infer.py` |
| `jobs/finetune/train.py` | Unsloth `FastModel` 4-bit, LoRA r 16 / alpha 16 on the language layers, loss on the answer only (checked on 20 encoded examples), one epoch (or `MAX_STEPS`), batch 1 x 8, lr 1e-4 cosine; drops examples over `MAXLEN` tokens (fails above 5 %); saves `lora/`, exports a Q8_0 GGUF (path in `gguf.txt`) |
| `jobs/finetune/run.sh` | Unsloth's Gemma 4 install recipe (transformers pinned to 5.5.0 or the newest, `TRANSFORMERS`), `train.py`, then Ollama: model `ft` from the GGUF with the base model's template lines, `infer.py ft` |
| `build_job.py` | builds a job folder `AML_COLAB_HOME/job<name>/`: the scripts above, `prompt.txt` copied from `src/Aml.Infra/Models/stage-prompt.txt`, `job.env` (the parameters), `data/<set>.jsonl` for the measured sets (suspects only: id and their forms, no stage; an inference job the `--sets`, recorded as `SETS` in `job.env` and read by `infer.py`, default all eight; a fine-tune all eight), and for a fine-tune `data/train.jsonl`; `check <name>` re-verifies all of it (below) |
| `sft.py` | builds the fine-tune examples: one per classifier train row (`AML_STAGE_WORK/data17v/train.jsonl`, tools/stage-model), the user turn the runtime prompt for person `1`, the answer `{"persons":[{"id":"1","stage":...,"quote":""}]}`; asserts every rebuilt row equals its source row and that no train document id or text is in an evaluation set; writes `AML_LLM_WORK/data/train.jsonl` |
| `common.py` | locations, set list, the text cut, person forms, the per-call prompt, the data line |
| `selfcheck.py` | asserts on the helpers, `infer.py`'s prompt and answer reading against a fake Ollama and its `SETS`, `job.env` quoting and `--sets`; no data, GPU or network; prints `selfcheck OK` |

`build_job.py check <name>` (ends `ALL OK`, exit 1 otherwise): scripts and prompt byte-identical to their sources;
exactly the expected files, the data files those of `SETS` (all eight in a job.env without it); every evaluation file byte-identical to a fresh build from the measured rows, keys
doc/text/persons/id/forms only; for a fine-tune, `train.jsonl` byte-identical to sft.py's output, every example wrapping
the current prompt, no train text (cut) equal to an evaluation text and no train source document in an evaluation set;
`run.sh` LF-only and passing `bash -n`; sha256 of every file.

## Prerequisites

- Host: Python 3; `build_job.py`, `common.py` and `selfcheck.py` use the standard library and `tools/stage-model`
  (`gold`, `paths`), `sft.py` also `prep_stage.py`'s row builder, so `transformers` and the tokenizer in
  `AML_TRAIN/models/bert-base-chinese` as [tools/stage-model](../stage-model/README.md) needs them. The host packages
come from `tools/stage-model/requirements.txt` (it pins `transformers` and `tokenizers`; nothing else here is imported
on the host). `requests`, which `infer.py` imports, is needed on the VM only, where `run.sh` installs it if it is missing. Jobs run through
  [tools/colab](../colab/README.md) (its prerequisites: the `colab` container signed in with your own account), on an L4.
- The VM side needs internet: `run.sh` downloads Ollama's install script from ollama.com and pulls the model tag. The
  deployed tag is `gemma4:12b-it-q4_K_M` (the default of `build_job.py infer`; `--size e4b` uses `gemma4:e4b-it-q4_K_M`);
  a fine-tune downloads `unsloth/gemma-4-12b-it` or `unsloth/gemma-4-E4B-it`.
- Local runs, without Colab: an Ollama server with `ollama pull gemma4:12b-it-q4_K_M`; the product reads it with
  `StageModels:LlmUrl` (the server's root) and `StageModels:LlmModel` (the tag), the synthetic-set test with
  `AML_STAGE_LLM_URL` / `AML_STAGE_LLM_MODEL` beside `AML_STAGE_MODEL`. The reader speaks Ollama's `/api/chat`, so a
  measurement goes through Ollama.

## Environment

| Variable | Default | Holds |
|---|---|---|
| `AML_COLAB_HOME` | `C:/tmp/colab/home` | job folders and packages (mounted at `/home/colab` in the `colab` container) |
| `AML_LLM_WORK` | `AML_TRAIN/gemma-ft` | `data/train.jsonl` from sft.py |
| `AML_TRAIN`, `AML_DATA`, `AML_STAGE_WORK` | see [tools/stage-model](../stage-model/README.md) | measured sets (`measurements/report`, `measurements/court`), label folders, `data17v/` |

The defaults are the paths of the machine the tools were written on; set the variables on any other host. The Python
tools fall back to the defaults, the shell does not: the commands below use `$AML_COLAB_HOME` and `$AML_TRAIN`, so export
both first (the defaults on that machine, your own paths elsewhere):

```bash
export AML_TRAIN=/path/to/aml-train AML_COLAB_HOME=/path/to/colab/home
```

## Run the inference (the deployed 12B q4_K_M)

```bash
python -I -X utf8 tools/llm-stage/selfcheck.py
python -I -X utf8 tools/llm-stage/build_job.py infer <name>            # --models a,b for other Ollama tags; --sets a,b for some sets
python -I -X utf8 tools/llm-stage/build_job.py check <name>            # ALL OK
python -I tools/colab/colabjob.py package $AML_COLAB_HOME/job<name> <name> --alive infer.py --milestones 'DONE|FAIL |MODEL_START| sanity | think: | pulled | tps '
python -I tools/colab/colabjob.py check <name>                         # ALL OK
bash tools/colab/run_job.sh <name> L4 $AML_TRAIN/downloads/aml<name>          # --dry-run first to see the steps
dotnet run -c Release --project tools/Aml.StageEval -- <dl>/out/gemma4_12b-it-q4_K_M <dl>/out/gemma4_12b-it-q4_K_M
```

One call per suspect: 1,754 over the eight sets at the last build (`check` prints the count; the runner's default limit
is 12 h). The default job answers the held-back v11 and accept5 too, so the evaluator has their cached answers; their
outputs (`out/<model tag>/v11.jsonl`, `accept5.jsonl`) are not opened or scored before the end (no StageEval
`--heldback`, no `score_one.py --heldback`). `--sets` takes known names only (kept in the set order); a held-back set
goes in only when listed, and the builder then prints a WARNING line. `--sets accept` alone is 40 documents, 114 calls.

## Fine-tune and measure

```bash
python -I -X utf8 tools/llm-stage/sft.py                               # needs data17v/ from tools/stage-model add17.py --verdicts
python -I -X utf8 tools/llm-stage/build_job.py finetune <name> --size 12b   # or --size e4b; --maxlen N, --steps N, --transformers latest|pin
python -I -X utf8 tools/llm-stage/build_job.py check <name>
python -I tools/colab/colabjob.py package $AML_COLAB_HOME/job<name> <name> --extra lora,Modelfile --alive 'train.py|infer.py'
python -I tools/colab/colabjob.py check <name>
bash tools/colab/run_job.sh <name> L4 $AML_TRAIN/downloads/aml<name>
dotnet run -c Release --project tools/Aml.StageEval -- <dl>/out/ft <dl>/out/ft
```

The download holds `log.txt`, `out/ft/<set>.jsonl`, `lora/` and the `Modelfile` (no GGUF). After every run read the
final `colab sessions` lines: no `aml` session may be listed. Add `--heldback` to StageEval only once the design is fixed.

## Known limits

- The Unsloth Gemma 4 recipe pins `transformers==5.5.0`, which loads E4B but not 12B: `--size 12b` installs the newest
  transformers (`TRANSFORMERS=latest`); under it the 12B template may open the answer turn with an empty thought channel,
  which the label check allows.
- Unsloth exports Gemma 4 GGUF only as Q8_0, BF16 or F16: a fine-tuned model is measured as Q8_0, so it is not directly
  comparable to the deployed q4_K_M (a q4 deployment needs its own measure).
- Training examples reach about 15,000 tokens, so `MAXLEN` is 16,384; a smaller one drops long examples (more than 5 %
  fails the job).
- Gemma 4 runs on an L4 (24 GB, bf16). The GGUF export writes no log lines of its own, so train.py logs a heartbeat
  every 5 minutes (up to 90) to keep the runner's stall rule from stopping it.
- The prompt is the runtime prompt. Changing it means changing `src/Aml.Infra/Models/stage-prompt.txt`, rebuilding
  `train.jsonl` with sft.py (check fails on examples made with another prompt) and re-measuring.
