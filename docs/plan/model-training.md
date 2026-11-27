# Model training: start here

The entry point for whoever trains or measures the models. It says what the models are for, what a model result can change, how a change is judged, where the data and jobs live and what is already settled. The details belong to [stage model training](stage-model-training.md), [news model training](news-model-training.md), the tool READMEs in `tools/` and the skills in `.claude/skills/`; this page links to them and does not repeat them. What is left to do is in [model training work items](model-training-work-items.md).

## What the models are for

Two model families, both optional at run time and both readers only: what they read is an input that a fixed rule weighs, never a decision ([ADR-0001](../adr/0001-identity-adjudication.md), [ADR-0012](../adr/0012-sources-and-web-information.md)).

- **Stage readers** read the legal stage of a Profile person in attached news ([three readers](../feature/watchlist-and-adverse-information.md#three-readers)). The person-stage classifier is `bert-base-chinese` with a 7-way head, run as ONNX (`Infra/Models/OnnxStageClassifier.cs`, `StageModels:ClassifierDirectory`). The language model is Gemma 4 12B q4_K_M on Ollama (Ollama tag `gemma4:12b-it-q4_K_M`; `Infra/Models/OllamaStageReader.cs`, `StageModels:LlmUrl` and `LlmModel`, prompt `src/Aml.Infra/Models/stage-prompt.txt`). They vote with the rules (`StageVote`): only a stage all three read alike is given; with one model or none configured the rules' stage stands; a configured model whose answer is missing gives UNKNOWN with `STAGE_READER_NO_ANSWER`, which a batch job retries before writing.
- **News models** are the NER role tagger and the event classifier, both ONNX, read by `OnnxNewsModel` (`NewsModels:Directory`) through `NewsModelReading` ([news models](../feature/watchlist-and-adverse-information.md#news-models)). They feed the news digest of the explanation, and the persons read on a received page, marked `MODEL_READ` ([received URL](../feature/external-sources.md#received-url-source-2)).

## Read this before training anything

What a model result can change in a Case is set out in [what evidence is worth reading](../feature/system-overview.md#what-evidence-is-worth-reading). Today only two outcomes change a Case result: an identity NO_MATCH, and a condition that is NOT_HIT. While automatic MATCH is not enabled, the legal stage and the news digest change only review priority and the explanation (and, for a page person, whether a condition is HIT or UNKNOWN, which combine the same way). A model gain is worth pursuing only if it changes something a reviewer acts on.

The development material shows what real documents hold ([real material](poc-delivery-plan.md#real-material)). Agency releases and judgments mask most names: 41 % of labelled suspects have a full name at all, and none has an age or birth date beside it. On public media pages an age stands beside 1 of 52 full-name suspects that the rules read. Targets therefore measure what a KYC reviewer acts on: whether the person is involved, the outcome steps, and the steps at case level. Per-cell model capability is not a target. A wrong conclusion costs far more than a REVIEW.

## How a change is judged

- **Stage:** run the unanimous vote through `SourceConditions.Determine` with `tools/Aml.StageEval` (skill `measure-stage`) and compare its precision and coverage with the [baseline table](stage-model-training.md#decisions-in-force-and-their-evidence). A change is adopted only by [the rule](stage-model-training.md#what-is-judged): no development set's precision falls, and coverage rises on the sets the change is meant for. One reader's own score never decides.
- **News:** measure end-to-end F1 as defined in [the acceptance gates](news-model-training.md#acceptance-has-three-gates): the goal is above 0.90 on the same NER/event pair, and persons written identically count as one name.
- **Held-back sets** are used once, at the end, as aggregate counts only, and never to choose between variants. For the stage models they are v11 and accept5. For the news models they are the test split, `data-synth/heldout.jsonl` and `data-court/heldout.jsonl`.

## Data

Labels, texts, measured sets, rows, cached answers and models all live outside the repository. The tools find them through these environment variables (defaults in `tools/stage-model/paths.py`, `tools/news-model/paths.py`, `tools/llm-stage/common.py`):

| Variable | Points to |
|---|---|
| `AML_DATA` | labelled material: `labels/` (rounds), source texts `judgments/`, `press/`, `news/`, court releases `court/raw/` |
| `AML_TRAIN` | training workspace: data rounds `data-r*/`, `data-dev2/`, `data-synth/`, `data-court/`, measured sets `measurements/{report,court}/`, `models/bert-base-chinese/`, Colab `downloads/`, Python venv |
| `AML_STAGE_WORK` | classifier rows `data/`, `data17/`, `data17v/` (default `AML_TRAIN/stage-clf`) |
| `AML_LLM_WORK` | the language model's fine-tune examples (default `AML_TRAIN/gemma-ft`) |
| `AML_COLAB_HOME` | Colab job folders and packages, mounted at `/home/colab` in the `colab` container |
| `AML_EVAL` | the working folder of the news evaluator `tools/Aml.NewsEval`: its documents in, its rows out (`e2e.sh`, `score.py`, `score_gemma.py`) |
| `AML_STAGE_EVAL_OUT` | `tools/Aml.StageEval`'s `out=` folder, read by `gold.py verify` |
| `AML_JUDGMENTS` | `tools/judgments` lookups |
| `AML_PY` | the Python interpreter `e2e.sh` runs (by default the venv's interpreter under `AML_TRAIN/.venv`) |
| `AML_STAGE_MODEL`, `AML_NEWS_MODELS` | model folders for the opt-in model tests |

Set `AML_DATA`, `AML_TRAIN`, `AML_EVAL` and `AML_COLAB_HOME` before running any tool. If they are unset, the tools fall back to the previous owner's machine layout. For the Python environment, run `python -m venv` and then `pip install -r tools/stage-model/requirements.txt` (or `tools/news-model/requirements.txt`; the pins are the same), and point `AML_PY` at that environment's interpreter.

**In the repository**, solution folder `/training/` of `Aml.slnx`: the two measurement tools `tools/Aml.StageEval` and `tools/Aml.NewsEval` (built by CI with the solution), the `colab` container's `tools/colab/Dockerfile`, these plan documents and the tool READMEs.

**In the data bundle** (`tools/data`; its password from the data owner): labels, texts, measured sets, rows, the bert-base-chinese tokenizer files, the recorded language-model answers that `Aml.StageEval` replays, the news evaluator's inputs (`aml-eval/`: `dev.jsonl`, `train.jsonl`, `evids-dev.jsonl` when present, `sg-<set>-r17.tsv`), the baseline stage classifier aml47 (`AML_TRAIN/downloads/aml47/onnx/`) and the retained NER / event pair (`AML_TRAIN/measurements/aml43-pair/ner/`, `ev/`; its evaluator caches are rebuilt once with `Aml.NewsEval ... tagcache`). The original alone holds the news-event labelling guideline, checker and prompts (`AML_DATA/labels/tools/`); the pseudonymised copy leaves them out. The models were trained on the real texts and are as sensitive as the data. Restore steps: [tools/data](../../tools/data/README.md#restore-on-another-host).

**What you must get yourself** (neither in the repository nor in the bundle):

- the base model's weights: Hugging Face `google-bert/bert-base-chinese` at the revision [tools/data](../../tools/data/README.md#base-model) names, `model.safetensors` into `AML_TRAIN/models/bert-base-chinese/` beside the bundled tokenizer files (no script downloads it there; a Colab job of `train_stage.py` fetches it by id);
- a Google sign-in of your own inside the `colab` container;
- Ollama with the model tag `gemma4:12b-it-q4_K_M`.

The data is **sensitive personal data**: real names of people in criminal proceedings. Any output, log, commit or report holds counts and hashes only; never copy a name or a quote. To move the data to another machine, use `tools/data` (skill `data-bundle`). It makes an encrypted original and a pseudonymised copy. The data owner is the person who types the 7-Zip password. The pseudonymised copy is **not** name-free and is still sensitive. The real→pseudonym map exists only inside the original, and no one opens or prints it.
**Labelling** uses `tools/labels` (skill `label-data`) for stage labels, with [`stage-brief.md`](../../tools/labels/stage-brief.md) as the brief of record. The news-event labels follow the guideline of record, `AML_DATA/labels/tools/guideline.md` (outside the repo, with its `check.py`). Labels go through annotate → independent verify → sweep, each step by a different annotator. Fix label errors before any training, and stop a run as soon as a label error turns up. Re-running a site's boilerplate filter changes old texts and breaks label offsets, so back up first.

Some decided news-event rules are easy for a newcomer to get wrong. They are guideline rule 45, summarised in [where the artifacts live](news-model-training.md#where-the-artifacts-and-entry-points-live):

- an implied step is not labelled;
- another case's step is at most a `prior`;
- a group's step goes only to the members the sentence or its context ties to it, never to an unnamed member.

The court-release stage brief asks a different question: the highest stage the text states, another case's included.

## Where it runs

- **Colab** runs the small models (classifier, NER, event) on a T4 and Gemma 4 on an L4, through `tools/colab` (skill `colab-job`; [README](../../tools/colab/README.md)):
  - The runner refuses to start when the session already exists, and it must confirm the stop.
  - After every job, check yourself that `colab sessions` lists no `aml` session. An idle VM burns compute units.
  - Every call runs under a timeout.
  - After the start, confirm that the job really runs on the VM: peek shows `alive=True` and `log.txt` advances, with `nvidia-smi` in the job's log. A start command that returned proves nothing.
- **The Colab login belongs to each person.** The new owner signs in with their own Google account inside the container. The stored token is never printed, copied or committed.
- **Local inference** uses the quantised model on a modest GPU. The product reader and `infer.py` speak Ollama's `/api/chat`, so measure through Ollama. Another local server, such as LM Studio, is good for trying a model by hand only.

## Licences

Under [ADR-0015](../adr/0015-licences.md), the code, the weights **and** every training corpus must allow commercial use. GPL weights and research-only corpora are out; CKIP was rejected for this reason. Gemma 4 has Apache-2.0 weights but an unpublished corpus, so whether it may be used is an open decision for the bank, and the language model reader stays off unless it is configured. Embedded third-party data is listed in `THIRD-PARTY-NOTICES.md`.

## Current state

- The baseline measures classifier aml47 (`AML_TRAIN/downloads/aml47/onnx/`) and the replayed answers of Gemma 4 12B q4_K_M (`AML_TRAIN/downloads/answers-20261123/gemma4_12b-it-q4_K_M/`) ([where things live](stage-model-training.md#where-things-live)).
- The retained news pair is NER20 / event43 (`AML_TRAIN/measurements/aml43-pair/`). The NER gate passes; the event and end-to-end gates do not ([figures](news-model-training.md#current-candidate-choice)).
- The repository ships no model files. CI configures no model, so the model tests skip (`AML_STAGE_MODEL`, `AML_NEWS_MODELS`).

## Tried and not adopted

Do not repeat any of these without a new idea. The evidence is in the owning document.

| Tried | Why not adopted |
|---|---|
| Fine-tuning Gemma (E4B and 12B, QLoRA on the classifier's train rows) | Each reader improved alone, but the vote's precision fell. Trained on the classifier's labels, the language model fails on the same persons as the classifier, and the readers' independence is what makes unanimity precise. Only an independently labelled set could change this ([stage](stage-model-training.md#decisions-in-force-and-their-evidence)) |
| Retraining the classifier with non-suspect rows (two ratios, aml58 and aml59) | Fewer wrong stages on non-suspects, but lower suspect precision and coverage ([non-suspects](stage-model-training.md#persons-who-are-not-suspects)) |
| More data of the same kind (v17 court rows for the classifier; more documents for the news models) | The classifier rose on one set and fell on another and in the vote. For the news models, retraining moves only low-margin cells |
| A larger Gemma (26B mixture of experts) | No better than the 12B |
| A revised prompt (the label brief's definitions) | Worse on two development sets |
| Looking the person up in judgments | Few releases have a findable judgment, and most of those mask the names (`tools/judgments`) |
| Court press releases as news training rows (event44) | No dev gain: the release errors come from reading and ownership, not from missing rows ([news](news-model-training.md#what-was-tried-and-left-out)) |

## Open items the new owner inherits

The goals in force, their measured gap and every open item as a work item (done-when, dependencies, size, owner decisions) are in [model training work items](model-training-work-items.md).

## Working rules for an agent doing this work

- A rule reads only a fixed formal form; free-text wording belongs to the models ([why the rules are few](../feature/watchlist-and-adverse-information.md#stage-from-attached-news)).
- A missing model answer stays missing end to end: no fallback label, never imputed.
- Data first: fix labels before training, and stop on a found error.
- Measure through the system's own entry points (`Aml.StageEval`, `e2e.sh`). Update the owning document's conclusions in the same change, without a run log.
- Do not push without the owner's word.
- Stop background jobs, watchers and Colab sessions when the work ends, and check that none is left.
