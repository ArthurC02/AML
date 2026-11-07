# ADR-0029: Models and training text only under clean licences

- Status: Accepted
- Date: 2026-11-07

## Context

The rule-based news digest ([ADR-0028](0028-rule-based-news-digest.md)) reads keywords and their positions, and cannot tell who did what to whom when a sentence is phrased otherwise. The bank chose small models run inside the bank over keywords alone and over a large language model.

The ready-made Traditional Chinese models of CKIP (ckip-transformers) were checked:
- The code and all 30 models on Hugging Face are GPL-3.0.
- Their segmentation and part-of-speech models were trained on the Academia Sinica Balanced Corpus 4.0. Its user agreement licenses research only. Article 5 bars the corpus and its directly derived data from any commercial product.
- Their entity model was trained on OntoNotes 5.0, and their language models partly on Chinese Gigaword. Both come from the LDC, whose non-member agreement allows non-commercial research only.

## Decision

- A model used by the system has permissive code and weights (Apache-2.0, MIT), and every corpus it was trained on allows commercial use. CKIP models are not used.
- The base model is Google's `bert-base-chinese` (Apache-2.0). Task models are fine-tuned on text the bank may use:
  - court judgments and government press releases, outside copyright under 著作權法 第 9 條
  - news the bank has licensed for model training
- Training and export to ONNX run in Python, outside the .NET solution. The system runs the exported models in .NET.
- Web text is gathered by a polite crawler that keeps only the main text ([web text](../feature/web-text.md)). Gathered text stays outside the repository.

## Consequences

- Labelled data must be made by the bank, so the first models wait on annotation.
- Ordinary press articles are not used for training until the bank's licence allows it.
- Google has not published the corpus of `bert-base-chinese`; it is generally taken to be Chinese Wikipedia. This is a residual uncertainty.
