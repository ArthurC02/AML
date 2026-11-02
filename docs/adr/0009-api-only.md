# ADR-0009: API only

- Status: Accepted
- Date: 2026-11-02

## Context

The PoC must prove adjudication logic and traceability. A UI would add scope without testing either.

## Decision

The PoC delivers an API documented with OpenAPI and Swagger UI. No UI is built.

## Consequences

- Reviewer and audit views are API responses, mainly `GET /api/cases/{caseId}/explanation` ([pipeline, API and batch](../feature/pipeline-api-and-batch.md#rest-resources)).
- Sensitive evidence is returned only by authorized Case API endpoints.
