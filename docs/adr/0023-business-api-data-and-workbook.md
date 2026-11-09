# ADR-0023: Business API, AI-ready data and the case workbook

- Status: Accepted. "Each fact on one sheet only" is decided but not built yet: the 命中 sheet's summary columns still restate 比對 and 證據.
- Date: 2026-11-04; updated 2026-11-09 (merged former ADR-0022, 0024, 0025, 0027)

## Context

The API is integrated by the bank's business systems and reviewers' tools. They read whole business objects and look operations up by business area. The stored results will calibrate thresholds, monitor review rates and missing data, and reproduce any revision for audit; each of those properties is real only if a test fails when it is lost. Provenance practice records what a run actually used (W3C PROV).

Reviewers also read, filter and file cases in Excel. Spreadsheet practice (Broman and Woo, 2018) is one table per sheet, one header row, one value per cell, no meaning carried by colour, and a data dictionary. Text a spreadsheet takes as a formula runs when opened (OWASP CSV injection, CWE-1236).

## Decision

- **Full objects by default.** `GET /api/cases/{caseId}`, `…/hits/{hit}` and `GET /api/profiles/{profileId}` return the full object; `view=summary` returns pure functions over the stored records, without identifier values and, of personal data, only names. Writes return the Case summary with its links.
- **Operations.** One tag per operation by business area (`Cases`, `Batches`, `Profiles`, `Reference data`), a unique stable `operationId`, and a description of what it does and its business rules. RFC 9457 problems with a code-catalog `type`, `errors[]` and `fix`; key paging with `nextPageToken`; `top` and `truncated` on candidate lists.
- **Contracts.** The OpenAPI document and every published JSON Schema are snapshots in `tests/Aml.Tests/E2E/Contracts`; any difference fails a test.
- **AI-ready data**, each condition checked by a test:

| Use | Condition |
|---|---|
| Integration | typed records with published JSON Schemas; every field described; every emitted code in the code catalog |
| Calibration, monitoring | fact tables (`case_hit`, `hit_evidence`, `hit_reason`) written with the JSON in one transaction rebuild every Golden decision; `analysis_quality` per revision |
| Audit | `analysis_lineage` names only what ran: input file hashes, each Profile's list version, source-policy, code-catalog, schema and application versions; a model that runs records its version and parameters, its output kept apart from the decision |
| Minimization | `x-sensitivity` (`PERSONAL`, `IDENTIFIER`) on schema fields |

- The code catalog (`GET /api/codes`) and schema descriptions (`GET /api/schemas`) are the one source of meaning. **No old formats**: a record change raises `CaseJson.SchemaVersion`, changes the single schema script, and rebuilds the PoC database.
- **Case workbook.** `GET /api/cases/{caseId}/workbook?view=summary|full&revision=` renders one revision as `.xlsx`. It restates the stored revision and adds no finding. Each sheet is one table with a frozen, filterable header; the hit number joins the sheets. **Each fact appears on one sheet only**; other sheets refer to it by hit number, `[S#]` or code. `view=full` adds identifying values. Text is a text cell, never a formula; text past 32,767 characters is cut with a note. Written with the .NET standard library; the same revision gives the same bytes. `Cache-Control: no-store`.

## Consequences

- A default response can hold identifier values; a consumer that passes data on asks for `view=summary` or drops `x-sensitivity` fields. The OpenAPI examples are GC-03 summaries.
- A route, field or description change updates the snapshots in the same commit (`AML_UPDATE_SNAPSHOTS=1`).
- A schema change costs a database rebuild in the PoC; before production, add-only migrations from the current schema.
- Calibration recomputes Fellegi-Sunter from stored revisions; only hits are stored, so non-matching pairs must be gathered for it.
- A workbook is a copy outside the system: access control stops at the download, and each issue is logged. There is no batch workbook.
