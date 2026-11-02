# ADR-0006: Modular monolith

- Status: Accepted
- Date: 2026-11-02

## Context

The system has six business modules but modest volume and one team. Distributed services would add network, deployment and consistency cost before any measured need.

## Decision

The six L2 modules are logical boundaries. Phase 1 deploys them as one application unless measured capacity or operational requirements justify separation.

## Consequences

- Only Case and Adjudication Management publishes Case state; other modules return typed results ([system overview](../feature/system-overview.md#write-ownership)).
- Module boundaries are folders and namespaces inside the projects of [ADR-0013](0013-four-project-solution.md).
