# ADR-0004: Identity and source conditions remain independent

- Status: Accepted
- Date: 2026-11-02

## Context

"Does this Profile carry a Fraud/Sanction/PEP condition?" and "Is this Profile our customer?" are different questions. Merging them lets a strong source condition push identity, or a non-match erase a source fact.

## Decision

Identity decisions and source-condition results are computed separately and combined only at pair level. A Profile can be Fraud HIT while identity remains REVIEW, and can be identity NO_MATCH while its own source condition remains HIT.

## Consequences

- One canonical [combination matrix](../feature/data-contracts.md#combination-matrix) produces the pair result; Sanction, PEP/RCA and watchlist screening all apply it unchanged.
- The same IdentityComparison is reused across classifications when customer, Profile and identity-policy versions are identical.
