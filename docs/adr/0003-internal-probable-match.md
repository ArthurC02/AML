# ADR-0003: Internal PROBABLE_MATCH

- Status: Accepted
- Date: 2026-11-02

## Context

Some pairs have several strong supports but no unique identifier. Reviewers should see them first, but consumers must not treat them as confirmed matches.

## Decision

PROBABLE_MATCH is an internal prioritization class. External consumers receive REVIEW until a permitted final decision is made.

## Consequences

- PROBABLE_MATCH maps to high-priority REVIEW in every pair result ([combination matrix](../feature/data-contracts.md#combination-matrix)).
- The evidence combination that defines PROBABLE_MATCH is an [open question](../plan/open-questions.md#identity-policy) to calibrate with GC-06 and GC-07.
