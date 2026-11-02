# ADR-0001: Evidence-driven adjudication

- Status: Accepted
- Date: 2026-11-02

## Context

Name-screening systems often reduce identity to one similarity score. A score cannot explain why a pair matched, hides conflicting facts, and mixes retrieval ranking with identity.

## Decision

Model adjudication around explicit records: CandidateProfile, Evidence, Conflict, SystemDecision, HumanDecision and FinalDisposition, rather than a single similarity score.

## Consequences

- Every decision lists supporting evidence, conflicts, unknown fields, reason codes and policy version ([identity adjudication](../feature/identity-adjudication.md#explainability)).
- Retrieval scores (BM25, displayed similarity) are ranking aids only, never identity probabilities.
- More data is stored per decision; this is the price of traceability.
