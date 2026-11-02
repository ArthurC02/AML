# ADR-0010: Replaceable persistence

- Status: Accepted
- Date: 2026-11-02

## Context

The PoC needs a database that runs anywhere with no setup. Production will use Microsoft SQL Server or Oracle. Most records are append-only (revisions, evidence, decisions), and the job queue needs precise atomic SQL. Bank reviewers prefer SQL they can read directly.

## Decision

- Use SQLite for the PoC; design for replacement by SQL Server or Oracle from day one.
- Use Dapper for data access and DbUp for schema scripts. EF Core is not used.
- Follow the portability rules, type mapping and dialect seam in [persistence](../feature/persistence.md).

## Consequences

- Switching databases means adding a dialect class, a `Sql.<provider>.xml` catalog ([ADR-0014](0014-per-provider-xml-sql-catalog.md)) and DDL scripts; Core and Services do not change.
- The repository contract test suite is the acceptance criterion for a new database.
- Oracle pitfalls (`''` as NULL, `:` parameters, no `AS` on table aliases, identifier limits) only surface on Oracle; the rules reduce rework but do not eliminate it.
- No automatic migrations or change tracking; mapping is written by hand.
