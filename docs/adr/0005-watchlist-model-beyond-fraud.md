# ADR-0005: Watchlist model broader than Fraud

- Status: Accepted (supersedes the earlier Fraud-only design); source-type list superseded by [ADR-0016](0016-bank-list-type-vocabulary.md)
- Date: 2026-11-02

## Context

The earlier design handled Fraud only. Real Profiles carry official-list entries and several overlapping risk categories.

## Decision

Support source types (Sanction, PEP, RCA, official wanted lists, other official lists, adverse media, financial crime, tax crime) and risk categories (Fraud, Bribery, Corruption, Forgery, Breach of Trust, Tax Evasion, Drug Trafficking, Other) from the first version.

## Consequences

- `conditionType` holds the source type; `riskCategories` and `legalStage` carry what the source alleges ([data contracts](../feature/data-contracts.md#sourceconditionresult)).
- A "Fraud HIT" is a HIT with `FRAUD` in `riskCategories`, not a separate condition type.
