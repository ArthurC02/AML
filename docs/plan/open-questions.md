# Open Questions

Questions that need a bank decision. When one is answered, record the answer here and, if it is an architectural choice, add an ADR.

## Data and source

- What are the stable HTML/source schemas and source versions? The HTML conversion assumes the pages use the `.txt` export's labels.
- Are the `.txt` exports (as in GC-11) the production input, or derived from HTML? Both are read today.
- What is the column layout of the candidate table in 案件資訊? Its header has two rows; the reader finds columns by label and expands each parent label over its sub-labels in order (是否符合名單？ → 全不符合, 全符合; Name Property → PEP, 黑名單, 負面新聞), which puts the three flags in the last three columns after Is RCA? (zero-based indexes 9–11). GC-11 and GC-12 show this header with every flag empty; an export with flags set is needed to confirm the expansion.
- Do bank exports write ROC (民國) dates in any form other than an explicit 民國 prefix? Only 民國 dates are converted; a bare two- or three-digit year (62/6/12) is UNPARSEABLE.
- Does the Profile export carry dated roles (`職位`), keywords (`關鍵字`) and official-list details (`名單資訊`) as structured fields? The synthetic GC-01 to GC-03 samples assume so.
- Is a Taiwan address written only in English (No. 1, Shifu Rd., Taipei) consistent with a TW country field? The reader recognises a Taiwan address by its Chinese city names, so such an address under a TW country field is recorded as `FIELD_CONFLICT`.
- How does the export mark a bank-internal list (`BANK_INTERNAL_LIST`), and which internal lists also count as PEP or ADVERSE_MEDIA? Does an internal-list note count as attached news (proposed: when it holds a URL and at least one complete sentence)? No sample has one, so a bank-internal category is unmapped (`UNKNOWN`, `UNMAPPED_CATEGORY`) and its notes are not read.
- Which source fields are authoritative for classifications and status?
- How are Profile removal, correction and historical states represented?

## Identity policy

