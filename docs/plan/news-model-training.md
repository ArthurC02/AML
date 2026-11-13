# News model training

How the two news models that `NewsModelReading` corrects are made, measured and handed to the runtime, and where that work stands. The models and every piece of training data live outside the repository (ADR-0015, [security and privacy](../feature/security-and-privacy.md)); this page is the map to them. The runtime side is described in [watchlist and adverse information](../feature/watchlist-and-adverse-information.md#news-digest).

## Mental model

- **Two models, one base.** Both are `bert-base-chinese` (Apache-2.0) fine-tuned; the base must stay a clean-licence model (ADR-0015).
  - **NER** tags every person and organisation with a role: `PER-`/`ORG-` + `Suspect`, `Victim`, `Witness`, `Official`, `Other`.
  - **Event model** answers, per (entity form, sentence), which `NEWS_EVENT` labels the sentence states for that entity (multi-label sigmoid, plus `-Negated`, `-Requested` and `prior`).
- **The runtime decides which pairs are asked**, and training rows are built the same way. A sentence is asked for an entity when it holds one of its forms; one following sentence that names nobody is asked for the last-named person, only when that person is a suspect (`NewsModelReading.Asked`, mirrored in `prep.py`). A gold event whose sentences are never asked gives no row ("unreachable", by design).
- **Score that matters: end-to-end F1** over (document, gold person, event label). It is measured on whole dev documents through the real .NET code (`OnnxNewsModel` + `NewsModelReading`), not on training rows. The goal is **≥ 0.9 on dev**, not only on test.
- **Data first.** A found label error stops training: fix all of them, re-run `prep.py`, then retrain. Never train on labels known to be wrong.

## Where things are

| What | Path (outside the repo) |
|---|---|
| Texts | `C:\tmp\aml-data\judgments\jd-*.main.txt` (court judgments), `press\*.main.txt / .para.txt` (government press releases), `news\npa-*.main.txt` (police releases) |
| Gold labels | `C:\tmp\aml-data\labels\v6\ver` (577 judgments and press releases), `v6\npa\fin` (1041 police releases), `v7\ver` (292 judgments, train only) |
| Guideline, checker, prompts | `C:\tmp\aml-data\labels\tools\`: `guideline.md` (rules 1–44, later readings win; 44am–44at are the newest), `check.py`, `jd7-annotate/verify/sweep-prompt.md`, crawler source `crawl-src\` |
| Progress log and handoff | `C:\tmp\aml-data\labels\v6\progress.md` (sections HANDOFF and HOW TO TRAIN ON COLAB) |
| Training code | `C:\tmp\aml-train\`: `prep.py`, `train_ner.py`, `train_ev.py`, `export.py`, venv `.venv` |
| Training data | `C:\tmp\aml-train\data\`: `split.json`, `ner-{train,dev,test}.jsonl`, `ev-{train,dev,test}.jsonl` |
| Models | `C:\tmp\aml-train\models\ner`, `models\ev` (current), older ones beside them (`ner-v5prod`, `ev-v5prod` = the last shipped pair, `ev-v9`, …) |
| End-to-end evaluation | `C:\tmp\aml-eval\` (.NET console referencing `Aml.Core`/`Aml.Infra`; `Program.cs`, `dump_split.py`) |
| Colab | `C:\tmp\colab\` (Dockerfile, `home\` = the container's home with job folders and scripts) |

## Pipeline

1. **Crawl.** `crawl-src` (`dotnet run -c Release -- fjud <dir> <query-hash> <total>` for judgments, `npa <dir> <total>` for police releases), 3 s between requests, generic User-Agent. The judgment query hash comes from a keyword search on the public judgment site. Government texts are free under 著作權法 §9; police releases are OGDL-Taiwan 1.0.
2. **Label.** Annotate → independent verify → targeted sweep, each by a different subagent (a mid-size model is enough), batches of 7–8 documents, every file through `check.py`.
   - Labels quote exact strings of the text: entities with mentions and party, and events with type, certainty, parties, trigger, sentence, `also`, `prior` and so on.
   - Disagreements between subagents become new guideline readings, applied by the next pass.
   - Subagent reports never contain names or quotes.
3. **Prepare.** `.venv/Scripts/python.exe -I prep.py` writes `data/`.
   - `split.json` is fixed: dev and test stay the same documents across rounds, and new crawl rounds go to train only.
   - Prints counts only.
4. **Train.** `train_ner.py 3`, then `train_ev.py 4` (epochs as the argument). Both use CUDA when present.
   - Event input: `[CLS] form [SEP] sentence [SEP]`, the sentence cut to 460 characters around the form, every occurrence of the form wrapped in `[unused1]`/`[unused2]`, 512 tokens.
   - Every negative row is used each epoch, and the best dev threshold is chosen per epoch.
5. **Export.** `export.py` writes `model.onnx` for both and checks ONNX against torch. Copy `models\bert-base-chinese\vocab.txt` into `ner\` and `ev\` (training saves no vocab).
6. **Evaluate** in `C:\tmp\aml-eval`:
   - Run `dotnet run -c Release -- C:/tmp/aml-train/models dev tagcache` after a NER change.
   - Move `evcache-dev.tsv` aside after an event-model change.
   - Then run `dotnet run -c Release --no-build -- C:/tmp/aml-train/models dev cached e2e min=0.4`. Options:
     - `min=` is the event threshold;
     - `perlabel` prints per-label counts;
     - `dump=<file>` writes error rows; they hold text, so keep the file in a scratch folder;
     - `evids` checks C#/Python input parity.
   - After any gold change in dev or test, re-run `prep.py` and `dump_split.py dev` (or `test`).
   - Use test only for a final check; choose thresholds on dev.
7. **Ship.** Point `NewsModels:Directory` (`Aml.Api/Program.cs`) at a folder with `ner\` and `ev\`, each holding `model.onnx`, `config.json` and `vocab.txt`. Then update the measured numbers in the feature doc and `NewsModelReading.EventThreshold`.

## Training on Colab

Google Colab T4 GPUs, driven by the `colab` CLI inside Docker.

- **Setup.**
  - Container `colab` (image `colab-cli` from `C:\tmp\colab\Dockerfile`; mounts `C:\tmp\colab\home` → `/home/colab` and `C:\tmp\aml-train` → `/train`).
  - The user's Google sign-in is already stored by the container's file keyring under `C:\tmp\colab\home\.local\share\python_keyring\`. Never print, copy or commit it. If it expires, the user signs in again themself; an agent never handles the password.
- **Command form** (Git Bash):

  ```bash
  export MSYS_NO_PATHCONV=1; docker exec -i -e OAUTHLIB_RELAX_TOKEN_SCOPE=1 -w /home/colab colab sh -c 'colab <cmd> < /dev/null'
  ```

  - Commands: `new -s NAME --gpu T4`, `upload -s NAME <local> <remote>` (lands at `/` on the VM), `exec -s NAME -f script.py --timeout N`, `download -s NAME <remote> <local>`, `stop -s NAME`, `usage`.
  - About 1.15 compute units per hour; a NER plus event-model run takes about 1–2 h.
- **A job** (`C:\tmp\colab\home\jobN\`): `data\`, `models\bert-base-chinese\`, `train_ner.py`, `train_ev.py`, and `run.sh`, which installs `transformers==5.22.2`, trains, tars the models and writes `done.txt`.
  - Pack it with `tar czf` and `split -b 40m`; larger uploads fail with HTTP 500.
  - Upload the parts.
  - `startN.py` joins and unpacks the parts, then starts `run.sh` with nohup.
  - `peekN.py` shows progress; `watchN.sh` polls every 5 minutes.
  - To get results back, `pack*.py` splits the models into 40 MB parts on the VM. Download the parts, check the md5, untar them into `C:\tmp\aml-train`, then go on with steps 5–6.
  - Stop the session when done.

## Where it stands (2026-11-14)

Scores are end-to-end on the fixed dev set (161 documents) and test set (158 documents).

| Models | dev F1 | test F1 |
|---|---|---|
| v5 (last shipped pair), threshold 0.3 | 0.615 | 0.660 |
| v9 NER + v9 event model, 0.45 | 0.695 | 0.690 |
| v9 NER + v10 event model (460-character crop, mention markers), 0.4 | 0.763 | 0.814 |
| same, organisations limited to organisation steps, gold fixes | 0.776 (P 0.769, R 0.782) | not yet measured |

- Judgments are the weak part: 0.719 on dev, against about 0.79 for press releases.
- 72 of the 91 dev misses have a model probability below 0.3. The model needs more and better judgment data more than new rules.

In progress:
- 292 new judgments (v7) are labelled, verified and swept. With them, train has 1575 documents and 4157 positive event rows (from 2779).
- Two Colab runs:
  - `aml4`: NER and event model on the v7 data;
  - `aml3`: event model, 6 epochs, on the old data, as an epoch check.
  - When they finish: steps 5–6 above, compared with 0.776.

### Results of the current models

There are two kinds of score:
- **Row-level F1** is what `train_ev.py` prints per epoch, over (form, sentence) rows at the chosen threshold. Use it to compare epochs of one run.
- **End-to-end F1** is what `C:\tmp\aml-eval` prints, over whole documents through the .NET code. Use it to decide.

They differ a lot; for example, v10 is 0.684 row-level against 0.763 end to end on dev.

| Model | Measure | dev | test |
|---|---|---|---|
| NER v9 (3 epochs) | span F1, role included | 0.849 | 0.861 |
| event v10 (4 epochs) | row-level F1, threshold 0.3 | 0.684 | 0.819 |
| v9 + v10 + organisation steps | end-to-end F1, threshold 0.4 | 0.776 (P 0.769, R 0.782; tp 327, fp 98, fn 91) | not yet measured |

NER v9 per role on dev / test:
- ORG-Other: 0.674 / 0.818.
- PER-Other: 0.519 / 0.585.
- ORG-Victim: about 0.2 / 0.0.
- Suspects, victims and officials are found well. The weak roles are the "other" person and organisation roles, which are rare in the data.

End to end on dev by source:

| Source | tp | fp | fn | F1 |
|---|---|---|---|---|
| judgments (`jd-`) | 138 | 55 | 53 | 0.719 |
| police releases (`npa-`) | 108 | 32 | 24 | 0.794 |
| other press releases (each site) | 4–18 | 0–4 | 0–5 | 0.74–1.0 |

End to end on dev by label (current pair, threshold 0.4):

| Label | tp | fp | fn | Label | tp | fp | fn |
|---|---|---|---|---|---|---|---|
| Arrest | 55 | 11 | 10 | Bail | 11 | 5 | 1 |
| Indictment | 41 | 7 | 10 | Confiscation | 11 | 1 | 4 |
| Questioning | 33 | 2 | 4 | AssetRestraint | 7 | 4 | 8 |
| Investigation | 31 | 15 | 8 | Fine | 7 | 0 | 0 |
| Conviction | 27 | 1 | 8 | WantedNotice | 2 | 0 | 5 |
| prior | 24 | 11 | 9 | Appeal / Appeal-Negated | 4 / 4 | 4 / 4 | 3 / 0 |
| Referral | 23 | 7 | 3 | Acquittal | 1 | 2 | 3 |
| Detention | 21 | 8 | 5 | Confiscation-Negated | 4 | 2 | 2 |
| Search | 19 | 11 | 4 | Detention-Negated / -Requested | 0 / 0 | 2 / 0 | 2 / 2 |

What the errors are. A review of 202 disagreements on dev by subagents gave these causes:

| Cause | Count |
|---|---|
| model missed a stated step | 88 |
| right step, wrong party | 42 |
| event invented where none is stated | 26 |
| wrong type | 20 |
| unclear | 15 |
| unreachable by design | 5 |
| gold error | 3 (fixed) |

- 72 of the 91 misses have model probability below 0.3, so lowering the threshold cannot recover them.
- Hard sentences are long multi-step ones, parties named only by code in the operative part or in tables, appeal outcomes, and group references ("the other suspects").

Runs in flight (row-level F1 on dev per epoch, when last read):
- `aml3`, event model, 6 epochs, old data: epoch 1 0.332, epoch 2 0.554, at step 3280 of 8364. Six epochs warm up more slowly, so compare it at the end with v10's 0.684.
- `aml4`, NER then event model, v7 data: NER at step 580 of 3198, no epoch result yet.
- On the VM, the logs are `/content/jobN/logs-ner.txt` and `logs-ev.txt`; `peekN.py` prints their epoch lines. Copy them to `C:\tmp\aml-train\logs-*-vN.txt` when you download a run, since logs of older runs are kept there.

Model files now:
- `C:\tmp\aml-train\models\ner` is v9 NER and `models\ev` is the v10 event model (4 epochs), both exported with `model.onnx`. They give the 0.776 above.
- Move both aside before unpacking a new run, e.g. as `ner-v9` and `ev-v10-4ep`. The eval caches in `C:\tmp\aml-eval` (`tags-dev.jsonl`, `evcache-dev.tsv`) belong to this pair.

Runs on Colab now (the same sign-in, so the same container sees them):
- **`aml4`**: VM folder `/content/job4`. Its `run.sh` trains NER (3 epochs), then the event model (4 epochs), then writes `/content/job4/models-out.tar` and `done.txt`.
  - Progress: `colab exec -s aml4 -f peek4.py` (from `/home/colab`, i.e. `C:\tmp\colab\home`).
  - To get the models back, write a `pack4.py` like `pack.py` with `job4` in place of `job`. It splits `models/ner models/ev` into `/content/out4.part.NN` and prints the md5.
  - Download each part, then `cat` the parts and `tar x` into `C:\tmp\aml-train`.
- **`aml3`**: VM folder `/content/job3`, the event model only, 6 epochs, old data. Its `run.sh` writes no tar, so pack it like `pack2.py` (`models/ev` only).
- `colab sessions` lists the live sessions and `colab status -s NAME` shows one; `colab usage` gives the balance and active assignments. The session names are kept in `C:\tmp\colab\home\.config\colab-cli\` (the CLI's session state), so a session started here is reachable from the same container by its name. `colab readme` prints the CLI's own manual. A Colab VM is recycled after some idle hours, and what is on it is lost. If a session is gone before its models were downloaded, re-run its job: the job folders and `.tgz` parts are kept in `C:\tmp\colab\home`.

In the repository, `OnnxNewsModel.EventInput` (crop and markers) and `NewsModelReading` (carry-over only after a suspect, organisation steps) are built and unit-tested. The scores above use `min=0.4`; the shipped `NewsModelReading.EventThreshold` is still 0.3, so the code at 0.3 gives other numbers.

Still to do in the repository:
- `EventThreshold` 0.3 → 0.4 (chosen on dev), with the models;
- swap in the models;
- refresh the measured numbers in the feature doc.

Known open points:
- 18 judgments are hard-wrapped (a line break every 20–40 characters), so their sentences are fragments at run time and in training. Fix: join hard-wrapped lines in the text reader and in `prep.raw_text`, together.
- Guideline readings still split: a company named on forged documents (cover name vs victim), and telecoms and platforms as organisations.
- Levers not yet tried:
  - a final model trained on train+dev;
  - an ensemble;
  - recall weighting;
  - a stronger base model, which needs a licence check and the user's decision.
