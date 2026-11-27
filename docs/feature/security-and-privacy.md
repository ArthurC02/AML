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
- Runtime Query receives customer and related-party data and keeps it inside the bank, with one exception: a grounding-search query sends the screened subject's name forms with fixed keywords to the search service (D1, [ADR-0012](../adr/0012-sources-and-web-information.md), [external lookups](#external-lookups)). No other external call carries customer data, and results stay in the Case store.
- No external AI service is used in either half. The models that read attached news run inside the bank: the small ONNX models in the process, and the optional large language model ([three readers](watchlist-and-adverse-information.md#three-readers)) on an Ollama endpoint the bank runs (`StageModels:LlmUrl`). A call to it carries only the attached news text and the Profile's names, as person 1 of a fixed prompt; never a customer or related-party field, and nothing the customer side supplied. The endpoint is inside the bank's network; its URL is configuration, so pointing it at an outside host is a deployment decision to refuse.

## Data classification

The JSON Schemas of the stored records ([published schemas](data-contracts.md#published-schemas)) mark personal data with `x-sensitivity`: `PERSONAL` for names, dates of birth and death, addresses and summary text quoting them; `IDENTIFIER` for document, registration and reference numbers, masked or not, and any field that can hold one (the subject and Profile values of a comparison row). The Case, hit and Profile are returned whole by default, identifier values included ([ADR-0023](../adr/0023-business-api-data-and-workbook.md)). A consumer that passes data on, for example to a model, uses the marks to leave fields out, or asks for `view=summary`: the Case, hit and Profile summaries hold no `IDENTIFIER` field and no compared value, only names ([views](pipeline-api-and-batch.md#views), [retrieval](pipeline-api-and-batch.md#retrieval-and-merges)). The case workbook leaves the system as a file, so it is the summary by default; `view=full` is asked for explicitly, and every workbook is sent with `Cache-Control: no-store`. Each workbook issued is logged with its Case, revision and view; who downloaded it is known once authentication is wired ([case workbook](pipeline-api-and-batch.md#case-workbook)). `subjectRef` is the caller's opaque reference for the screened subject and appears in summaries and search URLs, so it must not be a customer, account or document number.

## Stored inputs

- The input files of every revision (`case_input`) and the `ANALYZE_CASE` job payloads are stored unmasked, so a later re-analysis can run on them; database access is restricted accordingly.
- The list database (`list_profile`) holds list data only: Profile versions with their SHA-256. A Profile import compares that hash with the Profile files in the Cases' stored inputs and queues `REANALYZE_CASE` jobs whose payload carries IDs and a version number, no customer data.
- A received page (`web_page`) holds public data only: its URL, main text and the persons it names. Its `FETCH_PAGE` payload is the page id and URL; no customer field reaches the page, its job or its reading. The text names people, so it is personal data like gathered text ([web text](web-text.md#sources-whose-text-may-be-used)); `GET /api/pages/{pageId}` returns the summary, without text or stated values, unless `view=full` is asked for.

## External lookups

The system's own lookups ([external sources](external-sources.md)) are a received URL (built), the grounding search and the judgment sync (decided, not built); lookups made outside the system for [verification records](review-and-disposition.md#verification-records) follow the same rules:

- **URL guard** (built, `Infra/Web/UrlGuard`): a URL the system fetches is untrusted input, and a request it makes on someone's behalf must never reach the bank's own network (SSRF). It runs before every request, each `robots.txt` request and every redirect hop included:
  - the URL is absolute `http` or `https`, holds no user name or password, uses port 80 or 443 and has at most 2,048 characters (`Core/Pages/PageUrl.Broken`);
  - every address the host resolves to is public, not just one: no loopback (127/8, `::1`), private (10/8, 172.16/12, 192.168/16), link-local (169.254/16, `fe80::/10`, `fec0::/10`), unique-local (`fc00::/7`), carrier-grade NAT (100.64/10), this-network (0/8), multicast, reserved (240/4) or unspecified (`::`) address, nor 192.0.0/24 or 198.18/15. An IPv4 address inside IPv6 (mapped `::ffff:`, NAT64 `64:ff9b::/96`, 6to4 `2002::/16`) is judged as that IPv4 address; Teredo (`2001::/32`), SIIT (`::ffff:0:0/96`), local-use NAT64 (`64:ff9b:1::/48`), the discard prefix (`100::/64`) and the IPv6 documentation prefixes (`2001:db8::/32`, `3fff::/20`) are refused. The IPv4 documentation ranges (192.0.2/24, 198.51.100/24, 203.0.113/24) count as public: they route nowhere, and the tests' stub site uses one. A host written as a number (`2130706433`, `0x7f000001`, `0177.0.0.1`, `127.1`, `[::ffff:a00:1]`) is the address it denotes;
  - the production handler resolves the host again when it connects and connects only to those checked addresses, so a name that changes its answer between check and connect (DNS rebinding) is refused too. It uses no proxy, so the check sees the real destination, and keeps no cookies.

  A refusal is the page problem `URL_NOT_ALLOWED` (the page `REFUSED`), and the refused address is never requested. Not built: the bank's own public ranges as non-public addresses (added to `UrlGuard.IsPublic`), and the rules an egress proxy would need. The fetcher's own limits (one request at a time, per-host state in memory) are in [external sources](external-sources.md#common-path).

- New sources come only from a maintainer-managed domain allowlist: company registry (MOEA), government sites (Judicial Yuan, FSC, Banking Bureau), mainstream media. No forums, blogs, content farms, aggregators or wikis. A search engine is only an entry point; the cited URL must be on the allowlist, and a result snippet is never evidence.
- URLs attached to the Profile or case, and a URL received for screening, are recorded as "attached" and exempt from the allowlist (not from the URL checks against internal addresses); that exemption does not mean the full text was read or that the content supports identity. A newly found URL is never marked as attached.
- Queries use only the name or company name (the customer's under D1) plus fixed keyword lists. Dates of birth, document numbers, addresses, phone numbers, emails, accounts, employers, `subjectRef`, Case ids, ID-like strings and digit runs of four or more never go into a query (adding an employer or a city is a planned extension needing its own decision, [external sources](external-sources.md#grounding-search-source-3)); a blocked query is recorded, not rewritten to get around the block.
- Only the search service receives a query; fetching a page sends no customer data. Query texts are stored with the Case (`search_run`), never in general logs.
- URL present, URL allowed and full text retrieved are recorded separately. A Factiva redirect that does not open is recorded as attached but unread.

## Test data and documentation

Golden Case material records design test scenarios. It is not a legal finding and should not reproduce unnecessary personal identifiers in logs or public documents.
