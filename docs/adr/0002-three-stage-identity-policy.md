# ADR-0002: Three-stage identity policy

- Status: Accepted
- Date: 2026-11-02

## Context

Many weak agreements (same name, gender, nationality) can look convincing in aggregate even when one reliable fact proves two records are different people.

## Decision

Identity is decided in three ordered stages:

1. Hard Conflict Gate
2. Evidence Strength Assessment
3. Decision Policy

A confirmed hard conflict cannot be offset by several weak supporting attributes.

## Consequences

- Each hard-conflict rule names its evidence and policy version and carries its own validation status ([hard conflict gate](../feature/identity-adjudication.md#hard-conflict-gate)).
- Only validated rules may close a pair automatically; unvalidated rules still fire and surface as REVIEW.
