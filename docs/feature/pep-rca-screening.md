# PEP and RCA Screening

> Module: Source Screening and Risk Assessment.

## Principle

PEP status is not a criminal finding. Identity, PEP classification and subsequent customer-risk treatment remain separate.

## Supported conceptual categories

```text
PEP_SELF
PEP_FAMILY
PEP_CLOSE_ASSOCIATE
FORMER_PEP
RCA_UNCONFIRMED
```

## PEP source attributes

- office/title;
- government body and country;
- term start/end;
- current/former status;
- relationship type and related Profile;
- relationship evidence;
- source review date and version.

## PepPolicy

The policy maps only approved source fields and source values. SIP, Financial Crime, RCA or free-text job titles are not silently converted to PEP. PEP and RCA are decided by the [list-type determination](watchlist-and-adverse-information.md#list-type-determination): flags first, `Is RCA? = YES` always adds RCA, and the bank-internal PEP list counts as PEP evidence. Being on a bank-internal list does not by itself make a Profile a PEP.

```yaml
conditionType: PEP | RCA
status: HIT | NOT_HIT | UNKNOWN
pepType: null
policyVersion: pep-policy-0.1
evidenceRefs: []
```

Pair results use the canonical [combination matrix](data-contracts.md#combination-matrix).

## Identity reuse

The same IdentityComparison is reused across PEP, Sanction and other classifications when customer version, Profile version and identity policy version are identical.

## Review

A foreign PEP hit on a non-Taiwanese subject may receive the Article 22 ① presumption ([disposition recommendation](review-and-disposition.md#article-22-1-foreign-pep-presumption)); the identity decision is unchanged.

The explanation lists these checks as pending ([verification records](review-and-disposition.md#verification-records)):

- an employment-relevance check for every PEP HIT;
- an identity check against a published birth year or age when the PEP flag is explicitly Yes, `Is RCA?` is not Yes and the Profile has no DOB or age.

Until GC-04 is validated:

- all PEP/RCA candidates remain REVIEW;
- former-PEP duration and RCA relationship scope are configurable and not hardcoded.
