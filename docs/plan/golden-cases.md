# Golden Cases

> Fixed, versioned regression scenarios. Validated Golden Cases are authoritative: when a policy or spec disagrees with one, the policy is changed. A case's expected result changes only through a new, business-approved case version. This document records design test scenarios. It is not a legal finding and should not reproduce unnecessary personal identifiers in logs or public documents.

## Summary

| ID | Scenario | Expected system result | Human outcome (label) |
|---|---|---|---|
| GC-01 | Same name, impossible age/role | NO_MATCH | Not matched |
| GC-02 | Fraud HIT, identity insufficient | REVIEW | Human NO_MATCH |
| GC-03 | Multilingual strong partial match, active wanted list | PROBABLE_MATCH / REVIEW | Human MATCH |
| GC-04 | PEP or RCA | Pending | Pending |
| GC-05 | Sanction | Pending | Pending |
| GC-06 | Full DOB hard conflict | Pending | Pending |
| GC-07 | Highly similar but final NO_MATCH | Pending | Pending |
| GC-08 | Source update reverses result | Pending | Pending |
| GC-09 | Foreign PEP, non-Taiwanese customer, insufficient data (Article 22 ①) | REVIEW, recommendation ON_LIST | Pending |
| GC-10 | Same person, adverse news unrelated to ML/TF/PF (Article 22 ②) | recommendation NOT_ON_LIST for that condition | Pending |
| GC-11 | Taiwanese customer on adverse media, year-only DOB | PROBABLE_MATCH / REVIEW | Human MATCH |
| GC-12 | Related party with a common name, no comparable identifiers | REVIEW | Human UNRESOLVED; senior approver NOT_ON_LIST |

## GC-01: Same name with impossible age/role

Synthetic sample (fictional data built from this scenario; replaced when the real case is supplied): [samples/golden/GC-01](../../samples/golden/GC-01/expected.yaml).

### Conditions

- multiple alias rows point to two unique Profiles;
- name, gender and nationality provide weak-to-medium support;
- an explicitly attributed adult corporate role occurred when the customer was approximately five to six years old.

### Expected processing

- deduplicate by Profile ID;
- preserve name support once within the NAME group;
- create `IMPOSSIBLE_AGE_AT_REPORTED_EVENT` from dated role evidence;
- system result: `NO_MATCH`;
- source classifications remain independent.

### Regression assertions

- must not count alias rows as separate people;
- must not let several weak agreements offset the hard conflict;
- must not convert source Financial Crime into customer identity.

## GC-02: Fraud condition HIT, insufficient identity evidence

Synthetic sample: [samples/golden/GC-02](../../samples/golden/GC-02/expected.yaml).

### Conditions

- exact local-language name and same nationality;
- source keyword explicitly contains Fraud and Tax Evasion;
- no Profile DOB, document, address, contact or comparable employment identifier;
- event timing is plausible, not contradictory;
- legacy case says confirmed non-match without detailed evidence.

### Expected processing

- source condition: `conditionType: ADVERSE_MEDIA`, `HIT`, `riskCategories: [FRAUD, TAX_EVASION]`;
- system identity decision: `REVIEW`;
- imported decision: `NO_MATCH`, `independentlyVerified=false`;
- the documented human outcome (NO_MATCH) is a label for measurement; the system result stays REVIEW.

### Regression assertions

- Fraud HIT must not force identity MATCH;
- temporal plausibility must not count as proof;
- legacy decision must not masquerade as system output.

## GC-03: Multilingual probable match on official wanted list

Synthetic sample: [samples/golden/GC-03](../../samples/golden/GC-03/expected.yaml).

### Conditions

- Vietnamese name with and without diacritics normalizes consistently;
- gender, nationality and birth year agree;
- Profile contains year-only DOB;
- customer resides in Taiwan while the source has a Vietnam address;
- source is an active official wanted list;
- legacy/human review confirms a match.

### Expected processing

- two name hits deduplicate to one Profile;
- DOB comparison is `BIRTH_YEAR_MATCH_FULL_DATE_NOT_COMPARABLE`;
- cross-country addresses form a soft difference, not hard exclusion;
- system identity result: `PROBABLE_MATCH`, externally `REVIEW`;
- source condition: `conditionType: OFFICIAL_WANTED_LIST`, `status: HIT`, `legalStage: WANTED`;
- human decision: `MATCH`;
- final disposition: `ON_LIST`, based on the human decision.

### Regression assertions

- do not treat different current/source addresses as automatic NO_MATCH;
- do not claim full-date agreement;
- do not describe wanted status as conviction.

## GC-11: Taiwanese customer on adverse media with year-only DOB

Real case, anonymized. Inputs and expected results: [samples/golden/GC-11](../../samples/golden/GC-11/expected.yaml).

### Conditions

