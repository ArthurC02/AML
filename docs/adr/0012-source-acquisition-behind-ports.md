# ADR-0012: Source acquisition behind ports

- Status: Accepted
- Date: 2026-11-02

## Context

PoC input is supplied HTML files. Later the bank may provide direct API integration, or data may need to be crawled.

## Decision

Acquire customer and source data through `ICustomerSource` and `IProfileSource` in Core. The PoC implements only the HTML-file sources in Infra.

## Consequences

- API or crawler acquisition is a new Infra implementation; extraction semantics, normalization and adjudication do not change.
- Normalization rules stay in Core, independent of where the data came from ([extraction and normalization](../feature/extraction-and-normalization.md)).
