# Candidate Retrieval and Identity Adjudication

> Module: Candidate and Identity Adjudication. Design baseline 2.0-PoC, 2026-11-02.

## Separate concerns

- Retrieval score: ranking for which Profiles to inspect.
- Identity evidence: facts supporting or opposing same-person identity.
- Identity decision: policy result.
- AML risk: separate downstream assessment.

BM25 and displayed similarity values are not identity probabilities ([ADR-0001](../adr/0001-evidence-driven-adjudication.md)).

## Retrieval modes

- DIRECT_REFERENCE for known Profile ID;
- CONTROLLED_SET for small PoC datasets (the PoC default; no search index);
- INDEX_SEARCH for large source sets;
- PREVIOUS_LINK for re-analysis after source updates.

Possible routes include exact alias, word BM25, character n-gram and prior Case/Profile links.

INDEX_SEARCH (steps 3 and 5 of the [operating architecture](system-overview.md#operating-architecture)) needs a list database of structured Profiles and a search index over names and aliases (BM25 words, character n-grams, alias table). A name query returns the Top N Profiles, counted after deduplication; candidate data is then fetched for the named or retrieved Profiles. The PoC uses CONTROLLED_SET: the bank's case table supplies the candidates.

## Profile-level deduplication

All hits with the same `sourceNamespace + profileId` become one CandidateProfile. Top K is counted by Profile, not alias rows.

Merging different Profiles that describe the same person (across Profile IDs or sources) happens when the list database is built, not per Case. A merge needs a shared direct identifier or a recorded source link; name similarity alone never merges. Merged Profiles keep their own IDs and sources so every value stays traceable.

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
TEMPORAL_ROLE
OTHER_IDENTIFIER
```

Jaro-Winkler, Levenshtein and exact comparison of the same name are correlated features inside one NAME group. Aliases, former names, maiden names and other transliterations are compared inside NAME, not as extra groups.

`OTHER_IDENTIFIER` holds reference numbers and other labelled identifiers that are not identity documents; they keep their source label and never become DOCUMENT evidence, however much they look like one, even when a reference number passes the national-ID checksum (GC-12).

Photos are not evidence: appearance is never used to support or oppose identity or to estimate age, nationality, gender or name. This is the bank's current specification, not an industry rule; adding photo comparison needs a business-rule change, source and quality conditions and human review ([ADR-0018](../adr/0018-legacy-ai-report-rules-not-adopted.md)).

These groups describe natural persons. Organization, vessel and other Profiles receive `NON_PERSON_SUBJECT_REVIEW` and stay REVIEW in Phase 1.

## Hit prerequisites and direct identifiers

NAME, NATIONALITY and GENDER are hit prerequisites: they are what made the pair a candidate. They are compared in full and shown, with direction `PREREQUISITE`, but never count as support towards MATCH or PROBABLE_MATCH. At least one other group must support.

Direct identifiers are a full date of birth (DAY precision) and a comparable document key. A direct identifier is usable only when the same one is present on both sides; otherwise the pair records `NO_BILATERAL_DIRECT_IDENTIFIER`. Present on both sides means comparable, not equal.

## Comparison rules

- Values from different periods are compared at the same time point. Different values from different or unknown periods are `UNCERTAIN` with direction UNKNOWN.
- Employment and address count as same-period support only when both sides have an explicit attribution and validity period. Without them, they stay UNKNOWN and the limitation is recorded.
- Different residence, registration or incident locations are weak evidence and can never alone, or as the main reason, push towards NO_MATCH.
- An event location is not a residential address; a report date or account-opening date is not an employment or residence period.
- Two values from different records or sources are not independent unless their source relationship is known; otherwise the explanation states "source relationship unknown". Several reports of the same event are one piece of evidence.
- Precision is never lowered: a full DOB is not rewritten as a year, and a year match is never a full-date match.
- When the Profile lists several DOB values, the best agreeing value is used at its own precision; the other values are source ambiguity (`PROFILE_MULTIPLE_DOB`), not a conflict. A conflict needs every Profile value to disagree.

## Comparison measures

Per evidence group (step 6 of the [operating architecture](system-overview.md#operating-architecture)):

| Group | Measure | Built |
|---|---|---|
| NAME | exact after normalization and controlled variants; romanization variants and Jaro-Winkler / Levenshtein as `PARTIAL` features inside the group | yes; a romanization match is `PARTIAL` with `ROMANIZATION_VARIANT`, otherwise the best similarity is shown with the PARTIAL; no threshold yet |
| DATE_OF_BIRTH | precision-aware comparison and conflict check | yes |
| DOCUMENT | type + issuing country/authority + number | yes: same key and number is `UNIQUE` support (row 2), same key with a different number is `DOCUMENT_IDENTITY_CONFLICT` (row 1), anything else `NOT_COMPARABLE`; no sample Profile carries a document yet |
| ADDRESS | structured Taiwan address fields; token Jaccard for unstructured text | yes: city to floor key (old names and full-width digits folded), else Jaccard over words and CJK bigrams; always UNCERTAIN (`SAME_ADDRESS_NO_PERIOD` or `SOFT_DIFFERENCE`), since neither source gives validity periods |
| EMPLOYMENT | occupation and employer company, same period only | yes: same organization with overlapping periods on both sides is `MEDIUM` support; without periods `SAME_ORGANIZATION_NO_COMMON_PERIOD`; a different employer is never a conflict. The bank export has an occupation category only, so subject employments are not yet extracted |
| RELATIONSHIP / COMPANY_ASSOCIATION | spouse, parents, related companies named on both sides | yes: same relation and an EXACT name is support (`MEDIUM` for relatives, `LOW` for companies); the employer goes to EMPLOYMENT. Subject-side relatives are not in the bank export yet |

Similarity values only grade `PARTIAL` within a group; they are never summed across groups or turned into a likelihood ([ADR-0001](../adr/0001-evidence-driven-adjudication.md)). Thresholds are set from labelled cases, not by default.

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
- Requires a full DOB on the subject side; otherwise the result is UNKNOWN and the explanation may show the possible age range.
- |difference| ≤ 2 (inclusive) → `AGE_COMPATIBLE_AT_REPORT`, recorded like `TEMPORALLY_PLAUSIBLE`: compatible is not support.
- |difference| > 2 → `AGE_DIFFERENCE_AT_REPORT`, a soft conflict that keeps the pair in REVIEW; it is never a hard conflict.
- Without a full subject DOB or a report date the result is `AGE_NOT_COMPARABLE`.
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

A pending rule still fires and records its reason code; it only cannot close the pair automatically. Impossible role and death before birth are checked in the gate (`HardConflictGate`); a full DOB or document conflict reaches REVIEW through stage 3 row 1. Impossible role uses a controlled title list pending approval: directors and supervisors (董事, 監察人) need full legal capacity, adulthood being 20 before 2023-01-01 and 18 since. Death before birth needs every date the death value can stand for to precede every date the DOB can stand for. A rule moves to automatic NO_MATCH when its Golden Case is validated and the policy version is released.

Each rule must name its evidence and policy version. Source-quality concerns can downgrade a potential hard conflict to REVIEW.

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

Phase 1 does not broadly enable automatic MATCH. PROBABLE_MATCH is internal ([ADR-0003](../adr/0003-internal-probable-match.md)). There is no numeric score ([ADR-0019](../adr/0019-strength-decision-table-before-fellegi-sunter.md)).

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
| 1 | a soft conflict in DATE_OF_BIRTH, DOCUMENT or EMPLOYMENT (`CONFLICT` without a hard rule), or `AGE_DIFFERENCE_AT_REPORT` | REVIEW | the conflict's code |
| 2 | UNIQUE support | MATCH when the release policy allows; Phase 1: PROBABLE_MATCH | `UNIQUE_IDENTIFIER_AGREEMENT` |
| 3 | HIGH support plus at least one other non-prerequisite support | PROBABLE_MATCH | `STRONG_SUPPORT_COMBINATION` |
| 4 | every hit prerequisite agrees (name after normalization, gender, nationality), DATE_OF_BIRTH agrees at the finest precision both sides allow, and no DOCUMENT or EMPLOYMENT value is CONFLICT or UNCERTAIN | PROBABLE_MATCH | `CONSISTENT_AT_AVAILABLE_PRECISION` |
| 5 | at least one non-prerequisite support | REVIEW | `PARTIAL_SUPPORT_ONLY` |
| 6 | none | REVIEW | `NO_NON_PREREQUISITE_SUPPORT` |

- Row 4 uses the prerequisites as preconditions, not as support: they never raise a pair on their own (row 6 still applies when nothing else agrees).
- Address and other location differences are weak ([comparison rules](#comparison-rules)); they never trigger row 1 and never block row 4.
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
| `BIRTH_YEAR_MATCH_FULL_DATE_NOT_COMPARABLE` | IdentityDecisionPolicy | years agree, but at least one side has YEAR precision only |
| `NON_PERSON_SUBJECT_REVIEW` | IdentityDecisionPolicy | organization, vessel or other Profile; Phase 1 forces REVIEW |
| `NO_BILATERAL_DIRECT_IDENTIFIER` | IdentityDecisionPolicy | neither full DOB nor document key is present on both sides |
| `UNIQUE_IDENTIFIER_AGREEMENT` | IdentityDecisionPolicy | comparable document key agrees |
| `STRONG_SUPPORT_COMBINATION` | IdentityDecisionPolicy | full DOB agrees plus another non-prerequisite support |
| `CONSISTENT_AT_AVAILABLE_PRECISION` | IdentityDecisionPolicy | prerequisites agree and DOB agrees as precisely as the data allows, with nothing uncertain in DOCUMENT or EMPLOYMENT; GC-03 |
| `PARTIAL_SUPPORT_ONLY` | IdentityDecisionPolicy | support exists but below the PROBABLE_MATCH combinations |
| `NO_NON_PREREQUISITE_SUPPORT` | IdentityDecisionPolicy | only hit prerequisites agree |
| `AGE_COMPATIBLE_AT_REPORT` | IdentityDecisionPolicy | stated age within ±2 years at the same time point; not support |
| `AGE_DIFFERENCE_AT_REPORT` | IdentityDecisionPolicy | stated age differs by more than 2 years; soft conflict |
| `ART22_FOREIGN_PEP_PRESUMPTION` | DispositionPolicy | Article 22 ① presumption; identity unchanged |
| `ART22_ADVERSE_UNRELATED` | DispositionPolicy | Article 22 ②; an ADVERSE_MEDIA condition set aside |
| `ART22_IDENTITY_NO_MATCH` | DispositionPolicy | default; identity NO_MATCH, NOT_ON_LIST |
| `ART22_NOT_DETERMINED` | DispositionPolicy | Article 22 ③; no recommendation, pending checks listed |

Article 22 codes are defined in [disposition recommendation](review-and-disposition.md).

## Explainability

Every decision includes:

- supporting evidence;
- conflicts;
- unknown fields;
- reason codes;
- match policy version;
- retrieval scope and limitations.

A name similarity score is shown only for names located in a single source field. A full score means the names are identical after normalization; a missing score does not mean the source has no name. The score is never converted into a likelihood.
