# ADR-0020: The system does identification only

- Status: Accepted
- Date: 2026-11-03

## Context

The PoC must prove judgment, evidence and explanation. Reviewer roles, human decisions, approvals, close/reopen and a user interface add scope without testing identification.

## Decision

- The system produces identity evidence, the identity decision, source conditions, the pair result, an advisory Article 22 recommendation and the explanation.
- It does not model reviewer or approver roles, HumanDecision, approvals, FinalDisposition, close/reopen or a user interface. Those happen outside the system.
- Human outcomes recorded in Golden Cases are labels for measuring the system, not behaviour to implement.

## Consequences

- `DispositionPolicy` approval code is removed; Case status follows the analysis only.
- [ADR-0009](0009-api-only.md) (API only) and [ADR-0017](0017-article-22-disposition-policy.md) (advisory recommendation) stay.
- A later phase that adds the human workflow writes a new ADR.
