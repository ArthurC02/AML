# Security and Privacy

> Cross-cutting.

## Controls

- least-privilege service and database identities;
- TLS and approved secret management;
- OIDC/OAuth 2.0 authentication with policy-based authorization;
- immutable or tamper-evident audit events;
- no full document number, address, phone, email, DOB or raw Profile Notes in general application logs;
- sensitive evidence returned only by authorized Case API endpoints;
- retention, deletion and access policies aligned with bank compliance requirements.

## Personal data boundary

The [operating architecture](system-overview.md#operating-architecture) separates the two halves by personal data:

- Data Processing (list acquisition, normalization, index) handles list and public data only. No customer data is sent to it, stored with it or used to query outside sources.
- Runtime Query receives customer and related-party data and keeps it inside the bank: no external calls carry it, and results stay in the Case store.
- No LLM or external AI service is used in either half.

## Data classification

The JSON Schemas of the stored records ([published schemas](data-contracts.md#published-schemas)) mark personal data with `x-sensitivity`: `PERSONAL` for names, dates of birth and death, addresses and summary text quoting them; `IDENTIFIER` for document, registration and reference numbers, masked or not, and any field that can hold one (the subject and Profile values of a comparison row). The Case, hit and Profile are returned whole by default, identifier values included ([ADR-0024](../adr/0024-business-api.md)). A consumer that passes data on, for example to a model, uses the marks to leave fields out, or asks for `view=summary`: the Case, hit and Profile summaries hold no `IDENTIFIER` field and no compared value, only names ([views](pipeline-api-and-batch.md#views), [retrieval](pipeline-api-and-batch.md#retrieval-and-merges)). `subjectRef` is the caller's opaque reference for the screened subject and appears in summaries and search URLs, so it must not be a customer, account or document number.

## Stored inputs

- The input files of every revision (`case_input`) and the `ANALYZE_CASE` job payloads are stored unmasked, so a later re-analysis can run on them; database access is restricted accordingly.
- The list database (`list_profile`) holds list data only: Profile versions with their SHA-256. A Profile import compares that hash with the Profile files in the Cases' stored inputs and queues `REANALYZE_CASE` jobs whose payload carries IDs and a version number, no customer data.

## External lookups

The system performs no web search or external lookup in Phase 1. Lookups made outside the system for [verification records](review-and-disposition.md#verification-records) follow these rules:

- New sources come only from a maintainer-managed domain allowlist: company registry (MOEA), government sites (Judicial Yuan, FSC, Banking Bureau), mainstream media. No forums, blogs, content farms, aggregators or wikis. A search engine is only an entry point; the cited URL must be on the allowlist.
- URLs attached to the Profile or case are recorded as "attached" and exempt from the allowlist; that exemption does not mean the full text was read or that the content supports identity. A newly found URL is never marked as attached.
- Queries use only the name or company name plus case keywords. Document numbers, addresses, phone numbers, ID-like strings and long digit runs never go into a query; a blocked query is recorded, not rewritten to get around the block.
- URL present, URL allowed and full text retrieved are recorded separately. A Factiva redirect that does not open is recorded as attached but unread.

## Test data and documentation

Golden Case material records design test scenarios. It is not a legal finding and should not reproduce unnecessary personal identifiers in logs or public documents.
