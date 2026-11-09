# Disposition Recommendation

> Module: Source Screening and Risk Assessment. Source: bank operating procedure Article 22 and the legacy name-screening rules ([ADR-0004](../adr/0004-identification-only.md)).

## Two results, read separately

| Result | Question | Produced by |
|---|---|---|
| IdentityDecision | Is this the same person? | IdentityDecisionPolicy ([identity adjudication](identity-adjudication.md)) |
| DispositionRecommendation | What does Article 22 recommend selecting? | DispositionPolicy (this spec) |

The system focuses on identification ([ADR-0004](../adr/0004-identification-only.md)): it produces the identity decision and an advisory recommendation. Final disposition, reviewer actions and approvals happen outside the system and are not modelled.

A NOT_ON_LIST recommendation does not prove a different person, and an ON_LIST presumption does not prove the same person. Neither may be derived from the other.

## Article 22 mapping

Article 22 covers message scanning (AML SWIFT/RTGS) and name screening of individuals, legal persons and groups. This system covers name screening only.

| Article 22 | System meaning |
|---|---|
| Individual initial check: name, nationality, DOB | NAME, NATIONALITY, DATE_OF_BIRTH groups; all three are always compared |
| Legal person / group initial check: name and responsible person's name | non-person Profile; REVIEW in Phase 1 ([system overview](system-overview.md#excluded-for-phase-1)); never compared with a natural person's nationality and DOB |
| On list: every item the same | identity MATCH; automatic MATCH is disabled in Phase 1, so the pair stays REVIEW |
| On list: non-Taiwanese subject hits a foreign PEP and data is insufficient | rule ① below |
| Not on list: every item different | identity NO_MATCH; automatic only for validated hard conflicts, otherwise REVIEW |
| Not on list: adverse news unrelated to ML/TF/PF | rule ② below |
| Cannot be determined | rule ③ below |

"Every item the same" and "every item different" keep their literal meaning: a missing value is neither the same nor different, and one differing field is not "every item different".

## DispositionPolicy

`Screening/DispositionPolicy` is evaluated per pair after the source-condition policies. Its inputs are the IdentityComparison and the SourceConditionResults of one pair; its result (`recommendation`, reason codes, pending checks) is on every hit and in the explanation's conclusion. Every recommendation carries `advisory: true`, so machine-readable output states that it is for reference only.

### Default

- identity MATCH with at least one remaining HIT condition → ON_LIST;
- identity MATCH with no HIT condition left (all NOT_HIT or set aside) → NOT_ON_LIST;
- identity NO_MATCH → NOT_ON_LIST, reason code `ART22_IDENTITY_NO_MATCH`;
- otherwise no automatic recommendation; the pair stays REVIEW.

Automatic MATCH is disabled in Phase 1, so the two MATCH rows never apply in Phase 1: a PROBABLE_MATCH pair that ① and ② do not resolve gets no recommendation.

### Article 22-1: foreign PEP presumption

Preconditions, all required:

- both the screened subject and the Profile are confirmed non-Taiwanese, using nationality-labelled fields only (nationality, citizenship); residence and allegation countries do not count;
- the Profile has a PEP condition that is HIT or UNKNOWN, or an UNKNOWN condition whose category could not be mapped (`UNMAPPED_CATEGORY`) and might be PEP; an unmapped category does not count when the PEP flag is NO, and an undecided 黑名單 type (`BLACKLIST_TYPE_UNDECIDED`) never counts;
- identity is REVIEW or PROBABLE_MATCH.

| Subject \ Profile nationality | Taiwan | Non-Taiwan | Missing or ambiguous |
|---|---|---|---|
| Taiwan | not applicable | not applicable | not applicable |
| Non-Taiwan | not applicable | applicable | manual confirmation |
| Missing or ambiguous | not applicable | manual confirmation | manual confirmation |

Effect: ON_LIST, reason code `ART22_FOREIGN_PEP_PRESUMPTION`. The identity decision is unchanged. Nationality remains a hit prerequisite: the presumption comes from the rule, not from nationality as evidence.

Exception: same name but a clearly different background (e.g. a known migrant worker) returns to normal review. The system cannot detect this from source data; it is out of scope.

A side is non-Taiwanese only when every nationality-labelled value maps to a country code other than TW. A value that did not map to a country (recorded as `AMBIGUOUS:<field>`) makes that side ambiguous, even next to a mapped value. Another party's nationality is never applied to the subject. When manual confirmation is required, `CONFIRM_NATIONALITY` is added to the pending checks.

