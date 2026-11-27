# Stage model training and measurement

How the two optional stage readers (the person-stage classifier and the large language model) are built and measured, and how a change to them or to the stage rules is judged. The product behaviour is specified in [three readers](../feature/watchlist-and-adverse-information.md#three-readers); this document is the working side: data, tools, decisions in force and the evidence behind them. The tools are in `tools/` (the agent skills `measure-stage` and `colab-job` in `.claude/skills/` walk through them); data, labels, models and results stay outside the repository (`C:\tmp\…`, set by arguments or environment variables), and nothing here is needed to run the product or its tests.

## What is judged

The legal stage is advisory to the reviewer, and only a unanimous vote of the rules, the classifier and the language model gives it (`StageVote.Decide`). A change is therefore judged by two numbers per measured set, both taken through the system's own entry (`SourceConditions.Determine` on an adverse-media Profile with the news attached):

- **precision of the automatic stage**: of the persons the three readers agree on, how many get the reference stage;
- **coverage**: how many persons get an automatic stage at all (the rest go to a reviewer).

A change to a reader or to the vote is adopted only if, on each of the six development sets, the precision is not below the current one (the counts under decisions below) and coverage rises on the sets it is meant for; the held-back sets are then computed once as a check. Removing a stage rule follows the same measure: it may go when no set's precision falls. Each reader's own count (rules, classifier, language model alone) and the old two-of-three vote are diagnostics, not the criterion. Rules are kept only where they are needed for this and cheap to keep ([why the rules are few](../feature/watchlist-and-adverse-information.md#stage-from-attached-news)).

## Measured sets and their boundaries

| Set | Kind | Use |
|---|---|---|
| dev (185 persons), dev2 (126), v9 (390) | reports, press releases, judgments | development: may be looked at |
| accept (114), accept3 (172), accept4 (257) | court press releases | development: may be looked at |
| v11 (362), accept5 (148) | reports; court press releases | held back: aggregate counts only, computed once at the end of a change, never used for a choice; texts and per-person rows never opened |

Train data never shares a document id or a text with any of the eight (each builder asserts it). Reference stages come from the annotated events (`gold.py`, the evaluator's derivation); court sets from two blind labelling passes, keeping the persons both give the same stage. A recheck of accept3's hardest 27 persons by two further blind passes confirmed 26 and split on 1, so the remaining misses there are the readers', not the labels'.

The rules alone, as each court batch was first measured: on the 40 releases first labelled they gave the stage of 88 of the 90 suspects against 12 before, and on batches of 40 releases the rules were not written against when measured, 108 of 172 persons (accept3), 185 of 257 (accept4) and 140 of 148 (accept5, 0.946). accept3 and accept4 then became development sets for the next round; the rules alone now give 145 and 248 on them.

## Where things live

Tools are in the repository; data, models and results are outside it.

| Responsibility | Location |
|---|---|
| System-level evaluator | `tools/Aml.StageEval` (`dotnet run -c Release --project tools/Aml.StageEval -- <report answers> <court answers> [--heldback] [onnx=<dir>] [out=<dir>] [judgments=<dir>] [sets=<dir>]`): runs the real ONNX classifier and replays the language model's answers through `SourceConditions.Determine`, prints the paths it uses, then per set RULES, CLASSIF, LLM, VOTE, SPLIT, DECIDED and RIGHT (the unanimous persons and how many of them are right); per-person stage rows in `<out>\<set>.tsv` for development sets only. Defaults: sets `C:\tmp\aml-train\measurements\`, classifier aml47, out `C:\tmp\eval-sys\out\` |
| Classifier rows, trainer, scorers, ONNX export | `tools/stage-model/` (Python, pinned `requirements.txt`; data locations from `AML_DATA` / `AML_TRAIN` in `paths.py`; `prep_stage.py` builds the rows, `add17.py` adds the v17 court rows, `train_stage.py --save`, `export_onnx.py`, `score*.py`; `selfcheck.py` runs without data); its outputs under `C:\tmp\aml-train\stage-clf\` |
| Deployed classifier | `C:\tmp\aml-train\downloads\aml47\onnx\` (`model.onnx`, `vocab.txt`, `labels.json`): the folder `StageModels:ClassifierDirectory` points at |
| Language model jobs (inference, fine-tune data, QLoRA fine-tune) | `tools/llm-stage/` (`build_job.py infer\|finetune` and `check`, `sft.py`; the job's prompt is copied from `Infra/Models/stage-prompt.txt`; skill `gemma-stage`) |
| Rule pruning | `tools/stage-rules/prune.py`: copies the checkout, switches off one rule at a time, builds and runs `Aml.StageEval` on the development sets and prints per rule the DECIDED/RIGHT deltas and whether it may go (skill `prune-rules`) |
| Labelling procedure | `tools/labels/` (the blind-pass brief, `choose.py`, `merge.py`, `recheck.py`; skill `label-data`) |
| Moving the data to another machine | `tools/data/` (`bundle.py build\|verify\|remove`, `pseudonymise.py`; skill `data-bundle`): copies everything the tools read except weights into one root with the same layout and a `MANIFEST.json` (role train, dev or heldback, SHA-256, counts per set), and a pseudonymised copy; both are personal data and travel only encrypted |
| Judgment lookup (measured, not adopted) | `tools/judgments/` (`jids.py`; `fetch.py` is run by the account holder, who types the password) |
| Language model answers replayed in measurement | `C:\tmp\aml-train\downloads\answers-20261123\gemma4_12b-it-q4_K_M\` (aml49's answers, accept from job56); before, `C:\tmp\aml-train\downloads\aml49\out\gemma4_12b-it-q4_K_M\` (Gemma 4 12B q4_K_M, one person per call, the runtime prompt) |
| Colab jobs | `tools/colab/` (`colabjob.py package` / `check`, `run_job.sh`); job folders and packages in `C:\tmp\colab\home\` (`AML_COLAB_HOME`), downloads in `C:\tmp\aml-train\downloads\aml<N>\`. Jobs up to 55 were run with per-job copies (`C:\tmp\colab\run<N>.sh`) |
| Labels and the working log of every round | `C:\tmp\aml-data\labels\` (court releases `v12`–`v17`, recheck `v14r`), log `C:\tmp\aml-data\labels\v6\progress.md` |

## Running a job on Colab

The `colab` CLI runs in the Docker container `colab` (see [news model training](news-model-training.md#colab-is-temporary-execution-not-durable-storage) for the mounts and the keyring rule). Small models (the classifier, the news models) run on a T4; Gemma 4 runs on an L4 (`--gpu L4`: 24 GB, bf16, about twice the T4's speed for the 12B model).

- A job is a folder with a `run.sh` that writes `log.txt`, `fail.txt` or `done.txt` and its results in `out/` ([contract](../../tools/colab/README.md)). Its data must be byte-identical to the measured rows and share no document with an evaluation set; check that before packaging. Derive a new job from an old one by deliberate, diffed edits, never by replacing numbers wholesale.
- `colabjob.py package` builds the archive, its parts and the start, peek and pack scripts; `check` must print ALL OK (archive SHA equals the start script's and the joined parts', `run.sh` LF-only and valid bash). `run_job.sh <name> <T4|L4> <download dir>` opens the session, uploads, starts, watches, packs, downloads with SHA checks and stops the session; every Colab call runs under `timeout`, and any abnormal state (fail file, no progress for 45 minutes, a start that hangs) stops the session. `--dry-run` prints the sequence without Colab. After every run confirm by hand that `colab sessions` shows none.
- A start exec can hang without starting the job: the runner then stops the session; start it again.
- The Unsloth recipe for Gemma 4 pins `transformers==5.5.0`, which loads E4B but not 12B; the 12B job installs the newest release. Unsloth exports Gemma 4 GGUF only as Q8_0, BF16 or F16, so a fine-tuned model is measured as Q8_0 in Ollama (a q4 deployment needs its own measure). Training examples reach 15,076 tokens, so `MAXLEN` is 16,384; the job checks on 20 examples that only the answer is trained on before it trains.

## Decisions in force and their evidence

Each run's counts and its job, download folder and settings are recorded in the working log (`C:\tmp\aml-data\labels\v6\progress.md`); the per-person stage rows of the development sets are under `C:\tmp\eval-sys\out*\`. The counts below are what `tools/Aml.StageEval` prints with the defaults and the answers named above.

| Question | Decision | Evidence (through `SourceConditions.Determine`) |
|---|---|---|
| Which vote decides | only all three alike; otherwise UNKNOWN for a reviewer | all three alike right 142/142, 98/100, 255/259, 85/85, 87/90, 193/193; held-back 220/223, 111/113 (rules as of 2026-11-23, answers `downloads\answers-20261123\`: aml49's plus job56's for accept); at the first measure, with accept at 93 persons, 141/141, 98/100, 251/255, 69/69, 87/90, 193/193 and 219/222, 111/113; two alike right only 68–95 % |
| Stage rules | the wanted-notice rule deleted (2026-11-23), the rest kept; three report forms added (2026-11-26, `stage-glossary-6`): a clause ending 宣判 read with the next, a clause opening 同案 not naming the person no longer theirs, 提起公訴 in a text that never names the person theirs | the three forms, found unread by the [synthetic set](poc-delivery-plan.md#synthetic-end-to-end-set): on the six development sets these four readings (宣判 join, 同案 clause, 同案 to the next naming clause, 提起公訴 in an unnamed text) moved no person, so the vote is unchanged (142/142, 98/100, 255/259, 85/85, 87/90, 193/193; coverage unchanged too): no measured gain, and they rest on forms in invented texts; held-back sets not measured for this change. The wanted-notice deletion: `tools/stage-rules/prune.py` under the unanimous vote: without it no development set's precision falls and dev decides one person more, v9 four (142/142, 255/259); accept unchanged at 85/85; held-back 220/223 against 219/222, 111/113 unchanged. The other rules lower a set's precision when removed, or (the subjectless step) keep it only by deciding 73 fewer persons on v9 |
| The accept set | 114 persons in 40 releases | one labelling pass of round v13 wrote 21 persons' forms as a string, so the agreement merge dropped them; written as lists, both passes agree on all 21, and the set was rebuilt (`tools/labels/merge.py`; the 93 earlier persons unchanged). The deployed model's answers for accept were asked again (job56, all 114): 92 of the 93 earlier answers came back the same |
| Which classifier | aml47 (bert-base-chinese, job47) | more court-release rows (v17, 249 or 209 rows) raised the classifier on accept3 (112 → 128 / 136) but lowered accept4 (230 → 216 / 218) and the vote: not adopted |
| Which language model and prompt | Gemma 4 12B q4_K_M with the measured prompt (`Infra/Models/stage-prompt.txt`) | the 26B mixture-of-experts model alone: accept3 152, the same as the 12B asked one person per call; a prompt with the label brief's INDICTED and UNKNOWN definitions: dev2 110 → 100, v9 355 → 341; neither adopted |
| Fine-tuning the language model | not adopted (E4B, 12B) | QLoRA on the classifier's 2,795 train rows, measured as Q8_0. E4B alone close to the 12B (dev 179, v9 358, accept3 150), but its errors join the classifier's, and unanimous precision falls on dev2 (0.962), v9 (0.970), accept (0.958), accept4 (0.990). The fine-tuned 12B (job55) alone 180, 108, 360, 88, 146, 244; unanimous right/decided 158/158, 100/104, 250/254, 70/73, 88/91, 193/194: more coverage on dev, but precision falls on dev2 (0.962), accept (0.959) and accept4 (0.995) and v9 is just below (250/254 against 251/255): the same joining of errors |
| Judgments behind court releases | not used for the stage | the Judicial Yuan judgment API (account, 00:00–06:00 only, judgment id from court, case number and date) found 56 of 110 releases; 45 of those judgments mask names as surname + ○○, so 92 of 191 persons cannot be found in them; judgment-first precision 0.81–0.87 |

The general finding: the readers fail where the court-release form is hard (a defendant without a judgment, a person only said to take part, tables of masked names), and making two readers learn from the same labels makes them fail together. What keeps the automatic stage precise is the independence of the three readers, so a change that raises one reader by training it on the others' labels has to be measured on the unanimous precision, not on its own count.

### Persons who are not suspects

The measured sets hold suspects only. On the three report development sets the labels also mark 924 persons as not suspects (officials, victims, witnesses, others); the right answer for them is no stage. Measured 2026-11-23 with the deployed readers (classifier aml47, which never saw a non-suspect in training; Gemma 4 12B with the deployed prompt):

- the rules alone give a stage to 358 (dev 137 of 515, dev2 89 of 105, v9 132 of 304);
- the classifier gives a stage to 670;
- rules and classifier give the same stage to 182; asked for those, the language model answers UNKNOWN for 160;
- all three agree on a wrong stage for 20 (dev 1, dev2 17, v9 2).

All 20 are ALLEGED, none INDICTED or later; by role Official 18, Other 2, Victim 0 of 175, Witness 0 of 20. Two blind label rechecks confirmed all 20 as non-suspects, mostly officials handling the case. So without models about 4 in 10 non-suspects get a stage; with the unanimous vote about 2 in 100, ALLEGED only. The court release sets carry no labels for persons other than defendants and are not measured.

Training the classifier on these persons was measured and not adopted; the classifier stays aml47. job58 (`--nonsuspects`, one non-suspect row per suspect row, 5,172 train rows) and job59 (`--nonsuspects --officials 1500`, 6,717 rows) were trained on a T4 and measured through `tools/Aml.StageEval` with the same replayed Gemma 4 12B answers as the baselines (held-back sets not run). Unanimous right/decided:

| Set | aml47 | aml58 | aml59 |
|---|---|---|---|
| dev | 142/142 | 146/146 | 146/146 |
| dev2 | 98/100 | 101/103 | 99/101 |
| v9 | 255/259 | 243/247 | 231/235 |
| accept | 85/85 | 84/84 | 82/82 |
| accept3 | 87/90 | 88/94 | 78/84 |
| accept4 | 193/193 | 193/194 | 193/194 |
| non-suspects (924): classifier gives a stage | 670 | 114 | 17 |
| non-suspects: unanimous wrong stage | 20 | 15 (lower bound: one agreement had no replayed answer) | 1 |

The rows remove the classifier's stages for non-suspects, but unanimous precision falls on v9, accept3 (wrong 3 → 6) and accept4 (0 → 1) for both, and aml59 also decides fewer persons (v9 −24, accept3 −6). A wrong ALLEGED for a non-suspect changes no condition status, matrix result or review priority (an adverse-media condition is HIT whatever its stage, [feature doc](../feature/watchlist-and-adverse-information.md#stage-from-attached-news)), while a wrong suspect stage is the output a reviewer reads, so the trade is not worth it and the same recipe is not to be retried. It would be worth measuring again only if a non-suspect's stage came to change a Case output, such as a stage weight in an approved `RiskPolicy`.

To repeat: a measurement root in the set format (`report/<set>-stage.jsonl`) holding the non-suspect persons with gold no stage (UNKNOWN), the language model's answers for them, then `tools/Aml.StageEval` with `sets=<that root>` (a set without a file is skipped). A wrong automatic stage is DECIDED − RIGHT.