- customer: Taiwanese man, local name 鐘仁聲, English name JONG JENN SHENG, full DOB, national ID, occupation wholesale and retail, address written with the pre-2010 county name and full-width digits;
- two candidate rows (鐘仁聲 and 钟仁声), both pointing to the same Profile;
- Profile: Taiwanese man, original-script names with 鍾/鐘 and 仁/任 variants, romanizations without "Jenn", two birth years (1973 and 1972), no document, no address, category SIP / Financial Crime;
- attached news: a bank branch manager colluding with a fraud ring; the snippet does not state indictment or judgment;
- the case already carries legacy manual results (符合, 確認後符合).

### Expected processing

- one hit after deduplication;
- list type ADVERSE_MEDIA by category mapping, `riskCategories: [FINANCIAL_CRIME]`;
- name, gender and nationality agree as hit prerequisites;
- DOB: 1973 agrees at YEAR precision; 1972 is recorded as `PROFILE_MULTIPLE_DOB`, not as a conflict;
- no document, employment or address on the Profile side;
- stage 3 row 4: `PROBABLE_MATCH`, externally REVIEW, with `NO_BILATERAL_DIRECT_IDENTIFIER`;
- Article 22 ① not applicable (both Taiwanese); `amlRelevance` stays UNCERTAIN until the reviewer sets it;
- region relevance and judgment lookup are triggered;
- legacy results stored as ImportedDecision only;
- human decision: `MATCH`; final disposition: `ON_LIST`.

### Regression assertions

- two rows must not become two hits;
- legacy 符合 must not become evidence or a SystemDecision;
- a second Profile birth year must not create a conflict, and a year match must not be reported as a full-date match;
- the news job title must not be compared with the customer's occupation as structured evidence;
- fraud must not be treated as unrelated to money laundering.

## GC-12: Related party with a common name and no comparable identifiers

Real case, anonymized; business label 無法判斷. Inputs and expected results: [samples/golden/GC-12](../../samples/golden/GC-12/expected.yaml).

### Conditions

- screened subject: the account holder's legal representative (related party), name 陳志明 (a very common name), full DOB, Taiwanese, no gender field, no document, an ID-shaped reference number, two addresses plus a postal-code-only line;
- Profile: Taiwanese man 陳志明, no DOB, no document, residence country only; related Profiles for an employer company and a wife; categories SIP / Financial Crime and Tax Crime; notes say he ran a nursing home and was indicted with six others;
- attached news: indictment of seven people; the headline's 18-year sentencing request is for another couple; the subject is named among the others;
- business outcome: 無法判斷, a grey area where identity cannot be decided; the legacy 理由 text 確認後不符合 is imported only and is not the identity outcome.

### Expected processing

- comparisons use the related party's own data; the account holder's data, when supplied, is case context only and changes no identity result;
- list type ADVERSE_MEDIA, `riskCategories: [FINANCIAL_CRIME, TAX_CRIME]`, `legalStage: INDICTED`;
- name and nationality agree; gender is SUBJECT_MISSING; nothing else is comparable;
- stage 3 row 6: `REVIEW` with `NO_NON_PREREQUISITE_SUPPORT` and `NO_BILATERAL_DIRECT_IDENTIFIER`;
- judgment lookup not required: the article names the person exactly and reports only indictment and sentencing requests;
- the reviewer cannot resolve identity: `UNRESOLVED` with pending checks (company registry, cross-check platform, asking the customer), Article 22 ③ escalation;
- the senior approver selects `NOT_ON_LIST` (confirmed by business); identity stays unresolved.

### Regression assertions

- the reference number must not become a document;
- a common-name exact match must add no support;
- the other couple's sentencing request must not be attributed to the subject;
- `NOT_ON_LIST` must not be reported as a different person.

## Cross-case regression rules

- retrieval score is never identity probability;
- aliases do not multiply risk;
- source HIT and identity decision remain separate;
- every system decision identifies evidence and decision lineage;
- incomplete data remains visible.

## Validation backlog

GC-04 to GC-10 ([ADR-0008](../adr/0008-validation-and-deferred-capabilities.md)):

- GC-04 PEP/RCA
- GC-05 Sanction
- GC-06 full DOB conflict
- GC-07 highly similar final NO_MATCH
- GC-08 source update result reversal
- GC-09 Article 22 ① foreign PEP presumption
- GC-10 Article 22 ② unrelated adverse news

GC-11 and GC-12 are validated with real data and are not part of the backlog.

```yaml
designStatus: EXTENSION_POINT_RESERVED
validationStatus: PENDING_REAL_CASE
releaseImpact: NON_BLOCKING_FOR_POC_PHASE_1
productionImpact: BLOCKING_FOR_RELEVANT_AUTO_DECISION
```

These are non-blocking for core Phase 1 development but block claims of validated automatic decisions in their respective areas.

