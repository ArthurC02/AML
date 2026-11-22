# judgments

The judgments behind the court releases of the court sets, from the Judicial Yuan judgment API. Measured as a stage
source and **not adopted** (too few releases have a findable judgment, and most of those mask the names):
[docs/plan/stage-model-training.md](../../docs/plan/stage-model-training.md), "Decisions in force", row "Judgments
behind court releases". Kept so the measure can be repeated; `tools/Aml.StageEval judgments=<AML_JUDGMENTS>/docs` reads
what `fetch.py` saved.

| Script | Does |
|---|---|
| `jids.py [<set> ...]` | candidate judgment ids per release (court code, case number, dates next to 宣判) -> `AML_JUDGMENTS/jids.jsonl`; development sets only (default accept, accept3, accept4); no network |
| `fetch.py` | asks the API for each release's candidates in turn, saves the first judgment as `AML_JUDGMENTS/docs/<doc>.json`, and deletes a saved judgment the service says it no longer holds |

`fetch.py` needs **the user's own account** on the Judicial Yuan open-data platform, and **the user runs it**, in their
own terminal: it asks for the account and password with `getpass`, keeps the token in memory only, and takes the
password in no other way. The service answers only between 00:00 and 06:00 (local time); the script waits one second
between requests. Agents never run it and never handle the password or the token. Locations: `AML_JUDGMENTS`, `AML_DATA`,
`AML_TRAIN` in [../stage-model/paths.py](../stage-model/paths.py).
