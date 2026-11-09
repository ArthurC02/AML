# ADR-0008: Validation backlog and deferred capabilities behind the Phase 1 controls

- Status: Accepted
- Date: 2026-11-02; updated 2026-11-09 (merged former ADR-0021)

## Context

Only GC-01 to GC-03 have real data; waiting for GC-04 to GC-08 would stall all work. The PoC decision of 2026-11-02 left out the list database and search index, Profile merging, source refresh, entity identity evidence, Fellegi-Sunter and risk scoring; on 2026-11-04 the project asked for all of them now.

## Decision

- **Pending Golden Cases do not block development.** The automatic decisions related to each pending case stay disabled or restricted until it is validated ([validation backlog](../plan/golden-cases.md#validation-backlog)). PEP and Sanction candidates stay in human review until GC-04 and GC-05 are validated.
- **Deferred capabilities are built without changing any release control:**
  - **List database and retrieval**: `list_profile` keeps every captured Profile version with its SHA-256; `Core/Retrieval/ProfileIndex` searches by exact alias, word BM25 and character n-grams, counted by Profile, through `GET /api/profiles/search`, `GET /api/profiles/{profileId}` and `GET /api/cases/{caseId}/candidates`. Scores rank only ([ADR-0001](0001-identity-adjudication.md)).
  - **Profile merging**: `ProfileMerge` groups Profiles on a shared comparable document key or a recorded "same as" link, never on names; merged Profiles keep their IDs.
  - **Source refresh**: `case_input` stores the files of every revision; a changed Profile re-analyzes each Case whose latest input used it.
  - **Entity evidence**: registration number with jurisdiction, IMO number. Agreement is PROBABLE_MATCH (external REVIEW); no automatic MATCH or NO_MATCH for entities.
  - **Fellegi-Sunter**: `FellegiSunter.Score` with placeholder m and u, outside every result and decision.
  - **Risk scoring**: `RiskScoring` runs only with a bank-approved `RiskPolicy`; none ships, so no pair is scored.

## Consequences

- Pending cases block claims of validated automation in their area, not the design.
- Phase 1 behaviour is unchanged for every Golden Case. Thresholds, m/u, entity rules and risk weights are placeholders until calibrated or approved, each marked in code.
- Case-table candidates remain the input of `POST /api/cases`; index search does not feed a Case automatically.
