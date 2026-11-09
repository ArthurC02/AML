# Architecture Decision Records

Each record holds the decisions in force on one subject: context, decision and consequences. When a decision changes, its record is edited in place and its date line says when and what; git history keeps the earlier text. A new subject that fits an existing record goes into it, so the set stays small. A part decided but not built yet is named in the status line.

| ADR | Title | Status |
|---|---|---|
| [0001](0001-identity-adjudication.md) | Evidence-driven identity adjudication | Accepted |
| [0004](0004-identification-only.md) | Identification only: identity, source conditions and an advisory Article 22 | Accepted |
| [0005](0005-list-types-and-risk-categories.md) | Bank list types, flag-first mapping and risk categories | Accepted |
| [0006](0006-one-dotnet-api-application.md) | One .NET API application in four projects | Accepted, subject to governance |
| [0008](0008-validation-and-deferred-capabilities.md) | Validation backlog and deferred capabilities behind the Phase 1 controls | Accepted |
| [0010](0010-persistence-and-batch.md) | Replaceable persistence and a database job table | Accepted |
| [0012](0012-sources-and-web-information.md) | Sources, web information and the news digest | Accepted; web search and web evidence not built yet |
| [0015](0015-licences.md) | Clean licences for libraries, models and text | Accepted |
| [0023](0023-business-api-data-and-workbook.md) | Business API, AI-ready data and the case workbook | Accepted; one fact per sheet not built yet |

The numbers are kept from the records they absorbed (2026-11-09). A former number maps to its record: 0002, 0003, 0018, 0019 → 0001; 0017, 0020 → 0004; 0016 → 0005; 0007, 0009, 0013 → 0006; 0021 → 0008; 0011, 0014, 0026 → 0010; 0028 → 0012; 0029 → 0015; 0022, 0024, 0025, 0027 → 0023.

Template:

```markdown
# ADR-NNNN: Title

- Status: Proposed | Accepted
- Date: YYYY-MM-DD; updated YYYY-MM-DD (what changed)

## Context
## Decision
## Consequences
```