## Augmented cases

Rules without a Golden Case are exercised by augmented cases: one controlled change to a Golden Case, run end to end through the reader and the analyzer ([AugmentedCaseTests](../../tests/Aml.Tests/GoldenCases/AugmentedCaseTests.cs)). The base folder is copied and never modified.

They are not Golden Cases:

- not authoritative: their expected values follow the current policy and change with it;
- no human label, so they stay out of the [metrics](poc-delivery-plan.md);
- they cannot validate a hard-conflict rule, which still needs a real case to become an automatic NO_MATCH;
- a real case for the same rule replaces the augmented one.

Changes use the samples' dummy values (`A123456789`, `A223456788` failing the checksum on purpose); no real identifier is introduced. Employment and relatives are not in the bank export, so those cases add them to the subject after reading.

| ID | Base | Change | Rule | Expected |
|---|---|---|---|---|
| AUG-01-01 | GC-01 | customer DOB 1985 → 1972 (18 when named chairman in 1990) | `IMPOSSIBLE_ROLE_AT_REPORTED_EVENT` | fires, REVIEW (not NO_MATCH); Case IN_REVIEW |
| AUG-01-02 | GC-01 | customer DOB 1985 → 1977/3/18 (13 as chairman, 14 as general manager) | `MinimumRoleAge` boundary | chairman NO_MATCH; general manager no conflict, row 6 REVIEW; Case IN_REVIEW |
| AUG-01-03 | GC-01 | customer DOB 1985 → 1960 | plausible age | no hard conflict, row 6 REVIEW on both Profiles |
| AUG-01-04 | GC-01 | chairman from 1990 (year only) | `SOURCE_QUALITY_DOWNGRADE` | REVIEW; the other Profile stays NO_MATCH |
| AUG-02-01 | GC-02 | Profile DOB 1975/9 | month-precision DOB agreement | PARTIAL, MEDIUM support, row 4 PROBABLE_MATCH, still no direct identifier |
| AUG-02-02 | GC-02 | Profile DOB 1980 | `DOB_CONFLICT` | row 1 REVIEW, not FULL_DOB_CONFLICT, no hard conflict |
| AUG-02-03 | GC-02 | news states 林美玲（49歲） | stated age compatible | `AGE_COMPATIBLE_AT_REPORT`, neutral, row 6 unchanged |
| AUG-02-04 | GC-02 | news states 林美玲（30歲） | `AGE_DIFFERENCE_AT_REPORT` | soft conflict, row 1 REVIEW, never NO_MATCH |
| AUG-02-05 | GC-02 | news 分案偵辦 → 起訴 | legal stage | INDICTED; `amlRelevance` stays UNCERTAIN |
| AUG-02-06 | GC-02 | keyword Money Laundering added | keyword risk category | `MONEY_LAUNDERING` added to risk categories |
| AUG-02-07 | GC-02 | SIP subcategory Fraud → Gambling | `UNMAPPED_CATEGORY` | UNKNOWN, `SOURCE_CONDITION_UNKNOWN`, REVIEW; relevance checks stay open |
| AUG-02-08 | GC-02 | Profile names 林美齡 / Lin Mei-Lin | near name | NAME PARTIAL `NOT_IDENTICAL_AFTER_NORMALIZATION`, row 6 REVIEW |
| AUG-02-09 | GC-02 | news: 法院一審判處林美玲有期徒刑 | judgment naming the person | legal stage CONVICTED |
| AUG-02-10 | GC-02 | news: indicted, a co-defendant sentenced | judgment attribution | INDICTED; the co-defendant's sentence is not the subject's |
| AUG-02-11 | GC-02 | news states 林美玲（四十九歲） | Chinese-numeral stated age | `AGE_COMPATIBLE_AT_REPORT` |
| AUG-02-12 | GC-02 | customer DOB written 1975年9月2日 | unparseable value | `UNPARSEABLE:出生日期`, DOB SUBJECT_MISSING |
| AUG-02-13 | GC-02 | category Adverse Media, no keywords, news on drunk driving | Article 22 ② | UNRELATED, set aside, NOT_ON_LIST; identity unchanged |
| AUG-02-14 | GC-02 | news adds 酒駕 to the fraud report | predicate offence | stays UNCERTAIN |
| AUG-03-01 | GC-03 | Profile category → Politically Exposed Person (both sides Vietnamese) | Article 22 ① | ON_LIST presumption, identity unchanged |
| AUG-03-02 | GC-03 | as AUG-03-01, customer nationality removed | Article 22 ① nationality missing | no recommendation, `CONFIRM_NATIONALITY` pending |
| AUG-03-03 | GC-03 | Profile category → Politically Exposed Person / Former PEP | former PEP | `FORMER_PEP`, ON_LIST presumption kept |
| AUG-03-04 | GC-03 | as AUG-03-01, customer nationality → Taiwan | Article 22 ① not applicable | nationality conflict, row 5 REVIEW, no recommendation, no `CONFIRM_NATIONALITY` |
| AUG-03-05 | GC-03 | as AUG-03-01, Profile DOB removed | PEP identity check | `IDENTITY_CHECK` pending |
| AUG-03-06 | GC-03 | Profile category → Relative or Close Associate | RCA | `RCA_UNCONFIRMED`, REVIEW, no presumption |
| AUG-03-07 | GC-03 | Profile category → Sanctions Lists, active named measure | sanction measure | Sanction HIT with program and jurisdiction, REVIEW, HIGH priority |
| AUG-03-08 | GC-03 | as AUG-03-07, measure status Delisted | `SANCTION_MEASURE_NOT_CONFIRMED` | UNKNOWN, `SOURCE_CONDITION_UNKNOWN`, REVIEW |
| AUG-03-09 | GC-03 | wanted list status Removed | inactive official list | OTHER_OFFICIAL_LIST, legal stage not WANTED |
| AUG-03-10 | GC-03 | address block country Taiwan → Viet Nam | `FIELD_CONFLICT` | finding recorded; field and text both kept |
| AUG-03-11 | GC-03 | candidate row PEP flag 是, 黑名單 and 負面新聞 否 | flags first | PEP HIT by FLAG with `FLAG_CATEGORY_CONFLICT`; NO gives NOT_HIT; ① ON_LIST |
| AUG-03-12 | GC-03 | every flag 否 | explicit NO | all NOT_HIT, NOT_APPLICABLE |
| AUG-03-13 | GC-03 | PEP flag 待確認 | `FLAG_UNPARSEABLE` | UNKNOWN |
| AUG-03-14 | GC-03 | 黑名單 flag 是 | blacklist type from source fields | OFFICIAL_WANTED_LIST, WANTED |
| AUG-03-15 | GC-03 | customer and Profile supplied as HTML pages | HTML reader | same result as the export; raw pages hashed |
| AUG-03-16 | GC-03 | case table as HTML with flag checkboxes | checkbox flags | PEP HIT by FLAG; unchecked Is RCA? is NO |
| AUG-11-01 | GC-11 | Profile ID table with the customer's national ID | document agreement | UNIQUE support, row 2 PROBABLE_MATCH |
| AUG-11-02 | GC-11 | Profile national ID with another number | `DOCUMENT_IDENTITY_CONFLICT` | row 1 REVIEW |
| AUG-11-03 | GC-11 | Profile passport only | document not comparable | UNCERTAIN `NOT_COMPARABLE`; row 4 PROBABLE_MATCH unchanged (a document of another type or country does not block row 4) |
| AUG-11-04 | GC-11 | Profile DOB 1973 → 1973/6/12 | full DOB agreement | HIGH support, row 4, direct identifier present |
| AUG-11-05 | GC-11 | Profile DOB 1973 → 1973/8/1 | `FULL_DOB_CONFLICT` (GC-06 stand-in) | row 1 REVIEW |
| AUG-11-06 | GC-11 | Profile deceased, died 1970 | `DEATH_BEFORE_CUSTOMER_BIRTH` | fires, REVIEW, Case IN_REVIEW |
| AUG-11-07 | GC-11 | Profile address = customer address in current form | structured address | `SAME_ADDRESS_NO_PERIOD`, never support, row 4 unchanged |
| AUG-11-08 | GC-11 | full DOB plus the same company with overlapping periods | employment support | row 3 PROBABLE_MATCH |
| AUG-11-09 | GC-11 | an image file and an unrecognised `.txt` holding page source | attachment inventory | `UNSUPPORTED_ATTACHMENT` for both |
| AUG-11-10 | GC-11 | 客戶檢核處理紀錄 table appended to 案件資訊 | legacy processing record | ImportedDecision only; no extra candidate row |
| AUG-11-11 | GC-11 | as AUG-11-10, free text after the table | record boundary | `LEGACY_RECORD_BOUNDARY_UNCLEAR` |
| AUG-12-01 | GC-12 | subject's spouse named as on the Profile | relationship support | MEDIUM support, row 5 REVIEW |
| AUG-12-02 | GC-12 | subject employer = Profile employer, undated | employment without period | UNCERTAIN, row 6 |
| AUG-12-03 | GC-12 | Profile record type Entity | `NON_PERSON_SUBJECT_REVIEW` | REVIEW |
| AUG-12-04 | GC-12 | Profile 其他識別碼 with the related party's reference number | OTHER_IDENTIFIER | LOW support, never DOCUMENT, row 5 REVIEW |
| AUG-12-05 | GC-12 | none | 2026-10-14 ruling | `ART22_RULING_20261014_MAY_APPLY`, recommendation NONE |
