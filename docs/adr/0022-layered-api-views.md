# ADR-0022: Layered API views for people and agents

- Status: Accepted
- Date: 2026-11-04

## Context

The API is read by reviewers through tools and by agents that call its operations directly ([ADR-0009](0009-api-only.md)). The full revision record is large and holds identifier values, but most questions need only what a Case concluded and why one hit was decided. An agent picks an operation by its name and description, and recovers from an error only if the error says what to send instead.

## Decision

- **Layered views.** A Case is read in layers, each linking to the next: Case summary → hit summary → hit detail → full revision record ([views](../feature/pipeline-api-and-batch.md#views)). The summaries are pure functions over the stored revision: they leave detail out and never add any. They hold no `IDENTIFIER` field and, of the `PERSONAL` fields, only names. The full record leaves nothing out.
- **Problems.** Every 4xx and 5xx is an RFC 9457 problem whose `type` is a code in the code catalog, with `errors[]` and `fix` when the handler knows them ([errors](../feature/pipeline-api-and-batch.md#errors)).
- **Operations as tools.** Every operation has an `operationId` that serves as its tool name, one tag (`read`, `write` or `operations`) and a description saying when to use it and what to call next, and, where it applies, which operation to use instead. The Case list and batch items are paged by key with an opaque `nextPageToken`; search lists are capped by `top` (exact alias matches are never cut), candidate lists by `top`, and both are marked `truncated`.
- **Contract snapshots.** The OpenAPI document and every published JSON Schema are kept as snapshots in `tests/Aml.Tests/E2E/Contracts`; a test fails on any difference, so a contract change is always a reviewed change.

## Consequences

- Views add no conclusion of their own: `wouldChangeIf` names the stage 3 row and identity decision a missing comparison would lead to, never an external result, and no view records a human decision ([ADR-0020](0020-identification-only.md)).
- Reports are API responses, not files ([ADR-0018](0018-legacy-ai-report-rules-not-adopted.md)).
- Changing a route, a field or a description means updating the snapshots in the same commit (`AML_UPDATE_SNAPSHOTS=1`).
