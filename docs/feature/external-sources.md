# External Sources

> Modules: Extraction and Data Quality, Candidate and Identity Adjudication. Decided 2026-11-26 ([ADR-0012](../adr/0012-sources-and-web-information.md)): the three sources, their build order, and that the grounding search sends the customer's name forms (and only those). Everything else here (the APIs, error and problem codes, limits, settings, namespaces, stored records) is a specified proposal, not approved and not built. `Infra/Web` (`PoliteFetcher`, `MainText`, `PdfText`, `RepeatedLines`) exists and no product path calls it yet ([web text](web-text.md)).

Besides the Sanction / PEP list Profiles, the customer is matched against three external sources:

| # | Source | Starts from | Half ([operating architecture](system-overview.md#operating-architecture)) | Build order |
|---|---|---|---|---|
| 2 | a news page whose URL is received | the URL | Data Processing (the page); the Case links it | first |
| 3 | grounding search | the customer's name and keyword lists | Runtime Query (the query holds the customer's name) | second, on the page path of 2 |
| 1 | court judgments from the Judicial Yuan open data | a periodic fetch | Data Processing | last |

Each ends as source Profiles matched like a list Profile, so identity, source condition, [scoring rule](scoring-rule.md) and explanation do not change per source.

## Common path

```text
fetch ─► text ─► persons (attributes, events, stage; with location) ─► source document + source Profiles (namespace)
      ─► candidates for a Case ─► identity score ─► condition score ─► pair result ─► explanation [S#]
```

