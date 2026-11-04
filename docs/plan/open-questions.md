# Open Questions

Questions that need a bank decision. When one is answered, record the answer here and, if it is an architectural choice, add an ADR.

## Data and source

- What are the stable HTML/source schemas and source versions?
- Are the `.txt` exports (as in GC-11) the production input, or derived from HTML? A `.txt` reader is needed if they are.
- What is the column order of the candidate table in 案件資訊 (two header lines: decision columns and PEP/blacklist/adverse-news flags)? GC-11 and GC-12 assume the flags are empty.
- Does the Profile export carry dated roles (`職位`), keywords (`關鍵字`) and official-list details (`名單資訊`) as structured fields? The synthetic GC-01 to GC-03 samples assume so.
- Which source fields are authoritative for classifications and status?
- How are Profile removal, correction and historical states represented?

## Identity policy

- Approve the controlled variant-character list used for name comparison (`Normalize.CjkVariants`: 鍾/鐘/钟 and common simplified forms; distinct surnames such as 涂/塗 are never folded). A name matched only after folding is EXACT with `VARIANT_CHARACTERS` disclosed.
- Approve the controlled romanization table (`Romanization`: Wade-Giles, Tongyong and passport spellings to Pinyin readings) and decide whether Jaro-Winkler / Levenshtein need a threshold, set from labelled cases.
- Minimum plausible age for an attributed adult role (hard conflict `IMPOSSIBLE_AGE_AT_REPORTED_EVENT`): the code uses 14 (`HardConflictGate.MinimumRoleAge`); GC-01 needs anything above 6.
- Approve the role constraints for `IMPOSSIBLE_ROLE_AT_REPORTED_EVENT` (`HardConflictGate.AdultOnlyTitles`: 董事, 監察人) and supply a Golden Case for it, for `DOCUMENT_IDENTITY_CONFLICT` and for `DEATH_BEFORE_CUSTOMER_BIRTH`.
- Does the Profile export carry identity documents? The reader assumes a `身分證明` table (編號, 類型, 號碼, 國家); no sample has one.
- Can the bank supply the subject's employer with periods and named relatives (spouse, parents, related companies)? EMPLOYMENT and RELATIONSHIP comparisons are built but have no subject-side data.
- Which complete DOB conflicts qualify as hard conflicts?
- Which unique-identifier combinations permit automatic MATCH?
- What temporal-role rules are approved?
- Is the proposed [stage 3 table](../feature/identity-adjudication.md#stage-3-table) approved? It reproduces GC-01 to GC-03; row 4 (agreement at available precision) may need narrowing once GC-07 exists.
- Which evidence groups identify organizations and vessels, and when are they in scope?
- Is the proposed [case-level precedence](../feature/case-management.md#case-level-result) approved as the ResultPublicationPolicy?

## PEP and Sanction

- What PEP/RCA categories and post-office periods apply?
- Which sanctions programs and jurisdictions are in scope?
- How are active, removed and historical entries treated?

## Article 22

- How does the 2026-10-14 ruling (no bilateral direct identifier and match likelihood below 65% → not on list) apply without likelihoods? Options: NOT_ON_LIST recommendation for every REVIEW pair with `NO_BILATERAL_DIRECT_IDENTIFIER`, or only when no non-prerequisite group supports. GC-03 (year-only DOB, human MATCH) must still be reachable.
- When ① and ② both apply, is per-condition handling (PEP presumption survives an unrelated adverse-news condition) approved? The legacy system let ② override the whole hit.
- For a hit with Sanction or PEP plus unrelated adverse news, does ② set aside only the adverse-news condition?
- Which channels must be used before "exhausted" (③) may be claimed?
- Final domain allowlist for lookups and the attached-URL exemption wording.

## Case and execution

- When is a new event merged into an open Case versus creating another Case?
- What are batch volume, latency and retry expectations?
- What is the result-publication and case-reopen policy?

## Technology governance

- Is .NET approved for production support?
- Will production use Microsoft SQL Server or Oracle, and which version?
- Which container platform, identity provider and CI/CD controls are standard?
- List data refreshes on a schedule or on an event ([operating architecture](../feature/system-overview.md#operating-architecture), 2026-11-03). What schedule, and which source events trigger a refresh and re-analysis?
- The list database and search index are built ([ADR-0021](../adr/0021-deferred-capabilities-built-behind-controls.md)); should index search feed Cases automatically, and with which Top N?
- Calibrate the Fellegi-Sunter shadow (m and u per group) and approve, or not, an AML risk policy; neither affects decisions today.
- Approve the entity evidence groups (registration number, IMO) with an entity Golden Case.
- Which name and address similarity thresholds (Jaro-Winkler, Levenshtein, Jaccard) does the bank accept, and from which labelled cases are they set?
- What are required SAST, SCA, SBOM and patch SLAs?

## Answered

| Question | Answer | Date |
|---|---|---|
| GC-12's final disposition? | NOT_ON_LIST, chosen by the senior approver; identity stays 無法判斷. | 2026-11-02 |
| GC-12's label says 無法判斷 but the legacy column says 確認後不符合; which is the outcome? | 無法判斷: the case is a grey area and identity cannot be decided. The legacy 理由 text is ImportedDecision only and never an identity outcome. | 2026-11-02 |
| When the hit is on a related party, is the account holder's data part of the input? | Yes, as case context only. Identity adjudication uses the hit related party's own data; the two people's data are never mixed. | 2026-11-02 |
| UI or API? | API + Swagger only | 2026-11-02 |
| PoC input format? | supplied HTML files; API or crawler possible later | 2026-11-02 |
| PoC database? | SQLite until insufficient; later SQL Server or Oracle | 2026-11-02 |
| Need search index? | no, data volume is small | 2026-11-02 |
| Legacy `Rule.md` rules? | merged where evidence-based; probabilities, Excel report and LLM flow not adopted ([ADR-0018](../adr/0018-legacy-ai-report-rules-not-adopted.md)) | 2026-11-02 |
| List-type vocabulary? | bank's seven types plus official wanted list, flags first ([ADR-0016](../adr/0016-bank-list-type-vocabulary.md)) | 2026-11-02 |
| Photo comparison? | not used; bank specification | 2026-11-02 |
| Numeric score or Fellegi-Sunter in Phase 1? | no; evidence-strength decision table, Fellegi-Sunter at Gate D ([ADR-0019](../adr/0019-strength-decision-table-before-fellegi-sunter.md)) | 2026-11-02 |
| Weight of nationality and gender? | none; hit prerequisites like name (bank rules) | 2026-11-02 |
