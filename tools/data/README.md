# data

The datasets the repository's tools read, packed so they can move to another machine: labels, raw texts, the train /
development / held-back measured sets and rows, the cached language-model answers `tools/Aml.StageEval` replays, the
news evaluator's inputs (root `aml-eval`, from `AML_EVAL`), the baseline stage classifier aml47
(`aml-train/downloads/aml47/onnx/`, what `AML_STAGE_MODEL` points to) and the retained news pair NER20 / event43
(`aml-train/measurements/aml43-pair/ner/` and `ev/`, what `AML_NEWS_MODELS` points to; without its caches). The
bert-base-chinese weights are not in it, only its tokenizer files (see [base model](#base-model)).

**The data is sensitive personal data**: real names of people in criminal proceedings. The scripts print counts and
hashes only. The original bundle exists unencrypted only while it is being packed; the pseudonymised copy is still
personal data. An NER sample of 120 texts of the pseudonymised copy still found person spans in 24 (an upper bound: NER
also flags non-names), so the copy is not name-free and must be encrypted and handled as personal data. **The models are
sensitive too**: they were trained on the real texts, can reproduce pieces of them, and are copied unchanged into both
copies.

**Original only:** the news-event labelling guideline `labels/tools/guideline.md`, its checker `check.py` and the
annotation prompts `labels/tools/*-prompt.md` are in the original bundle and never in the pseudonymised copy: they may
quote documents and no pseudonymisation step reads them (`ORIGINAL_ONLY` in `bundle.py`). The rest of `labels/tools/`
(the crawler, its logs, one-off scripts, backups) stays out of both.

| Script | Does |
|---|---|
| `bundle.py build <dest>` | copies the closure (`CLOSURE` in the script) from `AML_DATA` / `AML_TRAIN` / `AML_EVAL` into `<dest>/aml-data`, `<dest>/aml-train` and `<dest>/aml-eval`, same relative layout; writes `MANIFEST.json` (every file: path, size, sha256, role, set, `heldback`; per-set file, row, person and suspect counts) and `README.txt` |
| `bundle.py verify <root>` | every file against the manifest; exit 1 on a sha mismatch, a missing or extra file, a stray top-level file or directory (anything besides `aml-data`, `aml-train`, `aml-eval`, `MANIFEST.json`, `README.txt` and, in an original, `PSEUDONYM-MAP.json`), or a map file in a pseudonymised root |
| `bundle.py remove <doc id> <root>` | deletes a document from one bundle: its own files, its lines in every `.jsonl` (by `doc`; a row without one by `id`, as in `court/raw/index.jsonl`, or, such as the fine-tune examples, by a piece of its text no other document holds), its entries in every `.json` (split lists, keyed records) and `.tsv` (first column); drops map entries no remaining file holds; rebuilds the manifest and records the id's sha256 under `removed`. After the pass every text file still naming the id (logs, stats, the guideline, a row keyed another way such as `<id>#P1`) is counted, not edited, and a non-zero count prints `NOT FULLY REMOVED` (exit code stays 0): check those files by hand. `build` prints `MISSING (not in this bundle)` for an absent model folder or `AML_EVAL` |
| `pseudonymise.py <original> <dest> --map <original>/PSEUDONYM-MAP.json [--ner <dir>] [--extend]` | the pseudonymised copy (method in the script's docstring): same layout without the original-only files, model folders byte for byte, its own manifest and README; the real -> pseudonym map only to `--map`, which must lie inside the original; residual counts of inventory forms left (must be 0) and of person candidates no pseudonym covers (a cue detector over every raw text; with `--ner`, the NER model over a seeded sample); `--extend` adds the cue detector's names to the inventory and prints the residual before and after |
| `selfcheck.py` | a made-up tiny bundle through build, verify, pseudonymise and remove; prints `selfcheck OK` |

Roles in the manifest: `labels`, `raw` (texts and their indexes; `unseen.jsonl`, the unlabelled court releases),
`train`, `dev` (also the evaluator's `dev.jsonl`, `train.jsonl` (its second dev set) and `sg-<set>-r17.tsv`),
`heldback` (v11, accept5, the held-out rows, the test splits, `sg-v11-r17.tsv`), `split`, `stats`, `answers` (with
`heldback` on the v11 and accept5 answers), `tokenizer`, `model`, `guideline` (original only). Held-back files are for aggregate counts computed once at the
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
robocopy <dir>\original\aml-eval <eval work dir> /E
set AML_DATA=<dir>\original\aml-data
set AML_TRAIN=<dir>\original\aml-train
set AML_EVAL=<eval work dir>
set AML_STAGE_MODEL=<dir>\original\aml-train\downloads\aml47\onnx
set AML_NEWS_MODELS=<dir>\original\aml-train\measurements\aml43-pair
```

The news evaluator writes its dumps, `net-ids.json` and caches into `AML_EVAL`, so `AML_EVAL` is a copy of `aml-eval`
outside the bundle (inside it, `verify` would report the dumps as extra files). The tools then run unchanged
(`AML_STAGE_WORK`, `AML_LLM_WORK`, `AML_COLAB_HOME`, `AML_JUDGMENTS`, `AML_STAGE_EVAL_OUT` point outputs elsewhere).
`tools/Aml.StageEval` takes `sets=<AML_TRAIN>\measurements`, the answer folder
`<AML_TRAIN>\downloads\answers-20261123\gemma4_12b-it-q4_K_M` (twice) and `onnx=<AML_TRAIN>\downloads\aml47\onnx`.
Add the base model's weights ([base model](#base-model)). The news pair travels without its evaluator caches
(`cache/`, `cache-dev2/`: tagger spans and event answers keyed by mention and sentence); before the first
`tools/news-model/e2e.sh`, rebuild them from the pair once, from `AML_EVAL` (`dotnet tools/Aml.NewsEval/bin/Release/net10.0/Aml.NewsEval.dll`
after one `e2e.sh` build, or `dotnet run --project tools/Aml.NewsEval -c Release --`):

```bash
dotnet <Aml.NewsEval.dll> <AML_TRAIN>/measurements/aml43-pair dev tagcache cache=<AML_TRAIN>/measurements/aml43-pair/cache
dotnet <Aml.NewsEval.dll> <AML_TRAIN>/measurements/aml43-pair train tagcache cache=<AML_TRAIN>/measurements/aml43-pair/cache-dev2
```

(the event answers are recomputed and memoised on the first `e2e.sh` run). These writes land inside the bundle and
make `verify` report extra files; copy the pair out first if the copy is to stay verifiable. Record the copy in the register.

## Base model

The bert-base-chinese weights are not bundled: Apache-2.0 would allow it (ADR-0015), but the model is published under
a fixed id, so the bundle carries only its tokenizer files. The scripts expect the weights under
`AML_TRAIN/models/bert-base-chinese/` (`model.safetensors` beside the bundled `config.json` and `vocab.txt`), and no
script downloads them there. The copy the models were trained from is Hugging Face `google-bert/bert-base-chinese`
(alias `bert-base-chinese`), revision `8f23c25b06e129b6c986331a13d8d025a92cf0ea`, `model.safetensors` sha256
`3404a1ffd8da507042e8161013ba2a4fc49858b4e3f8fbf5ce5724f94883aec3`. With the venv of `tools/stage-model/requirements.txt`:

```bash
hf download google-bert/bert-base-chinese model.safetensors --revision 8f23c25b06e129b6c986331a13d8d025a92cf0ea --local-dir <AML_TRAIN>/models/bert-base-chinese
```

## Remove a judgment from every copy

When a document must go (a judgment withdrawn or a request to erase), remove it from **every** copy in the register:
decrypt each archive, `bundle.py remove <doc id> <root>`, `bundle.py verify <root>`, re-encrypt as above, delete the
unencrypted folder, note the removal in the register.

`court/list.jsonl` rows carry no document id (only `date`, `no`, `title`, `url`), and no tool derives the id from `no`
(court document ids are sequential; `jids.py` joins a list row to its document through the `url` of the document's
`court/raw/index.jsonl` row). So `bundle.py remove` keeps that row: before running it, delete by hand the
`court/list.jsonl` line whose `url` equals the `url` of the document's index row (the remove then rebuilds the
manifest over the edited file), in every copy and in the working data. The working data under `AML_DATA` / `AML_TRAIN` needs the same
removal by hand (the label file, the raw text, a rebuild of the rows), and so does a working `AML_EVAL` copy.

`bundle.py remove` also drops the document's rows from the evaluator's `aml-eval/dev.jsonl` / `train.jsonl` /
`evids-dev.jsonl` (by `doc`) and `sg-<set>-r17.tsv` (first column). It does **not** touch two things, which the
owner decides by hand: the **models** (a model trained with the document has learned from it; removing it there means
retraining without it and replacing the model folder in every copy), and the **guideline and prompts** in an original
(`labels/tools/`: `remove` counts them under "files still naming the id"; search them for the id and for quotes of the
document and edit them by hand).

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
replayed by (document, person id), so StageEval's LLM column is the cached reading of the original text; the
evaluator documents keep the token ids of the original text, so `Aml.NewsEval`'s "token ids identical" count falls there;
the models are the originals' (trained on real names), and the guideline and prompts are absent.

## Not in the bundle

- Model weights other than the two `MODELS` folders (`*.safetensors`, `*.onnx`, `*.bin`, `*.gguf`, `*.pt` anywhere
  else), the bert-base-chinese weights ([base model](#base-model)), caches (`measurements/*/cache*`, the news pair's
  `cache/` and `cache-dev2/`), classifier predictions (`downloads/aml*/out/pred-*`), older data rounds (`data-r4` ...
  `data-r10`, `data-v*`), snapshots other than NER20's rows, the rest of `labels/tools/` (the crawler, logs, one-off
  scripts, older guideline versions), original HTML / PDF and full `.txt` extractions next to the `.main.txt` /
  `.para.txt` the tools read, backups of superseded data (any path containing `.bak-`).
- Everything else in `AML_EVAL`: the evaluator's result dumps (`e2e-*.tsv`, `*.err.json` ...), its scratch scripts and
  caches; only `dev.jsonl`, `train.jsonl`, `evids-dev.jsonl` (when present) and `sg-<set>-r17.tsv` travel.
- `AML_JUDGMENTS` (`jids.jsonl`: `tools/judgments/jids.py` rebuilds it from the bundle; fetched judgments are not kept).
- Models never enter git.
