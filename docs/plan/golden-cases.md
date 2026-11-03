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

GC-04 to GC-10 ([ADR-0008](../adr/0008-validation-backlog-non-blocking.md)):

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
