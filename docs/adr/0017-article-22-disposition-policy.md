# ADR-0017: Article 22 as a disposition policy, separate from identity

- Status: Accepted
- Date: 2026-11-02

## Context

The bank's operating procedure, Article 22 "Handling of hits on controlled lists" (觸及管制名單之處理方式), tells the reviewer when to select *on list* (符合名單) or *not on list* (非名單) in the AML system and when to escalate. Some of its outcomes are policy presumptions rather than identity facts:

- ① a non-Taiwanese customer hitting a foreign PEP is presumed on list when data is insufficient;
- ② an adverse-news hit unrelated to money laundering, terrorist financing or proliferation financing is selected not on list, even if the person is the same;
- ③ a case that cannot be resolved after all channels are exhausted goes to the approver's senior supervisor.

Writing these into the identity decision would make identity mean "policy outcome" ([ADR-0004](0004-independent-identity-and-source-condition.md)).

## Decision

- A separate `DispositionPolicy` produces a `DispositionRecommendation` per pair: `ON_LIST | NOT_ON_LIST`, with a basis and reason codes. It reads the IdentityComparison and SourceConditionResults and never changes them.
- ② works per source condition: only the unrelated `ADVERSE_MEDIA` condition is set aside; other HIT conditions on the same Profile still count.
- ③ is a review-routing rule: an unresolved HumanDecision needs senior approval.
- In Phase 1 every recommendation is confirmed by a human; the FinalDisposition records which rule it relied on.

Details: [review and disposition](../feature/review-and-disposition.md).

## Consequences

- Reviewers see both "who is this person" and "what the procedure requires", with the policy basis named.
- The legacy precedence (② overriding ① for the whole hit) is not carried over; per-condition handling replaces it, pending business confirmation ([open questions](../plan/open-questions.md#article-22)).
- GC-09 and GC-10 are added to the validation backlog for ① and ②.
