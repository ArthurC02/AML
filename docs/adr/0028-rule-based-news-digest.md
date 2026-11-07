# ADR-0028: A rule-based news digest for the reviewer

- Status: Accepted
- Date: 2026-11-07

## Context

Reviewers read the news attached to a Profile to learn who is involved, in what role, and what has happened to them: searched, detained, indicted, convicted. They want that in the case workbook as a short summary they can check against the article. [ADR-0018](0018-legacy-ai-report-rules-not-adopted.md) rejected the legacy LLM report, in which a model summarised news, grouped events and judged AML relevance, because free-news understanding was out of Phase 1. The bank wants no language model for now: the result must be reproducible, explainable and run inside the bank. Taiwanese crime reporting is formulaic (王姓男子, 被告王大明, 昨遭臺北地檢署起訴), and the legal-stage rules already read it by sentence, clause, quotes, negation and modals.

## Decision

- Each hit with attached news carries a `newsDigest` in its explanation: the people (name or anonymised form, whether it is the Profile person, the role written before the name), the events (type, certainty, actor, date, agency, offences, amount, penalty, the sentence as a quote), and a summary in fixed sentence patterns over them ([news digest](../feature/watchlist-and-adverse-information.md#news-digest)).
- It is read by fixed rules, word lists and patterns, linear in the text, with no model of any kind. The same news and Profile names give the same digest.
- It restates the news and decides nothing: no source condition, legal stage, AML relevance, identity decision, pair result or disposition reads it. The legal stage keeps its own rules.
- The workbook adds two sheets, 新聞人物 and 新聞事件, and a 新聞摘要 column on 命中. The default view shows the event types, certainty, dates, agencies, offences and amounts. The names, actors, quotes and the summary are personal data and appear in `view=full` only ([ADR-0027](0027-case-workbook-renders-the-api.md)).
- Its codes (event types, certainty, match) are in the code catalog. The stored format is raised to version 6.

## Consequences

- ADR-0018's news row holds for decisions: nothing reads the news to judge relevance or identity. A digest for the reviewer is now offered.
- What the rules miss is a known limit, listed in the feature spec: rarer surnames, foreign names, names far from a role word or clause opening, pronouns beyond the last person named, and several cases in one article. A miss leaves an event without its person, never with the wrong decision.
- There is no Golden Case for the digest yet. Its accuracy on real reporting is unmeasured until annotated news samples are approved; until then the summary is a reading aid, checked against the quoted sentence.
