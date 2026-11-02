# ADR-0014: Per-provider XML SQL catalog

- Status: Accepted
- Date: 2026-11-02

## Context

With Dapper ([ADR-0010](0010-replaceable-persistence.md)), SQL would otherwise be scattered as strings across repositories, making it hard to review and to port to another database.

## Decision

Hold all DML in XML catalogs, one complete file per database, selected by file name: `Sql.sqlite.xml`, later `Sql.sqlserver.xml` or `Sql.oracle.xml`. A shared `Sql.xsd` groups statements by module and is validated at startup. DDL stays in DbUp scripts.

## Consequences

- Reviewers read all SQL in one place per database.
- Each catalog duplicates statements; startup fails if any `Sql.*.xml` lacks an id defined in another.
- Concentrated edits may cause merge conflicts; split modules into separate files under the same schema if that happens.
- Schema and startup rules are in [persistence](../feature/persistence.md#sql-catalog).
