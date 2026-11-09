# ADR-0012: Sources, web information and the news digest

- Status: Accepted. Web search and web identity evidence are decided but not built yet; until they are, the [news digest](../feature/watchlist-and-adverse-information.md#news-digest) reads only the news attached to a Profile and feeds no decision.
- Date: 2026-11-02; updated 2026-11-09 (merged former ADR-0028; web information added)

## Context

PoC input is supplied files. Later the bank may integrate by API, and data may need to be crawled.

Reviewers read the news and judgments about a listed person to learn who is involved, in what role and what happened: searched, detained, indicted, convicted. The same pages often settle identity: an age, a title, a place or a court case that fits or does not fit the customer. A Profile carries at most one attached article today, and several reports of one event are common. The bank wants no large language model: results must be reproducible, explainable and run inside the bank. Small models under clean licences are allowed ([ADR-0015](0015-licences.md)). Customer data stays inside the bank ([security and privacy](../feature/security-and-privacy.md)).

## Decision

- **Ports.** Customer and source data come through `ICustomerSource` and `IProfileSource` in Core. Another acquisition path is a new Infra implementation; normalization and adjudication do not change.
- **Web search starts from list-side data.** The system follows what the Profile already holds to the web: its names and aliases, titles, places and source URLs, searched on public judgment and news sites through the polite crawler ([web text](../feature/web-text.md)). Nothing from the customer side (name, date of birth, identifiers) is sent out.
- **Reading.** Pages are read by rules and the small models into people, roles and events with their dates, agencies, offences, amounts and penalties, and into identity facts about the listed person (age or birth year, title, place, nationality). Every fact keeps its sentence and source.
- **Web facts are identity evidence.** A fact that fits or contradicts the customer becomes an evidence group with its citation `[S#]`. Until a Golden Case validates it, web evidence cannot close a pair as NO_MATCH or raise it above PROBABLE_MATCH; it supports, conflicts and orders review.
- **One event, one piece of evidence.** Reports of the same party, event time and charge are one event; the summary lists every source of it. A fact stated by one source is never generalised to all.
- **Every source used has a summary**: publisher, date, title, then the people and events in fixed sentence patterns, each phrase a slot of a cited sentence. Rewording never swaps actor and object, and the event date is never the publication date.
- **Source condition, legal stage and AML relevance** keep their own rules over structured Profile fields; the digest does not set them.

## Consequences

- The news digest, its codes and its known limits are specified in the [feature spec](../feature/watchlist-and-adverse-information.md#news-digest); web identity evidence will add evidence groups and codes there and in [identity adjudication](../feature/identity-adjudication.md).
- Search queries carry the listed person's name to outside sites; this is public list data, logged per Case.
- Model accuracy is measured on the bank's annotated judgments and press releases; there is no Golden Case for web evidence yet.
