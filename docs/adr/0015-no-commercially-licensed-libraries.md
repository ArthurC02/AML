# ADR-0015: No commercially licensed libraries by default

- Status: Accepted
- Date: 2026-11-02

## Context

MediatR, AutoMapper and FluentAssertions moved to commercial licenses in 2025. In a bank, each commercial dependency needs procurement and legal review.

## Decision

Do not use them. Use plain handler classes, hand-written mapping and xUnit `Assert`. Prefer built-in .NET capabilities (logging, validation, health checks, OpenAPI) before adding packages.

## Consequences

- Slightly more hand-written code in exchange for no license exposure.
- Any new package goes through the [implementation baseline](../plan/implementation-baseline.md#packages) list.
