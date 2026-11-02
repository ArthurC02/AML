# ADR-0007: .NET technology stack

- Status: Accepted, subject to bank technology governance
- Date: 2026-11-02

## Context

The bank needs a stack with long-term support, a secure supply chain and available production support.

## Decision

Use C# on bank-approved .NET LTS (.NET 10 for the PoC) with ASP.NET Core, a relational database and Linux containers.

## Consequences

- The recommendation is subordinate to the bank's secure software supply-chain and production support standards.
- Package list, build settings and supply-chain controls are in the [implementation baseline](../plan/implementation-baseline.md).
