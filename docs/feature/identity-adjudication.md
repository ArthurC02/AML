# Candidate Retrieval and Identity Adjudication

> Module: Candidate and Identity Adjudication.

## Separate concerns

- Retrieval score: ranking for which Profiles to inspect.
- Identity evidence: facts supporting or opposing same-person identity.
- Identity decision: policy result.
- AML risk: separate downstream assessment.

BM25 and displayed similarity values are not identity probabilities ([ADR-0001](../adr/0001-evidence-driven-adjudication.md)).

## Retrieval modes

| Mode | Candidates come from | Use |
|---|---|---|
| CONTROLLED_SET | the bank's case table | input of `POST /api/cases`; the PoC default |
| DIRECT_REFERENCE | a known Profile ID | a named Profile |
| INDEX_SEARCH | a name query over the list database | large source sets |
| PREVIOUS_LINK | the Profiles used by the Case's latest input | re-analysis after a source update |

They are served by `GET /api/profiles/search` (INDEX_SEARCH), `GET /api/profiles/{profileId}` (DIRECT_REFERENCE) and `GET /api/cases/{caseId}/candidates?source=table|previous` (CONTROLLED_SET, PREVIOUS_LINK) ([ADR-0021](../adr/0021-deferred-capabilities-built-behind-controls.md)). INDEX_SEARCH (steps 3 and 5 of the [operating architecture](system-overview.md#operating-architecture)) runs `Core/Retrieval/ProfileIndex` over the structured Profiles of the list database:

- Exact alias tier: the query key (after controlled variant folding, in source order or with the surname moved to either end) equals an alias key. These Profiles are listed first, ordered by alias text, and never cut by Top N.
- The other Profiles are ranked by three routes, each on its own scale, and fused by reciprocal rank (k 60):
  - `BM25` over words (k1 1.2, b 0.3); Chinese aliases count characters and Latin aliases count words, so each script has its own average alias length.
  - `NGRAM`: Dice coefficient over character n-grams of the key padded with boundary marks (CJK bigrams, plus single characters for names of up to three characters; Latin trigrams), counted per code point. Floor by query script: 0.3 for Chinese, 0.46 (Jaccard 0.3) for Latin.
  - `READING`: a shared Hanyu Pinyin reading key (`Romanization.ReadingKeys`): a Chinese name by its characters' readings, a romanized name by its syllables in each word order, polyphones expanded to at most 16 keys; a name with an unread character or an unsegmentable word has none. It links Chinese characters to their romanizations and one romanization system to another; it is a retrieval route only, never an exact match.
- Ties break on alias text, then Profile key, so the order is deterministic. A name whose key is empty (punctuation or 「-」 only) is not indexed, and an empty query returns nothing. A Profile key given twice keeps its last Profile, with that Profile's aliases only.
- The index keeps postings by alias key, word, n-gram and reading key. A query scores only the aliases that share at least one of its terms, since every route scores the others 0, so a search never scans the whole list. A very common term (a frequent surname) still reaches a large share of it.

A name query returns the Top N Profiles, counted after deduplication; when more Profiles matched than are returned, or the exact tier alone exceeds Top N, every result carries `truncated`. The constants are literature defaults pending tuning on Recall@K (`ProfileIndex.RecallAtK`: the share of labelled name queries whose Profile is among the first K results). Candidate data is then fetched for the named or retrieved Profiles. Index search does not feed a Case automatically; whether it should, and with which Top N, is an [open question](../plan/open-questions.md#technology-governance).

## Profile-level deduplication

All hits with the same `profileKey` (`DJ:` + `profileId`) become one CandidateProfile. Top K is counted by Profile, not alias rows.

Merging different Profiles that describe the same person (across Profile IDs or sources) happens when the list database is built, not per Case. A merge needs a shared direct identifier or a recorded source link; name similarity alone never merges. Merged Profiles keep their own IDs and sources so every value stays traceable. Implemented by `ProfileMerge`, served by `GET /api/profiles/merges`:

- `SHARED_DOCUMENT_KEY`: the same document type, issuing country and number. A number that is blank, a placeholder, shorter than four characters or one repeated character, or a document with an unknown type or country, gives no key.
- `RECORDED_SOURCE_LINK`: a relationship the source labels as the same person (Same As, Duplicate, Duplicate Of, Alias Profile, 同一人).
- Groups are built by union-find (iterative, union by size). A merge that would put two Profiles with opposite genders (`GENDER_CONFLICT`) or with no agreeing DOB (`DOB_CONFLICT`) in one group, checked against every member of both groups, is refused and listed under `rejected` with the conflicting pair.
- Every accepted merge is kept as an edge with its own basis and document key, so each link in a group stays explainable.
- A group of more than ten Profiles carries `OVERSIZED_GROUP` for inspection; the group-size and document-length limits are judgement calls pending real list volumes.

The hit count of a Case is the number of distinct Profiles, including hits on related parties; it is never the number of candidate rows or news articles. A Profile listed in the source but missing from the candidate table still counts.

## Screened subject

Each pair compares one screened subject (the customer or one related party) with one Profile. Every comparison, nationality test and age calculation uses that subject's own data; a related-party hit never borrows the customer's DOB, name or occupation. When the hit is on a related party, the account holder's data is still kept as case context (shown to the reviewer, usable in verification records and disposition notes) but never becomes Evidence for the pair.

## Evidence groups

```text
NAME
DATE_OF_BIRTH
GENDER
NATIONALITY
DOCUMENT
ADDRESS
RELATIONSHIP
EMPLOYMENT
COMPANY_ASSOCIATION
STATED_AGE
TEMPORAL_ROLE
OTHER_IDENTIFIER
```

Jaro-Winkler, Levenshtein and exact comparison of the same name are correlated features inside one NAME group. Both measures compare at most the first 256 characters, far past any name: their cost is the product of the two lengths. Stated ages and the news legal stage look for the first 100 names of a Profile only, since each name is searched for in every sentence. Aliases, former names, maiden names and other transliterations are compared inside NAME, not as extra groups.

`OTHER_IDENTIFIER` holds reference numbers and other labelled identifiers that are not identity documents; they keep their source label and never become DOCUMENT evidence, however much they look like one, even when a reference number passes the national-ID checksum (GC-12).

Photos are not evidence: appearance is never used to support or oppose identity or to estimate age, nationality, gender or name. This is the bank's current specification, not an industry rule; adding photo comparison needs a business-rule change, source and quality conditions and human review ([ADR-0018](../adr/0018-legacy-ai-report-rules-not-adopted.md)).

These groups describe natural persons. A customer or related party is a natural person when its 客戶類型 (類型 for a related party) is missing or says 個人, 自然人, Person, Individual or Natural Person (in any case); any other value makes it a legal person or group. Organization, vessel and other Profiles use `ENTITY_IDENTIFIER` instead (registration number with a known, equal jurisdiction; IMO number for vessels, whatever the flag): agreement is `ENTITY_IDENTIFIER_AGREEMENT` (PROBABLE_MATCH, external REVIEW), a different number under the same key is `ENTITY_IDENTIFIER_CONFLICT` (REVIEW); otherwise, and always for a natural-person subject, `NON_PERSON_SUBJECT_REVIEW`. Entity rules are pending an entity Golden Case.

## Hit prerequisites and direct identifiers

NAME, NATIONALITY and GENDER are hit prerequisites: they are what made the pair a candidate. They are compared in full and shown, with direction `PREREQUISITE`, but never count as support towards MATCH or PROBABLE_MATCH. At least one other group must support.

Direct identifiers are a full date of birth (DAY precision) and a comparable document key. A direct identifier is usable only when the same one is present on both sides; otherwise the pair records `NO_BILATERAL_DIRECT_IDENTIFIER`. Present on both sides means comparable, not equal.

## Comparison rules

- Values from different periods are compared at the same time point. Different values from different or unknown periods are `UNCERTAIN` with direction NEUTRAL.
- Employment and address count as same-period support only when both sides have an explicit attribution and validity period. Without them, they stay `UNCERTAIN` with direction NEUTRAL and the limitation is recorded.
- Different residence, registration or incident locations are weak evidence and can never alone, or as the main reason, push towards NO_MATCH.
- An event location is not a residential address; a report date or account-opening date is not an employment or residence period.
- Two values from different records or sources are not independent unless their source relationship is known; otherwise the explanation states "source relationship unknown". Several reports of the same event are one piece of evidence.
- Precision is never lowered: a full DOB is not rewritten as a year, and a year match is never a full-date match.
- A coarser Profile DOB that a more precise one already covers (1970 next to 1970-05-01) is dropped before comparison.
- When the Profile lists several DOB values, the best agreeing value is used at its own precision; the other values are source ambiguity, not a conflict. A conflict needs every Profile value to disagree.
- Agreeing with one of many or scattered Profile DOBs (three or more values, or a span over two years; placeholder cut-off pending calibration) is no rare coincidence: it is recorded as `PROFILE_MULTIPLE_DOB`, neutral and never support.

## Comparison measures

Per evidence group (step 6 of the [operating architecture](system-overview.md#operating-architecture)):

| Group | Measure | Implementation |
|---|---|---|
| NAME | exact after normalization, with disclosed relaxations; romanization variants and Jaro-Winkler / Levenshtein as `PARTIAL` features inside the group | yes; see [name comparison](#name-comparison) |
| DATE_OF_BIRTH | precision-aware comparison and conflict check | yes; scattered Profile DOBs give `PROFILE_MULTIPLE_DOB` ([comparison rules](#comparison-rules)) |
| DOCUMENT | type + issuing country/authority + number | yes: same key and number is `UNIQUE` support (row 2), same key with a different number is `DOCUMENT_IDENTITY_CONFLICT` (row 1); a document of another type or country is UNCERTAIN `NOT_COMPARABLE`, which does not block row 4; no sample Profile carries a document yet |
| ADDRESS | structured Taiwan address fields; token Jaccard for unstructured text | yes: city to floor key (old names and full-width digits folded), else Jaccard over words and CJK bigrams; always UNCERTAIN (`SAME_ADDRESS_NO_PERIOD` or `SOFT_DIFFERENCE`), since neither source gives validity periods |
| EMPLOYMENT | occupation and employer company, same period only | yes: same organization with overlapping periods on both sides (each needs a start; an open end runs to the present; compared by year, the coarsest common precision) is `MEDIUM` support; without periods `SAME_ORGANIZATION_NO_COMMON_PERIOD`; a different employer is UNCERTAIN `DIFFERENT_ORGANIZATION`, never a conflict. The bank export has an occupation category only, so subject employments are not yet extracted: an occupation category against Profile roles is `NOT_COMPARABLE` (subject employer missing), which does not block row 4 |
| OTHER_IDENTIFIER | labelled number equality | yes: the same number on both sides is `LOW` support with `SAME_IDENTIFIER_NATURE_UNCONFIRMED`; a different number is UNCERTAIN, never a conflict |
| TEMPORAL_ROLE | age at every dated role start and the report date | yes: `TEMPORALLY_PLAUSIBLE` (neutral, never support) when every age reaches `MinimumRoleAge`; the hard-conflict code when the gate fires |
| RELATIONSHIP / COMPANY_ASSOCIATION | spouse, parents, related companies named on both sides | yes: same relation and an EXACT name is support (`MEDIUM` for relatives, `LOW` for companies); the employer goes to EMPLOYMENT. Subject-side relatives are not in the bank export yet |

Similarity values only grade `PARTIAL` within a group (placeholder thresholds, pending calibration: Jaro-Winkler 0.90 for names, set separately for Chinese-character and Latin keys, and Jaccard 0.5 for addresses label the detail SIMILAR or DISSIMILAR); they are never summed across groups or turned into a likelihood ([ADR-0001](../adr/0001-evidence-driven-adjudication.md)). Thresholds are set from labelled cases, not by default.

### Name comparison

`NameComparison.CompareNames` compares every subject name with every Profile name (aliases included). Comparison keys apply NFKC, fold case, drop spaces and punctuation, and keep letters, digits and private-use (造字) characters per code point, so a supplementary-plane character is one character. A character the source could not render (?, ＊, U+FFFD) stays in the key as an unknown character; a key holding one is never EXACT.

A name is EXACT when a subject key equals a Profile key, trying the fewest relaxations first (each alone, then combined). Each relaxation used is disclosed as a finding in the NAME detail:

| Relaxation | Finding |
|---|---|
| none: same characters in source order | none |
| surname moved to the other end (given-first vs surname-first) | `NAME_ORDER_VARIANT` |
| diacritics folded (Nguyễn = Nguyen) | `DIACRITIC_VARIANT` |
| controlled variant characters folded (鍾/鐘/钟, simplified → traditional); distinct surnames such as 涂/塗 or 余/餘 never fold | `VARIANT_CHARACTERS` |

Otherwise the name is `PARTIAL`:

- `ROMANIZATION_VARIANT` when both names are romanized and read the same: every word segments into syllables (Hanyu Pinyin, Wade-Giles, Tongyong or passport spellings), the syllable counts agree and every syllable shares a Pinyin reading, in source order or with the surname moved. A hyphenated given name marks Taiwanese passport style, so its syllables read only in those systems.
- `NOT_COMPARABLE_CROSS_SCRIPT` when no subject and Profile key share a script (for example Chinese characters against a romanization); no similarity is computed.
- Otherwise `NOT_IDENTICAL_AFTER_NORMALIZATION` with the best Jaro-Winkler and the lowest Levenshtein distance over key pairs sharing a script, labelled SIMILAR or DISSIMILAR against that script's threshold. Both measures count code points; the Winkler prefix bonus applies only when the Jaro value exceeds 0.7.

A PARTIAL name is never support, a conflict or a hit, whatever the similarity.

## Attribution

- A relative's age is not the subject's age; acts of family members, related companies or co-defendants are not attributed to the Profile person.
- Age or capacity at a time point is inferred only when the source names the person and the time point explicitly. "Family-controlled" or "signed by a family member" is limitation or background only.
- An indictment in a later year for a case that started earlier does not prove the person took part, or was an adult, in the start year.
- An undated spouse or family relationship is valid only as of the source capture date; it is not projected backwards.

## Temporal and role consistency

When the source states an event date and role, compute the customer's age at the event and test only approved role constraints.

Do not infer a hard conflict from vague family or organization wording.

### Age stated in a source

Applies only to an age explicitly stated for a named person in a bounded source section ([extraction](extraction-and-normalization.md#ages-in-source-text)).

- Compare ages at the same time point: the time point stated in the source, else the report date, which is disclosed. Today's age is never compared with an old report.
- Age at time point = year of time point − birth year, minus 1 if the time point's month/day precedes the birthday. Difference = stated age − age at time point.
- Requires a full DOB on the subject side; otherwise the result is `AGE_NOT_COMPARABLE`, and with a partial DOB and a report date the note gives the possible age range at the report date (`StatedAgeCheck.AgeRange`, e.g. 45～46 歲 for a birth year only), which is shown, not compared.
- |difference| ≤ 2 (inclusive) → `AGE_COMPATIBLE_AT_REPORT`, recorded like `TEMPORALLY_PLAUSIBLE`: compatible is not support.
- |difference| > 2 → `AGE_DIFFERENCE_AT_REPORT`, a soft conflict that keeps the pair in REVIEW; it is never a hard conflict.
- Without a full subject DOB or a report date the result is `AGE_NOT_COMPARABLE`.
- An age stated with 時年 is the age when the reported event happened, whose date the text does not give; it is never compared with the report date and gives `AGE_NOT_COMPARABLE`.
- Phase 1 reads the age only when it is adjacent to the Profile person's name in the same sentence of attached news (e.g. 陳志明（45歲）, 45歲的陳志明).
- The ±2 tolerance applies to stated ages only, never to DOB comparisons.

## Hard conflict gate

Stage 1 of the three-stage policy ([ADR-0002](../adr/0002-three-stage-identity-policy.md)).

| Rule | Reason code | Validation | Phase 1 effect |
|---|---|---|---|
| impossible age at explicitly attributed event | `IMPOSSIBLE_AGE_AT_REPORTED_EVENT` | GC-01 | automatic NO_MATCH |
| impossible role at explicitly attributed event | `IMPOSSIBLE_ROLE_AT_REPORTED_EVENT` | pending; exercised by [AUG-01-01](../plan/golden-cases.md#augmented-cases) | REVIEW |
| reliable exact DOB conflict | `FULL_DOB_CONFLICT` | GC-06 pending; exercised by AUG-11-05 | REVIEW |
| comparable document-identity conflict | `DOCUMENT_IDENTITY_CONFLICT` | pending; exercised by AUG-11-02 | REVIEW |
| source-record death preceding customer birth | `DEATH_BEFORE_CUSTOMER_BIRTH` | pending; exercised by AUG-11-06 | REVIEW |

A pending rule still fires and records its reason code; it only cannot close the pair automatically. Impossible age and impossible role need a subject DOB to the day: with a year or month only, the age at the role start is not known and neither rule fires. Impossible role and death before birth are checked in the gate (`HardConflictGate`); a full DOB or document conflict reaches REVIEW through stage 3 row 1. Impossible role uses a controlled title list pending approval: directors and supervisors (董事, 董事長, 副董事長, 常務董事, 獨立董事, 監察人, Director, Supervisor) need full legal capacity, adulthood being 20 before 2023-01-01 and 18 since. The whole title must be on the list, so "Director of Marketing" is not a board director. Death before birth needs every date the death value can stand for to precede every date the DOB can stand for. A rule moves to automatic NO_MATCH when its Golden Case is validated and the policy version is released.

Each rule records its reason code as the pair's `hardConflict`. Source-quality concerns can downgrade a potential hard conflict to REVIEW. `HardConflictGate.SourceQualityConcern` (pending approval) turns a validated rule into REVIEW with `SOURCE_QUALITY_DOWNGRADE` when:

- the Profile has a dated role without its organization, or a role dated to the year only;
- the Profile's own DOB makes a role just as impossible, so the role date itself is suspect;
- the Profile has values the reader could not parse;
- the same pair has HIGH or UNIQUE support.

## Decision policy

```text
Confirmed hard conflict
    → NO_MATCH

Approved unique or sufficiently strong combination, no hard conflict
    → MATCH or human confirmation, according to release policy

Multiple strong supports but no unique identifier
    → PROBABLE_MATCH / external REVIEW

Insufficient or conflicting evidence
    → REVIEW
```

Phase 1 has no automatic MATCH: the identity decision is NO_MATCH, REVIEW or PROBABLE_MATCH. PROBABLE_MATCH is internal ([ADR-0003](../adr/0003-internal-probable-match.md)). There is no numeric score ([ADR-0019](../adr/0019-strength-decision-table-before-fellegi-sunter.md)); `FellegiSunter.Score` computes a calibration weight for Gate D only; the analysis does not run it, and it is outside the result and the explanation; agreeing with any one of k Profile DOBs is k times as likely by chance, so the DOB u is multiplied by k. The calibration tools are in place and are not wired to any decision: `FellegiSunter.Pattern` turns a pair's evidence into an agreement pattern (support agrees, conflict disagrees, everything else is missing), `EstimateParameters` estimates m, u per group and the match share from unlabelled patterns by expectation-maximization under conditional independence, `Weight` scores a pattern with the estimate, and `Calibrate` picks the highest weight threshold that keeps a required recall on labelled pairs, with the precision it gives.

### Support strength

Proposed, pending GC-06/GC-07 calibration. Levels follow the chance of an accidental agreement (u): rarer agreement, higher level.

| Group | Comparison | Strength | Rough u |
|---|---|---|---|
| DOCUMENT | comparable document key, EXACT | UNIQUE | ≈ 0 |
| DATE_OF_BIRTH | DAY precision on both sides, EXACT | HIGH | ≈ 1/22,000 |
| DATE_OF_BIRTH | MONTH precision agrees | MEDIUM | ≈ 1/700 |
| DATE_OF_BIRTH | YEAR only agrees | LOW | ≈ 1/60 |
| ADDRESS | same period, house-number level | MEDIUM | |
| ADDRESS | same period, district level only | LOW | |
| EMPLOYMENT, COMPANY_ASSOCIATION, RELATIONSHIP | explicit, same period | MEDIUM | |
| OTHER_IDENTIFIER | EXACT, nature unconfirmed | LOW | |
| NAME, NATIONALITY, GENDER | any | none: hit prerequisite | |

Age compatibility and temporal plausibility have no strength: they are not support.

### Stage 3 table

Applied only when no hard conflict fired. The first matching row wins. The table must reproduce every validated [Golden Case](../plan/golden-cases.md); a row that breaks one is wrong, not the case.

| # | Condition | Decision | Reason code |
|---|---|---|---|
| 1 | a `CONFLICT` in DATE_OF_BIRTH or DOCUMENT without a hard rule, or `AGE_DIFFERENCE_AT_REPORT` | REVIEW | the conflict's code (`FULL_DOB_CONFLICT`, `DOB_CONFLICT`, `DOCUMENT_IDENTITY_CONFLICT`, `AGE_DIFFERENCE_AT_REPORT`) |
| 2 | UNIQUE support | MATCH when the release policy allows; Phase 1: PROBABLE_MATCH | `UNIQUE_IDENTIFIER_AGREEMENT` |
| 3 | HIGH support plus at least one other non-prerequisite support, and no `GENDER_CONFLICT` | PROBABLE_MATCH | `STRONG_SUPPORT_COMBINATION` |
| 4 | every hit prerequisite is EXACT (name after normalization and disclosed relaxations, gender, nationality), DATE_OF_BIRTH is support at the finest precision both sides allow, and no DOCUMENT or EMPLOYMENT value is CONFLICT or UNCERTAIN other than `NOT_COMPARABLE` | PROBABLE_MATCH | `CONSISTENT_AT_AVAILABLE_PRECISION` |
| 5 | at least one non-prerequisite support | REVIEW | `PARTIAL_SUPPORT_ONLY` |
| 6 | none | REVIEW | `NO_NON_PREREQUISITE_SUPPORT` |

- Row 4 uses the prerequisites as preconditions, not as support: they never raise a pair on their own (row 6 still applies when nothing else agrees).
- Address and other location differences are weak ([comparison rules](#comparison-rules)); they never trigger row 1 and never block row 4.
- Employment is never a conflict (a different employer is not evidence against identity), so it never triggers row 1; an uncertain employment still blocks row 4.
- A DOB agreement recorded as `PROFILE_MULTIPLE_DOB` is not support, so it does not satisfy row 4.
- Opposite genders rule out rows 3 and 4; the pair falls to row 5 or 6 and `GENDER_CONFLICT` is recorded alongside.
- `NO_BILATERAL_DIRECT_IDENTIFIER` is recorded alongside whenever it applies.
- Review priority follows the row: 2, 3 and 4 first, then 5, then 1 and 6.

Golden Case check:

| Case | Evidence | Row | Result | Expected |
|---|---|---|---|---|
| GC-01 | impossible age at an explicitly attributed role | stage 1 | NO_MATCH | NO_MATCH |
| GC-02 | exact name and nationality only | 6 | REVIEW | REVIEW |
| GC-03 | normalized name, gender, nationality agree; birth year agrees, Profile has year only; cross-country address | 4 | PROBABLE_MATCH | PROBABLE_MATCH / REVIEW |
| GC-11 | name, gender, nationality agree; one of two Profile birth years agrees; Profile has no document, employment or address | 4 | PROBABLE_MATCH | PROBABLE_MATCH / REVIEW |
| GC-12 | related party: name and nationality agree, gender missing; Profile has no DOB; nothing else comparable | 6 | REVIEW | REVIEW |

Row 4 makes common names with a shared birth year PROBABLE_MATCH. That only raises review priority; GC-07 (highly similar, final NO_MATCH) will show whether it needs narrowing.

## Reason code registry

Only registered codes may appear in decisions. New codes are added here with the policy that emits them.

| Code | Emitted by | Meaning |
|---|---|---|
| `TEMPORALLY_PLAUSIBLE` | IdentityDecisionPolicy | customer age and role at the reported event are possible; not support for identity |
| `IMPOSSIBLE_AGE_AT_REPORTED_EVENT` | IdentityDecisionPolicy | hard conflict; validated by GC-01 |
| `IMPOSSIBLE_ROLE_AT_REPORTED_EVENT` | IdentityDecisionPolicy | hard conflict; pending validation |
| `FULL_DOB_CONFLICT` | IdentityDecisionPolicy | hard conflict; pending GC-06 |
| `DOCUMENT_IDENTITY_CONFLICT` | IdentityDecisionPolicy | hard conflict; pending validation |
| `DEATH_BEFORE_CUSTOMER_BIRTH` | IdentityDecisionPolicy | hard conflict; pending validation |
| `DOB_CONFLICT` | IdentityDecisionPolicy | every Profile DOB disagrees, without a full date on both sides; soft conflict, row 1 |
| `BIRTH_YEAR_MATCH_FULL_DATE_NOT_COMPARABLE` | IdentityDecisionPolicy | years agree, but at least one side has YEAR precision only |
| `GENDER_CONFLICT` | IdentityDecisionPolicy | subject and Profile genders differ; prerequisite conflict that rules out rows 3 and 4 |
| `SOURCE_QUALITY_DOWNGRADE` | IdentityDecisionPolicy | a validated hard conflict kept in REVIEW because of a source-quality concern |
| `NON_PERSON_SUBJECT_REVIEW` | IdentityDecisionPolicy | organization, vessel or other Profile with no comparable entity identifier, or compared with a natural-person subject; REVIEW |
| `NO_BILATERAL_DIRECT_IDENTIFIER` | IdentityDecisionPolicy | neither full DOB nor document key is present on both sides |
| `UNIQUE_IDENTIFIER_AGREEMENT` | IdentityDecisionPolicy | comparable document key agrees |
| `STRONG_SUPPORT_COMBINATION` | IdentityDecisionPolicy | full DOB agrees plus another non-prerequisite support |
| `CONSISTENT_AT_AVAILABLE_PRECISION` | IdentityDecisionPolicy | prerequisites agree and DOB agrees as precisely as the data allows, with nothing conflicting or uncertain in DOCUMENT or EMPLOYMENT other than `NOT_COMPARABLE`; GC-03 |
| `PARTIAL_SUPPORT_ONLY` | IdentityDecisionPolicy | support exists but below the PROBABLE_MATCH combinations |
| `NO_NON_PREREQUISITE_SUPPORT` | IdentityDecisionPolicy | only hit prerequisites agree |
| `AGE_COMPATIBLE_AT_REPORT` | IdentityDecisionPolicy | stated age within ±2 years at the same time point; not support |
| `AGE_DIFFERENCE_AT_REPORT` | IdentityDecisionPolicy | stated age differs by more than 2 years; soft conflict |
| `AGE_NOT_COMPARABLE` | IdentityDecisionPolicy | stated age found but not comparable: no full subject DOB, no report date, or an age stated with 時年 |
| `ENTITY_IDENTIFIER_AGREEMENT` | IdentityDecisionPolicy | entity record: the same registration or IMO number on both sides |
| `ENTITY_IDENTIFIER_CONFLICT` | IdentityDecisionPolicy | entity record: a different number under the same key |
| `ART22_RULING_20261014_MAY_APPLY` | DispositionPolicy | the 2026-10-14 ruling may apply; flagged, not applied |
| `ART22_FOREIGN_PEP_PRESUMPTION` | DispositionPolicy | Article 22 ① presumption; identity unchanged |
| `ART22_ADVERSE_UNRELATED` | DispositionPolicy | Article 22 ②; an ADVERSE_MEDIA condition set aside |
| `ART22_IDENTITY_NO_MATCH` | DispositionPolicy | default; identity NO_MATCH, NOT_ON_LIST |
| `ART22_NOT_DETERMINED` | DispositionPolicy | Article 22 ③; no recommendation, pending checks listed |

Article 22 codes are defined in [disposition recommendation](review-and-disposition.md).

Comparison findings are evidence details, not decision reason codes; they explain an evidence status and never change a row on their own:

| Finding | Recorded on | Meaning |
|---|---|---|
| `NAME_ORDER_VARIANT` | NAME, EXACT | names agree with the surname moved |
| `DIACRITIC_VARIANT` | NAME, EXACT | names agree after diacritics are folded |
| `VARIANT_CHARACTERS` | NAME, EXACT | names agree after controlled variant characters are folded |
| `ROMANIZATION_VARIANT` | NAME, PARTIAL | romanized names read the same in different romanization systems |
| `NOT_COMPARABLE_CROSS_SCRIPT` | NAME, PARTIAL | no subject and Profile name share a script |
| `PROFILE_MULTIPLE_DOB` | DATE_OF_BIRTH, neutral | the agreeing DOB is one of many or scattered Profile values; not support |
| `NOT_COMPARABLE` | DOCUMENT or EMPLOYMENT | a value is present but cannot be compared (another document type or country; occupation category only); does not block row 4 |
| `OVERSIZED_GROUP` | Profile merge group | more than ten Profiles merged; inspect the edges |

## Explainability

Every decision records its evidence per group, its hard-conflict code or stage 3 row, and its reason codes. The hit explanation restates it as:

- supporting evidence;
- conflicts;
- unknown fields;
- hard conflict and stage 3 row;
- reason codes.

The revision's lineage records the input file hashes, the list version of each Profile used and the source-condition policy, code catalog, schema and app versions. The Fellegi-Sunter weight does not run in the analysis and so is not recorded ([ADR-0025](../adr/0025-lineage-records-what-ran.md)). The identity rules have no version of their own; the app version identifies them. Retrieval scope is not recorded.

Every code a decision or explanation emits is in the code catalog, served by `GET /api/codes` with its meaning and the section that defines it.

### Contrastive explanation

`IdentityDecisionPolicy.Contrast` answers "what would change the decision": for a document of the same type and issuing country, and for a full DOB, each not yet comparable on both sides, it assumes agreement and conflict in turn and re-runs the [stage 3 table](#stage-3-table) with every other piece of evidence fixed. An outcome on the current row is left out. For GC-03 (row 4): a document that agrees moves the pair to row 2 `UNIQUE_IDENTIFIER_AGREEMENT`, one that conflicts to row 1 `DOCUMENT_IDENTITY_CONFLICT`, and a full DOB that conflicts to row 1 `FULL_DOB_CONFLICT`; a full DOB that agrees keeps row 4 and is not listed.

- It is empty for non-Person records, a pair with a hard conflict, rows 1 and 2, and a Profile whose DOBs are scattered (`PROFILE_MULTIPLE_DOB`).
- It names the row, the identity decision and the code only, never the external result: Phase 1 shows every PROBABLE_MATCH as REVIEW.
- Only stage 3 is re-run. A full DOB would in reality also reach the stage 1 checks (age at a dated role), which are not re-run.
- The wording of each line (`若同類證件相符：身分判斷移至第三階段第 2 列 …`) is on the business confirmation list; changing it touches the text only, not the rule.

A name similarity score is shown only for names located in a single source field. A full score means the names are identical after normalization; a missing score does not mean the source has no name. The score is never converted into a likelihood.
