# ADR-0008: Validation backlog does not block development

- Status: Accepted
- Date: 2026-11-02

## Context

Only GC-01 to GC-03 have real data. Waiting for GC-04 to GC-08 would stall all work.

## Decision

GC-04 to GC-08 remain pending. Core development proceeds; the automatic decisions related to each pending case stay disabled or restricted until it is validated.

## Consequences

- Pending cases block claims of validated automation in their area, not the design ([golden cases](../plan/golden-cases.md#validation-backlog)).
- PEP and Sanction candidates stay in human review until GC-04 and GC-05 are validated.
