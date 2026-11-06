# ADR-0025: Lineage records what ran

- Status: Accepted
- Date: 2026-11-06

## Context

A revision's lineage ([ADR-0023](0023-ai-ready-data.md)) is the audit record of what the revision was computed from. It named the Fellegi-Sunter model version on every revision, but the analysis never runs that model: the weight is a calibration tool for Gate D ([ADR-0019](0019-strength-decision-table-before-fellegi-sunter.md)), outside every decision ([ADR-0021](0021-deferred-capabilities-built-behind-controls.md)). A lineage that names a model which did not run tells an auditor it took part in the result.

Provenance practice records what a run actually used, not what the code could do (W3C PROV: an activity `used` its inputs). Model-risk practice keeps an inventory of the models in use and runs a challenger in shadow on the same inputs, its output kept apart from any decision. The calibration data is already kept for every hit of every stored revision: its evidence (group, direction, strength) in the `hit_evidence` fact table, from which the agreement patterns are built, and the Profile in the stored result, whose dates of birth `FellegiSunter.Score` also needs.

## Decision

- The lineage names only the inputs and the rule sets that ran: input file hashes, the list version of each Profile, and the source-policy, code-catalog, schema and application versions. It does not name the Fellegi-Sunter model.
- Calibration recomputes the weight from the stored revisions (`FellegiSunter.Pattern`, `EstimateParameters`, `Weight`, `Calibrate`); the analysis does not run it. Only hits are stored, so calibration over non-matching pairs needs pairs gathered for it.
- When a model runs in the analysis, in shadow or in a decision, the revision records its version and parameters, and the result keeps its output apart from the decision.

## Consequences

- Amends [ADR-0023](0023-ai-ready-data.md): its audit-and-reproduction row names no model version.
- `CaseJson.SchemaVersion` goes up and the PoC database is rebuilt ([operations runbook](../plan/operations-runbook.md)).
