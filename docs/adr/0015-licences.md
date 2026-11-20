# ADR-0015: Clean licences for libraries, models and text

- Status: Accepted; open for the large language model (below)
- Date: 2026-11-02; updated 2026-11-10 (models run with ONNX Runtime), 2026-11-20 (the person-stage classifier; Gemma 4 does not meet the corpus rule as written, an open decision)

## Context

MediatR, AutoMapper and FluentAssertions moved to commercial licences in 2025; in a bank each commercial dependency needs procurement and legal review. The PoC reads news with small models run inside the bank beside the rules; a large language model is not excluded ([ADR-0012](0012-sources-and-web-information.md)), and the same licence rule would apply to one. The Traditional Chinese models of CKIP are GPL-3.0 and were trained on corpora licensed for non-commercial research only (Academia Sinica Balanced Corpus 4.0, OntoNotes 5.0, Chinese Gigaword).

## Decision

- **Libraries.** No commercially licensed packages: plain handler classes, hand-written mapping, xUnit `Assert`. Built-in .NET capabilities come before new packages; any package goes through the [implementation baseline](../plan/implementation-baseline.md#packages).
- **Models.** Code and weights are permissive (Apache-2.0, MIT) and every training corpus allows commercial use. CKIP models are not used. The base model is `bert-base-chinese` (Apache-2.0), fine-tuned on court judgments and government press releases (outside copyright under 著作權法 第 9 條) and on news the bank has licensed for training.
- Training and ONNX export run in Python outside the .NET solution; the system runs the exported models in .NET with ONNX Runtime (MIT). Model files and gathered text stay outside the repository. The person-stage classifier is such a model: `bert-base-chinese` fine-tuned on the same labelled text.
- **The large language model is an open decision for the bank.** Gemma 4 (the model measured for the [stage vote](../feature/watchlist-and-adverse-information.md#three-readers)) has Apache-2.0 weights, but its training corpus is not published, so it does not meet "every training corpus allows commercial use" as written; neither can any comparable model. Until the bank decides whether published weights under a permissive licence are enough, the language model reader is off: it runs only when `StageModels:LlmUrl` is configured, and the system works without it. Ollama, which serves it, is MIT and runs outside the .NET solution.

## Consequences

- Slightly more hand-written code, no licence exposure.
- Labelled data is made by the bank; ordinary press articles are not used for training until the bank's licence allows it.
- The corpus of `bert-base-chinese` is unpublished (generally taken to be Chinese Wikipedia): a residual uncertainty.
- Deciding against the language model leaves the vote with two readers (rules and classifier), where the rules' stage stands and a differing classifier orders review.
