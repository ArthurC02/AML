# ADR-0016: Bank list-type vocabulary and flag-first mapping

- Status: Accepted (supersedes the source-type list of [ADR-0005](0005-watchlist-model-beyond-fraud.md))
- Date: 2026-11-02

## Context

The bank's existing name-screening rules (legacy `Rule.md`) classify every hit into a fixed set of list types and read the source's PEP, blacklist and adverse-news flags before its category. ADR-0005 used Financial Crime and Tax Crime as source types, but in the Dow Jones data these are Special Interest Person (SIP) subcategories, i.e. allegations, not list types. The two vocabularies disagreed.

## Decision

- `conditionType` uses the bank's list types: `SANCTION | PEP | RCA | SIE | OFFICIAL_WANTED_LIST | OTHER_OFFICIAL_LIST | ADVERSE_MEDIA | BANK_INTERNAL_LIST`. A Profile can carry several.
- Financial Crime, Tax Crime and the other SIP crime subcategories become `riskCategories` on an `ADVERSE_MEDIA` condition.
- Flags decide first; the category mapping is used only when all three flags are empty. `Is RCA? = Yes` always adds RCA. Unmapped categories produce `UNKNOWN`, never a guess ([mapping table](../feature/watchlist-and-adverse-information.md#list-type-determination)).

## Consequences

- ADR-0005's risk-category principle stands; only its source-type list is replaced.
- A "Fraud HIT" is still an `ADVERSE_MEDIA` HIT with `FRAUD` in `riskCategories`.
- SIP is not SIE, and RCA is not the Case's related party; both are spelled out in the glossary.
