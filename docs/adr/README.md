# Architecture Decision Records

Each record states context, decision and consequences. Records are never edited after acceptance except for status; a changed decision gets a new record that supersedes the old one.

| ADR | Title | Status |
|---|---|---|
| [0001](0001-evidence-driven-adjudication.md) | Evidence-driven adjudication | Accepted |
| [0002](0002-three-stage-identity-policy.md) | Three-stage identity policy | Accepted |
| [0003](0003-internal-probable-match.md) | Internal PROBABLE_MATCH | Accepted |
| [0004](0004-independent-identity-and-source-condition.md) | Identity and source conditions remain independent | Accepted |
| [0005](0005-watchlist-model-beyond-fraud.md) | Watchlist model broader than Fraud | Accepted; source types superseded by 0016 |
| [0006](0006-modular-monolith.md) | Modular monolith | Accepted |
| [0007](0007-dotnet-technology-stack.md) | .NET technology stack | Accepted, subject to governance |
| [0008](0008-validation-backlog-non-blocking.md) | Validation backlog does not block development | Accepted |
| [0009](0009-api-only.md) | API only | Accepted |
| [0010](0010-replaceable-persistence.md) | Replaceable persistence | Accepted |
| [0011](0011-database-job-table-for-batch.md) | Database job table for batch | Accepted |
| [0012](0012-source-acquisition-behind-ports.md) | Source acquisition behind ports | Accepted |
| [0013](0013-four-project-solution.md) | Four-project solution | Accepted |
| [0014](0014-per-provider-xml-sql-catalog.md) | Per-provider XML SQL catalog | Accepted |
| [0015](0015-no-commercially-licensed-libraries.md) | No commercially licensed libraries by default | Accepted |
| [0016](0016-bank-list-type-vocabulary.md) | Bank list-type vocabulary and flag-first mapping | Accepted |
| [0017](0017-article-22-disposition-policy.md) | Article 22 as a disposition policy | Accepted |
| [0018](0018-legacy-ai-report-rules-not-adopted.md) | Legacy AI-report rules not adopted | Accepted |
| [0019](0019-strength-decision-table-before-fellegi-sunter.md) | Evidence-strength decision table before Fellegi-Sunter | Accepted |

Template:

```markdown
# ADR-NNNN: Title

- Status: Proposed | Accepted | Superseded by ADR-NNNN
- Date: YYYY-MM-DD

## Context
## Decision
## Consequences
```
