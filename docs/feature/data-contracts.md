# Data Contracts

> Shared by all modules.

## Published schemas

The stored analysis result and explanation, each revision's lineage and quality, and the views over them ([views](pipeline-api-and-batch.md#views)) are Core records serialized with `CaseJson.Options` (camelCase, enums as UPPER_SNAKE strings, nulls left out). Their JSON Schemas are generated from the records with the .NET `JsonSchemaExporter` and served by `GET /api/schemas/{name}`; they are the reference for field names and meanings, and this page explains the main records and concepts.

- Every field carries a description, taken from `[Description]` on the record.
- `schemaVersion` is in every stored result and explanation and in the schema `$id` (`aml:CaseAnalysis:v<schemaVersion>`). A changed record raises it, and the PoC database is rebuilt ([persistence](persistence.md#structure)).
- Personal data is marked `x-sensitivity`: `PERSONAL` for names, dates of birth and death, addresses and text quoting them; `IDENTIFIER` for document and identifier numbers, masked or not. A field that can hold either, such as the subject and Profile values of a comparison row, carries `IDENTIFIER`. An unmarked field is public ([security and privacy](security-and-privacy.md#data-classification)).
- Codes in any field are explained by `GET /api/codes` ([code catalog](pipeline-api-and-batch.md#rest-resources)).

## Core identifiers

- `caseId`
- `subjectRef`
- `triggerEventId`
- `revision` (`analysisRevision` on a batch item)
- `profileKey = "DJ:" + profileId`; the Case and hit views carry both
- `listVersion` and `contentHash` of each Profile a revision used (lineage)
- `batchId / jobId`

## CustomerSnapshot

The record is `ScreenedSubject`, the customer or one related party: role and relationship, names, gender, date of birth, nationalities, document, reference number, tax ID, occupation, addresses, employments, relatives, party type and findings. It is not part of the stored result: the revision stores its input files instead, with their hashes in the lineage.

## SourceProfileSnapshot

The record is `SourceProfile`, at `profile` of each hit: profile key, record type, gender, names, dates of birth and death with precision, countries (citizenship, address, allegation), documents and other identifiers, address lines, categories, keywords, roles, relationships, official-list entry, findings and source references.

## CandidateRow

One row of the candidate table in 案件資訊, read by header label ([candidate table](extraction-and-normalization.md#candidate-table)):

```yaml
matchedName: 陳志明       # 姓名/名稱
profileId: "14001741"     # 名單資料編號
legacyReason: null        # 理由; ImportedDecision only, never evidence
isRca: false              # true when Is RCA? reads YES
flags:                    # source flags exactly as read
  pep: YES | NO | MISSING | UNPARSEABLE           # Name Property → PEP
  blacklist: YES | NO | MISSING | UNPARSEABLE     # Name Property → 黑名單
  adverseNews: YES | NO | MISSING | UNPARSEABLE   # Name Property → 負面新聞
  rca: YES | NO | MISSING | UNPARSEABLE           # Is RCA? as read
```

`NO` is a value, not unknown ([flag reading](extraction-and-normalization.md#source-flags)). `rca` is kept so a NO can be told apart from a blank; it is not one of the three list flags. How the flags, `isRca` and the categories decide the list types is in [list-type determination](watchlist-and-adverse-information.md#list-type-determination). Several rows of one Profile are merged flag by flag: YES wins, then UNPARSEABLE, then NO, else MISSING.

## CandidateHit

A retrieval occurrence produced by a name or route. It is not a person and not a decision.

## CandidateProfile

A deduplicated Profile with all matching hits attached.

## Evidence

```yaml
group: DATE_OF_BIRTH
direction: PREREQUISITE | SUPPORT | CONFLICT | NEUTRAL
status: EXACT | PARTIAL | CONFLICT | UNCERTAIN
      | SUBJECT_MISSING | PROFILE_MISSING
strength: NONE | LOW | MEDIUM | HIGH | UNIQUE
detail: BIRTH_YEAR_MATCH_FULL_DATE_NOT_COMPARABLE   # code, see /api/codes
note: null                # reviewer text
isSupport: false          # derived: direction is SUPPORT
```

| status | When |
|---|---|
| `EXACT` | values identical after normalization |
| `PARTIAL` | only part agrees (spelling variant, name order, birth year only); the explanation says which part |
| `CONFLICT` | same field, same period, values genuinely differ |
| `UNCERTAIN` | both present but ambiguous, from different periods or period unknown; direction is NEUTRAL |
| `SUBJECT_MISSING` / `PROFILE_MISSING` | one side has no value; no other status is allowed then |

`PREREQUISITE` marks NAME, NATIONALITY and GENDER evidence: compared and shown, never counted as support ([identity adjudication](identity-adjudication.md#hit-prerequisites-and-direct-identifiers)).

## IdentityResult

The identity decision of one hit, at `identity` in the stored result; the hit's `profile` carries the `profileKey`.

```yaml
evidence: {}              # one Evidence per evidence group, keyed by group
hardConflict: null        # stage 1 code, e.g. IMPOSSIBLE_AGE_AT_REPORTED_EVENT
stage3Row: 4              # 1 to 6; null after a hard conflict and for some non-Person records
decision: NO_MATCH | REVIEW | PROBABLE_MATCH
reasonCodes: []           # the first is the deciding reason
```

Phase 1 has no MATCH decision. The explanation of each hit restates the decision with its support, conflicts and unknown fields ([explainability](identity-adjudication.md#explainability)).

## SourceConditionResult

```yaml
type: SANCTION | PEP | RCA | SIE | OFFICIAL_WANTED_LIST
    | OTHER_OFFICIAL_LIST | ADVERSE_MEDIA | BANK_INTERNAL_LIST | UNKNOWN
status: HIT | NOT_HIT | UNKNOWN
basis: FLAG | CATEGORY_MAPPING | UNMAPPED
riskCategories: []        # e.g. [FRAUD, TAX_EVASION]; ADVERSE_MEDIA only
legalStage: UNKNOWN       # UNKNOWN | ALLEGED | INDICTED | WANTED | CONVICTED | ACQUITTED | DISMISSED;
                          # source wording preserved, never upgraded
amlRelevance: null        # ADVERSE_MEDIA only: RELATED | UNRELATED | UNCERTAIN
policyVersion: source-policy-0.2
reasonCodes: []
pepType: null             # PEP and RCA only, see /api/codes
program: null             # SANCTION with a confirmed measure: the list name
jurisdiction: null        # SANCTION with a confirmed measure: the authority
```

The record is `SourceCondition`, at `conditions` of each hit. `type` is the bank's list type; `UNKNOWN` is an unmapped source category, left for the reviewer; `riskCategories` carries what the source alleges ([ADR-0005](../adr/0005-list-types-and-risk-categories.md)). A "Fraud HIT" is therefore an `ADVERSE_MEDIA` SourceConditionResult with `status: HIT` and `FRAUD` in `riskCategories`, not a separate condition type. Category and legal-stage values are listed in [watchlist and adverse information](watchlist-and-adverse-information.md). `amlRelevance` feeds Article 22 ② ([disposition recommendation](review-and-disposition.md#article-22-2-adverse-news-unrelated-to-mltfpf)).

## ScreeningAssessment

```text
HIT | NO_HIT | REVIEW | NOT_APPLICABLE | NOT_EVALUATED
```

### Combination matrix

Canonical pair-level matrix ([ADR-0004](../adr/0004-identification-only.md)), applied by `AssessmentPolicy` to Sanction, PEP/RCA and watchlist conditions alike.

| Source condition | Identity | Pair result |
|---|---|---|
| HIT | NO_MATCH | NO_HIT |
| HIT | REVIEW / PROBABLE_MATCH | REVIEW |
| NOT_HIT | any | NOT_APPLICABLE |
| UNKNOWN | REVIEW / PROBABLE_MATCH | REVIEW |
| UNKNOWN | NO_MATCH | NO_HIT |

- Phase 1 has no identity MATCH, so the system never produces a pair HIT.
- A pair with an UNKNOWN condition carries `SOURCE_CONDITION_UNKNOWN`, whatever its result.
- A pair with several conditions takes the highest result (NOT_APPLICABLE < NO_HIT < REVIEW < HIT); a pair with none is NOT_APPLICABLE.
- Phase 1 release control: a pair with a HIT or UNKNOWN Sanction, PEP or RCA condition that would be NO_HIT is REVIEW with `RELEASE_CONTROL_HUMAN_REVIEW`.
- A candidate Profile without Profile data is not evaluated: it is listed in `notEvaluatedProfiles`, and the Case result is NOT_EVALUATED.

PROBABLE_MATCH is never exposed; it surfaces as REVIEW with high priority. Case-level aggregation is in [case management](case-management.md#case-level-result).

## DispositionRecommendation

```yaml
recommendation: ON_LIST | NOT_ON_LIST   # null when no rule resolves the pair
reasonCodes: []           # e.g. ART22_FOREIGN_PEP_PRESUMPTION
pendingChecks: []         # checks still to run outside the system, with or without a recommendation
advisory: true            # always: the final selection happens outside the system
```

Never changes the IdentityResult or the SourceConditionResults ([ADR-0004](../adr/0004-identification-only.md)).

## VerificationRecord

The system has no verification-record type. It lists each triggered check as a code in `pendingChecks` of the DispositionRecommendation: `EMPLOYMENT_RELEVANCE`, `FORMER_PEP_INFLUENCE`, `REGION_RELEVANCE`, `IDENTITY_CHECK`, `JUDGMENT_PARTY_IDENTITY`, and `CONFIRM_NATIONALITY` from ①. The checks are performed and recorded outside the system ([ADR-0004](../adr/0004-identification-only.md)); their types, search outcomes, conclusions and rules are in [disposition recommendation](review-and-disposition.md#verification-records).

## Field value state

An input field that can be absent or unclear carries a value state: `FieldValue` (the tax ID) and each `Address`.

```yaml
value: null               # the value as read; null when MISSING
state: PRESENT | MISSING | AMBIGUOUS
detail: null              # finding code, e.g. MALFORMED, POSTAL_CODE_ONLY
```

An address keeps its raw and normalized text, its country and a `conflict` flag set when the block's country field contradicts the address text (FIELD_CONFLICT). A value the reader cannot parse is a finding (`UNPARSEABLE:<field>`), not a state; a date carries its precision (YEAR, MONTH or DAY).

"Missing" is always expressed by the state, never by an empty string (Oracle stores `''` as NULL; see [persistence](persistence.md#writing-rules)).
