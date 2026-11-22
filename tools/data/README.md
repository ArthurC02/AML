# data

The datasets the repository's tools read, packed so they can move to another machine: labels, raw texts, the train /
development / held-back measured sets and rows, and the cached language-model answers `tools/Aml.StageEval` replays.
Model weights and base models are never in it (only the bert-base-chinese tokenizer files `prep_stage.py` needs); the
deployed classifier, the news models and base models come separately.

**The data is sensitive personal data**: real names of people in criminal proceedings. The scripts print counts and
hashes only. The original bundle exists unencrypted only while it is being packed; the pseudonymised copy is still
personal data. An NER sample of 120 texts of the pseudonymised copy still found person spans in 27 (an upper bound: NER
also flags non-names), so the copy is not name-free and must be encrypted and handled as personal data.

| Script | Does |
|---|---|
| `bundle.py build <dest>` | copies the closure (`CLOSURE` in the script) from `AML_DATA` / `AML_TRAIN` into `<dest>/aml-data` and `<dest>/aml-train`, same relative layout; writes `MANIFEST.json` (every file: path, size, sha256, role, set, `heldback`; per-set file, row, person and suspect counts) and `README.txt` |
| `bundle.py verify <root>` | every file against the manifest; exit 1 on a sha mismatch, a missing or extra file, a stray top-level file or directory (anything besides `aml-data`, `aml-train`, `MANIFEST.json`, `README.txt` and, in an original, `PSEUDONYM-MAP.json`), or a map file in a pseudonymised root |
| `bundle.py remove <doc id> <root>` | deletes a document from one bundle: its own files, its lines in every `.jsonl` (by `doc`; a row without one by `id`, as in `court/raw/index.jsonl`, or, such as the fine-tune examples, by a piece of its text no other document holds), its entries in every `.json` (split lists, keyed records) and `.tsv` (first column); drops map entries no remaining file holds; rebuilds the manifest and records the id's sha256 under `removed`. Files that merely name the id (logs, stats) are counted, not edited |
| `pseudonymise.py <original> <dest> --map <original>/PSEUDONYM-MAP.json [--ner <dir>] [--extend]` | the pseudonymised copy (method in the script's docstring): same layout, its own manifest and README; the real -> pseudonym map only to `--map`, which must lie inside the original; residual counts of inventory forms left (must be 0) and of person candidates no pseudonym covers (a cue detector over every raw text; with `--ner`, the NER model over a seeded sample); `--extend` adds the cue detector's names to the inventory and prints the residual before and after |
| `selfcheck.py` | a made-up tiny bundle through build, verify, pseudonymise and remove; prints `selfcheck OK` |

Roles in the manifest: `labels`, `raw` (texts and their indexes; `unseen.jsonl`, the unlabelled court releases),
`train`, `dev`, `heldback` (v11, accept5, the held-out rows, the test splits), `split`, `stats`, `answers` (with
`heldback` on the v11 and accept5 answers), `tokenizer`. Held-back files are for aggregate counts computed once at the
end of a change; never open their texts or per-person rows.

## Make a bundle and encrypt it

```bash
python -I -X utf8 tools/data/selfcheck.py
python -I -X utf8 tools/data/bundle.py build <work>/original
python -I -X utf8 tools/data/pseudonymise.py <work>/original <work>/pseudonymised --map <work>/original/PSEUDONYM-MAP.json --ner <NER model dir> --extend
python -I -X utf8 tools/data/bundle.py verify <work>/original
python -I -X utf8 tools/data/bundle.py verify <work>/pseudonymised
```

The user encrypts, in their own terminal (7-Zip, AES-256, encrypted file names; `-p` without a value makes 7z ask for
the password, so it never appears on a command line or in a history):

```bat
"C:\Program Files\7-Zip\7z.exe" a -t7z -mhe=on -p aml-original.7z <work>\original
"C:\Program Files\7-Zip\7z.exe" a -t7z -mhe=on -p aml-pseudonymised.7z <work>\pseudonymised
"C:\Program Files\7-Zip\7z.exe" t -p aml-original.7z
"C:\Program Files\7-Zip\7z.exe" t -p aml-pseudonymised.7z
```

`7z t` must end with `Everything is Ok` (it asks for the password: with `-mhe=on` even the listing is encrypted). Then
the user deletes the unencrypted folders `<work>\original` and `<work>\pseudonymised` (and empties the recycle bin);
the original holds the map back to the real names. Agents never type, see or store the password and never create the
archive.

## Restore on another host

```bat
"C:\Program Files\7-Zip\7z.exe" x -p -o<dir> aml-original.7z
python -I -X utf8 tools/data/bundle.py verify <dir>\original
set AML_DATA=<dir>\original\aml-data
set AML_TRAIN=<dir>\original\aml-train
```

The tools then run unchanged (`AML_STAGE_WORK`, `AML_LLM_WORK`, `AML_COLAB_HOME`, `AML_JUDGMENTS`, `AML_STAGE_EVAL_OUT`
point outputs elsewhere). `tools/Aml.StageEval` takes `sets=<AML_TRAIN>\measurements`, the answer folder
`<AML_TRAIN>\downloads\aml49\out\gemma4_12b-it-q4_K_M` (twice) and `onnx=` the classifier folder, which is not bundled.
Record the copy in the register.

## Remove a judgment from every copy

When a document must go (a judgment withdrawn or a request to erase), remove it from **every** copy in the register:
decrypt each archive, `bundle.py remove <doc id> <root>`, `bundle.py verify <root>`, re-encrypt as above, delete the
unencrypted folder, note the removal in the register.

`court/list.jsonl` rows carry no document id (only `date`, `no`, `title`, `url`), and no tool derives the id from `no`
(court document ids are sequential; `jids.py` joins a list row to its document through the `url` of the document's
`court/raw/index.jsonl` row). So `bundle.py remove` keeps that row: before running it, delete by hand the
`court/list.jsonl` line whose `url` equals the `url` of the document's index row (the remove then rebuilds the
manifest over the edited file), in every copy and in the working data. The working data under `AML_DATA` / `AML_TRAIN` needs the same
removal by hand (the label file, the raw text, a rebuild of the rows).

