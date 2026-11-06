# ADR-0023: AI-ready data

- Status: Accepted; API consumers amended by ADR-0024; lineage amended by ADR-0025
- Date: 2026-11-04

## Context

The API will be used as tools by agents, and the stored results will be used to calibrate thresholds and Fellegi-Sunter parameters, to monitor review rates and missing data, and to reproduce any revision for audit. Data that serves these uses has to be aligned to them, qualified and governed. Each of those properties is only real if a test fails when it is lost.

The PoC database holds no data that must survive a format change.

## Decision

Every stored and returned record meets these conditions, each checked by a test:

| Use | Condition | Check |
|---|---|---|
| Agents call the API | typed records with a published JSON Schema; every field described; codes explained | every Golden result and explanation validates against its schema and reads back unchanged; every schema field has a description; every code the Golden and augmented cases emit is in the code catalog |
| Calibration and monitoring | per-pair decisions and evidence queryable without parsing JSON | the fact tables (`case_hit`, `hit_evidence`, `hit_reason`), written with the JSON in one transaction, rebuild every decision of every Golden Case |
| Monitoring | data quality per revision | `analysis_quality`, computed by the same function as the Golden metrics |
| Audit and reproduction | each revision traces to its inputs and rules | `analysis_lineage`: input file hashes, the list version of each Profile, source-policy, model, code-catalog, schema and application versions |
| Data minimization | personal data is marked | `x-sensitivity` (`PERSONAL`, `IDENTIFIER`) on the schema fields |

- The code catalog (`GET /api/codes`) and the schema descriptions (`GET /api/schemas`) are the one source of meaning; the documents point to them.
- **No old formats.** Stored results are read back into their types. When a record or the schema changes, `CaseJson.SchemaVersion` goes up, the single schema script changes, and the PoC database is rebuilt; nothing reads an older format.

## Consequences

- Projections and views work on types, not on JSON parsing, and have no "old data" cases.
- A schema change costs a database rebuild in the PoC. Before production, a new ADR moves to add-only migrations starting from the current schema.
- An evaluation dataset (pair features, system decision, Golden label) is a query over the fact tables; it is built when calibration needs it.
