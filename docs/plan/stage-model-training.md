# Stage model training and measurement

New to the model work: read [model training: start here](model-training.md) first (what a model result can change, data handling, where jobs run, licences, open items).

How the two optional stage readers (the person-stage classifier and the large language model) are built and measured, and how a change to them or to the stage rules is judged. The product behaviour is specified in [three readers](../feature/watchlist-and-adverse-information.md#three-readers); this document owns the measurements: data, tools, decisions in force and the evidence behind them. The tools are in `tools/` (the agent skills `measure-stage`, `prune-rules`, `gemma-stage`, `label-data` and `colab-job` in `.claude/skills/` walk through them); data, labels, models and results stay outside the repository, found through the `AML_*` environment variables and the tools' arguments, and nothing here is needed to run the product or its tests.

## What is judged

The legal stage is advisory to the reviewer, and only a unanimous vote of the rules, the classifier and the language model gives it (`StageVote.Decide`). A change is therefore judged by two numbers per measured set, both taken through the system's own entry (`SourceConditions.Determine` on an adverse-media Profile with the news attached):

- **precision of the automatic stage**: of the persons the three readers agree on, how many get the reference stage;
- **coverage**: how many persons get an automatic stage at all (the rest go to a reviewer).

A change to a reader or to the vote is adopted only if, on each of the six development sets, the precision is not below the current one (the counts under decisions below) and coverage rises on the sets it is meant for; the held-back sets are then computed once as a check. Removing a stage rule follows the same measure: it may go when no set's precision falls. Each reader's own count (rules, classifier, language model alone) is a diagnostic, not the criterion. Rules are kept only where they are needed for this and cheap to keep ([why the rules are few](../feature/watchlist-and-adverse-information.md#stage-from-attached-news)).

## Measured sets and their boundaries

| Set | Kind | Use |
|---|---|---|
| dev (185 persons), dev2 (126), v9 (390) | reports, press releases, judgments | development: may be looked at |
| accept (114), accept3 (172), accept4 (257) | court press releases | development: may be looked at |
| v11 (362), accept5 (148) | reports; court press releases | held back: aggregate counts only, computed once at the end of a change, never used for a choice; texts and per-person rows never opened |

Train data never shares a document id or a text with any of the eight (each builder asserts it). Reference stages come from the annotated events (`gold.py`, the evaluator's derivation); court sets from two blind labelling passes, keeping the persons both give the same stage (`tools/labels/merge.py`, which refuses a pass with format errors). A recheck of accept3's hardest 27 persons by two further blind passes confirmed 26 and split on 1, so the remaining misses there are the readers', not the labels'.

## Where things live

Tools are in the repository; data, models and results are outside it. `AML_DATA` and `AML_TRAIN` are the two roots (`tools/stage-model/paths.py` holds their defaults); the evaluator's built-in defaults (`tools/Aml.StageEval/Program.cs`) are one machine's, so on another host pass `sets=`, `onnx=` and `out=`.

| Responsibility | Location |
|---|---|
| System-level evaluator | `tools/Aml.StageEval` (`dotnet run -c Release --project tools/Aml.StageEval -- <report answers> <court answers> [--heldback] [onnx=<dir>] [out=<dir>] [judgments=<dir>] [sets=<dir>]`): runs the real ONNX classifier and replays the language model's answers through `SourceConditions.Determine`, prints the paths it uses, then per set RULES, CLASSIF, LLM, VOTE, SPLIT, DECIDED and RIGHT (the unanimous persons and how many of them are right); per-person stage rows in `<out>/<set>.tsv` for development sets only. Measured sets: `AML_TRAIN/measurements/` (`report/`, `court/`) |
| Classifier rows, trainer, scorers, ONNX export | `tools/stage-model/` (Python, pinned `requirements.txt`; `prep_stage.py` builds the rows, `add17.py` adds the v17 court rows, `train_stage.py --save`, `export_onnx.py`, `score*.py`; `selfcheck.py` runs without data); outputs under `AML_STAGE_WORK` (default `AML_TRAIN/stage-clf/`) |
| Deployed classifier | `AML_TRAIN/downloads/aml47/onnx/` (`model.onnx`, `vocab.txt`, `labels.json`): the folder `StageModels:ClassifierDirectory` points at, and `onnx=` of the evaluator |
| Language model jobs (inference, fine-tune data, QLoRA fine-tune) | `tools/llm-stage/` (`build_job.py infer\|finetune` and `check`, `sft.py`; the job's prompt is copied from `Infra/Models/stage-prompt.txt`; skill `gemma-stage`) |
| Language model answers replayed in measurement | `AML_TRAIN/downloads/answers-20261123/gemma4_12b-it-q4_K_M/` (Gemma 4 12B q4_K_M, one person per call, the runtime prompt; job aml49's answers, accept's from job56) |
| Rule pruning | `tools/stage-rules/prune.py`: copies the checkout, switches off one rule at a time, builds and runs `Aml.StageEval` on the development sets and prints per rule the DECIDED/RIGHT deltas and whether it may go (skill `prune-rules`) |
| Labelling procedure | `tools/labels/` (the blind-pass brief, `choose.py`, `merge.py`, `recheck.py`; skill `label-data`); labels in `AML_DATA/labels/` (court releases `v12`–`v17`, recheck `v14r`) |
| Moving the data to another machine | `tools/data/` (`bundle.py build\|verify\|remove`, `pseudonymise.py`; skill `data-bundle`): copies everything the tools read, with the baseline classifier and the retained news pair but no base-model weights, into one root with the same layout and a `MANIFEST.json` (role train, dev or heldback, SHA-256, counts per set), and a pseudonymised copy; both are personal data and travel only encrypted |
| Judgment lookup (measured, not adopted) | `tools/judgments/` (`jids.py`; `fetch.py` is run by the account holder, who types the password) |
| Colab jobs | `tools/colab/` (`colabjob.py package` / `check`, `run_job.sh`); job folders and packages in `AML_COLAB_HOME`, downloads in `AML_TRAIN/downloads/aml<N>/` |

## Running a job on Colab

Jobs run through `tools/colab` ([README](../../tools/colab/README.md): prerequisites, job contract, runner, exit codes); the session rules are in [where it runs](model-training.md#where-it-runs). The classifier runs on a T4 (job folder: [tools/stage-model](../../tools/stage-model/README.md#train-and-export-on-colab)); Gemma 4 runs on an L4 (24 GB, bf16, about twice the T4's speed for the 12B model; jobs built by `tools/llm-stage/build_job.py`, its known limits for fine-tuning in [tools/llm-stage](../../tools/llm-stage/README.md#known-limits)).

A job's data must be byte-identical to the measured rows and share no document with an evaluation set (`build_job.py check` asserts it for the language-model jobs; `prep_stage.py` and `add17.py` for the classifier rows). Derive a new job from an old one by deliberate, diffed edits, never by replacing numbers wholesale.

## Decisions in force and their evidence

The counts below are what `tools/Aml.StageEval` prints with the deployed classifier (aml47), the replayed answers named above and the rules in force (`stage-glossary-6`). They are the baseline a change is measured against; a change that moves them updates this table in the same commit.

| Set | dev | dev2 | v9 | accept | accept3 | accept4 | v11 (held back) | accept5 (held back) |
|---|---|---|---|---|---|---|---|---|
| Unanimous: right / decided | 142/142 | 98/100 | 255/259 | 85/85 | 87/90 | 193/193 | 220/223 | 111/113 |
| Coverage (decided / persons) | 77 % | 79 % | 66 % | 75 % | 52 % | 75 % | 62 % | 76 % |

| Question | Decision | Evidence (through `SourceConditions.Determine`) |
|---|---|---|
| Which vote decides | only all three alike; otherwise UNKNOWN for a reviewer | all three alike are right for 0.967 or more of the persons they decide on every set (table above); two alike were right for only 68–95 % (measured with accept at its earlier 93 persons). Combining readers gains most on text the rules were not written against and costs a little on fixed-form court releases, where the rules are near exact and the models are what is outvoted: under the earlier two-of-three vote with the fuller rules, the vote gave the reference stage to 335 persons on v11 against the rules' 295, and to 137 on accept5 against the rules' 140 (dev2 119 against 122) |
| Stage rules | only those the vote needs; the report forms of `stage-glossary-6` (a clause ending 宣判 read with the next, a clause opening 同案 that does not name the person not theirs, 提起公訴 in a text that never names the person theirs) | `tools/stage-rules/prune.py` under the unanimous vote: removing any kept rule lowers a development set's precision, or (the subjectless step) keeps it only by deciding 73 fewer persons on v9; removing the wanted-notice rule lowered none and decided more (dev +1, v9 +4), so it is deleted. The fuller rule set was reduced under the earlier two-of-three vote: of 42 rule units 34 went while that vote stayed above 0.90 on every set (accept3 not below its own baseline). The `stage-glossary-6` report forms were found unread by the [synthetic set](poc-delivery-plan.md#synthetic-end-to-end-set) and move no person on the six development sets: kept for the invented texts they read, not for a measured gain; held-back sets not measured for them |
| The accept set | 114 persons in 40 releases | both blind passes agree on all 114 (a format error in one pass had first left 21 out of the merge); the deployed model's answers for all 114 are job56's |
| Which classifier | aml47 (bert-base-chinese, job47) | more court-release rows (v17, 249 or 209 rows) raised the classifier alone on accept3 (112 → 128 / 136) but lowered accept4 (230 → 216 / 218) and the vote: not adopted |
| Which language model and prompt | Gemma 4 12B q4_K_M, one Profile person per call, with the measured prompt (`Infra/Models/stage-prompt.txt`) | the 26B mixture-of-experts model alone: accept3 152, the same as the 12B; a prompt with the label brief's INDICTED and UNKNOWN definitions: dev2 110 → 100, v9 355 → 341; neither adopted |
| Fine-tuning the language model | not adopted (E4B, 12B) | QLoRA on the classifier's 2,795 train rows, measured as Q8_0. E4B alone close to the 12B (dev 179, v9 358, accept3 150), but its errors join the classifier's, and unanimous precision falls on dev2 (0.962), v9 (0.970), accept (0.958), accept4 (0.990). The fine-tuned 12B (job55) alone 180, 108, 360, 88, 146, 244; unanimous right/decided 158/158, 100/104, 250/254, 70/73, 88/91, 193/194: more coverage on dev, but precision falls on dev2 (0.962), accept (0.959) and accept4 (0.995) and v9 is just below (250/254 against 251/255 of the baseline it was measured against): the same joining of errors |
| Judgments behind court releases | not used for the stage | the Judicial Yuan judgment API (account, 00:00–06:00 only, judgment id from court, case number and date) found 56 of 110 releases; 45 of those judgments mask names as surname + ○○, so 92 of 191 persons cannot be found in them; judgment-first precision 0.81–0.87 |

Each reader alone (a diagnostic, persons given the reference stage; measured with the wanted-notice rule still in place, not re-measured since; accept is left out because it was measured at an earlier 93 persons, not the 114 of the baseline):

| Set | Persons | Rules | Classifier | Gemma 4 12B |
|---|---|---|---|---|
| dev | 185 | 171 | 171 | 163 |
| dev2 | 126 | 120 | 106 | 115 |
| v9 | 390 | 331 | 300 | 364 |
| accept3 | 172 | 129 | 116 | 154 |
| accept4 | 257 | 212 | 230 | 251 |

No reader is best on every set, and none reaches 0.90 alone on accept3. The rules alone are weakest on court releases, which is why a deployment without both models reads the stage less well.

The general finding: the readers fail where the court-release form is hard (a defendant without a judgment, a person only said to take part, tables of masked names), and making two readers learn from the same labels makes them fail together. What keeps the automatic stage precise is the independence of the three readers, so a change that raises one reader by training it on the others' labels has to be measured on the unanimous precision, not on its own count.

### Persons who are not suspects

The measured sets hold suspects only. On the three report development sets the labels also mark 924 persons as not suspects (officials, victims, witnesses, others); the right answer for them is no stage. Measured with the deployed readers (classifier aml47, which never saw a non-suspect in training; Gemma 4 12B with the deployed prompt):

- the rules alone give a stage to 358 (dev 137 of 515, dev2 89 of 105, v9 132 of 304);
- the classifier gives a stage to 670;
- rules and classifier give the same stage to 182; asked for those, the language model answers UNKNOWN for 160;
- all three agree on a wrong stage for 20 (dev 1, dev2 17, v9 2).

All 20 are ALLEGED, none INDICTED or later; by role Official 18, Other 2, Victim 0 of 175, Witness 0 of 20. Two blind label rechecks confirmed all 20 as non-suspects, mostly officials handling the case. So without models about 4 in 10 non-suspects get a stage; with the unanimous vote about 2 in 100, ALLEGED only. The court release sets carry no labels for persons other than defendants and are not measured.

Training the classifier on these persons was measured and not adopted. aml58 (`prep_stage.py --nonsuspects`, one non-suspect row per suspect row, 5,172 train rows) and aml59 (`--nonsuspects --officials 1500`, 6,717 rows) were measured with the same replayed answers (held-back sets not run). Unanimous right/decided:

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
