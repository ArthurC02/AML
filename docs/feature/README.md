# Feature Specifications

What the system does, per module and cross-cutting concern.

| Spec | Covers |
|---|---|
| [System overview](system-overview.md) | objective, scope, operating architecture (9 steps), capability exposure, layers, modules, write ownership, deployment |
| [Case management](case-management.md) | lifecycle, transitions, versions, case-level result, decision records |
| [Disposition recommendation](review-and-disposition.md) | Article 22 mapping, advisory DispositionPolicy, verification records |
| [Data contracts](data-contracts.md) | identifiers, snapshots, evidence, comparisons, source conditions, combination matrix, field wrapper |
| [Extraction and normalization](extraction-and-normalization.md) | HTML processing, names, dates, countries, documents, Taiwan addresses, quality findings |
| [Identity adjudication](identity-adjudication.md) | retrieval, deduplication, evidence groups, hard conflict gate, decision policy, reason codes |
| [Sanctions screening](sanctions-screening.md) | sanction source model and policy |
| [PEP and RCA screening](pep-rca-screening.md) | PEP categories and policy |
| [Watchlist and adverse information](watchlist-and-adverse-information.md) | source types, risk categories, legal stages |
| [Pipeline, API and batch](pipeline-api-and-batch.md) | AnalyzeCase flow, services, REST resources, job table, idempotency |
| [Persistence](persistence.md) | portability rules, type mapping, job claim, SQL catalog, contract tests |
| [Security and privacy](security-and-privacy.md) | controls and test-data handling |
| [Glossary](glossary.md) | terms, states, policy families |
