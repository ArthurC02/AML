# ADR-0021: Deferred capabilities built behind the Phase 1 controls

- Status: Accepted
- Date: 2026-11-04

## Context

The PoC decision of 2026-11-02 left out the list database and search index, Profile merging across IDs, source refresh, entity identity evidence, Fellegi-Sunter (Gate D, [ADR-0019](0019-strength-decision-table-before-fellegi-sunter.md)) and AML risk scoring. On 2026-11-04 the project asked for all of them to be implemented now.

## Decision

Build them, without changing any Phase 1 release control:

- **List database and retrieval**: `list_profile` keeps every captured Profile version with its SHA-256; `Core/Retrieval/ProfileIndex` searches it by exact alias, word BM25 and character n-grams, counted by Profile. The four retrieval modes are served by `GET /api/profiles/search` (INDEX_SEARCH), `GET /api/profiles/{profileId}` (DIRECT_REFERENCE) and `GET /api/cases/{caseId}/candidates` (CONTROLLED_SET, PREVIOUS_LINK). Scores rank candidates only and are never evidence ([ADR-0001](0001-evidence-driven-adjudication.md)).
- **Profile merging**: `ProfileMerge` groups Profiles on a shared comparable document key or a recorded "same as" link, never on names; merged Profiles keep their IDs.
- **Source refresh**: `case_input` stores the files of every revision; a changed Profile re-analyzes each Case whose latest input used it into a new revision.
- **Entity evidence**: registration number with jurisdiction, IMO number for vessels. Agreement is PROBABLE_MATCH (external REVIEW); there is no automatic MATCH or NO_MATCH for entities.
- **Fellegi-Sunter**: a shadow weight (`FellegiSunter.Score`) with placeholder m and u; it is not in the identity result, the explanation or any decision.
- **Risk scoring**: `RiskScoring` runs only with a bank-approved `RiskPolicy`; none ships, so no pair is scored.

## Consequences

- Phase 1 behaviour is unchanged for every Golden Case; the metrics are unaffected.
- Thresholds, m/u values, entity rules and risk weights are placeholders until calibrated or approved; each is marked in code.
- The case-table candidates (CONTROLLED_SET) remain the input of `POST /api/cases`; index search does not yet feed a Case automatically.
