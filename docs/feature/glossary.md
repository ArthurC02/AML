# Glossary and Common States

## Terms

- **CandidateHit**: one retrieval occurrence from a name or route.
- **CandidateProfile**: a unique source Profile after hit deduplication.
- **EvidenceGroup**: correlated evidence for one identity dimension.
- **HardConflict**: approved, source-supported conflict that can exclude a pair.
- **PROBABLE_MATCH**: internal high-priority REVIEW classification.
- **ImportedDecision**: decision imported from a legacy or external process.
- **SystemDecision**: policy-versioned automated result.
- **HumanDecision**: documented reviewer result.
- **FinalDisposition**: effective business result with decision lineage.
- **GoldenCase**: fixed, versioned regression scenario.
- **ValidationBacklog**: pending scenario that restricts related automation but does not block core design.
- **Screened subject**: the customer or related party actually compared with a Profile; comparisons use only that person's data. Other people in the Case (e.g. the account holder when a related party is hit) are case context only.
- **Related party (關聯方)**: a person screened together with the customer in a Case. Not the list type RCA.
- **Hit**: one distinct Profile matched by the customer or a related party; aliases and repeated rows do not add hits.
- **Hit prerequisite**: NAME, NATIONALITY, GENDER; compared and shown, never counted as support.
- **Direct identifier**: full DOB or comparable document key, usable only when present on both sides; birth year or age alone does not count.
- **DispositionRecommendation**: Article 22 recommendation, ON_LIST (符合名單) or NOT_ON_LIST (非名單), separate from identity.
- **VerificationRecord**: a reviewer's check (employment, region, identity, judgment party, other) with search outcome and conclusion.
- **PEP**: politically exposed person. **RCA**: a PEP's relative or close associate. **SIE**: state-owned or state-invested enterprise. **SIP**: Dow Jones Special Interest Person; mapped by subcategory, not SIE.
- **Predicate offence**: a crime producing proceeds that can be laundered; counts as AML-related.
- **Delivered judgment (已宣判)**: any instance has ruled, final or not; indictment, procedural rulings and investigation do not count.

## States

| Kind | Values |
|---|---|
| Identity | `MATCH \| NO_MATCH \| REVIEW \| PROBABLE_MATCH` |
| Source condition | `HIT \| NOT_HIT \| UNKNOWN` |
| List type | `SANCTION \| PEP \| RCA \| SIE \| OFFICIAL_WANTED_LIST \| OTHER_OFFICIAL_LIST \| ADVERSE_MEDIA \| BANK_INTERNAL_LIST` |
| Comparison | `EXACT \| PARTIAL \| CONFLICT \| UNCERTAIN \| SUBJECT_MISSING \| PROFILE_MISSING` |
| Source flag | `YES \| NO \| MISSING \| UNPARSEABLE` |
| AML relevance | `RELATED \| UNRELATED \| UNCERTAIN` |
| Disposition | `ON_LIST \| NOT_ON_LIST` |
| Human decision | `MATCH \| NO_MATCH \| UNRESOLVED` |
| Pair screening | `HIT \| NO_HIT \| REVIEW \| NOT_APPLICABLE \| NOT_EVALUATED` |
| Data quality | `PRESENT \| MISSING \| UNPARSEABLE \| AMBIGUOUS \| FIELD_CONFLICT \| NOT_APPLICABLE` |
| Execution | `PENDING \| RUNNING \| COMPLETED \| PARTIAL \| FAILED` |
| Case | `OPEN \| IN_REVIEW \| CLOSED \| REOPENED` |

## Policy families

- IdentityDecisionPolicy
- SanctionPolicy
- PepPolicy
- OfficialListPolicy
- AdverseInformationPolicy
- DispositionPolicy
- RiskPolicy
- ResultPublicationPolicy

Every policy output includes policy ID/version, reason codes and evidence references. Registered reason codes are in [identity adjudication](identity-adjudication.md#reason-code-registry).
