# ADR-0006: One .NET API application in four projects

- Status: Accepted, subject to bank technology governance
- Date: 2026-11-02; updated 2026-11-09 (merged former ADR-0007, 0009, 0013)

## Context

The system has six business modules but modest volume and one team. Distributed services would add network, deployment and consistency cost before any measured need. The bank needs a stack with long-term support, a secure supply chain and production support. The PoC must prove adjudication logic and traceability; a UI would test neither. The team wants compile-time layering so decision logic cannot depend on infrastructure.

## Decision

- **One deployable.** The six L2 modules are logical boundaries (folders and namespaces), deployed as one application unless measured capacity or operations justify separation. Only Case and Adjudication Management publishes Case state; other modules return typed results ([write ownership](../feature/system-overview.md#write-ownership)).
- **Stack.** C# on bank-approved .NET LTS (.NET 10 for the PoC), ASP.NET Core, a relational database, Linux containers.
- **API only.** The PoC delivers an API documented with OpenAPI; no UI is built. Sensitive evidence is returned only by authorized Case endpoints.
- **Four projects, one-way dependencies:**

```text
Api ──► Services ──► Core
 │                    ▲
 └────► Infra ────────┘
```

  - **Core**: domain types, normalization, policies, ports; zero package dependencies.
  - **Services**: orchestration and the Case write path.
  - **Infra**: persistence, readers, job queue, web text; implements Core ports.
  - **Api**: composition root, endpoints, background worker.

## Consequences

- Golden Case tests run against Core without database or HTML.
- Case state mutators are `internal` with `InternalsVisibleTo("Aml.Services")`; within Services, `CaseService` stays the only writer by review.
- The stack is subordinate to the bank's supply-chain and production standards; packages, build settings and layout are in the [implementation baseline](../plan/implementation-baseline.md#solution-structure).
