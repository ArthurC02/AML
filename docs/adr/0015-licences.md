# ADR-0015: Clean licences for libraries, models and text

- Status: Accepted
- Date: 2026-11-02; updated 2026-11-09 (merged former ADR-0029)

## Context

MediatR, AutoMapper and FluentAssertions moved to commercial licences in 2025; in a bank each commercial dependency needs procurement and legal review. The bank chose small models run inside the bank over keywords alone and over a large language model. The Traditional Chinese models of CKIP are GPL-3.0 and were trained on corpora licensed for non-commercial research only (Academia Sinica Balanced Corpus 4.0, OntoNotes 5.0, Chinese Gigaword).

## Decision

- **Libraries.** No commercially licensed packages: plain handler classes, hand-written mapping, xUnit `Assert`. Built-in .NET capabilities come before new packages; any package goes through the [implementation baseline](../plan/implementation-baseline.md#packages).
- **Models.** Code and weights are permissive (Apache-2.0, MIT) and every training corpus allows commercial use. CKIP models are not used. The base model is `bert-base-chinese` (Apache-2.0), fine-tuned on court judgments and government press releases (outside copyright under 著作權法 第 9 條) and on news the bank has licensed for training.
- Training and ONNX export run in Python outside the .NET solution; the system runs the exported models in .NET. Gathered text stays outside the repository.

## Consequences

- Slightly more hand-written code, no licence exposure.
- Labelled data is made by the bank; ordinary press articles are not used for training until the bank's licence allows it.
- The corpus of `bert-base-chinese` is unpublished (generally taken to be Chinese Wikipedia): a residual uncertainty.
