# Review and Disposition

> Module: Case and Adjudication Management. Design baseline 2.0-PoC, 2026-11-02. Source: bank operating procedure Article 22 and the legacy name-screening rules ([ADR-0017](../adr/0017-article-22-disposition-policy.md)).

## Three results, read separately

| Result | Question | Produced by |
|---|---|---|
| IdentityDecision | Is this the same person? | IdentityDecisionPolicy ([identity adjudication](identity-adjudication.md)) |
| DispositionRecommendation | What does Article 22 tell the reviewer to select? | DispositionPolicy (this spec) |
| FinalDisposition | What was actually decided, and on which basis? | human, in Phase 1 |

A NOT_ON_LIST recommendation does not prove a different person, and an ON_LIST presumption does not prove the same person. Neither may be derived from the other.

## Roles

| Role | Bank title | Responsibility |
|---|---|---|
| Reviewer | 經辦 | records HumanDecision, verification records and reason codes |
| Approver | 覆核主管 | approves the reviewer's decision; required before CLOSED |
| Senior approver | 上階主管 | approves unresolved cases under Article 22 ③ |

Only people with the role perform the bank-internal channels: the AML system selection, the Fubon subsidiary customer cross-check platform (子公司既有客戶跨查平台) and asking the customer. Without an execution record, the system lists them as pending; it never reports them as done.

## Article 22 mapping

Article 22 covers message scanning (AML SWIFT/RTGS) and name screening of individuals, legal persons and groups. This system covers name screening only.

| Article 22 | System meaning |
|---|---|
| Individual initial check: name, nationality, DOB | NAME, NATIONALITY, DATE_OF_BIRTH groups; all three are always compared |
| Legal person / group initial check: name and responsible person's name | non-person Profile; REVIEW in Phase 1 ([system overview](system-overview.md#excluded-for-phase-1)); never compared with a natural person's nationality and DOB |
| On list: every item the same | identity MATCH; automatic MATCH is disabled in Phase 1, so a human confirms |
| On list: non-Taiwanese subject hits a foreign PEP and data is insufficient | rule ① below |
| Not on list: every item different | identity NO_MATCH; automatic only for validated hard conflicts, otherwise a human decides |
| Not on list: multiple channels used and the subject still appears not to be on the list | HumanDecision NO_MATCH with verification records for each channel used |
| Not on list: adverse news unrelated to ML/TF/PF | rule ② below |
| Cannot be determined | rule ③ below |

"Every item the same" and "every item different" keep their literal meaning: a missing value is neither the same nor different, and one differing field is not "every item different".

## DispositionPolicy

Evaluated after source-condition policies. Inputs are the IdentityComparison and the SourceConditionResults of one pair.

### Default

- identity MATCH with at least one remaining HIT condition → ON_LIST;
- identity MATCH with no HIT condition left (all NOT_HIT or set aside) → NOT_ON_LIST;
- identity NO_MATCH → NOT_ON_LIST;
- otherwise no automatic recommendation; the pair stays REVIEW.

### Article 22-1: foreign PEP presumption

Preconditions, all required:

- both the screened subject and the Profile are confirmed non-Taiwanese, using nationality-labelled fields only (nationality, citizenship); residence and allegation countries do not count;
- the Profile has a PEP condition with status HIT, or the list type is still UNKNOWN because its category could not be mapped;
- identity is REVIEW or PROBABLE_MATCH.

| Subject \ Profile nationality | Taiwan | Non-Taiwan | Missing or ambiguous |
|---|---|---|---|
| Taiwan | not applicable | not applicable | not applicable |
| Non-Taiwan | not applicable | applicable | manual confirmation |
| Missing or ambiguous | not applicable | manual confirmation | manual confirmation |

Effect: ON_LIST, reason code `ART22_FOREIGN_PEP_PRESUMPTION`. The identity decision is unchanged. Nationality remains a hit prerequisite: the presumption comes from the rule, not from nationality as evidence.

Exception: same name but a clearly different background (e.g. a known migrant worker) returns to normal review. The system cannot detect this; the reviewer records it with reason code `ART22_BACKGROUND_CLEARLY_DIFFERENT`.

