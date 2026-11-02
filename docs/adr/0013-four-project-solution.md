# ADR-0013: Four-project solution

- Status: Accepted
- Date: 2026-11-02

## Context

[ADR-0006](0006-modular-monolith.md) keeps one deployable. The team wants compile-time layering so decision logic cannot depend on infrastructure.

## Decision

Split `src` into four projects with one-way dependencies:

```text
Api ──► Services ──► Core
 │                    ▲
 └────► Infra ────────┘
```

- **Core**: domain types, normalization, policies, ports; zero package dependencies.
- **Services**: orchestration and the Case write path.
- **Infra**: persistence, HTML parsing, job queue; implements Core ports.
- **Api**: composition root, controllers, background worker.

## Consequences

- Golden Case tests run against Core without database or HTML.
- Case state mutators are `internal` with `InternalsVisibleTo("Aml.Services")`; within Services, `CaseService` stays the only writer by review.
- Layout details are in the [implementation baseline](../plan/implementation-baseline.md#solution-structure).
