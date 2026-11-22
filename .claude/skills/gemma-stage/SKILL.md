---
name: gemma-stage
description: Use when running, changing or fine-tuning the language-model stage reader (Gemma 4 via Ollama, Unsloth QLoRA) or its prompt, and when re-measuring its answers on the stage sets.
---

# Gemma stage reader

## Rules

- Gemma 4 tests (inference jobs) may open Colab without asking. Training (a fine-tune job) needs the user's yes for each run.
- GPU: L4 for every Gemma 4 job.
- After every run, read the final `colab sessions` yourself: no `aml` session may be listed. If one is, stop it and check again.
- Measure through `tools/Aml.StageEval` only, and adopt a change only by the rule in `docs/plan/stage-model-training.md`
  (unanimous-vote precision not below the current one on each development set, coverage up where intended; held-back
  sets once, at the end).
- The prompt is the runtime prompt: changing it means changing `src/Aml.Infra/Models/stage-prompt.txt`, rebuilding
  `train.jsonl` with `sft.py` for a fine-tune, and re-measuring. Never keep a second copy of the prompt.
- Privacy: data, downloads and models stay outside the repo; print counts only; no names, quotes or document text in
  logs, code, docs or commit messages; never print or move credentials.

## Locations

Locations differ per host; no machine paths are written here. Find them on this host first: the environment variables
(`AML_COLAB_HOME`, `AML_LLM_WORK`, `AML_TRAIN`, `AML_DATA`, `AML_STAGE_WORK`), the paths the tools print, and
`tools/llm-stage/README.md`. If a location is missing or does not hold what is described, stop and ask the user.

## Steps

1. `python -I -X utf8 tools/llm-stage/selfcheck.py` (after any change to `tools/llm-stage`).
2. Fine-tune only: `python -I -X utf8 tools/llm-stage/sft.py`.
3. Build: `build_job.py infer <name>` or `build_job.py finetune <name> --size 12b|e4b`, then `build_job.py check <name>`: `ALL OK`.
4. `python -I tools/colab/colabjob.py package <AML_COLAB_HOME>/job<name> <name>` with `--alive infer.py` (inference) or
   `--extra lora,Modelfile --alive 'train.py|infer.py'` (fine-tune); then `colabjob.py check <name>`: `ALL OK`.
5. Fine-tune: ask the user for this run. Then `bash tools/colab/run_job.sh <name> L4 <download dir>` in the background.
6. Read the end: `VERIFIED and extracted`, then the final `colab sessions` with no session.
7. `dotnet run -c Release --project tools/Aml.StageEval -- <dl>/out/<tag> <dl>/out/<tag>` (`<tag>` is `ft` for a
   fine-tune); report the counts against the current ones in the doc.

## References

- `tools/llm-stage/README.md`: parts, parameters, end-to-end commands, known limits.
- `tools/colab/README.md`: job contract, runner, exit codes.
- `docs/plan/stage-model-training.md`: decisions in force and how a change is judged.
