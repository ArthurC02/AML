# ADR-0005: Bank list types, flag-first mapping and risk categories

- Status: Accepted
- Date: 2026-11-02; updated 2026-11-09 (merged former ADR-0016)

## Context

An early design handled Fraud only, but real Profiles carry official-list entries and several overlapping risk categories. The bank's name-screening rules (legacy `Rule.md`) classify every hit into fixed list types and read the source's PEP, blacklist and adverse-news flags before its category. In the Dow Jones data, Financial Crime and Tax Crime are Special Interest Person (SIP) subcategories, i.e. allegations, not list types.

## Decision

- `conditionType` uses the bank's list types: `SANCTION | PEP | RCA | SIE | OFFICIAL_WANTED_LIST | OTHER_OFFICIAL_LIST | ADVERSE_MEDIA | BANK_INTERNAL_LIST`. A Profile can carry several.
- What the source alleges is carried as `riskCategories` and `legalStage` ([data contracts](../feature/data-contracts.md#sourceconditionresult)). Financial Crime, Tax Crime and the other SIP subcategories are `riskCategories` on an `ADVERSE_MEDIA` condition; a "Fraud HIT" is an `ADVERSE_MEDIA` HIT with `FRAUD` in `riskCategories`.
- Flags decide first; the category mapping is used only when all three flags are empty. `Is RCA? = Yes` always adds RCA. Unmapped categories give `UNKNOWN`, never a guess ([mapping table](../feature/watchlist-and-adverse-information.md#list-type-determination)).

## Consequences

- SIP is not SIE, and RCA is not the Case's related party; both are spelled out in the glossary.