① is exercised by [AUG-03-01 and AUG-03-02](../plan/golden-cases.md#augmented-cases); GC-09 is in the validation backlog.

### Article 22-2: adverse news unrelated to ML/TF/PF

- `amlRelevance` on an `ADVERSE_MEDIA` condition is `RELATED | UNRELATED | UNCERTAIN`, judged from the concrete conduct and jurisdiction, never from offence keywords alone.
- A predicate offence counts as RELATED even if the source does not mention money laundering.
- `amlRelevance` defaults to UNCERTAIN. The system extracts UNRELATED only when the attached news states a jurisdiction (dateline or `Country of Reported Allegation`) and a sentence naming the Profile person states a conduct from a controlled non-predicate list (酒駕, 過失傷害, 妨害名譽 …), with no predicate conduct (洗錢防制法 Article 3), no serious-consequence marker (致死, 致重傷, 逃逸 …) anywhere in the news, and no SIP crime category or risk keyword. This is a keyword heuristic pending an approved extraction ([AdverseInformationPolicy](watchlist-and-adverse-information.md#adverseinformationpolicy)). RELATED is never extracted; it is the reviewer's judgement (GC-02 and GC-11 stay UNCERTAIN).
- UNRELATED sets aside that `ADVERSE_MEDIA` condition only, reason code `ART22_ADVERSE_UNRELATED`. If every other condition is NOT_HIT, the recommendation is NOT_ON_LIST; PEP, Sanction or other conditions on the same Profile still apply.
- The relevance judgement says nothing about identity.
- ② is exercised by [AUG-02-13](../plan/golden-cases.md#augmented-cases); GC-10 is in the validation backlog.

### Article 22-3: cannot be determined

- A REVIEW pair that no rule above resolves gets no recommendation, reason code `ART22_NOT_DETERMINED`.
- The explanation lists the checks still pending, following the [verification-record](#verification-records) triggers plus `CONFIRM_NATIONALITY` from ①, and never reports a pending check as done.

## Direct identifiers and the 2026-10-14 ruling

A direct identifier is a full date of birth (DAY precision) or a comparable document key. It counts only when the same identifier is present on both sides; a DOB on one side and a document on the other does not. The absence is recorded as `NO_BILATERAL_DIRECT_IDENTIFIER`.

The bank ruled on 2026-10-14 that, outside Article 22 ①, a hit lacking a bilateral direct identifier with a match likelihood below 65% is not on list. This system has no likelihood ([ADR-0001](../adr/0001-identity-adjudication.md)); how the ruling applies to REVIEW and PROBABLE_MATCH pairs is an [open question](../plan/open-questions.md#article-22). Until it is answered the ruling is flagged, not applied: a row 6 pair with `NO_BILATERAL_DIRECT_IDENTIFIER` and no other recommendation records `ART22_RULING_20261014_MAY_APPLY` and keeps no recommendation (GC-12).

## Verification records

Verification records describe checks that confirm or weaken identity, plus the former-PEP influence assessment. The checks are performed outside the system; the explanation lists the triggered ones as pending and never invents a result.

| Type | Triggered when | Results |
|---|---|---|
| `EMPLOYMENT_RELEVANCE` | PEP condition HIT or UNKNOWN | RELEVANT / NOT_RELEVANT / UNDETERMINED / NOT_APPLICABLE |
| `FORMER_PEP_INFLUENCE` | PEP condition HIT or UNKNOWN on a former PEP, or RCA condition HIT or UNKNOWN whose subcategory marks the PEP as former ([former PEP](pep-rca-screening.md#former-pep)) | risk-based assessment of remaining influence (認定標準 Articles 5 and 8); no fixed period |
| `REGION_RELEVANCE` | ADVERSE_MEDIA condition HIT or UNKNOWN | RELEVANT / NOT_RELEVANT / UNDETERMINED / NOT_APPLICABLE |
| `IDENTITY_CHECK` | PEP condition HIT, no RCA condition, Profile has no date of birth | CONSISTENT / INCONSISTENT / UNCONFIRMED / NOT_APPLICABLE |
| `JUDGMENT_PARTY_IDENTITY` | natural-person Profile with attached news or an ADVERSE_MEDIA condition | ESTABLISHED / NOT_ESTABLISHED / UNCONFIRMED / NOT_MENTIONED / NOT_APPLICABLE |
| `OTHER` | any other check actually performed | VERIFIED / UNCONFIRMED / NOT_APPLICABLE |

A check that is not triggered is not listed; an unmapped list type leaves the trigger open. A performed check is reported with method, date, source, findings, attribution, comparison and reasoning, under these rules:

- Employment is compared for the same period; a past office against a current occupation with no same-period data is UNDETERMINED. Different employment or region is weak evidence and never alone a different person.
- Identity checks look up the Profile person only (name plus office), not spouse or relatives.
- Not finding information is normal: UNCONFIRMED, never NOT_MATCH.
- A judgment counts as delivered (已宣判) once any instance has ruled; indictment, procedural rulings and investigation do not. Judgment lookup is skipped (NOT_MENTIONED) only when every article clearly mentions no delivered judgment, with a quoted sentence naming the person exactly as the Profile does; a pronoun such as 「王姓男子」 does not qualify.
- The search outcome is recorded separately from the conclusion:

| Search outcome | Allowed conclusion |
|---|---|
| `RETRIEVED` | ESTABLISHED, NOT_ESTABLISHED or UNCONFIRMED |
| `NOT_FOUND` | UNCONFIRMED |
| `UNAVAILABLE` (network/system) | UNCONFIRMED |
| `PARSE_FAILED` | UNCONFIRMED |

- Without the full judgment text, the conclusion cannot be ESTABLISHED or NOT_ESTABLISHED. Masked public judgments with the same name are UNCONFIRMED; clearly contradictory party data can support a different person.
- Instance level and finality are recorded. Failed lookups are not retried automatically.
- Conclusions are not rewritten to fit a recommendation.

## Prior manual decisions

Earlier manual decisions found in the input ("是否符合名單", "理由", 客戶檢核處理紀錄) are stored as ImportedDecision and never used as evidence ([extraction](extraction-and-normalization.md#legacy-review-records)). Source flags are not decisions and remain usable.
