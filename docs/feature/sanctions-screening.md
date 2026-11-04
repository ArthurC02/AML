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

## SanctionPolicy output

```yaml
conditionType: SANCTION
status: HIT | NOT_HIT | UNKNOWN
program: null
jurisdiction: null
effectiveStatus: UNKNOWN
policyVersion: sanction-policy-0.1
evidenceRefs: []
reasonCodes: []
```

NOT_HIT requires complete and interpretable source fields. Missing mappings or unknown codes produce UNKNOWN.

## Pair result

Sanction pairs use the canonical [combination matrix](data-contracts.md#combination-matrix) without exceptions.

## Phase 1 restriction

Until GC-05 is validated, all apparent Sanction identity matches require high-priority human review. The architecture and contracts may be implemented, but production-grade automatic Sanction decisions are not claimed.
