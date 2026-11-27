# labels

How the court-release stage labels are made: the reference stages of the court sets (accept, accept3, accept4, held-back
accept5), the v17 train rows and the recheck of hard persons. Why the sets are kept apart and how they are used:
[docs/plan/stage-model-training.md](../../docs/plan/stage-model-training.md) ("Measured sets and their boundaries").
The agent skill `label-data` walks through it.

The labels hold real names. They stay outside the repo (`AML_DATA`, `AML_TRAIN` in
[../stage-model/paths.py](../stage-model/paths.py)); the scripts print counts only. Run Python isolated (`python -I -X utf8 ...`).

| File | Does |
|---|---|
| `stage-brief.md` | the brief every blind labelling pass gets (rounds v13 to v17 got it word for word; the text folder `<RAW>` comes from the assignment) |
| `recheck-brief.md` | the brief of a recheck pass (v14r's passes got the stage brief and their person list in the assignment) |
| `choose.py <round> <n> <seed> [candidates.txt] [batches=4]` | seeded sample of gathered releases no round, recheck or measured set holds yet; writes `labels/<round>/batches.json` |
| `merge.py check <round>/<pass>` | format of a pass: doc id, `kind`, `forms` a non-empty list of strings found in the text, `defendant`, `stage` |
| `merge.py <round> <out.jsonl> [--fin] [--force]` | the evaluator's documents for the persons both passes give the same stage (`--fin`: an adjudicated folder taken whole); refuses a round with format errors (counts of bad persons and files per pass) and an existing output unless `--force` |
| `recheck.py pick <set> <eval out> <dir>` / `compare <set> <dir>` | choose the hard persons of a development set; score two recheck passes against the reference |

Each script has `--selfcheck` (made-up input, no data).

## The procedure

1. **Choose documents.** `choose.py v18 40 18` samples 40 gathered releases (`AML_DATA/court/raw/jy-*.main.txt`, or the ids
   of a candidates file, e.g. verdict releases only) that no label round (`labels/**`), recheck list or measured set
   (`measurements/court`, `measurements/report`) holds, and deals the sorted ids round-robin into 4 batches. The rounds
   v13 to v16 took 40 releases each (seeds 13 to 16) from hand-picked verdict releases; v17 took the 73 left. That pool
   was not scripted, so `choose.py` reproduces their batching (checked on all five rounds), not their samples.
2. **Two blind passes.** For each pass (`a`, `b`) and batch, one labelling agent gets `stage-brief.md` and an assignment
   naming `<RAW>`, `<OUT>` = `labels/<round>/<pass>` and its document ids. An agent sees only the texts: never the other
   pass, other label folders, model answers or evaluator rows. It reports counts only.
3. **Check, then merge.** `merge.py check <round>/a` and `/b`; fix format errors in the pass files before merging (merge
   refuses a round with any; a person with no form in the text is not a format error, merge leaves it out).
   `merge.py <round> AML_TRAIN/measurements/court/<set>.jsonl` keeps a person when the first person of pass `b` sharing
   a written form gives the same stage; persons only one pass lists, or the passes judge differently, are left out (the
   counts say how many). Adjudicated labels, if ever needed, go in `labels/<round>/fin` and are built with `--fin`.
4. **Development or held back.** A new set is measured once unseen; it stays held back (aggregate counts only, texts and
   per-person rows never opened, never relabelled from model errors) until a newer round is chosen, then may become a
   development set. Now held back: v11 (reports) and accept5 (v16).
5. **Train-only rounds** (v17): `tools/stage-model/add17.py` builds classifier rows from the agreed persons and drops any
   document that shares an id, a text or 10-gram overlap of 0.2 or more with any evaluation document.
6. **Recheck hard persons** of a development set, after a measurement: `recheck.py pick accept3 <Aml.StageEval out>
   labels/<round>r` lists the persons where at least two of the three readers miss the reference (`persons.tsv`: doc,
   person, forms); two blind passes with `recheck-brief.md` write `a.tsv` and `b.tsv`; `recheck.py compare accept3
   labels/<round>r` counts confirmed (both give the reference), label error (both give the same other stage: correct
   it, through `fin/`, and rebuild the set) and split. The passes see the texts and the list, not the reference, the
   readers' stages or each other.

## Rounds and their reproduction

| Round | Made | Rebuilt here |
|---|---|---|
| v13 -> accept, v14 -> accept3, v15 -> accept4, v16 -> accept5 | two blind passes, `merge.py` (then `measurements/court/accept*.py`) | `merge.py` output byte-identical to the four set files |
| v17 (train) | two blind passes, `add17.py` | `add17.py` and `add17.py --verdicts` byte-identical to `data17/` and `data17v/`; `merge.py v17` agrees on 250 persons, 249 after `add17.py` drops one overlapping document |
| v14r (recheck of accept3) | 27 persons, two blind passes | `compare`: 26 confirmed, 1 split, as recorded; `pick` on today's rows finds 24 of the 27 (the other 3 were chosen because two language models both missed them, which the evaluator's rows do not hold) and 1 more |
