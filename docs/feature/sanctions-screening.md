# Sanctions Screening

> Module: Source Screening and Risk Assessment.

## Two independent questions

1. Does the source Profile satisfy the configured Sanction condition?
2. Does the Profile refer to the screened subject?

Only approved identity plus an applicable source condition produces a customer/Profile Sanction HIT.

## Sanction source model

Preserve:

- issuing authority and jurisdiction;
- sanctions program;
- list or measure identifier;
- subject type: person, organization, vessel or other;
- status and effective dates;
- addition, change, removal and expiry facts;
- source version and evidence.

A generic `Active` Profile status is not automatically an active sanction measure.

## Sanction condition

The [list-type determination](watchlist-and-adverse-information.md#list-type-determination) decides whether a Profile has a SANCTION condition: a Sanction category, or 黑名單 = YES with an official-list entry naming a sanction. The status is then:

- HIT only when the Profile's official-list entry is `Active` and is not a wanted list (an active wanted-list entry is OFFICIAL_WANTED_LIST, so the Sanction stays UNKNOWN with `SANCTION_MEASURE_NOT_CONFIRMED`); `program` is the list name and `jurisdiction` the issuing authority;
- UNKNOWN with `SANCTION_MEASURE_NOT_CONFIRMED` when no active, named measure is recorded (e.g. a delisted entry, or a Sanction category without an official-list entry);
- UNKNOWN with `FLAG_CATEGORY_CONFLICT` when the category is Sanction but 黑名單 = NO: the flag never clears a Sanction category;
- NOT_HIT only when 黑名單 = NO and the Profile has no Sanction category.

## SanctionPolicy output

```yaml
conditionType: SANCTION
status: HIT | NOT_HIT | UNKNOWN
program: null
jurisdiction: null
effectiveStatus: UNKNOWN
policyVersion: source-policy-0.2
evidenceRefs: []
reasonCodes: []
```

Missing mappings or unknown codes produce UNKNOWN, never NOT_HIT.

## Pair result

Sanction pairs use the canonical [combination matrix](data-contracts.md#combination-matrix); the Phase 1 restriction below then applies.

## Phase 1 restriction

Until GC-05 is validated, all apparent Sanction identity matches require high-priority human review. The architecture and contracts may be implemented, but production-grade automatic Sanction decisions are not claimed.

- A Sanction condition that is HIT or UNKNOWN keeps the pair in REVIEW (release control `RELEASE_CONTROL_HUMAN_REVIEW`) and gives it review priority High; a NOT_HIT Sanction condition does neither.
- Under an approved risk policy, an UNKNOWN Sanction condition scores as a HIT and any scored Sanction condition makes the risk level High ([risk scoring](watchlist-and-adverse-information.md#risk-scoring)).
