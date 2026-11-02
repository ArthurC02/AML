# System Overview

> Design baseline 2.0-PoC, 2026-11-02. Validated with GC-01 to GC-03, GC-11 and GC-12; seven scenarios remain in the [validation backlog](../plan/golden-cases.md#validation-backlog).

## Objective

Build a traceable identity adjudication flow for customer and related-party screening against Sanction, PEP/RCA, official-list and adverse-information Profiles.

## Scope

### Included

- structured HTML or controlled source documents (PoC input: supplied HTML files);
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
- automatic identity decisions for organization, vessel and other non-person Profiles: they are retrieved, deduplicated and preserved with their source conditions, but their identity decision is always REVIEW until entity evidence groups (registration number, jurisdiction, IMO number, etc.) are designed and validated;
- LLM-based identity or legal adjudication, and AI-assigned match probabilities ([ADR-0018](../adr/0018-legacy-ai-report-rules-not-adopted.md));
- photo or face comparison;
- unapproved AML scoring weights;
- automatic confirmation for all PEP or Sanction cases;
- any UI;
- the human workflow: reviewer and approver roles, HumanDecision, approvals, close and reopen, and Article 22 dispositions ([review and disposition](review-and-disposition.md) stays as the later design; Golden Case human results are documentation only). PoC decision of 2026-11-02: the PoC proves the system's judgment, evidence and explanation.

## Capability exposure policy

- Automatic NO_MATCH: enabled only for validated hard-conflict rules.
- Automatic MATCH: disabled in Phase 1 unless an approved unique-identifier policy exists.
- PROBABLE_MATCH: high-priority review only.
- PEP and Sanction candidates: human review until dedicated cases are validated.
- Article 22 recommendations: always confirmed by a human; unresolved cases need senior approval.
- Source updates: create a new analysis revision; do not overwrite history.

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
| Case and Adjudication Management | create or retrieve Case by trigger idempotency key; accept AnalysisRevision; pin input and policy versions; persist stage outputs; preserve ImportedDecision, SystemDecision, HumanDecision and FinalDisposition; evaluate DispositionPolicy (Article 22); enforce approval roles; publish an effective result only when completeness and version checks pass | [case management](case-management.md), [review and disposition](review-and-disposition.md) |
| Batch and Work Management | create BatchJob and BatchItem; control concurrency, timeout, retry and checkpoint; isolate failures; preserve job lineage to Case and AnalysisRevision; prevent repeated aliases from causing repeated Profile work | [pipeline, API and batch](pipeline-api-and-batch.md) |
| Extraction and Data Quality | parse supported source formats; preserve raw value, normalized value, precision and evidence location; normalize multilingual names, dates, countries, documents and Taiwan addresses; emit MISSING, UNPARSEABLE, AMBIGUOUS and FIELD_CONFLICT without inventing data | [extraction and normalization](extraction-and-normalization.md) |
| Candidate and Identity Adjudication | generate CandidateHit records; deduplicate into CandidateProfile by source namespace and Profile ID; group correlated evidence; apply temporal/role consistency and hard-conflict policies; produce MATCH, NO_MATCH, REVIEW or PROBABLE_MATCH | [identity adjudication](identity-adjudication.md) |
| Source Screening and Risk Assessment | execute SanctionPolicy, PepPolicy, OfficialListPolicy and AdverseInformationPolicy; combine identity and source condition into ScreeningAssessment; perform AML risk scoring only when an approved policy exists | [sanctions](sanctions-screening.md), [PEP/RCA](pep-rca-screening.md), [watchlist](watchlist-and-adverse-information.md) |
| Explanation and Reporting | assemble facts, inferences, decisions and limitations without re-deciding; display evidence, reason codes, version and completeness; provide audit and reviewer views as API responses | [pipeline, API and batch](pipeline-api-and-batch.md#rest-resources) |

## Write ownership

Only Case and Adjudication Management publishes Case state. Other modules return typed results. This prevents concurrent modules from overwriting each other.

## Deployment

Phase 1 baseline:

- one ASP.NET Core application;
- one relational database: SQLite in the PoC, replaced by Microsoft SQL Server or Oracle ([persistence](persistence.md));
- one durable job table;
- OpenTelemetry traces, metrics and structured logs;
- containerized deployment to approved Kubernetes/OpenShift or server platform.
