# ADR-0027: The case workbook renders the API

- Status: Accepted
- Date: 2026-11-07

## Context

[ADR-0018](0018-legacy-ai-report-rules-not-adopted.md) left out the legacy four-sheet Excel report: the API was the only output. Reviewers still read, filter and file cases in Excel. Spreadsheet practice for data that is read and filtered (Broman and Woo, *Data Organization in Spreadsheets*, 2018) is one table per sheet, one header row, one value per cell, no merged cells, no meaning carried by colour, and a data dictionary. OWASP's CSV-injection guidance and CWE-1236 warn that text a spreadsheet takes as a formula (`=`, `+`, `-`, `@`) runs when the file is opened. Excel holds at most 32,767 characters per cell, 1,048,576 rows per sheet and 31 characters per sheet name. [ADR-0015](0015-no-commercially-licensed-libraries.md) rules out commercially licensed libraries such as EPPlus.

## Decision

- `GET /api/cases/{caseId}/workbook?view=summary|full&revision=` returns one revision (the effective one by default) as an `.xlsx` workbook. It restates the stored revision and the Case summary and adds no finding of its own; beyond their values it holds only labels and a data dictionary.
- Seven sheets, each one table with a bold, frozen and filterable header row: 說明 (the Case, lineage, quality and a data dictionary of every column), 命中 (one row per hit), 比對, 證據, 來源, 變更 and 代碼 (the catalogued codes the other sheets show). The hit number joins the sheets.
- `view=summary`, the default, follows the summary views: no `IDENTIFIER` field, no compared value or note and, of personal data, only the Profile's main name. `view=full` adds the screened subject, matched names, the summary's internal and external data, compared values, notes, and source names and URLs.
- Text is always a text cell formatted as Text (@), never a formula. Characters XML cannot carry are dropped, and text past 32,767 characters is cut with a note pointing to the API.
- The writer is SpreadsheetML written with the .NET standard library (`System.IO.Compression`, `XmlWriter`), with no new dependency. The same revision gives the same bytes on the same runtime.
- The response carries `Cache-Control: no-store`.

## Consequences

- ADR-0018's Excel row no longer holds: the report it rejected is not revived, but a workbook rendering of the API is offered.
- A workbook is a copy outside the system: access control stops at the download. Each workbook issued is logged with its Case, revision and view; who downloaded it is known only once authentication is wired.
- The four legacy sheets are not reproduced; the seven sheets follow the stored records, so a new field reaches the workbook only by a change to `CaseWorkbook`.
- There is no batch workbook; a batch is read one Case at a time.
