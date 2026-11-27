# Open Questions

Questions that need a bank decision. When one is answered, the answer goes into the record that owns its subject (the ADR, feature spec or Golden Case; a new ADR only for a subject no record covers, [ADR index](../adr/README.md)) and the question leaves this list.

## Data and source

- What are the stable HTML/source schemas and source versions? The HTML conversion assumes the pages use the `.txt` export's labels.
- Are the `.txt` exports (as in GC-11) the production input, or derived from HTML? Both are read today.
- What is the column layout of the candidate table in 案件資訊? Its header has two rows; the reader finds columns by label and expands each parent label over its sub-labels in order (是否符合名單？ → 全不符合, 全符合; Name Property → PEP, 黑名單, 負面新聞), which puts the three flags in the last three columns after Is RCA? (zero-based indexes 9–11). GC-11 and GC-12 show this header with every flag empty; an export with flags set is needed to confirm the expansion. What do the bank's 全不符合 and 全符合 cells of a candidate row mean (a decision on that row, or on every row of the Case)? They are kept per row as imported decisions, never evidence.
- How should the legal stages of several news items of one Profile combine? The items that read a stage must agree, else `UNKNOWN` with `STAGE_NEWS_ITEMS_DIFFER` for the reviewer, so a later conviction beside an earlier indictment is not concluded. Not measured: no labelled Profile with several items exists.
- Implausible age at a dated document, an advisory hint ([identity adjudication](../feature/identity-adjudication.md#implausible-age-at-a-dated-document-advisory)): switch it on as a concluding rule once its error rate is measured on real labelled Cases with the bank's decisions (the per-row imported decisions are read for this)? Which further role words and cues, and which wrong header dates in real exports (load or reprint dates), should be checked before it is relied on? The same for its Profile-notes source, an owner decision with two variants: conclude NO_MATCH only when the customer was not yet born at the earliest event the notes date for the Profile person, or also when under 14. The measurement that would support either: on WI-05's real Profiles with notes and the bank's decisions, the count of pairs each variant would decide and how many of those the bank judged the same person (wanted 0), with the sentences behind any wrong one read; the real-shape synthetic set only checks the logic (3 strangers, 0 same-person pairs under 14; 1 and 0 not yet born).
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
- Release `DOCUMENT_IDENTITY_CONFLICT` as a basis for NO_MATCH? Pending: a reissued passport carries a new number, so the rule needs a Golden Case and a rule for reissued documents.
- Should a list DOB on 1 January (a likely placeholder for a year-only value) be compared as a year? Today it is a full date, so it can be more than two years from the customer's and conclude NO_MATCH ([birth-date tolerance](../feature/identity-adjudication.md#birth-date-tolerance)); within the customer's own year it agrees within tolerance and raises a same-name stranger to PROBABLE_MATCH (15 of 15 on the [adversarial set](poc-delivery-plan.md#adversarial-synthetic-set)).
- Should agreement within the two-year tolerance score more than a birth year (`DOB_AGREES_WITHIN_TOLERANCE`, proposed +8)? On the synthetic set +25 changes no band.
- **Approve the scoring rule's weights and thresholds** ([proposal table](../feature/scoring-rule.md#proposal-table), `score-proposal-0.2`): identity points, the PROBABLE_MATCH and NO_MATCH thresholds, source reliability factors (including the factor of a model-read value, proposed 0), condition points and bands, and `T_match` (proposed 60, for example a document and a full DOB). Until approved they are used for trial calculation in measurement only, and the stage 3 table decides.
- **Automatic MATCH on a document: approve, change or reject the candidate condition [B′](../feature/scoring-rule.md#candidate-match-condition-b)** (same number, type and known issuing country, the name holding, nothing against it), or B″ (B′ and an agreeing list birth date, exact at the list's precision or within two years). On the [adversarial set](poc-delivery-plan.md#adversarial-synthetic-set)'s realistic group B′ concludes 59 customers, B″-exact 29, B″-tol 44, and none a stranger (probes, not a rate). Its theoretical-limit group shows what they cannot see: a passport number held by another person (reuse unverified) on a list record with no birth date beats B′ (10), with the customer's birth year only beats both (10); a same-full-date holder is rarer and not built. To decide: B′, B″-exact, B″-tol, a full birth date required, or automatic MATCH only for document types that are never reissued; and whether a document with no issuing country on either side may ever conclude (taken as agreement, B′∅ also concludes 10 realistic strangers holding the same number, so the measurement says it must not). A missed customer stays in REVIEW under every variant; none concludes a customer NO_MATCH. Real labelled pairs (recycled and reissued documents among them) are still absent, so nothing measured is a precision for release.
- What temporal-role rules are approved?
- Approve the wording of the [contrastive explanation](../feature/identity-adjudication.md#contrastive-explanation) lines (`wouldChangeIf`, such as `若同類證件相符：身分判斷移至第三階段第 2 列 UNIQUE_IDENTIFIER_AGREEMENT`): they name the stage 3 row, decision and code only, never an external result. Changing the wording touches the text only, not the rule.
- The [stage 3 table](../feature/identity-adjudication.md#stage-3-table) decides until the scoring rule is approved; it reproduces GC-01 to GC-03; row 4 (agreement at available precision, the `T_probable_exact` threshold in the score) may need narrowing once GC-07 exists.
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

## External sources

[External sources](../feature/external-sources.md): the sources, their build order and the customer's name in the search are decided ([ADR-0012](../adr/0012-sources-and-web-information.md)).

- **Search provider.** The system runs the search itself, through an official API (the crawler obeys `robots.txt`, and Google's result pages disallow crawling). Google's Custom Search JSON API is closed to new customers and ends on 2027-01-01 (its overview page, read 2026-11-19); Google points to Vertex AI Search, which searches at most 50 named sites, or to a request form for full web search. Which provider can the bank get, and do its terms on keeping queries (they hold the customer's name) pass the bank's review?
- **Query keywords.** Only name forms and fixed keyword lists go out (an employer or a city is a planned extension, to be supplemented later with its own decision). Approve the keyword lists and `Search:MaxQueries` (proposed 4 queries × 10 results).
- **Judgments.** Who holds the 司法院資料開放平臺 account; what schedule (daily, monthly with the archives); which courts and kinds (criminal only?); should a new judgment re-screen existing customers, which needs a customer-name index in the Runtime Query half?
- **Retention.** How long are fetched pages, judgments and search runs kept, and may a page's full text be stored with the Case? When the platform removes a judgment, is it deleted from stored Case inputs too, against append-only revisions?
- **Freshness.** After how long is a page fetched again for a new Case? Today one day (`PageService.MaxAge`, a constant; the job key holds the date), to become a setting once the bank answers.
- **Allowlist.** The final domain list for search results (also under [Article 22](#article-22)).
- **How often the two-year conflict on stated values closes the same person.** The rule and its residual risks are in [two-year conflict on stated values](../feature/identity-adjudication.md#two-year-conflict-on-stated-values); none is measured, and the synthetic sets hold no such text. How often do they occur on real labelled pages and news, before the rule is relied on?
- **Page kind and publisher.** Not read: the publisher is the URL's host, the kind is scored as allowlisted news. Read them from the page, or take them from the allowlist entry?
- **An update-dated page's stated age.** A page dated only by its update date (`UPDATE_DATE`, 103 of the 128 development HTML pages, one template) has no report date, so its stated ages are not compared (`AGE_NOT_COMPARABLE`). Should the labelled update date become a report date once its gap to the publication date is measured on more templates ([measured so far](../feature/external-sources.md#common-path))?
- **Role words that conclude NOT_HIT on a page.** A victim's or an official's word concludes NOT_HIT, 證人 nothing ([external sources](../feature/external-sources.md#matched-against-the-customer)). Measure the victim words on the labelled development news, since a complainant in one count can be a defendant in another (互告) with no suspect's word before the name.
- **Anonymised forms on a page.** A form linked to no full name (王男, 王姓男子) and a masked name (王○明) are no candidates. Should they become candidates as `PROFILE_SURNAME` (proposed, [external sources](../feature/external-sources.md#common-path)), or stay out until judgments are read?

## Technology governance

- Is .NET approved for production support?
- Will production use Microsoft SQL Server or Oracle, and which version?
- Which container platform, identity provider and CI/CD controls are standard?
- List data refreshes on a schedule or on an event ([operating architecture](../feature/system-overview.md#operating-architecture)). What schedule, and which source events trigger a refresh and re-analysis?
- Should index search over the list database ([ADR-0008](../adr/0008-validation-and-deferred-capabilities.md)) feed Cases automatically, and with which Top N?
- Calibrate the Fellegi-Sunter weight (m and u per group, by `FellegiSunter.EstimateParameters` over representative pairs, and a threshold by `Calibrate` over labelled ones), which may later replace the scoring rule's identity points; and say whether an AML risk level (`RiskScoring`, graded after the decision, not the scoring rule) is wanted at all.
- Approve the entity evidence groups (registration number, IMO) with an entity Golden Case.
- Legal persons' senior managers (the ninth comparison field): does the export of a legal person's own Case carry `客戶-關聯人背景資料.txt` with one `姓名/名稱` block per manager, as the reader assumes? Is a same-named manager on both sides enough to raise review priority from LOW to NORMAL (`ENTITY_SENIOR_MANAGER_AGREEMENT`), or should the post or a second field also agree?
- Calibrate the placeholder thresholds from labelled cases. None of them makes evidence support, a conflict or a hit; they grade details, rank candidates or cap groups:
  - name similarity per script (`NameComparison.NameSimilarityThresholdCjk` and `…Latin`, 0.90) and address Jaccard (`AddressCompare.JaccardThreshold`, 0.5), which label a PARTIAL detail;
  - n-gram Dice floors per query script (`ProfileIndex.NGramFloorCjk` 0.3, `NGramFloorLatin` about 0.46), BM25 constants (k1 1.2, b 0.3) and the reciprocal-rank-fusion k (60), from Recall@K;
  - the scattered-DOB cut-off (three or more Profile DOBs, or a span over two years, makes an agreeing DOB `PROFILE_MULTIPLE_DOB` without support);
  - the merge group size limit (10 Profiles) and the minimum comparable document length (4 characters).
- What are required SAST, SCA, SBOM and patch SLAs?
