---
name: data-bundle
description: Move the AML training and measurement data (labels, raw texts, measured sets, rows, language-model answers) to another machine, restore it there, make the pseudonymised copy, or remove a document from every copy. Use when the user asks to pack, copy, back up, restore, pseudonymise or delete data, or names tools/data.
---

# data-bundle

Tools: `tools/data/` (`bundle.py build|verify|remove`, `pseudonymise.py`, `selfcheck.py`); read `tools/data/README.md`
first, it holds the exact commands.

## Rules

- The data is **sensitive personal data** (real names of people in criminal proceedings). Print and report counts and
  hashes only: never a name, a quote, a text fragment or a real -> pseudonym pair. Nothing data-derived goes into the
  repository.
- The **original bundle exists only encrypted** (7z AES-256 with `-mhe=on`). **The user types the password**: you give
  the command with `-p` and no value, they run it in their own terminal; you never see, type, store or pass it. You do
  not create archives yourself. After `7z t` passes, the user deletes the unencrypted folders.
- The **map file** (`PSEUDONYM-MAP.json`) lives only inside the original root, so it is encrypted with it; never in the
  pseudonymised root (`bundle.py verify` fails if it is there), never anywhere else.
- The bundle carries the baseline stage classifier and the retained news pair (`MODELS` in `bundle.py`), copied
  unchanged into both copies: **a model trained on real text is sensitive** like the texts. The labelling guideline,
  checker and prompts (`labels/tools/`) are in the **original only** (`ORIGINAL_ONLY`). The bert-base-chinese weights are
  not bundled (README "Base model": id, revision, sha256).
- The **pseudonymised copy is still personal data** (case numbers, dates, places and unlabelled people re-identify):
  same handling, encrypted at rest, in the register.
- **Held-back sets** (manifest `heldback: true`: v11, accept5, held-out rows, test splits and their answers) are for
  aggregate counts computed once; never open their texts or per-person rows.
- A **removed judgment is deleted from every copy** listed in the copies register (`tools/data/COPIES.md` template) and
  from the working data, then each copy is re-verified and re-encrypted.

## Paths

Nothing here is machine-specific. The sources are the environment variables `AML_DATA`, `AML_TRAIN` and `AML_EVAL`
(defaults in `tools/stage-model/paths.py`); the destination, the archive names and the NER model folder (for
`pseudonymise.py --ner`, which needs the venv's numpy and onnxruntime; the bundle's `aml43-pair/ner`) are arguments:
find them from the user's environment and the docs (`docs/plan/stage-model-training.md`, "Where things live"), or ask.

## Steps

- **Pack:** `selfcheck.py`; `bundle.py build <work>/original`; `pseudonymise.py <work>/original <work>/pseudonymised
  --map <work>/original/PSEUDONYM-MAP.json [--ner <dir>] --extend` (report its residual counts, before and after); `bundle.py verify` both; give
  the user the 7z commands from the README; register the copies.
- **Restore:** the user extracts (`7z x -p`); `bundle.py verify <root>`; set `AML_DATA=<root>/aml-data`,
  `AML_TRAIN=<root>/aml-train`, `AML_EVAL=<a copy of root/aml-eval>`, `AML_STAGE_MODEL`, `AML_NEWS_MODELS` as the README
  says; fetch the base-model weights; rebuild the news pair's caches; register the copy.
- **Remove:** for every registered copy, once the user has decrypted it: `bundle.py remove <doc id> <root>`,
  `bundle.py verify <root>`; the user re-encrypts and deletes the folder; note the removal (sha256 of the id). The
  models and the original's guideline / prompts are not edited by `remove`: tell the owner (README, "Remove").
- After changing a tool that reads new data, extend `CLOSURE` in `bundle.py` and re-run the completeness proof in the
  README's spirit: rebuild the outputs from the bundle and from the working locations and compare sha256.
