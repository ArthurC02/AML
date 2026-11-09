# ADR-0004: Identification only: identity, source conditions and an advisory Article 22

- Status: Accepted
- Date: 2026-11-02; updated 2026-11-09 (merged former ADR-0017, 0020)

## Context

"Does this Profile carry a Sanction/PEP/adverse condition?" and "Is this Profile our customer?" are different questions. Merging them lets a strong source condition push identity, or a non-match erase a source fact.

The bank's operating procedure, Article 22 "Handling of hits on controlled lists" (觸及管制名單之處理方式), tells the reviewer when to select *on list* (符合名單) or *not on list* (非名單) and when to escalate. Some outcomes are policy presumptions, not identity facts:

- ① a non-Taiwanese customer hitting a foreign PEP is presumed on list when data is insufficient;
- ② an adverse-news hit unrelated to money laundering, terrorist financing or proliferation financing is selected not on list, even for the same person;
- ③ a case not resolved after all channels goes to the approver's senior supervisor.

The PoC must prove judgment, evidence and explanation; reviewer roles, approvals and close/reopen add scope without testing identification.

## Decision

- **Two independent questions.** Identity and source conditions are computed separately and combined only at pair level by one [combination matrix](../feature/data-contracts.md#combination-matrix), the same for Sanction, PEP/RCA and watchlists. A Profile can be HIT while identity is REVIEW, and NO_MATCH while its own condition stays HIT. The IdentityComparison is reused across classifications when customer, Profile and policy versions are identical.
- **Article 22 is a separate policy.** `DispositionPolicy` gives a `DispositionRecommendation` per pair (`ON_LIST | NOT_ON_LIST`) with basis and reason codes; it reads identity and source conditions and never changes them. ② works per condition: only the unrelated `ADVERSE_MEDIA` condition is set aside. ③ gives no recommendation; the explanation lists pending checks ([disposition recommendation](../feature/review-and-disposition.md)).
- **The system identifies; humans decide.** It produces identity evidence, the identity decision, source conditions, the pair result, the advisory recommendation and the explanation. It does not model reviewer or approver roles, HumanDecision, approvals, FinalDisposition, close/reopen or a user interface. Human outcomes in Golden Cases are labels for measurement, not behaviour.

## Consequences

- The explanation shows both "who is this person" and "what the procedure requires", with the policy basis named.
- The legacy precedence (② overriding ① for the whole hit) is not carried over, pending business confirmation ([open questions](../plan/open-questions.md#article-22)); GC-09 and GC-10 cover ① and ②.
- Case status follows the analysis only. A later phase with a human workflow updates this record.
