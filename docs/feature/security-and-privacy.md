# Security and Privacy

> Cross-cutting. Design baseline 2.0-PoC, 2026-11-02.

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

## External lookups

The system performs no web search in Phase 1. Lookups recorded as [verification records](review-and-disposition.md#verification-records) follow these rules:

- New sources come only from a maintainer-managed domain allowlist: company registry (MOEA), government sites (Judicial Yuan, FSC, Banking Bureau), mainstream media. No forums, blogs, content farms, aggregators or wikis. A search engine is only an entry point; the cited URL must be on the allowlist.
- URLs attached to the Profile or case are recorded as "attached" and exempt from the allowlist; that exemption does not mean the full text was read or that the content supports identity. A newly found URL is never marked as attached.
- Queries use only the name or company name plus case keywords. Document numbers, addresses, phone numbers, ID-like strings and long digit runs never go into a query; a blocked query is recorded, not rewritten to get around the block.
- URL present, URL allowed and full text retrieved are recorded separately. A Factiva redirect that does not open is recorded as attached but unread.

## Test data and documentation

Golden Case material records design test scenarios. It is not a legal finding and should not reproduce unnecessary personal identifiers in logs or public documents.