- Approve the controlled variant-character list used for name comparison (`NameComparison.CjkVariants`: OpenCC one-to-one simplified forms plus a controlled list of same-reading variants such as 鍾/鐘/钟; distinct surnames such as 涂/塗 are never folded). A name matched only after folding is EXACT with `VARIANT_CHARACTERS` disclosed.
- Approve the controlled romanization table (`Romanization`: the MOFA table of Hanyu Pinyin, Wade-Giles and Tongyong spellings plus a controlled list of common passport spellings) and the Chinese-character readings it maps through (Unihan).
- Minimum plausible age for an attributed adult role (hard conflict `IMPOSSIBLE_AGE_AT_REPORTED_EVENT`): the code uses 14 (`HardConflictGate.MinimumRoleAge`); GC-01 needs anything above 6.
- Approve the role constraints for `IMPOSSIBLE_ROLE_AT_REPORTED_EVENT` (`HardConflictGate.AdultOnlyTitles`: 董事, 董事長, 副董事長, 常務董事, 獨立董事, 監察人, Director, Supervisor; the whole title must match) and supply a Golden Case for it, for `DOCUMENT_IDENTITY_CONFLICT` and for `DEATH_BEFORE_CUSTOMER_BIRTH`.
- Does the Profile export carry identity documents? The reader assumes a `身分證明` table (編號, 類型, 號碼, 國家); no sample has one.
- Can the bank supply the subject's employer with periods and named relatives (spouse, parents, related companies)? EMPLOYMENT and RELATIONSHIP comparisons are built but have no subject-side data.
- Which complete DOB conflicts qualify as hard conflicts?
- Which unique-identifier combinations permit automatic MATCH?
- What temporal-role rules are approved?
- Approve the wording of the [contrastive explanation](../feature/identity-adjudication.md#contrastive-explanation) lines (`wouldChangeIf`, such as `若同類證件相符：身分判斷移至第三階段第 2 列 UNIQUE_IDENTIFIER_AGREEMENT`): they name the stage 3 row, decision and code only, never an external result. Changing the wording touches the text only, not the rule.
- Is the proposed [stage 3 table](../feature/identity-adjudication.md#stage-3-table) approved? It reproduces GC-01 to GC-03; row 4 (agreement at available precision) may need narrowing once GC-07 exists.
- Which evidence groups identify organizations and vessels, and when are they in scope?
- Is the proposed [case-level precedence](../feature/case-management.md#case-level-result) approved as the ResultPublicationPolicy?

## PEP and Sanction

- What PEP/RCA categories and post-office periods apply?
- Approve the keywords read from the PEP / RCA subcategory text: Former and Inactive mark a former PEP (`FORMER_PEP`), and the relation words outside the statutory family scope (cousin, uncle, aunt, nephew, niece, grand-, 堂/表 relatives, 叔, 伯, 姑, 舅, 姨, 姪, 甥, 祖父母, 孫) keep a relative from `PEP_FAMILY`.
- Which sanctions programs and jurisdictions are in scope?
- How are active, removed and historical entries treated?

## Article 22

- How does the 2026-10-14 ruling (no bilateral direct identifier and match likelihood below 65% → not on list) apply without likelihoods? Options: NOT_ON_LIST recommendation for every REVIEW pair with `NO_BILATERAL_DIRECT_IDENTIFIER`, or only when no non-prerequisite group supports. GC-03 (year-only DOB, human MATCH) must still be reachable.
- When ① and ② both apply, is per-condition handling (PEP presumption survives an unrelated adverse-news condition) approved? The legacy system let ② override the whole hit.
- For a hit with Sanction or PEP plus unrelated adverse news, does ② set aside only the adverse-news condition?
- Which channels must be used before "exhausted" (③) may be claimed?
- Final domain allowlist for lookups and the attached-URL exemption wording.
- Approve the controlled keyword lists read from attached news: the legal-stage keywords (indictment, dismissal, conviction and acquittal wording, the modal markers such as 最重 and 求刑, and the other-party markers such as 同案), the ② predicate-offence list (洗錢防制法第 3 條), the non-predicate conduct list and the serious-consequence markers (致死, 致重傷, 逃逸) that keep drunk driving or an accident a predicate offence.

## Case and execution

- When is a new event merged into an open Case versus creating another Case?
- What are batch volume, latency and retry expectations?
- What is the result-publication and case-reopen policy?
- Mass download of personal data in case workbooks: a workbook leaves the system as a file, and one account pulling many `view=full` workbooks is how personal data leaks in bulk. Today the endpoint has an instance-wide concurrency limit, a log entry and the `aml.workbooks` counter per view, but no per-user limit and no record of who downloaded. Once authentication is wired: limit each user, with a much smaller allowance for `view=full`; record the user in the log; alert on unusual counts; and decide whether `view=full` needs its own permission ([case workbook](../feature/pipeline-api-and-batch.md#case-workbook)). Deferred by the business owner on 2026-11-07.

## Technology governance

- Is .NET approved for production support?
- Will production use Microsoft SQL Server or Oracle, and which version?
- Which container platform, identity provider and CI/CD controls are standard?
- List data refreshes on a schedule or on an event ([operating architecture](../feature/system-overview.md#operating-architecture)). What schedule, and which source events trigger a refresh and re-analysis?
- Should index search over the list database ([ADR-0021](../adr/0021-deferred-capabilities-built-behind-controls.md)) feed Cases automatically, and with which Top N?
- Calibrate the Fellegi-Sunter weight (m and u per group, by `FellegiSunter.EstimateParameters` over representative pairs, and a threshold by `Calibrate` over labelled ones) and approve, or not, an AML risk policy; neither affects decisions today.
- Approve the entity evidence groups (registration number, IMO) with an entity Golden Case.
- Calibrate the placeholder thresholds from labelled cases. None of them makes evidence support, a conflict or a hit; they grade details, rank candidates or cap groups:
  - name similarity per script (`NameComparison.NameSimilarityThresholdCjk` and `…Latin`, 0.90) and address Jaccard (`AddressCompare.JaccardThreshold`, 0.5), which label a PARTIAL detail;
  - n-gram Dice floors per query script (`ProfileIndex.NGramFloorCjk` 0.3, `NGramFloorLatin` about 0.46), BM25 constants (k1 1.2, b 0.3) and the reciprocal-rank-fusion k (60), from Recall@K;
  - the scattered-DOB cut-off (three or more Profile DOBs, or a span over two years, makes an agreeing DOB `PROFILE_MULTIPLE_DOB` without support);
  - the merge group size limit (10 Profiles) and the minimum comparable document length (4 characters).
- What are required SAST, SCA, SBOM and patch SLAs?

## Answered

| Question | Answer | Date |
|---|---|---|
| GC-12's final disposition? | NOT_ON_LIST, chosen by the senior approver; identity stays 無法判斷. | 2026-11-02 |
| GC-12's label says 無法判斷 but the legacy column says 確認後不符合; which is the outcome? | 無法判斷: the case is a grey area and identity cannot be decided. The legacy 理由 text is ImportedDecision only and never an identity outcome. | 2026-11-02 |
| When the hit is on a related party, is the account holder's data part of the input? | Yes, as case context only. Identity adjudication uses the hit related party's own data; the two people's data are never mixed. | 2026-11-02 |
| UI or API? | API + Swagger only | 2026-11-02 |
| PoC input format? | supplied HTML files and their `.txt` exports; API or crawler possible later | 2026-11-02 |
| PoC database? | SQLite until insufficient; later SQL Server or Oracle | 2026-11-02 |
| Need search index? | not for the PoC input, which uses the case-table candidates; the index is available for the other retrieval modes ([ADR-0021](../adr/0021-deferred-capabilities-built-behind-controls.md)) | 2026-11-02 |
| Legacy `Rule.md` rules? | merged where evidence-based; probabilities, the legacy Excel report and LLM flow not adopted ([ADR-0018](../adr/0018-legacy-ai-report-rules-not-adopted.md)); a case workbook renders the API instead ([ADR-0027](../adr/0027-case-workbook-renders-the-api.md)) | 2026-11-02 |
| List-type vocabulary? | bank's seven types plus official wanted list, flags first ([ADR-0016](../adr/0016-bank-list-type-vocabulary.md)) | 2026-11-02 |
| Photo comparison? | not used; bank specification | 2026-11-02 |
| Numeric score or Fellegi-Sunter in Phase 1? | no; evidence-strength decision table, Fellegi-Sunter at Gate D ([ADR-0019](../adr/0019-strength-decision-table-before-fellegi-sunter.md)) | 2026-11-02 |
| Weight of nationality and gender? | none; hit prerequisites like name (bank rules) | 2026-11-02 |
| License of the consular romanization table? | Open Government Data License v1.0 per the Bureau of Consular Affairs' open data announcement: free use without application, attribution required ([third-party data](implementation-baseline.md#third-party-data)) | 2026-11-04 |
