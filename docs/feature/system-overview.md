# System Overview

> Validated with GC-01 to GC-03, GC-11 and GC-12; seven scenarios remain in the [validation backlog](../plan/golden-cases.md#validation-backlog).

## Objective

Build a traceable identity adjudication flow for customer and related-party screening against Sanction, PEP/RCA, official-list and adverse-information Profiles.

## Scope

### Included

- structured HTML or controlled source documents (PoC input: the supplied `.txt` exports and HTML pages);
- multilingual names, partial dates, nationality, document and address evidence;
- candidate retrieval and Profile-level deduplication;
- MATCH, NO_MATCH, REVIEW and internal PROBABLE_MATCH;
- source-condition HIT, NOT_HIT and UNKNOWN;
- the explanation of every system decision: evidence, comparison table, reason codes and a summary restating recorded facts;
- batch execution, retry, idempotency and analysis revision;
- API with OpenAPI / Swagger UI ([ADR-0009](../adr/0009-api-only.md)).

### Excluded for Phase 1

- unrestricted web search;
- OCR where structured HTML is available;
- universal news event extraction;
- automatic identity decisions for organization, vessel and other non-person Profiles: they are retrieved, deduplicated and preserved with their source conditions; entity identifiers (registration number with jurisdiction, IMO number) can raise review priority, but the external result stays REVIEW until the entity rules are validated with an entity Golden Case;
- LLM-based identity or legal adjudication, and AI-assigned match probabilities ([ADR-0018](../adr/0018-legacy-ai-report-rules-not-adopted.md));
- photo or face comparison;
- unapproved AML scoring weights;
- automatic confirmation for all PEP or Sanction cases;
- any UI;
- the human workflow: reviewer and approver roles, human decisions, approvals, final disposition, close and reopen. The system does identification only ([ADR-0020](../adr/0020-identification-only.md)); Golden Case human results are labels for measurement.

## Capability exposure policy

- Automatic NO_MATCH: enabled only for validated hard-conflict rules.
- Automatic MATCH: disabled in Phase 1 unless an approved unique-identifier policy exists.
- PROBABLE_MATCH: high-priority review only.
- PEP and Sanction candidates: human review until dedicated cases are validated.
- Article 22 recommendations: advisory only; the final selection happens outside the system.
- Source updates: create a new analysis revision; do not overwrite history.

## Operating architecture

Rules and algorithms only, no LLM. It has two halves split by personal data.

- **Data Processing** handles lists and public data (official lists for Sanction/PEP/RCA, public news and court rulings, other public sources). It holds no customer personal data and refreshes on a schedule or on an event.
- **Runtime Query** analyzes and adjudicates one Case. Customer personal data enters here and stays inside the bank ([security and privacy](security-and-privacy.md#personal-data-boundary)).

| Step | Half | Content | Module and spec | PoC status |
|---|---|---|---|---|
| 1 Acquisition and pre-processing | Data Processing | fetch files or data (PDF / HTML / API), clean and convert formats, parse structure (person, event, role, legal status) | Extraction and Data Quality ([extraction](extraction-and-normalization.md#sources-and-refresh)) | `.txt` export reader (strict UTF-8, else CP950 / Big5 with `ENCODING_FALLBACK`); HTML pages converted to the export layout (encoding from BOM or meta charset, form-control values, hidden elements skipped), with labels assumed until HTML samples arrive; no PDF or API adapter |
| 2 Normalization | Data Processing | text (case, full/half width, Traditional/Simplified, romanization), date precision, fields (country, document, address) | Extraction and Data Quality ([extraction](extraction-and-normalization.md#name-normalization)) | built; Simplified to Traditional by the OpenCC one-to-one table plus a controlled variant list; romanization by the MOFA table (Hanyu Pinyin, Wade-Giles, Tongyong) plus controlled passport spellings, and Chinese characters by Unihan readings ([third-party data](../plan/implementation-baseline.md#third-party-data)); countries to ISO 3166-1; ROC years only with an explicit 民國 |
| 3 List index and Profile | Data Processing | list database of structured Profiles, search index (BM25 / n-gram / alias), Profile deduplication (merge the same person) | Candidate and Identity Adjudication ([identity](identity-adjudication.md#retrieval-modes)) | built ([ADR-0021](../adr/0021-deferred-capabilities-built-behind-controls.md)): `list_profile` versions with the SHA-256 of the export text (an HTML page and its `.txt` export are one version); exact-alias / BM25 / n-gram / reading-key index, the reading key (shared Pinyin) giving cross-script retrieval between Chinese characters and romanizations; merge on a shared comparable document or a recorded link, refused when DOBs or genders contradict; a changed Profile queues `REANALYZE_CASE` jobs for the Cases that used it |
| 4 Case input | Runtime Query | customer data (name, DOB, document, address, occupation, related parties) and case settings (chosen sources, query conditions) | Case and Adjudication Management ([pipeline](pipeline-api-and-batch.md#rest-resources)) | `POST /api/cases` and `/api/batches`; no case settings: every source in the input is used |
| 5 Candidate retrieval and deduplication | Runtime Query | name search (exact / BM25 / n-gram), Top N candidate list, merge into one Profile, fetch candidate data (named or automatic) | Candidate and Identity Adjudication ([identity](identity-adjudication.md#profile-level-deduplication)) | case table (CONTROLLED_SET) feeds `POST /api/cases`; INDEX_SEARCH (exact alias matches first and never cut, a cut list marked `truncated`), DIRECT_REFERENCE and PREVIOUS_LINK via `GET /api/profiles/search`, `GET /api/profiles/{profileId}` and `GET /api/cases/{caseId}/candidates`; Profile-level deduplication built |
| 6 Field comparison and evidence | Runtime Query | name (Jaro-Winkler / Levenshtein), DOB (precision / conflict), document (type / issuer / number), address (structured / Jaccard), occupation (job / company), related parties (spouse / parents / related companies) | Candidate and Identity Adjudication ([identity](identity-adjudication.md#comparison-measures)) | all measures built; employment and relatives have no subject-side data in the bank export yet (an occupation alone is not compared with Profile roles) |
| 7 Hard conflict and identity decision | Runtime Query | hard-conflict rules (DOB / document / event time), combined evidence decision (same person / different person / cannot tell) | Candidate and Identity Adjudication ([identity](identity-adjudication.md#hard-conflict-gate)) | built |
| 8 Policy rules and recommendation | Runtime Query | Article 22, the 65% ruling, list conditions; system recommendation (on list / not on list / review) | Source Screening and Risk Assessment; DispositionPolicy ([disposition recommendation](review-and-disposition.md)) | list conditions, combination matrix and advisory Article 22 (①②③) built; ② extracts UNRELATED only; the 65% ruling is flagged (`ART22_RULING_20261014_MAY_APPLY`), not applied, while it is an [open question](../plan/open-questions.md#article-22); risk scoring runs only under an approved policy |
| 9 Report | Runtime Query | traceable report, formal record | Explanation and Reporting; Case management | Case summary, hit views and revision records built |

## Layers

- **L1 Data Integration**: acquire source data, preserve snapshots, technical formats and indexes.
- **L2 Business Capability**: extract business fields, adjudicate identity, evaluate source policy and manage Case decisions.
- **L3 API**: authenticate, validate request, route Manage/Analyze/Explain operations and return contracts.

## L2 modules

A modular monolith ([ADR-0006](../adr/0006-modular-monolith.md)); the modules are folders and namespaces inside the four projects of [ADR-0013](../adr/0013-four-project-solution.md).

```text
AML Screening Application
├── Case and Adjudication Management
├── Batch and Work Management
├── Extraction and Data Quality
├── Candidate and Identity Adjudication
├── Source Screening and Risk Assessment
└── Explanation and Reporting
```

| Module | Responsibilities | Spec |
|---|---|---|
| Case and Adjudication Management | create or retrieve Case by trigger idempotency key; accept AnalysisRevision; pin input and policy versions; persist stage outputs; preserve ImportedDecision and SystemDecision; publish an effective result only when completeness and version checks pass | [case management](case-management.md) |
| Batch and Work Management | create BatchJob and BatchItem; control concurrency, timeout, retry and checkpoint; isolate failures; preserve job lineage to Case and AnalysisRevision; prevent repeated aliases from causing repeated Profile work | [pipeline, API and batch](pipeline-api-and-batch.md) |
| Extraction and Data Quality | parse supported source formats; preserve raw value, normalized value, precision and evidence location; normalize multilingual names, dates, countries, documents and Taiwan addresses; emit MISSING, UNPARSEABLE, AMBIGUOUS and FIELD_CONFLICT without inventing data | [extraction and normalization](extraction-and-normalization.md) |
| Candidate and Identity Adjudication | generate CandidateHit records; deduplicate into CandidateProfile by source namespace and Profile ID; group correlated evidence; apply temporal/role consistency and hard-conflict policies; produce MATCH, NO_MATCH, REVIEW or PROBABLE_MATCH | [identity adjudication](identity-adjudication.md) |
| Source Screening and Risk Assessment | execute SanctionPolicy, PepPolicy, OfficialListPolicy and AdverseInformationPolicy; combine identity and source condition into ScreeningAssessment; evaluate DispositionPolicy (Article 22, advisory); perform AML risk scoring only when an approved policy exists | [sanctions](sanctions-screening.md), [PEP/RCA](pep-rca-screening.md), [watchlist](watchlist-and-adverse-information.md), [disposition recommendation](review-and-disposition.md) |
| Explanation and Reporting | assemble facts, inferences, decisions and limitations without re-deciding; display evidence, reason codes, version and completeness; provide the explanation and audit view as API responses | [pipeline, API and batch](pipeline-api-and-batch.md#rest-resources) |

## Write ownership

Only Case and Adjudication Management publishes Case state. Other modules return typed results. This prevents concurrent modules from overwriting each other.

## Deployment

Phase 1 baseline:

- one ASP.NET Core application;
- one relational database: SQLite in the PoC, replaced by Microsoft SQL Server or Oracle ([persistence](persistence.md));
- one durable job table;
- OpenTelemetry traces, metrics and structured logs;
- containerized deployment to approved Kubernetes/OpenShift or server platform.