Only nationality values that map unambiguously to a country are used; another party's nationality is never applied to the subject. When manual confirmation is required, "confirm nationality" becomes a pending verification.

### Article 22-2: adverse news unrelated to ML/TF/PF

- `amlRelevance` on an `ADVERSE_MEDIA` condition is `RELATED | UNRELATED | UNCERTAIN`, judged from the concrete conduct and jurisdiction, never from offence keywords alone.
- A predicate offence counts as RELATED even if the source does not mention money laundering.
- In Phase 1 the reviewer sets `amlRelevance`; the system defaults to UNCERTAIN.
- UNRELATED sets aside that `ADVERSE_MEDIA` condition only, reason code `ART22_ADVERSE_UNRELATED`. If no other HIT condition remains, the recommendation is NOT_ON_LIST; PEP, Sanction or other conditions on the same Profile still apply.
- The relevance judgement says nothing about identity.

### Article 22-3: cannot be determined

- Based on the identity decision, not on the recommendation.
- An unresolved HumanDecision (`UNRESOLVED`) lists at least one pending verification and the channels already used: AML system data, public information, the subsidiary cross-check platform, asking the customer.
- The FinalDisposition stays binary: the senior approver selects ON_LIST or NOT_ON_LIST; UNRESOLVED is never a disposition.
- The reviewer records reason code `ART22_UNRESOLVED_ESCALATION`; the pair cannot be closed without senior approval. The explanation keeps the original recommendation, appends "尚待執行的查證：〈事項一〉；〈事項二〉。" and the fixed notice; the Case summary lists each pending item prefixed with its Profile ID. While any pair is unresolved, the notice stays:

> 若窮盡判斷方式後仍無法釐清，屬第22條「無法判斷」案件，建議呈送覆核主管之上階主管簽核。

## Direct identifiers and the 2026-10-14 ruling

A direct identifier is a full date of birth (DAY precision) or a comparable document key. It counts only when the same identifier is present on both sides; a DOB on one side and a document on the other does not. The absence is recorded as `NO_BILATERAL_DIRECT_IDENTIFIER`.

The bank ruled on 2026-10-14 that, outside Article 22 ①, a hit lacking a bilateral direct identifier with a match likelihood below 65% is not on list. This system has no likelihood ([ADR-0018](../adr/0018-legacy-ai-report-rules-not-adopted.md)); how the ruling applies to REVIEW and PROBABLE_MATCH pairs is an [open question](../plan/open-questions.md#article-22).

## Verification records

Reviewers attach verification records to a HumanDecision.

| Type | Triggered when | Results |
|---|---|---|
| `EMPLOYMENT_RELEVANCE` | PEP condition HIT | RELEVANT / NOT_RELEVANT / UNDETERMINED / NOT_APPLICABLE |
| `REGION_RELEVANCE` | ADVERSE_MEDIA condition HIT | same |
| `IDENTITY_CHECK` | PEP flag explicitly Yes, `Is RCA?` not Yes, Profile has no DOB or age | CONSISTENT / INCONSISTENT / UNCONFIRMED / NOT_APPLICABLE |
| `JUDGMENT_PARTY_IDENTITY` | natural-person Profile with attached news or an ADVERSE_MEDIA condition | ESTABLISHED / NOT_ESTABLISHED / UNCONFIRMED / NOT_MENTIONED / NOT_APPLICABLE |
| `OTHER` | any other check actually performed | VERIFIED / UNCONFIRMED / NOT_APPLICABLE |

The system pre-fills NOT_APPLICABLE only for checks not triggered; an unmapped list type leaves the trigger to the reviewer. Each record holds method, date, source, findings, attribution, comparison and reasoning.

Rules:

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
- Conclusions are not rewritten to fit the final disposition.

## Prior manual decisions

Earlier manual decisions found in the input ("是否符合名單", "理由", 客戶檢核處理紀錄) are stored as ImportedDecision and never used as evidence ([extraction](extraction-and-normalization.md#legacy-review-records)). Source flags are not decisions and remain usable.
