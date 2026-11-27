# ADR-0008: Validation backlog and deferred capabilities behind the Phase 1 controls

- Status: Accepted
- Date: 2026-11-02; updated 2026-11-09 (merged former ADR-0021), 2026-11-20 (Fellegi-Sunter groups and placeholders), 2026-11-26 (risk scoring is not the scoring rule; no blanket human review for PEP and Sanction under the scoring rule), 2026-11-27 (the DOB conflict released by the user's decision ahead of GC-06; the document conflict stays pending; the Sanction/PEP/RCA human-review release control lifted)

## Context

Only GC-01 to GC-03 have real data; waiting for GC-04 to GC-08 would stall all work. The PoC decision of 2026-11-02 left out the list database and search index, Profile merging, source refresh, entity identity evidence, Fellegi-Sunter and risk scoring; on 2026-11-04 the project asked for all of them now.

## Decision

- **Pending Golden Cases do not block development.** The automatic decisions related to each pending case stay disabled or restricted until it is validated ([validation backlog](../plan/golden-cases.md#validation-backlog)), or until the user releases one by decision: the DOB conflict (more than two years apart) concludes NO_MATCH since 2026-11-27 without GC-06, which will check it when supplied ([birth-date tolerance](../feature/identity-adjudication.md#birth-date-tolerance)). GC-04 and GC-05 will check PEP/RCA and Sanction acceptance; they no longer hold those candidates in blanket human review (the user lifted that control on 2026-11-27, [ADR-0001](0001-identity-adjudication.md)): an identity NO_MATCH gives NO_HIT for them as for any list type, so until GC-04 and GC-05 are supplied that NO_HIT rests on the validated hard-conflict rules alone.
- **Deferred capabilities are built without changing any Phase 1 control:**
  - **List database and retrieval**: `list_profile` keeps every captured Profile version with its SHA-256; `Core/Retrieval/ProfileIndex` searches by exact alias, word BM25 and character n-grams, counted by Profile, through `GET /api/profiles/search`, `GET /api/profiles/{profileId}` and `GET /api/cases/{caseId}/candidates`. Scores rank only ([ADR-0001](0001-identity-adjudication.md)).
  - **Profile merging**: `ProfileMerge` groups Profiles on a shared comparable document key or a recorded "same as" link, never on names; merged Profiles keep their IDs.
  - **Source refresh**: `case_input` stores the files of every revision; a changed Profile re-analyzes each Case whose latest input used it.
  - **Entity evidence**: registration number with jurisdiction, IMO number. Agreement is PROBABLE_MATCH (external REVIEW); no automatic MATCH or NO_MATCH for entities.
  - **Fellegi-Sunter**: `FellegiSunter.Score` over the hit prerequisites, DATE_OF_BIRTH, DOCUMENT and any other support, with placeholder m and u partly measured on sanctions lists against each other ([calibration weight](../feature/identity-adjudication.md#calibration-weight)), outside every result and decision.
  - **Risk scoring**: `RiskScoring` runs only with a bank-approved `RiskPolicy`; none ships, so no pair is scored. It grades AML risk after the decision and is not the [scoring rule](../feature/scoring-rule.md) that decides the match ([ADR-0001](0001-identity-adjudication.md)); its list and stage weights may be superseded there by the source-condition score (not decided).

## Consequences

- Pending cases block claims of validated automation in their area, not the design.
- Phase 1 behaviour is unchanged for every Golden Case. Thresholds, m/u, entity rules and risk weights are placeholders until calibrated or approved, each marked in code.
- Case-table candidates remain the input of `POST /api/cases`; index search does not feed a Case automatically.
