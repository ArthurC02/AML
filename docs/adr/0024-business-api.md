# ADR-0024: Business API

- Status: Accepted
- Date: 2026-11-06

## Context

[ADR-0022](0022-layered-api-views.md) shaped the API for agents that call its operations as tools: summaries by default to save context, operation descriptions that steer the next call, and tags by kind of call (`read`, `write`, `operations`). The API is designed instead for the bank's business systems and reviewers' tools that integrate with it ([ADR-0009](0009-api-only.md)). They read whole business objects, look operations up by business area, and need the description of what an operation does, not which one to call next.

## Decision

- **Full objects by default.** `GET /api/cases/{caseId}`, `GET …/hits/{hit}` and `GET /api/profiles/{profileId}` return the full object: the Case with the full record of its effective revision (`CaseDetail`), the hit explanation, and the Profile as read. `view=summary` returns the summary views: pure functions over the stored records, without identifier values and, of personal data, only names. A summary's links lead to other summaries, except the links named for the full shape (`detail`, `full`, `revision`). The writes (`POST /api/cases`, `POST …/analyses`) return the Case summary with its links.
- **Tags by business area.** Every operation has one tag: `Cases`, `Batches`, `Profiles` or `Reference data` (codes and schemas).
- **Business descriptions.** An operation's description says what it does and the business rules that apply to it; it does not direct the caller to other operations. `operationId` stays unique and stable as the client-generation name.
- **Kept from ADR-0022.** RFC 9457 problems with the code-catalog `type`, `errors[]` and `fix`; paging by key with `nextPageToken`; `top` and `truncated` on candidate lists; contract snapshots of the OpenAPI document and every published schema.

## Consequences

- Supersedes [ADR-0022](0022-layered-api-views.md).
- Amends [ADR-0023](0023-ai-ready-data.md): its first row's consumers are the business systems that call the API, with the same conditions and checks (typed records with published JSON Schemas, every field described, every code explained).
- A default response can hold identifier values; a consumer that passes data on asks for `view=summary` or leaves out the fields marked `x-sensitivity` ([data classification](../feature/security-and-privacy.md#data-classification)).
- The OpenAPI response examples are the GC-03 summaries, published as the `view=summary` example of `getCase` and `getHit` and as the example of `createCase`; the full views have none, since they hold identifier values.
- No view records a human decision ([ADR-0020](0020-identification-only.md)).
