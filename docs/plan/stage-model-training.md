# Stage model training and measurement

How the two optional stage readers (the person-stage classifier and the large language model) are built and measured, and how a change to them or to the stage rules is judged. The product behaviour is specified in [three readers](../feature/watchlist-and-adverse-information.md#three-readers); this document is the working side: data, tools, decisions in force and the evidence behind them. Data, models and tools live outside the repository (`C:\tmp\…`); nothing here is needed to build or test the solution.

## What is judged

The legal stage is advisory to the reviewer, and only a unanimous vote of the rules, the classifier and the language model gives it (`StageVote.Decide`). A change is therefore judged by two numbers per measured set, both taken through the system's own entry (`SourceConditions.Determine` on an adverse-media Profile with the news attached):

- **precision of the automatic stage**: of the persons the three readers agree on, how many get the reference stage;
- **coverage**: how many persons get an automatic stage at all (the rest go to a reviewer).

A change to a reader or to the vote is adopted only if, on each of the six development sets, the precision is not below the current one (the counts under decisions below) and coverage rises on the sets it is meant for; the held-back sets are then computed once as a check. Removing a stage rule follows the same measure: it may go when no set's precision falls. Each reader's own count (rules, classifier, language model alone) and the old two-of-three vote are diagnostics, not the criterion. Rules are kept only where they are needed for this and cheap to keep ([why the rules are few](../feature/watchlist-and-adverse-information.md#stage-from-attached-news)).

## Measured sets and their boundaries

| Set | Kind | Use |
|---|---|---|
| dev (185 persons), dev2 (126), v9 (390) | reports, press releases, judgments | development: may be looked at |
| accept (93), accept3 (172), accept4 (257) | court press releases | development: may be looked at |
| v11 (362), accept5 (148) | reports; court press releases | held back: aggregate counts only, computed once at the end of a change, never used for a choice; texts and per-person rows never opened |

Train data never shares a document id or a text with any of the eight (each builder asserts it). Reference stages come from the annotated events (`gold.py`, the evaluator's derivation); court sets from two blind labelling passes, keeping the persons both give the same stage. A recheck of accept3's hardest 27 persons by two further blind passes confirmed 26 and split on 1, so the remaining misses there are the readers', not the labels'.

## Where things live

All paths are outside the repository.

| Responsibility | Location |
|---|---|
| Classifier rows, trainer, scorers, ONNX export | `C:\tmp\aml-train\stage-clf\` (`prep_stage.py` builds `data\`; `add17.py` adds the v17 court rows to `data17\` / `data17v\`; `train_stage.py --save`, `export_onnx.py`, `score*.py`) |
| Deployed classifier | `C:\tmp\aml-train\downloads\aml47\onnx\` (`model.onnx`, `vocab.txt`, `labels.json`): the folder `StageModels:ClassifierDirectory` points at |
| Language model answers replayed in measurement | `C:\tmp\aml-train\downloads\aml49\out\gemma4_12b-it-q4_K_M\` (Gemma 4 12B q4_K_M, one person per call, the runtime prompt) |
| System-level evaluator | `C:\tmp\eval-sys\` (`run.sh <report answers> <court answers> [--heldback] [onnx=<dir>] [out=<dir>] [judgments=<dir>]`): builds against this checkout, runs the real ONNX classifier and replays the language model's answers, prints per set RULES, CLASSIF, LLM, VOTE, SPLIT, DECIDED and RIGHT (the unanimous persons and how many of them are right); per-person stage rows in `out\<set>.tsv` for development sets only |
| Rule switch-off copy (which rules the vote needs) | `C:\tmp\exp-prune\` (`drive_court.py`, `score.py`; environment `SCORE_GEMMA_REPORT` / `SCORE_GEMMA_COURT`) |
| Colab jobs | `C:\tmp\colab\run<N>.sh` and `C:\tmp\colab\home\job<N>\` with `mk<N>.py`, `chk<N>.py`, `start`, `peek`, `pack` scripts; downloads in `C:\tmp\aml-train\downloads\aml<N>\` |
| Labels and the working log of every round | `C:\tmp\aml-data\labels\` (court releases `v12`–`v17`, recheck `v14r`), log `C:\tmp\aml-data\labels\v6\progress.md` |

## Running a job on Colab

The `colab` CLI runs in the Docker container `colab` (see [news model training](news-model-training.md#colab-is-temporary-execution-not-durable-storage) for the mounts and the keyring rule). Small models (the classifier, the news models) run on a T4; Gemma 4 runs on an L4 (`--gpu L4`: 24 GB, bf16, about twice the T4's speed for the 12B model).

- Build a job with its `mk<N>.py` and check it with `chk<N>.py` until it prints ALL OK: archive SHA equals the start script's, members and data byte-identical to the measured ones, no train document in any evaluation set. Derive a new job from an old one by deliberate, diffed edits, never by replacing numbers wholesale.
- `run<N>.sh` opens the session, uploads, starts, watches (`peek`), packs, downloads with SHA checks and stops the session; every Colab call runs under `timeout`, and any abnormal state (fail file, no progress for 45 minutes, a start that hangs) stops the session. After every run confirm by hand that `colab sessions` shows none.
- A start exec can hang without starting the job: the runner then stops the session; start it again.
- The Unsloth recipe for Gemma 4 pins `transformers==5.5.0`, which loads E4B but not 12B; the 12B job installs the newest release. Unsloth exports Gemma 4 GGUF only as Q8_0, BF16 or F16, so a fine-tuned model is measured as Q8_0 in Ollama (a q4 deployment needs its own measure). Training examples reach 15,076 tokens, so `MAXLEN` is 16,384; the job checks on 20 examples that only the answer is trained on before it trains.

## Decisions in force and their evidence

Each run's counts and its job, download folder and settings are recorded in the working log (`C:\tmp\aml-data\labels\v6\progress.md`); the per-person stage rows of the development sets are under `C:\tmp\eval-sys\out*\`.

| Question | Decision | Evidence (through `SourceConditions.Determine`) |
|---|---|---|
| Which vote decides | only all three alike; otherwise UNKNOWN for a reviewer | all three alike right 141/141, 98/100, 251/255, 69/69, 87/90, 193/193; held-back 219/222, 111/113; two alike right only 68–95 % |
| Which classifier | aml47 (bert-base-chinese, job47) | more court-release rows (v17, 249 or 209 rows) raised the classifier on accept3 (112 → 128 / 136) but lowered accept4 (230 → 216 / 218) and the vote: not adopted |
| Which language model and prompt | Gemma 4 12B q4_K_M with the measured prompt (`Infra/Models/stage-prompt.txt`) | the 26B mixture-of-experts model alone: accept3 152, the same as the 12B asked one person per call; a prompt with the label brief's INDICTED and UNKNOWN definitions: dev2 110 → 100, v9 355 → 341; neither adopted |
| Fine-tuning the language model | not adopted for E4B | E4B with QLoRA on the classifier's 2,795 train rows: alone close to the 12B (dev 179, v9 358, accept3 150), but its errors join the classifier's, and unanimous precision falls on dev2 (0.962), v9 (0.970), accept (0.958), accept4 (0.990) |
| Judgments behind court releases | not used for the stage | the Judicial Yuan judgment API (account, 00:00–06:00 only, judgment id from court, case number and date) found 56 of 110 releases; 45 of those judgments mask names as surname + ○○, so 92 of 191 persons cannot be found in them; judgment-first precision 0.81–0.87 |

The general finding: the readers fail where the court-release form is hard (a defendant without a judgment, a person only said to take part, tables of masked names), and making two readers learn from the same labels makes them fail together. What keeps the automatic stage precise is the independence of the three readers, so a change that raises one reader by training it on the others' labels has to be measured on the unanimous precision, not on its own count.
