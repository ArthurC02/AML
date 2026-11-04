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

The policy maps only approved source fields and source values. SIP, Financial Crime, RCA or free-text job titles are not silently converted to PEP. PEP and RCA are decided by the [list-type determination](watchlist-and-adverse-information.md#list-type-determination): a PEP flag with a value decides PEP, a PEP category with a blank flag keeps its PEP condition, `Is RCA? = YES` always adds RCA, `Is RCA? = NO` never clears an RCA category (UNKNOWN with `FLAG_CATEGORY_CONFLICT`), and the bank-internal PEP list counts as PEP evidence. Being on a bank-internal list does not by itself make a Profile a PEP.

```yaml
conditionType: PEP | RCA
status: HIT | NOT_HIT | UNKNOWN
pepType: null
policyVersion: source-policy-0.2
evidenceRefs: []
reasonCodes: []
```

`pepType` is read from the PEP or RCA category's subcategory text, a heuristic until the approved PEP role and relationship fields are validated by GC-04:

| pepType | When |
|---|---|
| `PEP_FAMILY` | the subcategory names a family relation (Family, Relative, Spouse, Child, Parent, Sibling) and no relation outside the [statutory scope](#rca-relationship-scope) |
| `PEP_CLOSE_ASSOCIATE` | the subcategory names an associate |
| `RCA_UNCONFIRMED` | any other RCA condition |
| `FORMER_PEP` | a PEP condition on a [former PEP](#former-pep) |
| none | a current PEP's own Profile; `PEP_SELF` is not assigned |

Pair results use the canonical [combination matrix](data-contracts.md#combination-matrix).

## Former PEP

Under 重要政治性職務之人與其家庭成員及有密切關係之人範圍認定標準 (認定標準) Article 5, whether a person who has left a prominent public function still needs PEP treatment is a risk-based assessment of their remaining influence, with no fixed period; Article 8 extends the same assessment to their family members and close associates.

A Profile is a former PEP when a PEP or RCA subcategory says Former or Inactive or, for the PEP's own Profile, every recorded role has ended. A role has ended when the last day its end date can stand for lies before the analysis date: an end date of `2026` is still current during 2026, and `2026/10` has ended from 1 November 2026. An RCA's own roles say nothing about the PEP's office. Every recorded role counts, not only public functions, until approved PEP role fields arrive with GC-04.

Being a former PEP does not change the condition status. The pending check `FORMER_PEP_INFLUENCE` is listed for a HIT or UNKNOWN PEP condition on a former PEP's own Profile, and for a HIT or UNKNOWN RCA condition whose subcategory marks the PEP as former; the remaining influence is assessed outside the system.

## RCA relationship scope

認定標準 Article 6 limits PEP family members to first-degree relatives by blood or marriage, siblings, a spouse or cohabiting partner, and the spouse's siblings. A PEP or RCA subcategory naming a relation beyond that (cousin, uncle, aunt, nephew, niece, grandparent or grandchild, and the Chinese terms 堂/表 relatives, 叔, 伯父, 伯母, 姑, 舅, 姨, 姪, 甥, 祖父母, 孫) gives the PEP and RCA conditions reason code `RCA_OUTSIDE_STATUTORY_SCOPE` and no `PEP_FAMILY` type. The condition status is unchanged; whether the relationship still warrants treatment is the reviewer's call. An unnamed "Family Member" or "Relative" is not judged. The relation words are a heuristic until approved relationship fields are validated by GC-04.

## Identity reuse

The same IdentityComparison is reused across PEP, Sanction and other classifications when customer version, Profile version and identity policy version are identical.

## Review

A foreign PEP hit on a non-Taiwanese subject may receive the Article 22 ① presumption ([disposition recommendation](review-and-disposition.md#article-22-1-foreign-pep-presumption)); the identity decision is unchanged.

The explanation lists these checks as pending ([verification records](review-and-disposition.md#verification-records)):

- an employment-relevance check for every PEP condition that is HIT or UNKNOWN;
- `FORMER_PEP_INFLUENCE` for a [former PEP](#former-pep) and their relatives and close associates;
- an identity check against a published birth year or age when the PEP condition is HIT, the Profile has no RCA condition and no date of birth.

Under an approved risk policy, an UNKNOWN PEP condition scores as a HIT ([risk scoring](watchlist-and-adverse-information.md#risk-scoring)).

Until GC-04 is validated:

- all PEP/RCA candidates remain REVIEW: a PEP or RCA condition that is HIT or UNKNOWN keeps the pair in REVIEW (`RELEASE_CONTROL_HUMAN_REVIEW`);
- former-PEP treatment has no fixed duration: it follows the 認定標準 Article 5 risk-based assessment, left to the reviewer through `FORMER_PEP_INFLUENCE`.
