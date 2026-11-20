# ADR-0012: Sources, web information and the news digest

- Status: Accepted. Web search and web identity evidence are decided but not built yet; until they are, the [news digest](../feature/watchlist-and-adverse-information.md#news-digest) and the [news models](../feature/watchlist-and-adverse-information.md#news-models) read only the news attached to a Profile. The models feed no decision; of the rule readings, the age, birth date and gender written next to the Profile person's name in attached news are identity evidence.
- Date: 2026-11-02; updated 2026-11-10 (the small models read attached news beside the rules), 2026-11-19 (birth date and gender in attached news are identity evidence), 2026-11-20 (a large language model is not excluded; measurements decide)

## Context

PoC input is supplied files. Later the bank may integrate by API, and data may need to be crawled.

Reviewers read the news and judgments about a listed person to learn who is involved, in what role and what happened: searched, detained, indicted, convicted. The same pages often settle identity: an age, a title, a place or a court case that fits or does not fit the customer. A Profile carries at most one attached article today, and several reports of one event are common. Results must be reproducible, explainable and run inside the bank. A large language model is not excluded: none reads the news so far, because the PoC machine cannot run one at a useful speed and no measurement on the annotated documents has shown what one would give; whether one is used is decided by such measurements. Models come under clean licences ([ADR-0015](0015-licences.md)). Customer data stays inside the bank ([security and privacy](../feature/security-and-privacy.md)).

## Decision

- **Ports.** Customer and source data come through `ICustomerSource` and `IProfileSource` in Core. Another acquisition path is a new Infra implementation; normalization and adjudication do not change.
- **Web search starts from list-side data.** The system follows what the Profile already holds to the web: its names and aliases, titles, places and source URLs, searched on public judgment and news sites through the polite crawler ([web text](../feature/web-text.md)). Nothing from the customer side (name, date of birth, identifiers) is sent out.
- **Reading.** Pages are read by rules and the small models into people, roles and events with their dates, agencies, offences, amounts and penalties, and into identity facts about the listed person (age or birth year, title, place, nationality). Every fact keeps its sentence and source.
- **Rules and models side by side.** The rule digest and the models' readout are kept apart in the digest, each with its own provenance; neither overrides the other. The models' known errors are corrected by fixed rules only where a correction measurably helped on the annotated documents, and a rule result is not merged into the models' (on judgments and press releases it made them worse). The models are optional: without them the rules read alone.
- **Web facts are identity evidence.** A fact that fits or contradicts the customer becomes an evidence group with its citation `[S#]`. Until a Golden Case validates it, web evidence cannot close a pair as NO_MATCH or raise it above PROBABLE_MATCH; it supports, conflicts and orders review. The same holds for attached news, where it is built: an age, birth date or gender that rules read next to the Profile person's name is compared with the customer's. Such a value weighs less than the Profile's own field (a full birth date is `MEDIUM`, not `HIGH`), does not count twice when the Profile says the same, and is a conflict only when the Profile's own field does not agree with the customer. Only rule readings are used; a model reading is not evidence.
- **One event, one piece of evidence.** Reports of the same party, event time and charge are one event; the summary lists every source of it. A fact stated by one source is never generalised to all.
- **Every source used has a summary**: publisher, date, title, then the people and events in fixed sentence patterns, each phrase a slot of a cited sentence. Rewording never swaps actor and object, and the event date is never the publication date.
- **Source condition, legal stage and AML relevance** keep their own rules over structured Profile fields; the digest does not set them.

## Consequences

- The news digest, its codes and its known limits are specified in the [feature spec](../feature/watchlist-and-adverse-information.md#news-digest); web identity evidence will add evidence groups and codes there and in [identity adjudication](../feature/identity-adjudication.md).
- Search queries carry the listed person's name to outside sites; this is public list data, logged per Case.
- Model accuracy is measured on the bank's annotated judgments and press releases, outside the repository, and recorded in the feature spec; there is no Golden Case for web evidence yet.
