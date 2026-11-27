# Model training work items

What the owner of model training must still do for the models to reach the goals in force. Read [model training: start here](model-training.md) first; the figures belong to the documents linked here, and an item that is done is deleted from this page in the same change that updates its owning document.

## Goals in force

| Goal | In force | Measured now | Gap |
|---|---|---|---|
| **G1 stage** | Only a unanimous vote of rules, classifier and language model gives a stage. A change is adopted when no development set's precision falls and coverage rises on the sets it targets; rules are cut as far as that allows ([what is judged](stage-model-training.md#what-is-judged)) | unanimous precision 0.967–1.000 and coverage 52–79 % on the six development sets ([baseline](stage-model-training.md#decisions-in-force-and-their-evidence)); every kept rule is needed by that measure | coverage, lowest on accept3 (52 %) and v9 (66 %), at no loss of precision. No coverage target is set |
| **G2 news** | End-to-end F1 strictly above 0.90 on both development sets for one NER/event pair, measured as a KYC reviewer acts (person involved, outcome steps, case-level steps), then confirmed once on the held-back sets ([acceptance](news-model-training.md#acceptance-has-three-gates)) | NER20 / event43, working measure: 0.884 dev, 0.894 second dev; event gate 0.797; NER gate passes ([figures](news-model-training.md#current-candidate-choice)) | +0.016 and +0.006 on the working measure; the KYC-aligned form is not written down as a gate (WI-06) |
| **G3 Case value** | A model result matters only when it gives a deterministic rule a fact that can support MATCH, NO_MATCH or NOT_HIT and has a counterpart on the customer record; a wrong conclusion costs far more than a REVIEW ([what evidence is worth reading](../feature/system-overview.md#what-evidence-is-worth-reading)) | no rule weighs a model result: a page person's NOT_HIT reads rule-read role words only, and the news models change no decision on the received-page set ([received URL](poc-delivery-plan.md#received-url-matched-against-the-customer)); no real labelled Case exists ([real material](poc-delivery-plan.md#real-material)) | every Case-level claim rests on synthetic sets; no model fact is measured where a rule could use it |
| **G4 handover** | Every figure is reproducible from the repository plus the data bundle ([data](model-training.md#data)) | `Aml.StageEval` and `e2e.sh` run from the bundle | the baseline classifier's recipe is not recorded, the per-reader stage figures predate the sets in force, the model tests run nowhere automatically |

Sizes: **S** at most a day, **M** at most a week, **L** more. **OWNER** marks an item that waits on the owner's or the bank's decision, data or account.

## A. Foundation, before any training

| ID | Work | Goal | Done when | Depends on | Size | Start |
|---|---|---|---|---|---|---|
| WI-01 | Restore the bundle, set the `AML_*` variables, fetch the base-model weights, and reproduce the stage baseline and the news working figures | G4 | `Aml.StageEval` prints the baseline right/decided on all six development sets; `e2e.sh` prints 508/64/69 and 353/40/44 for the retained pair | OWNER: bundle password from the data owner | S | [tools/data restore](../../tools/data/README.md#restore-on-another-host), [data](model-training.md#data), skills `data-bundle`, `measure-stage`, `news-models` |
| WI-02 | Record a reproducible classifier recipe: train with `train_stage.py` defaults, measure through the vote; adopt it if no development set falls below aml47, else recover aml47's arguments from the previous owner | G4, G1 | a classifier job template and its arguments are in `tools/stage-model`, and the model it trains meets the baseline on all six sets (it then becomes the baseline), or aml47's arguments are recorded there | WI-01; your own Colab sign-in; OWNER (previous owner's job folder) only if the defaults fall short | M | [train and export](../../tools/stage-model/README.md#train-and-export-on-colab), skill `colab-job` |
| WI-03 | Re-measure each reader alone and "two alike" with the rules in force and accept at 114 persons | G4, G1 | the per-reader table and the two-alike range in [decisions in force](stage-model-training.md#decisions-in-force-and-their-evidence) are re-measured, with accept included | WI-01 | S | `tools/Aml.StageEval` (columns RULES, CLASSIF, LLM, SPLIT) |
| WI-04 | Sweep the existing news gold against guideline rule 45 (implied steps not labelled, another case's step at most a `prior`, a group step only to the members tied to it) | G2 | a sweep record beside the labels (annotate → independent verify, each by a different annotator), `check.py` passes, and the dev figures are re-measured on the swept gold | WI-01 | M | [labelling](model-training.md#data), `AML_DATA/labels/tools/guideline.md`, skill `label-data` |
| WI-05 | **OWNER:** obtain real Cases with the bank's recorded decision and reason per candidate row, and build a Case-level measurement set from them | G3 | the set is in the bundle with a `MANIFEST.json` role, and a measurement prints decided rate, wrong conclusions and the counts per model-read fact; [real material](poc-delivery-plan.md#real-material) states them | the bank's data | L | [real Case shape](../feature/system-overview.md#what-evidence-is-worth-reading) (point 5), [legacy review records](../feature/extraction-and-normalization.md#legacy-review-records) |

**WI-02.** The baseline cannot be retrained from the repository today, so every classifier change is measured against a model nobody can rebuild. A recipe from the defaults that meets the baseline is as good as the original and is reproducible.

**WI-04.** The repository records no sweep of the existing gold against rule 45. A sweep changes the gold the retained pair is scored against, and a model trained on the earlier reading loses F1 on swept gold; compare candidates only on one gold revision, and sweep train and dev together before the next training. Fix labels before training ([data first](model-training.md#working-rules-for-an-agent-doing-this-work)).

**WI-05.** The case file's per-row decision is read today as an imported decision and never as evidence. It is the only source of truth for whether a reading changed a decision rightly, and items WI-13, WI-14 and WI-16 cannot be measured without it.

## B. News models to G2

| ID | Work | Goal | Done when | Depends on | Size | Start |
|---|---|---|---|---|---|---|
| WI-06 | Write the KYC-aligned measure into the acceptance gates (which steps are outcome steps, which case-level), and re-measure the gate form of end to end for event43 | G2 | [acceptance](news-model-training.md#acceptance-has-three-gates) defines the measure, and its figures on both development sets are in [current candidate choice](news-model-training.md#current-candidate-choice) | OWNER: confirm the definition | S | the evaluator's `profile` mode; [current candidate choice](news-model-training.md#current-candidate-choice) (Profile-person rows) |
| WI-07 | Reduce wrong ownership in sentences naming several suspects or a group: give the model where the step word stands, or resolve the owner outside it | G2 | end-to-end F1 above 0.90 on both development sets for one pair, on one gold revision | WI-01, WI-04, WI-06 | L | [what the evidence establishes](news-model-training.md#what-the-existing-evidence-establishes) (ownership pilot, jobs 38/39), skill `news-models` |
| WI-08 | Mirror the run-time questions in training: the unasked suspects about a sentence naming one for a group (等N人), and a sentence naming a suspect for investigation and referral | G2 | `prep.py` builds both kinds of row, a parity check against `NewsModelReading.Asked` passes, and both development sets are measured with a pair trained on them | WI-01 | M | [the question boundary](news-model-training.md#the-question-boundary-controls-what-can-be-learned-and-found), `tools/news-model/prep.py` |
| WI-09 | Court material: split a list of defendants or a table into one record per defendant before reading; read the 40 releases through the same text path the product uses | G2 | recall on the 40 court releases rises with neither development set falling; the evaluator reads a PDF-sourced release as `WebPages.ReadAsync` does | WI-01 | M | [current candidate choice](news-model-training.md#current-candidate-choice) (79 of 92 misses in two releases), [what was tried](news-model-training.md#what-was-tried-and-left-out) (unwrap row), `tools/news-model` |
| WI-10 | Commercial media: label a media set (persons, roles, outcome steps) and measure the retained pair on it; this is where full names stand | G2, G3 | the set's end-to-end figures are in [current candidate choice](news-model-training.md#current-candidate-choice); media rows enter training only with the bank's licence | **OWNER:** media licence for training ([ADR-0015](../adr/0015-licences.md)); measurement can start without it | L | media sample in [real material](poc-delivery-plan.md#real-material), skill `label-data` |
| WI-11 | One final measurement on the held-back sets, aggregate counts only, after the pair, thresholds and reader rules are frozen | G2 | the test split, `data-synth/heldout.jsonl` and `data-court/heldout.jsonl` figures are in [current candidate choice](news-model-training.md#current-candidate-choice), each gate stated pass or fail | WI-06 to WI-09 frozen | S | `measure_dev.py --split test`, [entry points](news-model-training.md#where-the-artifacts-and-entry-points-live) |

**WI-07** is the main G2 gap. Retraining, more documents of the same kind and thresholds move only low-margin cells; the remaining errors are whose step and which case. Ownership was learnable only when the step word's position was given, so the work is a run-time step-word finder or an ownership rule over a fixed form, measured on both sets. The five source-reviewed actor/case errors have not been re-checked against the current pair; re-check them first.

**WI-09.** Unwrapping alone was measured (0.692 against 0.681 on the 40 releases) and 15 of the 18 hard-wrapped judgments are tables, so the lever is the record structure, not joining lines. The 40 releases are the design set of the court readings; the eight held-out releases stay for WI-11.

**WI-10.** The labelled corpus is agency releases and judgments, where names are mostly masked; G3 facts beside a full name occur in media pages. Without a labelled media set no figure says how the pair reads the text a Case is most often screened against.

## C. Models that feed a Case decision (G3)

Work here only as far as WI-05's Cases show that the fact changes decisions.

| ID | Work | Goal | Done when | Depends on | Size | Start |
|---|---|---|---|---|---|---|
| WI-12 | Measure the news models' party for a page person (suspect or defendant against victim, official), so a rule could weigh a model-read role for `NOT_HIT` | G3 | the party's precision per person for victims and officials, and the count of labelled suspects it would mark non-suspect, are in [news model training](news-model-training.md#current-candidate-choice); weighing it is a separate change with its own measure | WI-01, WI-06 | M | [condition on a page](../feature/external-sources.md#matched-against-the-customer) (step 4), `Core/Pages/PageReadings` |
| WI-13 | Measure a model-read accused or company-officer role in place of the closed word lists of the advisory age hint | G3 | wrong and right hints per pair on the real-shape set and on WI-05's Cases are in [the hint's section](../feature/identity-adjudication.md#implausible-age-at-a-dated-document-advisory) | WI-05 | M | `Core/Identity/DocumentAgeWords.cs`, [open question](open-questions.md#data-and-source) |
| WI-14 | Decide by measurement whether the vendor Profile's English notes yield facts comparable with the customer's birth date (event and charge years by rule, roles by a model) | G3 | on WI-05's Profiles, the count of notes holding such a fact and how often it would conclude, stated in [Profile notes](../feature/extraction-and-normalization.md#profile-notes) with the decision | WI-05 | M | [Profile notes](../feature/extraction-and-normalization.md#profile-notes) (kept, not interpreted) |

**WI-12.** Today only a victim's or an official's role word read by the rules concludes `NOT_HIT`; the models' party is not used because no one measured it per person. The NER gate scores spans with roles, not the party of a person, and `ORG-Victim` has no true positive on the dev set. A wrong `NOT_HIT` on a suspect is a missed hit, so the count of suspects it would clear decides.

**WI-13.** The hint becomes a concluding rule only on a measured error rate on real Cases; the word lists are known to be incomplete. On the real-shape set it is given to 6 pairs, all strangers, which says what it reads, not how often it is wrong.

### Measured, not worth building

Do not start these without a new observation; the evidence is in the linked section.

| Not built | Measured reason |
|---|---|
| ages and birth dates beside names in agency releases and judgments | none beside a full name among 311 labelled suspects; 1 of 52 on media pages ([real material](poc-delivery-plan.md#real-material)) |
| gender as a veto, an age through an anonymised form, visible ID digits | gender about 2.3 per 100 suspects; the other two 0 ([real material](poc-delivery-plan.md#real-material)) |
| company and position beside a named person | frequent in financial-crime reports, but the customer record has no employer field ([what evidence is worth reading](../feature/system-overview.md#what-evidence-is-worth-reading)) |
| per-cell model capability beyond the G2 measure | not what a reviewer acts on ([read this before training](model-training.md#read-this-before-training-anything)) |
| the recipes in [tried and not adopted](model-training.md#tried-and-not-adopted) and [what was tried](news-model-training.md#what-was-tried-and-left-out) | each measured against the vote or both development sets |

## D. Stage readers (G1)

| ID | Work | Goal | Done when | Depends on | Size | Start |
|---|---|---|---|---|---|---|
| WI-15 | Measure the language model with the run-time person list (the Profile's names) instead of the measured run's document forms | G1, G4 | unanimous precision and coverage with Profile-name lists are in [decisions in force](stage-model-training.md#decisions-in-force-and-their-evidence) | WI-01, Ollama with the model tag; Profile names that differ from the text's forms (WI-05 or the real-shape set) | M | [three readers](../feature/watchlist-and-adverse-information.md#three-readers) (the language model's input), skill `gemma-stage` |
| WI-16 | Measure whether a combining rule (the latest dated item, the most advanced stage) is right more often than UNKNOWN for a Profile whose news items read different stages | G1, G3 | a labelled set of multi-item Profiles is measured and the result stated in [several news items](../feature/watchlist-and-adverse-information.md#several-news-items); the code changes only if it is right more often | WI-05 (no labelled Profile with several items exists) | M | `SourceConditions.NewsStage`, [open question](open-questions.md#data-and-source) |
| WI-17 | Re-run rule pruning after every adopted reader change | G1 | `prune.py` output for the new readers recorded in the stage-rules row of [decisions in force](stage-model-training.md#decisions-in-force-and-their-evidence); a rule that may go is deleted with its code, catalog entry, tests and docs | an adopted reader change (WI-02) | S | `tools/stage-rules/prune.py`, skill `prune-rules` |
| WI-18 | Build a stage set labelled independently of the classifier's training labels; only then may a language-model fine-tune be tried again | G1 | the set is in the bundle with its labelling record (two blind passes, merged) | OWNER: labelling effort | L | [fine-tuning row](stage-model-training.md#decisions-in-force-and-their-evidence), skill `label-data` |
| WI-19 | Low priority: non-suspects given a stage by the vote | G1 | only when a non-suspect's stage changes a Case output (a stage weight in an approved `RiskPolicy`); court sets would first need non-defendant labels | an approved stage weight | M | [persons who are not suspects](stage-model-training.md#persons-who-are-not-suspects) |

**WI-17.** Under the current readers every kept rule is needed (removing any lowers a set's precision), so pruning now finds nothing; a new reader changes what the vote needs.

**WI-18.** A fine-tune on the classifier's labels made each reader better alone and the vote worse, because the two readers then fail on the same persons. Without an independently labelled set do not retry.

**WI-19.** All 20 unanimous wrong stages for non-suspects are ALLEGED, and an adverse-media condition is HIT at any stage, so nothing the matrix or priority acts on changes. The two retrain recipes cost suspect precision.

## E. Operation

| ID | Work | Goal | Done when | Depends on | Size | Start |
|---|---|---|---|---|---|---|
| WI-20 | Run the model tests before every adoption, since CI has no models: a documented local run with `AML_STAGE_MODEL` and `AML_NEWS_MODELS` set, or a scheduled job on a machine holding the models | G4 | [how a change is judged](model-training.md#how-a-change-is-judged) names the command and the tests that must pass; for a scheduled job, the machine is named | OWNER for a scheduled machine | S | [commands](../../AGENTS.md#commands), `StageReaderCaseTests`, `SyntheticPageCaseTests` |
| WI-21 | **OWNER (bank):** decide whether the language model may be used | G1 | [ADR-0015](../adr/0015-licences.md) records the decision | the bank | — | [licences](model-training.md#licences) |
| WI-22 | **OWNER:** every bundle copy encrypted and in the register; after a document is removed from the copies, retrain the models that learned it | G4 | the register lists every copy as encrypted; a removal is followed by a retrained pair or classifier measured as WI-01 | the data owner | S | [make a bundle](../../tools/data/README.md#make-a-bundle-and-encrypt-it), [remove a judgment](../../tools/data/README.md#remove-a-judgment-from-every-copy) |

**WI-21.** Against the language model, the vote has two readers, the rules' stage stands, and a differing classifier only orders review; G1's baseline would have to be re-measured for that deployment.

**WI-22.** The news evaluator's `dev.jsonl` exists only in the bundle ([what is not here](../../tools/news-model/README.md#what-is-not-here)), so a lost bundle loses G2's measure.

## Order of work

A first: WI-01, then WI-02, WI-03 and WI-04 in parallel; WI-05 as soon as the bank can supply Cases. Then WI-06 to WI-10 for G2, WI-07 first, and WI-11 once they are frozen. C (WI-12 to WI-14) only as far as WI-05's Cases show the fact changes decisions. D as capacity allows, WI-17 after any adopted reader change. E alongside: WI-20 before the first adoption. Enabling automatic MATCH would change the plan: the stage and the support evidence would then change decisions, which raises WI-12 to WI-16 above B.
