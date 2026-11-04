# Data Contracts

> Shared by all modules.

## Published schemas

The stored analysis result and explanation, each revision's lineage and quality, and the views over them ([views](pipeline-api-and-batch.md#views)) are Core records serialized with `CaseJson.Options` (camelCase, enums as UPPER_SNAKE strings, nulls left out). Their JSON Schemas are generated from the records with the .NET `JsonSchemaExporter` and served by `GET /api/schemas/{name}`; they are the reference for field names and meanings, and this page describes concepts only.

- Every field carries a description, taken from `[Description]` on the record.
- `schemaVersion` is in every stored result and explanation and in the schema `$id` (`aml:CaseAnalysis:v1`). A changed record raises it, and the PoC database is rebuilt ([persistence](persistence.md#structure)).
- Personal data is marked `x-sensitivity`: `PERSONAL` for names, dates of birth and death, addresses and text quoting them; `IDENTIFIER` for document and identifier numbers, masked or not. A field that can hold either, such as the subject and Profile values of a comparison row, carries `IDENTIFIER`. An unmarked field is public ([security and privacy](security-and-privacy.md#data-classification)).
- Codes in any field are explained by `GET /api/codes` ([code catalog](pipeline-api-and-batch.md#rest-resources)).

## Core identifiers

- `caseId`
- `subjectRef`
- `triggerEventId`
- `analysisRevision`
- `profileKey = sourceNamespace + profileId`
- `sourceVersion`
- `evidenceId`
- `decisionId`
- `batchId / itemId`

## CustomerSnapshot

Contains identity, customer context, source version, extraction version and evidence references.

## SourceProfileSnapshot

Contains names, dates with precision, countries with roles, documents, addresses, relationships, classifications, keywords, notes, legal-status facts and source references.

## CandidateRow

One row of the candidate table in 案件資訊, read by header label ([candidate table](extraction-and-normalization.md#candidate-table)):

```yaml
matchedName: 陳志明       # 姓名/名稱
profileId: "14001741"     # 名單資料編號
legacyReason: null        # 理由; ImportedDecision only, never evidence
isRca: false              # true when Is RCA? reads YES
listFlags:                # source flags exactly as read
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
evidenceId: ev-001
group: DATE_OF_BIRTH
direction: SUPPORT | CONFLICT | UNKNOWN | PREREQUISITE
strength: LOW | MEDIUM | HIGH | UNIQUE
comparisonStatus: EXACT | PARTIAL | CONFLICT | UNCERTAIN
                | SUBJECT_MISSING | PROFILE_MISSING
asOf: null                # time point both values refer to, when known
rawValues: []
normalizedValues: []
sourceRefs: []
ruleVersion: normalization-v2
```

| comparisonStatus | When |
|---|---|
| `EXACT` | values identical after normalization |
| `PARTIAL` | only part agrees (spelling variant, name order, birth year only); the explanation says which part |
| `CONFLICT` | same field, same period, values genuinely differ |
| `UNCERTAIN` | both present but ambiguous, from different periods or period unknown; direction is UNKNOWN |
| `SUBJECT_MISSING` / `PROFILE_MISSING` | one side has no value; no other status is allowed then |

`PREREQUISITE` marks NAME, NATIONALITY and GENDER evidence: compared and shown, never counted as support ([identity adjudication](identity-adjudication.md#hit-prerequisites-and-direct-identifiers)).

## IdentityComparison

```yaml
subjectRef: customer-001
profileKey: DJ:14001741
customerVersion: c-1
profileVersion: p-3
matchPolicyVersion: identity-policy-0.1
supportingEvidence: []
conflicts: []
unknownFields: []
identityDecision: MATCH | NO_MATCH | REVIEW | PROBABLE_MATCH
reasonCodes: []
```

## SourceConditionResult

```yaml
conditionType: SANCTION | PEP | RCA | SIE | OFFICIAL_WANTED_LIST
             | OTHER_OFFICIAL_LIST | ADVERSE_MEDIA | BANK_INTERNAL_LIST
status: HIT | NOT_HIT | UNKNOWN
basis: FLAG | CATEGORY_MAPPING | UNMAPPED
riskCategories: []        # e.g. [FRAUD, TAX_EVASION]; empty for SANCTION/PEP/RCA
legalStage: UNKNOWN       # source wording preserved, never upgraded
amlRelevance: null        # ADVERSE_MEDIA only: RELATED | UNRELATED | UNCERTAIN
sourceField: sourceKeywords
rawValue: fraud
policyVersion: source-policy-0.1
evidenceRefs: []
reasonCodes: []
```

`conditionType` is the bank's list type ([ADR-0016](../adr/0016-bank-list-type-vocabulary.md)); `riskCategories` carries what the source alleges ([ADR-0005](../adr/0005-watchlist-model-beyond-fraud.md)). A "Fraud HIT" is therefore an `ADVERSE_MEDIA` SourceConditionResult with `status: HIT` and `FRAUD` in `riskCategories`, not a separate condition type. Category and legal-stage values are listed in [watchlist and adverse information](watchlist-and-adverse-information.md). `amlRelevance` feeds Article 22 ② ([disposition recommendation](review-and-disposition.md#article-22-2-adverse-news-unrelated-to-mltfpf)).

## ScreeningAssessment

```text
HIT | NO_HIT | REVIEW | NOT_APPLICABLE | NOT_EVALUATED
```

### Combination matrix

Canonical pair-level matrix ([ADR-0004](../adr/0004-independent-identity-and-source-condition.md)); Sanction, PEP/RCA and watchlist screening apply it unchanged.

| Source condition | Identity | Pair result |
|---|---|---|
| HIT | MATCH | HIT |
| HIT | NO_MATCH | NO_HIT |
| HIT | REVIEW / PROBABLE_MATCH | REVIEW |
| NOT_HIT | any evaluated identity | NOT_APPLICABLE |
| UNKNOWN | MATCH / REVIEW / PROBABLE_MATCH | REVIEW |
| UNKNOWN | NO_MATCH | NO_HIT, while retaining source UNKNOWN |
| any | not evaluated (stage failed, timed out or not run) | NOT_EVALUATED |
| not evaluated | any | NOT_EVALUATED |

PROBABLE_MATCH is never exposed; it surfaces as REVIEW with high priority. Case-level aggregation is in [case management](case-management.md#case-level-result).

## DispositionRecommendation

```yaml
subjectRef: customer-001
profileKey: DJ:14001741
recommendation: ON_LIST | NOT_ON_LIST | NONE
advisory: true            # always: the final selection happens outside the system
basis: IDENTITY | ART22_FOREIGN_PEP_PRESUMPTION | ART22_ADVERSE_UNRELATED
setAsideConditions: []    # conditions excluded by Article 22 ②
policyVersion: disposition-policy-0.1
reasonCodes: []
```

Never changes the IdentityComparison or SourceConditionResults ([ADR-0017](../adr/0017-article-22-disposition-policy.md)).

## VerificationRecord

```yaml
verificationId: vr-001
type: EMPLOYMENT_RELEVANCE | REGION_RELEVANCE | IDENTITY_CHECK
    | FORMER_PEP_INFLUENCE | JUDGMENT_PARTY_IDENTITY | OTHER
triggered: true
searchOutcome: RETRIEVED | NOT_FOUND | UNAVAILABLE | PARSE_FAILED | null
conclusion: null          # allowed values per type, see disposition recommendation
method: null
performedAt: null
performedBy: null
sourceRefs: []
reasoning: null
```

The system emits a triggered check as pending: its type, with the result fields empty. The result fields are filled by checks performed outside the system ([ADR-0020](../adr/0020-identification-only.md)). Result values and rules are in [disposition recommendation](review-and-disposition.md#verification-records).

## Field value wrapper

Every important field preserves:

```text
rawValue
normalizedValue
valueState
precision
sourceLabel
evidenceRefs
normalizationVersion
```

Value states:

```text
PRESENT | MISSING | UNPARSEABLE | AMBIGUOUS | FIELD_CONFLICT | NOT_APPLICABLE
```

"Missing" is always expressed by `valueState`, never by an empty string (Oracle stores `''` as NULL; see [persistence](persistence.md#writing-rules)).