## Completeness

The closure is checked by rebuilding every tool output twice, once from the working locations and once with
`AML_DATA` / `AML_TRAIN` on the bundle (outputs to a scratch `AML_STAGE_WORK`, `AML_LLM_WORK`, `AML_COLAB_HOME`,
`AML_JUDGMENTS`): `prep_stage.py`, `add17.py` (and `--verdicts`), `sft.py`, `build_job.py infer|finetune` and `check`,
`prep.py --round r12` and `r11`, `merge.py` v13 to v17, `jids.py`, `Aml.StageEval` (`sets=` and the answers from the
bundle, `onnx=` the deployed classifier), `gold.py verify`, `recheck.py pick`; every output must be sha-identical and the
Python runs, traced with an audit hook, must open nothing outside the bundle. When a tool starts reading a new file, add
it to `CLOSURE` and repeat. For the pseudonymised copy the same rebuilds must run with the same row counts, labels and
split; its StageEval counts may differ (the rules and the classifier read other names; the language model's stages
are replayed from cache, so its column measures the cached answers, not a reading of the pseudonymised text).

What the pseudonymised copy keeps: every file, row count, split, document id, label structure and reference stage;
character offsets (same-length names); masked forms consistent with the full names. What it does not keep: a form that
occurs only inside another person's longer name (names are mapped whole), so a few persons lose their form in the text
and the news-model event rows differ by a few dozen; people the labels do not list keep their names unless the cue
detector (`--extend`) finds them, so the NER sample still finds unlabelled names; the language model's answers are
replayed by (document, person id), so StageEval's LLM column is the cached reading of the original text.

## Not in the bundle

- Model weights and base models (`models/`, `*.safetensors`, `*.onnx`, `*.bin`, `*.gguf`, `*.pt`), caches
  (`measurements/*/cache*`), classifier predictions (`downloads/aml*/out/pred-*`), older data rounds (`data-r4` ...
  `data-r10`, `data-v*`), snapshots other than NER20's rows, the crawler (`labels/tools/`), original HTML / PDF and full
  `.txt` extractions next to the `.main.txt` / `.para.txt` the tools read, one-off scripts.
- `AML_JUDGMENTS` (`jids.jsonl`: `tools/judgments/jids.py` rebuilds it from the bundle; fetched judgments are not kept).
- The outside evaluator `aml-eval-r4` (`AML_EVAL`) and its own documents, which `score.py`, `score_gemma.py` and
  `tools/news-model/e2e.sh` read.