1. **Fetch.** `PoliteFetcher` has today: the `http` / `https` scheme check, robots.txt, the per-site delay, at most 5 redirects each checked against its own site's robots.txt, HTML and PDF only, 10 MB; on any failure it returns null, without a reason. **New, to be added:** a URL guard before every request and on every redirect hop (no user name or password, port 80 or 443 only, a host that resolves to no loopback, private, link-local or bank-internal address); dropping the fragment and the tracking parameters `utm_*`; the canonical URL (lower-case host, no default port) as the page's key; and a failure reason for each refusal, reported as the page problem codes [below](#received-url-source-2).
2. **Text.** HTML by `MainText` (title, main text, one line per block); PDF by `PdfText`, then `PdfText.Unwrap`; a site's repeated letterhead by `RepeatedLines` once a site has enough documents. A page with no main text is refused (`NO_MAIN_TEXT`). Each line keeps its offset, so every fact has a location.
3. **Persons.** The rules of the [news digest](watchlist-and-adverse-information.md#news-digest) read every person the text names, not only one: each mention, the identifiers next to it (gender, age, birth date, masked ID number, nationality, residence, occupation, employer), the role, the events with their dates, agencies, offences and penalties, and the legal stage by the [three readers](watchlist-and-adverse-information.md#three-readers). A rule reading is `RULE_READ`; a model reading is `MODEL_READ` and is an input the scoring rule weighs (proposed factor 0 for identity values), never a verdict ([scoring rule](scoring-rule.md#invariants)).
4. **Source document and source Profiles.** The page is one source document: canonical URL, final URL, media type, publisher (the host's allowlist entry), title, publication date, kind (`JUDGMENT`, `PRESS_RELEASE`, `NEWS`), fetch time, SHA-256 of the body and of the text. Each person read becomes one source Profile, `SourceProfile` like a list Profile: names, stated birth dates and gender, citizenship, roles, the events as notes, findings. Its key names the namespace:

   | Namespace | Key | From |
   |---|---|---|
   | `DJ:` | the list vendor's Profile ID | list exports (today the only one built; `ProfileKeys` is hard-wired to it) |
   | `WEB:` | first 16 hex of SHA-256 of the canonical URL, `-`, person number in the document | a received URL or a search result |
   | `JY:` | the judgment's id on the open-data platform, `-`, person number | a judgment |

   A new version of the same page (another text hash) is a new version of its source Profiles, as with list Profiles.
5. **Candidates.** A source person is a candidate for a Case when one of its names matches the screened subject's: EXACT after normalization, a PARTIAL name above the name threshold, or the reading key ([name comparison](identity-adjudication.md#name-comparison)). Retrieval mode `SOURCE_PAGE` (a received URL), `SOURCE_SEARCH` (search) or `INDEX_SEARCH` over the judgment index. An anonymised form linked to no full name (王男, 王姓男子) is a candidate only as `PROFILE_SURNAME`, NAME `PARTIAL`, with the finding `SURNAME_ONLY`.
6. **Identity, condition, score.** `IdentityDecisionPolicy` (then the scoring rule) compares the subject with the source Profile. Every value of a source Profile is stated in a source, so its agreement is at most MEDIUM times the source's reliability, and until a Golden Case validates web evidence a source Profile cannot close a pair as NO_MATCH or rise above PROBABLE_MATCH (ADR-0012). The condition is ADVERSE_MEDIA with the person's stage and risk categories from the offences; a person who is not a suspect raises none.
7. **Explanation.** The page is `[S#]` with its URL, publisher, date, retrieval time and kind; the news digest of the page is given for the matched person; every item cites its sentence.

### One page, many persons

A page naming several people gives one source Profile per person. Only those whose names match the subject become hits of that Case; the others stay on the document (they may be another customer's hit) and are never assigned the matched person's events: an event belongs to the persons the sentence makes its actor or object, as in the news digest. Two mentions merge into one person only by the digest's rules (an anonymised form linked to exactly one full name); otherwise they stay apart.

### Same event

Reports of the same party, event time and charge are one event, whether they come from two pages, a page and a judgment, or a page and a list Profile's attached news. The event keeps every source; identity facts and the condition stated about it count once, at the most reliable source ([scoring rule](scoring-rule.md#identity-score)). Hits on different namespaces for the same person stay separate hits, linked by the shared event (`SAME_EVENT_AS`), so the Case hit count stays a count of Profiles.

### Redacted names

Published judgments mask names and addresses (王○明, ○○路, A男, 甲). A name with masked characters matches only by its visible characters at the same positions and the same length: NAME `PARTIAL` with `REDACTED_NAME`, never EXACT, so it never meets the exact-prerequisite threshold. A fully masked name (A男, 甲) is no candidate. A masked ID number or address is shown and not compared.

### Licence and use

- Judgments and government press releases are outside copyright (著作權法 第 9 條); ordinary news is not ([web text](web-text.md#sources-whose-text-may-be-used)). For screening, a page is read and its text kept as the Case's evidence; quotes in the explanation are the cited sentences. A page is training text only when it is in web text's table.
- The open-data platform asks that a judgment it removes be deleted: a removed judgment's text and source Profiles are deleted from the source store, and later revisions no longer use it.

### What is stored, for how long

| Record | Holds | Half | Kept |
|---|---|---|---|
| `source_document` | URL, kind, hashes, fetch outcome, text | Data Processing | while used by a Case revision, then per retention ([open question](../plan/open-questions.md#external-sources)) |
| `source_profile` | versions of each source Profile with SHA-256 | Data Processing | as `list_profile` |
| `search_run` | the queries, the results with their allowlist outcome, the pages fetched | Runtime Query, with the Case | as the Case |

A Case revision's lineage names every source document with its text hash and fetch time; a re-analysis uses the stored text, never a refetch.

## Received URL (source 2)

Fetching is slow (robots.txt, a site's `Crawl-delay` up to a minute), so it is a job; screening against a page already read is synchronous, as `POST /api/cases` is today. A received URL counts as attached: it is exempt from the domain allowlist ([external lookups](security-and-privacy.md#external-lookups)), not from the URL checks of step 1.

```text
POST /api/pages                      fetchPage      tag Sources
GET  /api/pages/{pageId}?view=summary|full          getPage
POST /api/cases                      + pageIds      createCase (existing)
POST /api/cases/{caseId}/analyses    + pageIds      (existing)
POST /api/batches                    items + pageIds (existing)
```

- `POST /api/pages` body `{ "url": "…" }`. A canonical URL already read within `Pages:MaxAge` returns `200` with that page; otherwise `202`, `Location: /api/pages/{pageId}`, body `{ pageId, url, status: PENDING, jobId, batchId }`, and a `FETCH_PAGE` job (payload: pageId and URL, no customer data; idempotency key `FETCH_PAGE:{pageId}:{date}`). `pageId` is the `WEB:` hash of the canonical URL.
- `GET /api/pages/{pageId}` returns `{ pageId, url, finalUrl, status, problem, kind, publisher, title, publishedOn, fetchedAt, contentHash, persons[] }`; status `PENDING`, `READ`, `REFUSED` (with `problem`) or `FAILED`. Each person has `profileKey`, names, role, stage with its readers, identifiers (ID numbers masked), events, and mention locations. `view=summary` (default) leaves out identifiers other than names and the text, as the other summaries do; `view=full` adds them and the text.
- Screening: `pageIds` (JSON array, or a repeated multipart field) sits beside `files`. Each `READ` page adds its persons whose names match the subject as candidates (`SOURCE_PAGE`); a page with none adds no hit and is recorded in the revision as read with no match. Files may be absent when `pageIds` names a page, but the Case information and subject files stay required (`INPUT_INCOMPLETE`).
- Job: `FETCH_PAGE` retries a transient failure (5xx, 429, timeout, robots.txt unavailable) under `Batch:MaxAttempts`; a permanent one sets the page `REFUSED` at once and succeeds the job.

Errors, RFC 9457 as elsewhere ([errors](pipeline-api-and-batch.md#errors)):

| Code | Status | When |
|---|---|---|
| `INVALID_REQUEST` | 400 | no URL, not absolute, not http(s), user info, a port other than 80 / 443, over 2,048 characters |
| `URL_NOT_ALLOWED` | 400 | the host resolves to a loopback, private, link-local or bank-internal address |
| `PAGE_NOT_READY` | 409 | `pageIds` names a page still `PENDING`; fix: wait for `READ` |
| `PAGE_UNUSABLE` | 400 | `pageIds` names a page `REFUSED` or `FAILED`; `errors[]` gives each page and its problem |
| `NOT_FOUND` | 404 | an unknown page |

Page problems (`problem`; new codes, to be added to the code catalog with the build; `PoliteFetcher` reports no reason today): `ROBOTS_DISALLOWED`, `ROBOTS_UNAVAILABLE`, `UNSUPPORTED_MEDIA_TYPE`, `TOO_LARGE`, `TOO_MANY_REDIRECTS`, `HTTP_STATUS:<n>`, `NO_MAIN_TEXT`, `PDF_REFUSED`.

## Grounding search (source 3)

The system searches for the customer itself ([ADR-0012](../adr/0012-sources-and-web-information.md), D1) and reads the pages found through the path of source 2.

- **Queries.** From the subject's names only: the name as written, its Traditional form, its romanizations (`Romanization`, at most two spellings), each quoted, combined with one fixed keyword list per query (for example offences: 詐欺, 洗錢, 貪污, 賄賂, 背信; procedure: 起訴, 判決, 通緝, 羈押, 地檢署). At most `Search:MaxQueries` (proposal 4) queries per subject and the first 10 results each, the bank specification's 「搜尋引擎結果前 10 筆」. Nothing else from the customer side goes into a query (no date of birth, identifier, address, phone, email, account, employer, `subjectRef` or Case id); a query that would hold a digit run of four or more is refused and recorded, not rewritten.
- **To be supplemented later.** Adding an employer, a city or another attribute to the queries, to narrow a common name, is a planned extension ([ADR-0012](../adr/0012-sources-and-web-information.md)): not built, and it needs its own decision on what leaves the bank.
- **Port.** `IWebSearch` in Core (`SearchAsync(query) → results[]` with rank, URL, title, snippet); one Infra implementation per provider, chosen by configuration, none built. The provider is [open](../plan/open-questions.md#external-sources): Google's Custom Search JSON API is closed to new customers and ends on 2027-01-01; Google points to Vertex AI Search (at most 50 named sites) or to a request form for full web search. Only the result list is used, never a generated answer.
- **Filtering.** Each result URL is canonicalised and kept only when its host is on the maintainer-managed allowlist (judicial and government sites, mainstream media; no forums, blogs, content farms, aggregators, wikis); results are de-duplicated by canonical URL, and a page already read within `Pages:MaxAge` is reused. Each kept result becomes a `FETCH_PAGE`; the snippet is never evidence.
- **API.** `POST /api/cases/{caseId}/searches` → `202` with a `SEARCH_WEB` job, which runs the queries, fetches the kept pages and then re-analyses the Case with them (`SOURCE_SEARCH`), a new revision; `GET /api/cases/{caseId}/searches/{searchId}` returns the queries, each result with its allowlist outcome and page status. A Case-level batch option follows the same job.
- **Record.** The `search_run` is stored with the Case: query texts, provider, time, results. General logs carry the search id only.

## Court judgments (source 1, outline)

- **Fetch.** The 司法院資料開放平臺 API and monthly archives need an account; a `SYNC_JUDGMENTS` job runs on a schedule (a scheduler only enqueues into the job table, [ADR-0010](../adr/0010-persistence-and-batch.md)).
- **Incremental sync.** A cursor per court and kind (the last period or change list read) is stored; each run reads what is new or changed since, and deletes what the platform has removed.
- **Store and index.** Each judgment is a source document of kind `JUDGMENT` with `JY:` source Profiles from its party lines (被告, with the age, birth date and residence the court states, masked as published) and its holding; the source Profiles enter the list index, so `INDEX_SEARCH` finds them by name inside the bank.
- **Open.** Re-screening existing customers when a new judgment arrives needs a customer-name index in the Runtime Query half; the account, schedule and scope are [open](../plan/open-questions.md#external-sources).
