# stage-rules

Which legal-stage rules (`src/Aml.Core/Screening/Stage/`) the unanimous vote of rules, classifier and language model
needs. Why and how a rule change is judged: [docs/plan/stage-model-training.md](../../docs/plan/stage-model-training.md)
("What is judged") and [why the rules are few](../../docs/feature/watchlist-and-adverse-information.md#stage-from-attached-news).
The agent skill `prune-rules` walks through it.

```bash
A=<language model answers folder>      # the deployed model's replayed answers, as for tools/Aml.StageEval
python -I -X utf8 tools/stage-rules/prune.py $A $A [only=Keyword,WantedNotice] [onnx=<dir>] [sets=<dir>]
python -I -X utf8 tools/stage-rules/prune.py --selfcheck
```

`prune.py` copies this checkout (`src/`, `tools/Aml.StageEval`, `licenses/`, the root build files; no `bin`/`obj`) into a
temporary folder, measures it unchanged, then for each rule unit applies one source patch, builds `Aml.StageEval` and
runs it on the six development sets, and restores the file. It prints, per unit and set, the change of DECIDED and RIGHT
(the unanimous persons and how many of them are right), `!` where the unanimous precision RIGHT / DECIDED falls, and the
change of the rules-alone count (a diagnostic). The verdict per unit: "precision falls" when any set's precision falls;
"no effect" when no set's DECIDED, RIGHT or rules-alone count moves at all (dead or covered by another rule); "may go"
otherwise. `out=` is not taken: the evaluator's rows go into the copy. The held-back sets are
never run; the copy, its build and its per-person rows are removed at the end. About six minutes per unit (one
evaluator run each).

Units:

| Unit | Switched off by |
|---|---|
| every `StageBasis.X` entry of `RulingReading.Readers` and `LegalStageReading.Transitions` (found in the code) | removing the entry from its list |
| `UnderInvestigation` (ALLEGED from 偵辦 / 調查) | `ReportRules.Closing` never returns it |
| `Bystander` (a judgment's non-defendant) | `ReportRules.Bystander` is always false |
| `Subjectless` (a sentence not naming the person tells their dismissal or indictment) | `ReportForm.Scope` makes only sentences naming them attributable |

The last three are exact texts in `EXTRA`; when the code changes, a unit whose text no longer occurs once is reported as
not applying, and `EXTRA` is brought up to date. They and the list entries correspond to the earlier switches
`C_INVESTIGATION`, `BYSTANDER`, `SUBJECTLESS`, `R_ORDER`, `R_RELEASE_WHOLE`, `R_PROCEEDS`, `R_ROW_NUMBER`, `KEYWORD`
and `T_WANTED` of the 42-unit switch-off copy the deletion was decided with (outside the repository, environment switch
`AML_OFF`); the other 34 units are deleted from the code.

A unit off by a patch needs a build per unit. A switch inside the product (the evaluator naming rule codes to leave out
of the two decision lists) would measure every unit in one build, but it is a hook in `src/` for measurement only, so
it is not there.
