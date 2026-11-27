# Candidate Retrieval and Identity Adjudication

> Module: Candidate and Identity Adjudication.

## Separate concerns

- Retrieval score: ranking for which Profiles to inspect.
- Identity evidence: facts supporting or opposing same-person identity.
- Identity decision: policy result.
- AML risk: separate downstream assessment.

BM25 and displayed similarity values are not identity probabilities ([ADR-0001](../adr/0001-identity-adjudication.md)).

## Retrieval modes

| Mode | Candidates come from | Use |
|---|---|---|
| CONTROLLED_SET | the bank's case table | input of `POST /api/cases`; the PoC default |
| DIRECT_REFERENCE | a known Profile ID | a named Profile |
| INDEX_SEARCH | a name query over the list database | large source sets |
| PREVIOUS_LINK | the Profiles used by the Case's latest input | re-analysis after a source update |
| SOURCE_PAGE | the persons of a received page the Case names (`pageIds`) | scenario 2; a person whose written name is EXACT against the subject's by the name comparison below is a candidate, every other person is recorded as not one ([external sources](external-sources.md#matched-against-the-customer)) |

A page person's Profile holds its names only, and the page's text is its attached news, the publication date the page states its report date ([external sources](external-sources.md#common-path); none when it states none or its sources disagree, or when only its update date stands in, never the fetch date): the age, birth dates and gender written next to the name are stated values (`StatedAgeCheck`, `NewsStatedCheck`), and when every comparable one is more than two years from the customer's DOB the pair is NO_MATCH (`STATED_DOB_CONFLICT`), a page's birth date within two years neutral ([two-year conflict on stated values](#two-year-conflict-on-stated-values)); an age on a page without a publication date is `AGE_NOT_COMPARABLE` and concludes nothing.

They are served by `GET /api/profiles/search` (INDEX_SEARCH), `GET /api/profiles/{profileId}` (DIRECT_REFERENCE) and `GET /api/cases/{caseId}/candidates?source=table|previous` (CONTROLLED_SET, PREVIOUS_LINK) ([ADR-0008](../adr/0008-validation-and-deferred-capabilities.md)). INDEX_SEARCH (steps 3 and 5 of the [operating architecture](system-overview.md#operating-architecture)) runs `Core/Retrieval/ProfileIndex` over the structured Profiles of the list database:

- Exact alias tier: the query key (after controlled variant folding, in source order or with the surname moved to either end) equals an alias key. These Profiles are listed first, ordered by alias text, and never cut by Top N.
- The other Profiles are ranked by three routes, each on its own scale, and fused by reciprocal rank (k 60):
  - `BM25` over words (k1 1.2, b 0.3); Chinese aliases count characters and Latin aliases count words, so each script has its own average alias length.
  - `NGRAM`: Dice coefficient over character n-grams of the key padded with boundary marks (CJK bigrams, plus single characters for names of up to three characters; Latin trigrams), counted per code point. Floor by query script: 0.3 for Chinese, 0.46 (Jaccard 0.3) for Latin.
  - `READING`: a shared Hanyu Pinyin reading key (`Romanization.ReadingKeys`): a Chinese name by its characters' readings, a romanized name by its syllables in each word order, polyphones expanded to at most 16 keys; a name with an unread character or an unsegmentable word has none. It links Chinese characters to their romanizations and one romanization system to another; it is a retrieval route only, never an exact match.
- Ties break on alias text, then Profile key, so the order is deterministic. A name whose key is empty (punctuation or 「-」 only) is not indexed, and an empty query returns nothing. A Profile key given twice keeps its last Profile, with that Profile's aliases only.
- The index keeps postings by alias key, word, n-gram and reading key. A query scores only the aliases that share at least one of its terms, since every route scores the others 0, so a search never scans the whole list. A very common term (a frequent surname) still reaches a large share of it.

A name query returns the Top N Profiles, counted after deduplication; when more Profiles matched than are returned, or the exact tier alone exceeds Top N, every result carries `truncated`. The constants are literature defaults pending tuning on Recall@K (`ProfileIndex.RecallAtK`: the share of labelled name queries whose Profile is among the first K results). Candidate data is then fetched for the named or retrieved Profiles. Index search does not feed a Case automatically; whether it should, and with which Top N, is an [open question](../plan/open-questions.md#technology-governance).

## Profile-level deduplication

All hits with the same `profileKey` (`DJ:` + `profileId`) become one CandidateProfile. Top K is counted by Profile, not alias rows.

Merging different Profiles that describe the same person (across Profile IDs or sources) happens when the list database is built, not per Case. A merge needs a shared direct identifier or a recorded source link; name similarity alone never merges. Merged Profiles keep their own IDs and sources so every value stays traceable. Implemented by `ProfileMerge`, served by `GET /api/profiles/merges`:

- `SHARED_DOCUMENT_KEY`: the same document type, issuing country and number. A number that is blank, a placeholder, shorter than four characters or one repeated character, or a document with an unknown type or country, gives no key.
- `RECORDED_SOURCE_LINK`: a relationship the source labels as the same person (Same As, Duplicate, Duplicate Of, Alias Profile, 同一人).
- Groups are built by union-find (iterative, union by size). A merge that would put two Profiles with opposite genders (`GENDER_CONFLICT`) or with no agreeing DOB (`DOB_CONFLICT`) in one group, checked against every member of both groups, is refused and listed under `rejected` with the conflicting pair.
- Every accepted merge is kept as an edge with its own basis and document key, so each link in a group stays explainable.
- A group of more than ten Profiles carries `OVERSIZED_GROUP` for inspection; the group-size and document-length limits are judgement calls pending real list volumes.

The hit count of a Case is the number of distinct Profiles, including hits on related parties; it is never the number of candidate rows or news articles. A Profile listed in the source but missing from the candidate table still counts.

## Screened subject

Each pair compares one screened subject (the customer or one related party) with one Profile. Every comparison, nationality test and age calculation uses that subject's own data; a related-party hit never borrows the customer's DOB, name or occupation. When the hit is on a related party, the account holder's data is still kept as case context (shown to the reviewer, usable in verification records and disposition notes) but never becomes Evidence for the pair.

## Evidence groups

```text
NAME
DATE_OF_BIRTH
GENDER
NATIONALITY
DOCUMENT
ADDRESS
RELATIONSHIP
EMPLOYMENT
COMPANY_ASSOCIATION
STATED_AGE
STATED_BIRTH_DATE
STATED_GENDER
TEMPORAL_ROLE
OTHER_IDENTIFIER
ENTITY_IDENTIFIER
```

Jaro-Winkler, Levenshtein and exact comparison of the same name are correlated features inside one NAME group. Both measures compare at most the first 256 characters, far past any name: their cost is the product of the two lengths. Stated ages and the news legal stage look for the first 100 names of a Profile only, since each name is searched for in every sentence. Aliases, former names, maiden names and other transliterations are compared inside NAME, not as extra groups.

`OTHER_IDENTIFIER` holds reference numbers and other labelled identifiers that are not identity documents; they keep their source label and never become DOCUMENT evidence, however much they look like one, even when a reference number passes the national-ID checksum (GC-12).

Photos are not evidence: appearance is never used to support or oppose identity or to estimate age, nationality, gender or name. This is the bank's current specification, not an industry rule; adding photo comparison needs a business-rule change, source and quality conditions and human review ([ADR-0001](../adr/0001-identity-adjudication.md)).

These groups describe natural persons. A customer or related party is a natural person when its 客戶類型 (類型 for a related party) is missing or says 個人, 自然人, Person, Individual or Natural Person (in any case); any other value makes it a legal person or group. Organization, vessel and other Profiles use `ENTITY_IDENTIFIER` instead (registration number with a known, equal jurisdiction; IMO number for vessels, whatever the flag): agreement is `ENTITY_IDENTIFIER_AGREEMENT` (PROBABLE_MATCH, external REVIEW), a different number under the same key is `ENTITY_IDENTIFIER_CONFLICT` (REVIEW); otherwise, and always for a natural-person subject, `NON_PERSON_SUBJECT_REVIEW`.

The people a legal person names are its senior managers (the ninth comparison field of the bank's specification). Against an organization record they are compared with the people that record names, by EXACT name only: the bank and the list word the post differently (負責人, Director), so the relation label is not compared. A manager both sides name is RELATIONSHIP support with detail `SAME_NAMED_SENIOR_MANAGER`. It never outweighs the identifier: with a comparable identifier the identifier decides; without one the pair is `ENTITY_SENIOR_MANAGER_AGREEMENT`, REVIEW on row 5, so review priority NORMAL instead of LOW. A name alone can be a coincidence, which is why this is never PROBABLE_MATCH. The managers come from the related-party file of the customer's own Case ([extraction](extraction-and-normalization.md)); a manager who is screened is also a Case of their own, as a related party.

Entity rules are pending an entity Golden Case; until one is supplied they are checked on made-up companies only.

## Hit prerequisites and direct identifiers

NAME, NATIONALITY and GENDER are hit prerequisites: they are what made the pair a candidate. They are compared in full and shown, with direction `PREREQUISITE`, but never count as support towards MATCH or PROBABLE_MATCH. At least one other group must support.

Direct identifiers are a full date of birth (DAY precision) and a comparable document key. A direct identifier is usable only when the same one is present on both sides; otherwise the pair records `NO_BILATERAL_DIRECT_IDENTIFIER`. Present on both sides means comparable, not equal.

## Comparison rules

- Values from different periods are compared at the same time point. Different values from different or unknown periods are `UNCERTAIN` with direction NEUTRAL.
- Employment and address count as same-period support only when both sides have an explicit attribution and validity period. Without them, they stay `UNCERTAIN` with direction NEUTRAL and the limitation is recorded.
- Different residence, registration or incident locations are weak evidence and can never alone, or as the main reason, push towards NO_MATCH.
- An event location is not a residential address; a report date or account-opening date is not an employment or residence period.
- Two values from different records or sources are not independent unless their source relationship is known; otherwise the explanation states "source relationship unknown". Several reports of the same event are one piece of evidence.
- Precision is never lowered: a full DOB is not rewritten as a year, and a year match is never a full-date match.
- A coarser Profile DOB that a more precise one already covers (1970 next to 1970-05-01) is dropped before comparison.
- When the Profile lists several DOB values, the best agreeing value is used at its own precision; the other values are source ambiguity, not a conflict. A conflict needs every Profile value to be more than two years from the customer's ([birth-date tolerance](#birth-date-tolerance)).
- Agreeing with one of many or scattered Profile DOBs (three or more values, or a span over two years; placeholder cut-off pending calibration) is no rare coincidence: it is recorded as `PROFILE_MULTIPLE_DOB`, neutral and never support.

### Birth-date tolerance

The customer's DOB against the Profile's own DOB field (list data), by `EvidenceBuilder.DateOfBirthEvidence`, with one policy value, `EvidenceBuilder.DobToleranceYears` = 2. It reads the user's decisions of 2026-11-27: 「生日兩年內都可以接受」 and 「兩年以內可以判定符合」, so a difference of more than two years concludes "different person" and one of two years or less is a birth-date agreement.

| Customer against the Profile's DOBs (best value first) | Evidence | Code | Decision |
|---|---|---|---|
| a value agrees at the precision both sides have | support at that level (HIGH day, MEDIUM month, LOW year) | none, or `BIRTH_YEAR_MATCH_FULL_DATE_NOT_COMPARABLE` | stage 3 by its level |
| none agrees, a value is two years or less away | support, PARTIAL, LOW: the weakest DOB agreement, as a birth year agreeing | `DOB_AGREES_WITHIN_TOLERANCE`; the note says how far apart (相差 N 天 for two full dates, else 出生年相差 N 年) | stage 3 like a birth year: row 4 with every prerequisite EXACT and nothing unsettled in DOCUMENT or EMPLOYMENT, else row 5; never row 3 (not HIGH) |
| every value is more than two years away | CONFLICT | `FULL_DOB_CONFLICT` (full dates on both sides) or `DOB_CONFLICT` | [hard conflict](#hard-conflict-gate), validated: NO_MATCH |
| either side has no DOB | NEUTRAL, missing | none | no conflict, no support |

The boundary, in calendar terms. Each value stands for the days it can mean: a full date one day, a year-month its month, a year its year. Two values are more than two years apart only when the later one's first possible day comes after the earlier one's last possible day plus two calendar years (`PartialDate.IsMoreThanYearsApart`); 29 February plus two years is 28 February. So:

- full dates: 1980-05-20 and 1982-05-20 are within (exactly two years), 1982-05-21 is a conflict; 2000-02-29 and 2002-02-28 are within, 2002-03-01 is a conflict;
- coarser values are compared at the coarser precision, and a difference that may be two years or less is never a conflict: years 1980 and 1982 are within, 1980 and 1983 a conflict; 1980-05 and 1982-05 within, 1982-06 a conflict; 1980-05-20 against the year 1982 within, against 1983 a conflict;
- 民國 dates are read as their AD year first (民國69年 = 1980).

Where it applies, and where not:

- Only the Profile's own DOB field. A birth date or age stated in attached text keeps its own rules: a stated birth date still agrees only when equal at its precision, a stated age is compatible when the births it allows at the report date are within two years of the customer's DOB ([age stated in a source](#age-stated-in-a-source), [birth date and gender stated in a source](#birth-date-and-gender-stated-in-a-source)); only the rule's conflict side applies to them, when the Profile has no DOB of its own ([two-year conflict on stated values](#two-year-conflict-on-stated-values)). A Profile DOB within tolerance counts as the Profile's DOB agreeing there (the same fact told twice).
- Scattered Profile DOBs (three or more values, or over two years apart) stay source ambiguity: agreeing within tolerance with one of them is `PROFILE_MULTIPLE_DOB`, neutral; a conflict with all of them is downgraded to REVIEW (`SOURCE_QUALITY_DOWNGRADE`).
- Profile merges still refuse a merge only on no agreeing DOB (`DOB_CONFLICT` there keeps its merge meaning).

Its cost, stated plainly:

- a different person with the same name born within two years of the customer is read as a birth-date agreement: with name, gender and nationality EXACT that is PROBABLE_MATCH (row 4), as a shared birth year is; on the synthetic set 10 strangers' pairs carry it and 4 of them reach PROBABLE_MATCH ([measurement](../plan/poc-delivery-plan.md#synthetic-end-to-end-set));
- a Profile DOB entered more than two years wrong concludes NO_MATCH for the right person, without a reviewer. A one-day or a one-year recording error is within tolerance, so it is not a conflict;
- a placeholder day on the list (1 January for a year-only value) is compared as a full date; no placeholder check exists.

## Comparison measures

Per evidence group (step 6 of the [operating architecture](system-overview.md#operating-architecture)):

| Group | Measure | Implementation |
|---|---|---|
| NAME | exact after normalization, with disclosed relaxations; romanization variants and Jaro-Winkler / Levenshtein as `PARTIAL` features inside the group | yes; see [name comparison](#name-comparison) |
| DATE_OF_BIRTH | precision-aware comparison, two-year tolerance and conflict check | yes; agreement within two years is `DOB_AGREES_WITHIN_TOLERANCE`, more than two years the hard conflict ([birth-date tolerance](#birth-date-tolerance)); scattered Profile DOBs give `PROFILE_MULTIPLE_DOB` ([comparison rules](#comparison-rules)) |
| DOCUMENT | type + issuing country/authority + number | yes: same key and number is `UNIQUE` support (row 2), same key with a different number is `DOCUMENT_IDENTITY_CONFLICT` (row 1); a document of another type or country is UNCERTAIN `NOT_COMPARABLE`, which does not block row 4; no sample Profile carries a document yet |
| ADDRESS | structured Taiwan address fields; token Jaccard for unstructured text | yes: city to floor key (old names and full-width digits folded), else Jaccard over words and CJK bigrams; always UNCERTAIN (`SAME_ADDRESS_NO_PERIOD` or `SOFT_DIFFERENCE`), since neither source gives validity periods |
| EMPLOYMENT | occupation and employer company, same period only | yes: same organization with overlapping periods on both sides (each needs a start; an open end runs to the present; compared by year, the coarsest common precision) is `MEDIUM` support; without periods `SAME_ORGANIZATION_NO_COMMON_PERIOD`; a different employer is UNCERTAIN `DIFFERENT_ORGANIZATION`, never a conflict. The bank export has an occupation category only, so subject employments are not yet extracted: an occupation category against Profile roles is `NOT_COMPARABLE` (subject employer missing), which does not block row 4 |
| STATED_BIRTH_DATE / STATED_GENDER | the birth date and gender attached news writes next to the Profile person's name, against the customer's | yes; see [birth date and gender stated in a source](#birth-date-and-gender-stated-in-a-source) |
| OTHER_IDENTIFIER | labelled number equality | yes: the same number on both sides is `LOW` support with `SAME_IDENTIFIER_NATURE_UNCONFIRMED`; a different number is UNCERTAIN, never a conflict |
| TEMPORAL_ROLE | age at every dated role start and the report date | yes: `TEMPORALLY_PLAUSIBLE` (neutral, never support) when every age reaches `MinimumRoleAge`; the hard-conflict code when the gate fires |
| RELATIONSHIP / COMPANY_ASSOCIATION | spouse, parents, related companies named on both sides | yes: same relation and an EXACT name is support (`MEDIUM` for relatives, `LOW` for companies); the employer goes to EMPLOYMENT. Subject-side relatives are not in the bank export yet |

Similarity values only grade `PARTIAL` within a group (placeholder thresholds, pending calibration: Jaro-Winkler 0.90 for names, set separately for Chinese-character and Latin keys, and Jaccard 0.5 for addresses label the detail SIMILAR or DISSIMILAR); they are never summed across groups or turned into a likelihood ([ADR-0001](../adr/0001-identity-adjudication.md)). Thresholds are set from labelled cases, not by default.

### Name comparison

`NameComparison.CompareNames` compares every subject name with every Profile name (aliases included). Comparison keys apply NFKC, fold case, drop spaces and punctuation, and keep letters, digits and private-use (造字) characters per code point, so a supplementary-plane character is one character. A character the source could not render (?, ＊, U+FFFD) stays in the key as an unknown character; a key holding one is never EXACT.

A name is EXACT when a subject key equals a Profile key, trying the fewest relaxations first (each alone, then combined). Each relaxation used is disclosed as a finding in the NAME detail:

| Relaxation | Finding |
|---|---|
| none: same characters in source order | none |
| surname moved to the other end (given-first vs surname-first) | `NAME_ORDER_VARIANT` |
| diacritics folded (Nguyễn = Nguyen) | `DIACRITIC_VARIANT` |
| controlled variant characters folded (鍾/鐘/钟, simplified → traditional); distinct surnames such as 涂/塗 or 余/餘 never fold | `VARIANT_CHARACTERS` |

Otherwise the name is `PARTIAL`:

- `ROMANIZATION_VARIANT` when both names are romanized and read the same: every word segments into syllables (Hanyu Pinyin, Wade-Giles, Tongyong or passport spellings), the syllable counts agree and every syllable shares a Pinyin reading, in source order or with the surname moved. A hyphenated given name marks Taiwanese passport style, so its syllables read only in those systems.
- `NOT_COMPARABLE_CROSS_SCRIPT` when no subject and Profile key share a script (for example Chinese characters against a romanization); no similarity is computed.
- Otherwise `NOT_IDENTICAL_AFTER_NORMALIZATION` with the best Jaro-Winkler and the lowest Levenshtein distance over key pairs sharing a script, labelled SIMILAR or DISSIMILAR against that script's threshold. Both measures count code points; the Winkler prefix bonus applies only when the Jaro value exceeds 0.7.

A PARTIAL name is never support, a conflict or a hit, whatever the similarity.

### Native-script name conflict

The customer's names (local name, romanised name, alias) and the Profile's names (primary, native-script forms, spelling variants) are each one list, and the name is EXACT when any form agrees with any form. So a customer whose romanised name equals the Profile's while the Chinese characters differ would read as an agreeing name with nothing against it. The same romanisation with other characters (張文華 against 章文華, both Chang Wen-Hua) is evidence of another person: homophone names are common, the bank's record always holds the local name and the vendor lists native forms for Chinese, Japanese and Korean persons. `NativeNames.Find` (called by `EvidenceBuilder`) records it as `NATIVE_NAME_CONFLICT` when all of these hold:

- the names agree by romanisation: NAME is EXACT, or PARTIAL with `ROMANIZATION_VARIANT`;
- both sides have a Han form: a name holding a Chinese character ([which forms count](extraction-and-normalization.md#name-normalization));
- every Han form on either side is complete: Han characters only (no kana, hangul or Latin in the same form), no masked form (`CjkText.IsMasked`), no unknown or private-use (造字) character, not an anonymised form (林姓男子, 歐陽男);
- no customer Han form, the alias included, may be the same as a Profile Han form: same length and, at every position, characters that may be the same (below), in either name order, spaces and middle dots ignored;
- no customer and Profile Han form are one character apart: the shorter is the longer with one character left out, compared the same way (張華 against 張文華, 歐文華 against 歐陽文華);
- at least one customer and one Profile Han form have the same length.

"May be the same character" (`NativeNames.MayBeSameCharacter`) is wider than name equality and serves only to silence: two characters fold equal under the name comparison's table, or they are joined, directly or through others, by `Data/possibly-same-characters.txt`, a list of 169 pairs this project curated by hand: Simplified ↔ Traditional mappings of one to many that the equality table drops on purpose (余/餘, 于/於, 云/雲, 里/裡/裏, 后/後, 谷/穀 …), Taiwan and Hong Kong variants (启/啟/啓, 为/為/爲, 裏/裡, 衞/衛, 着/著, 綫/線), old forms (眞/真, 敎/教, 髙/高, 邨/村) and Japanese shinjitai (沢/澤, 広/廣, 栄/榮, 斉/齊). The list is closed and known incomplete; the full OpenCC variant tables are not in the repository (only the one-to-one simplified → traditional entries are). A pair of forms that may be the same is silence, not agreement: name equality, the NAME status and every other comparison are unchanged, and no support is added.

Otherwise the two native names are not comparable and nothing is recorded: one masked, partial or mixed-script form anywhere silences the pair, a Profile with kana or hangul forms only has no Han form, and forms of lengths two or more apart are left out. Each boundary errs to silence.

The NAME evidence keeps its status and direction (EXACT or PARTIAL, PREREQUISITE): the romanised names do agree. Its detail lists `NATIVE_NAME_CONFLICT` after any relaxation finding, and its note shows the first contrasted pair, customer form first (羅馬拼音姓名相同，但中文姓名不同：客戶姓名「張文華」、名單 DJ:1 姓名「章文華」；同音不同字可能是另一人，未自動判定，請覆核). In [stage 3](#stage-3-table) it rules out rows 3 and 4 as opposite genders do: the pair falls to row 5 (REVIEW, priority NORMAL) or row 6 (LOW), with `NATIVE_NAME_CONFLICT` among the reason codes. Row 1 and row 2 keep their place; with an agreeing document the pair stays row 2 PROBABLE_MATCH and the code is recorded beside it. It is not a hard conflict and not a validated code, so it never gives NO_MATCH. The explanation opens the 相符／衝突 sentence with the note. Agreement in native script is never flagged and adds no support: it is already EXACT. The [score](scoring-rule.md#proposal-table) gives it a proposed −15 and a cap at REVIEW, the cap left out with an agreeing document, as row 2.

Known ways the signal is wrong (each a same person left to REVIEW, not concluded):

- a variant pair neither the fold table nor the possibly-same list holds (for example 喆/哲, 堃/坤, 珎/珍, 敍/敘);
- a vendor native form of another reading or of an alias the bank does not hold, or a vendor that lists another person's characters under a shared romanisation;
- a data-entry error in one character on either side.

The other way round, the list silences a different person whose characters differ only by a listed pair (余 against 餘 are two surnames). On invented probes all ten different persons are signalled, and four of twenty-one same-person pairs, exactly the four variants in neither table above ([measurement](../plan/poc-delivery-plan.md#native-script-name-probes)). Whether the signal may conclude NO_MATCH is an [open question](../plan/open-questions.md#data-and-source).

## Attribution

- A relative's age is not the subject's age; acts of family members, related companies or co-defendants are not attributed to the Profile person.
- Age or capacity at a time point is inferred only when the source names the person and the time point explicitly. "Family-controlled" or "signed by a family member" is limitation or background only.
- An indictment in a later year for a case that started earlier does not prove the person took part, or was an adult, in the start year.
- An undated spouse or family relationship is valid only as of the source capture date; it is not projected backwards.

## Temporal and role consistency

When the source states an event date and role, compute the customer's age at the event and test only approved role constraints.

Do not infer a hard conflict from vague family or organization wording.

### Age stated in a source

Applies only to an age explicitly stated for a named person in a bounded source section ([extraction](extraction-and-normalization.md#ages-in-source-text)).

- Compare ages at the same time point: the time point stated in the source, else the report date, which is disclosed. Today's age is never compared with an old report.
- Age at time point = year of time point − birth year, minus 1 if the time point's month/day precedes the birthday. Difference = stated age − age at time point.
- Requires a full DOB on the subject side; otherwise the result is `AGE_NOT_COMPARABLE`, and with a partial DOB and a report date the note gives the possible age range at the report date (`StatedAgeCheck.AgeRange`, e.g. 45～46 歲 for a birth year only), which is shown, not compared.
- |difference| ≤ 2 (inclusive) → `AGE_COMPATIBLE_AT_REPORT`, recorded like `TEMPORALLY_PLAUSIBLE`: compatible is not support.
- |difference| > 2 → `AGE_DIFFERENCE_AT_REPORT`, a conflict: row 1 of [stage 3](#stage-3-table) (row 2 when a document agrees), and the hard conflict `STATED_DOB_CONFLICT` when nothing else stated and no Profile DOB says otherwise ([two-year conflict on stated values](#two-year-conflict-on-stated-values)).
- The comparison is made on the births the age allows (below), so it is the ±2 test except by a day at 29 February.
- Without a full subject DOB or a report date the result is `AGE_NOT_COMPARABLE`. The report date of attached news is its published date; of a received page, the publication date the page states (`publishedOn`, [external sources](external-sources.md#common-path)), so a page without one gives `AGE_NOT_COMPARABLE`, never a comparison at the fetch date. A page dated only by its update date (`publishedOnSource` `UPDATE_DATE`) has no report date either: `AGE_NOT_COMPARABLE`, so an update-dated age never concludes `STATED_DOB_CONFLICT`: an update date can lie up to a year after the publication date, which makes a stated age look too young, toward a wrong conflict (measured in [external sources](external-sources.md#common-path)). The date is shown in the explanation, marked as an update date.
- An age stated with 時年 is the age when the reported event happened, whose date the text does not give; it is never compared with the report date and gives `AGE_NOT_COMPARABLE`. The marker counts in either order: after the name (張文華時年45歲) or before the age that precedes the name (時年45歲的張文華).
- Phase 1 reads the age only when it is adjacent to the Profile person's name in the same sentence of attached news (e.g. 張文華（45歲）, 張文華（男，45歲）, 45歲的張文華).
- This ±2 tolerance is the stated age's own; the Profile's DOB field has its [two-year tolerance](#birth-date-tolerance), in calendar days, not in ages.

### Birth date and gender stated in a source

Attached news and judgments often write a person's birth date and gender right next to the name (a judgment's party line, a bracket after the name). `NewsStatedCheck` sets the ones written next to the Profile person's names (the first 20, the identifier reader's limit) against the customer's. They are read by the [identifier rules](watchlist-and-adverse-information.md#news-digest), not by the news models, which feed no decision.

The news is not an approved Profile field, and the value belongs to the Profile person only because it stands next to the name. So it weighs less than the Profile's own field ([ADR-0012](../adr/0012-sources-and-web-information.md)); it concludes only on the different-person side, by the [two-year conflict](#two-year-conflict-on-stated-values).

Birth date (`STATED_BIRTH_DATE`). A year under 1000 is a ROC year (民國70年1月1日生, 70年次); precision is kept as written.

| News against the customer | Profile's own DATE_OF_BIRTH | Evidence | Code |
|---|---|---|---|
| a news date agrees | not support | support one level below a Profile field: `MEDIUM` for a full date on both sides, else `LOW` | `NEWS_BIRTH_DATE_AGREES` |
| a news date agrees | support | NEUTRAL: the same fact told twice is one piece of evidence | `NEWS_BIRTH_DATE_AGREES` |
| a news date agrees, but the news dates are scattered (3 or more, or over 2 years apart) | any | NEUTRAL: several people written the same way (masked names) | `NEWS_BIRTH_DATE_AMBIGUOUS` |
| no news date agrees | not support | CONFLICT, [stage 3](#stage-3-table) row 1, REVIEW; every one more than two years off and no Profile DOB: [`STATED_DOB_CONFLICT`](#two-year-conflict-on-stated-values) | `NEWS_BIRTH_DATE_DIFFERENCE` |
| no news date agrees | support | NEUTRAL, UNCERTAIN: two sources disagree with each other, not with the customer | `NEWS_BIRTH_DATE_DIFFERENCE` |
| the customer has no birth date | any | NEUTRAL, `SUBJECT_MISSING`; the news date is shown | none |

- A news birth date is not a direct identifier and never satisfies row 4, which needs the Profile's own DATE_OF_BIRTH; `MEDIUM` is below the `HIGH` row 3 needs. Alone it moves a pair from row 6 to row 5.
- A coarser news date that a more precise one covers is dropped, as for Profile DOBs.
- An agreeing document outranks it: with UNIQUE support a news birth date difference does not make row 1 nor the hard conflict; the pair is row 2 and `NEWS_BIRTH_DATE_DIFFERENCE` is recorded alongside. The same holds for a stated-age difference.

Gender (`STATED_GENDER`). Read only when the customer has a gender and exactly one gender stands next to the names.

| News against the customer | Profile's own gender | Evidence | Code |
|---|---|---|---|
| same | any | NEUTRAL; shown, never support | `NEWS_GENDER_AGREES` |
| different | missing or different | CONFLICT: rules out rows 3 and 4 like `GENDER_CONFLICT`, recorded alongside the row's code | `NEWS_GENDER_DIFFERENCE` |
| different | same as the customer | NEUTRAL, UNCERTAIN: two sources disagree | `NEWS_GENDER_DIFFERENCE` |

The comparison table has no row for news values: the birth date note and code sit on the 出生日期 row, the gender ones on the 性別 row, and those rows keep the status of the Profile's own field. A conflict from the news (stated age, birth date or gender) is therefore named in the explanation summary's 衝突 list (報導年齡, 新聞所載出生日期, 新聞所載性別).

**When the two sources disagree** (the NEUTRAL, UNCERTAIN rows above: the Profile's own birth date or gender agrees with the customer and the news next to the name says otherwise), the identity decision stands on the Profile's fields, but the reviewer is told, since the rows show no conflict and the attached news may be another person's:

- the pair carries `ATTACHED_NEWS_DISAGREES_WITH_PROFILE` in `pairReasonCodes` (whenever its result is REVIEW);
- the summary's 相符／衝突／無法比對 sentence ends with 來源不一致：新聞所載出生日期（、新聞所載性別）與名單記錄不同，附加新聞可能不是這位名單人物的。;
- a PROBABLE_MATCH is reviewed at NORMAL priority instead of HIGH: what the news says of the person (and the legal stage read from it) is in doubt, the match with the Profile is not. A raised Sanction condition keeps HIGH.

Measured on a development set of annotated judgments (35 suspects with an annotated birth date, 34 with a gender; the customer set to the annotated value, then to another person with the same name): for the same person no conflict was raised (birth date 27 support, 7 ambiguous, 1 not read; gender 30 agree, 4 not read); for the other person 33 of 35 birth dates and 30 of 34 genders gave the conflict. Judgments there give a birth year or year and month, so the support was `LOW`. The supplied cases' news carries no such value.

Nationality and residence written next to the name are shown in the news digest for the reviewer and are not compared: a residence has no period, and a nationality word in news is often a description, not a citizenship.

### Several news items

A Profile can have several attached news files (新聞資訊_, 新聞資訊1_, 新聞資訊2_ …, [extraction](extraction-and-normalization.md#file-layouts)). Every item is read; with one item the rules below reduce to the single-item rules.

- Stated age: read per item, each at its own report date. A compatible age in any item stands (`AGE_COMPATIBLE_AT_REPORT`); otherwise a difference stands; otherwise the first item's reading. The note lists every item's reading.
- Stated birth dates and genders: the values of all items are one reading, as if written in one item. 男 in one item and 女 in another gives no gender reading.
- Temporal check: every item's report date is an event.
- The [two-year conflict](#two-year-conflict-on-stated-values) keeps its meaning across items: at least one value compared, and every compared value, in every item, more than two years off. One near item beside one far item never concludes; an item whose value is not compared neither blocks nor concludes.
- [Implausible age at a dated document](#implausible-age-at-a-dated-document-advisory): one qualifying item gives the hint, whatever the others say.
- The legal stage across items: [watchlist and adverse information](watchlist-and-adverse-information.md#several-news-items).

### Two-year conflict on stated values

By the user's decisions of 2026-11-27, the two-year rule of the [birth-date tolerance](#birth-date-tolerance) concludes on what attached news of a list Profile, or a received page (`WEB:` key, [external sources](external-sources.md#matched-against-the-customer)), states next to the name, on the different-person side only (`HardConflictGate`):

| What the text states, against the customer's DOB | Evidence | Decision |
|---|---|---|
| every comparable value (each stated birth date; the age at the report date) more than two years off, the Profile with no DOB of its own and no HIGH or UNIQUE support | the value's own codes (`NEWS_BIRTH_DATE_DIFFERENCE`, `AGE_DIFFERENCE_AT_REPORT`) | [hard conflict](#hard-conflict-gate) `STATED_DOB_CONFLICT`: NO_MATCH |
| the same, but the Profile has a DOB (reaching the check, it agrees with the customer's, exactly or within two years) | the value's own rules: the age a conflict, the birth date NEUTRAL, UNCERTAIN | stage 3: the news may be about someone else |
| the same, but a document agrees (UNIQUE) or other HIGH support | the value's own rules | stage 3 row 2, the difference recorded alongside |
| a birth date not equal but within two years | attached news: CONFLICT `NEWS_BIRTH_DATE_DIFFERENCE`, row 1; a page: NEUTRAL, PARTIAL, `PAGE_BIRTH_DATE_WITHIN_TOLERANCE`, no score | stage 3; blocks the conflict |
| an age within ±2 (`AGE_COMPATIBLE_AT_REPORT`), or an equal birth date (`NEWS_BIRTH_DATE_AGREES`) | the value's own rules | stage 3; blocks the conflict |
| an age not comparable (no report date, 時年, no full customer DOB), or no customer DOB | not compared; stays explicitly `AGE_NOT_COMPARABLE` or `SUBJECT_MISSING` | nothing changes |

- A birth date is compared as a Profile DOB is (`PartialDate.IsMoreThanYearsApart`, at its precision: 67年次 may be two years from 1980-05-20, 66年次 cannot). An age N at the report date stands for the births from the day after the date N + 1 years before up to the date N years before; `AGE_DIFFERENCE_AT_REPORT` means that interval is more than two calendar years from the customer's full DOB, counted as for birth dates (29 February plus two years is 28 February). Away from 29 February this is the ±2 age test; for a customer born on 29 February the two differ by a day (aged 23 on 2026-02-28 allows births from 2002-03-01, one day past two years from 2000-02-29: a conflict, where an age difference gives 25 and compatible). With either date coarser the age is not compared, so it never concludes.
- Every comparable value must be off: a compatible age beside a far birth date keeps the conflict of row 1. A value not compared does not block.
- Within two years, a page and attached news differ: a page's birth date within two years is neutral, since on the synthetic pages agreement would lift same-name strangers (12 of them: 4 birth dates within a year, 8 ages 1 to 2 years off), while the conflict side concludes 10 strangers and no customer ([measurement](../plan/poc-delivery-plan.md#received-url-matched-against-the-customer)); attached news keeps its difference (row 1).
- On the synthetic list set the conflict concludes 34 pairs with a stated difference (an age 26, a birth date 8), none the same person ([measurement](../plan/poc-delivery-plan.md#synthetic-end-to-end-set)).
- Only a value read next to a full name form concludes: a CJK form of three characters or more that names somebody, or a form without CJK characters standing as a whole word (no letter, combining mark, digit, hyphen or apostrophe next to it). Anonymised and masked forms are never full forms: they fit anyone of that surname. Anonymised is a surname followed by 姓 and any noun (林姓男子, 張姓負責人, 歐陽姓主嫌) or by one of the short marks of the news digest (歐陽男, 歐陽嫌); 姓 counts only right after the surname, so a 姓 elsewhere stays a name character. Masked is a form holding 某 (陳某某), or one whose characters other than ideographs are all masks (○, 〇, ◯, Ｏ, O, ＊, *, Ｘ, X: 歐陽○明). The readers find a name where it appears, so a two-character alias (文華) is read inside another person's name (林文華（30歲）), and 'Hua' inside 'Wen-Hua'; such a reading stays evidence with its row (and a near value read by it still blocks), but it never concludes. A Profile whose only form has two CJK characters therefore never concludes by this rule. Residual cases of the guard: (a) a three-character name that is itself part of another person's four-character name (a two-character surname before it) still concludes; (b) rare characters outside the basic CJK range (written as surrogate pairs) are not counted as CJK, so a two-character alias made of them counts as a full form and the guard does not protect it.
- The source-quality downgrade applies as to any validated rule (an unparseable Profile value gives REVIEW with `SOURCE_QUALITY_DOWNGRADE`); HIGH or UNIQUE support stops the check first, MEDIUM or LOW support (an employer over the same period, a birth year) does not.
- A pending impossible-role conflict (`IMPOSSIBLE_ROLE_AT_REPORTED_EVENT`) comes first in the gate: such a pair stays REVIEW on the pending rule, not NO_MATCH on this one.
- List Profiles without a birth date of their own are the usual case for this rule: there the stated value is the only date, so it decides. Residual risk of closing the same person, accepted with the decision and not measured (the synthetic sets have no such text); to be checked on real labelled pages and news before the rule is relied on: (a) a text that states the age as of a past event without 時年 (in either order) while its report or publication date is later (a retrospective report, a reprint, an updated article) has that age compared with the age at the report date; (b) an age counted the traditional way (虛歲, up to two years over) sits on the boundary, so any further offset concludes; (c) a wrong or late report date (a retrieval or reprint date taken for the publication date) shifts every stated age; (d) a typo in a stated age or birth date; (e) MEDIUM support for the same person (an agreeing employer) does not stop the conclusion.

## Hard conflict gate

Stage 1 of the three-stage policy ([ADR-0001](../adr/0001-identity-adjudication.md)).

| Rule | Reason code | Validation | Phase 1 effect |
|---|---|---|---|
| impossible age at explicitly attributed event | `IMPOSSIBLE_AGE_AT_REPORTED_EVENT` | GC-01 | automatic NO_MATCH |
| impossible role at explicitly attributed event | `IMPOSSIBLE_ROLE_AT_REPORTED_EVENT` | pending; exercised by [AUG-01-01](../plan/golden-cases.md#augmented-cases) | REVIEW |
| every Profile DOB more than two years from the customer's, full dates on both sides | `FULL_DOB_CONFLICT` | user's decision 2026-11-27 ([birth-date tolerance](#birth-date-tolerance)); GC-06 not supplied; exercised by unit tests | automatic NO_MATCH |
| the same at year or month precision | `DOB_CONFLICT` | user's decision 2026-11-27; exercised by AUG-02-02 | automatic NO_MATCH |
| every birth date and dated age attached news or a received page states next to the name more than two years from the customer's DOB, the Profile without a DOB of its own | `STATED_DOB_CONFLICT` | user's decisions 2026-11-27 ([two-year conflict on stated values](#two-year-conflict-on-stated-values)); exercised by unit tests, AUG-02-04, AUG-02-16 and both synthetic sets | automatic NO_MATCH |
| comparable document-identity conflict | `DOCUMENT_IDENTITY_CONFLICT` | pending (reissued documents unverified); exercised by AUG-11-02 | REVIEW |
| source-record death preceding customer birth | `DEATH_BEFORE_CUSTOMER_BIRTH` | pending; exercised by AUG-11-06 | REVIEW |

A pending rule still fires and records its reason code; it only cannot close the pair automatically. The gate checks, first rule wins: death before birth, impossible age, the DOB conflict, impossible role, the stated-value conflict. Impossible age and impossible role need a subject DOB to the day: with a year or month only, the age at the role start is not known and neither rule fires. A document conflict reaches REVIEW through stage 3 row 1. The DOB conflict is validated by the user's decision, not by a Golden Case: GC-06 (different persons with full DOBs) is still to come and will check it. Impossible role uses a controlled title list pending approval: directors and supervisors (董事, 董事長, 副董事長, 常務董事, 獨立董事, 監察人, Director, Supervisor) need full legal capacity, adulthood being 20 before 2023-01-01 and 18 since. The whole title must be on the list, so "Director of Marketing" is not a board director. Death before birth needs every date the death value can stand for to precede every date the DOB can stand for. A rule moves to automatic NO_MATCH when its Golden Case is validated and the policy version is released.

Each rule records its reason code as the pair's `hardConflict`. Source-quality concerns can downgrade a potential hard conflict to REVIEW. `HardConflictGate.SourceQualityConcern` (pending approval) turns a validated rule into REVIEW with `SOURCE_QUALITY_DOWNGRADE` when:

- the Profile has values the reader could not parse;
- the same pair has HIGH or UNIQUE support (for a DOB conflict: an agreeing document);
- for impossible age: the Profile has a dated role without its organization, or a role dated to the year only, or its own DOB makes a role just as impossible, so the role date itself is suspect;
- for a DOB conflict: the Profile's DOBs are scattered (three or more, or over two years apart).

### Implausible age at a dated document (advisory)

The shape it reads is a Profile without a birth date of its own and a customer far too young at a dated fact of the Profile person: on the header date of an attached item naming the person as the accused or a company officer, or at the earliest event the Profile's English notes date for the person (the second source, below). It is a hint, not a hard conflict (user's decision 2026-11-27, [ADR-0001](../adr/0001-identity-adjudication.md)): `HardConflictGate.ImplausibleAgeAtDocument` gives the TEMPORAL_ROLE evidence a CONFLICT with the advisory code `AGE_IMPLAUSIBLE_AT_DOCUMENT` (not a validated code), and the pair goes to [stage 3](#stage-3-table) row 1, REVIEW, where a stated age difference sits; it is never NO_MATCH. With an agreeing document the pair is row 2 and the code is recorded alongside. With a DOCUMENT conflict the pair is row 1 on `DOCUMENT_IDENTITY_CONFLICT` (that conflict comes first): the note and the explanation line still appear, but `AGE_IMPLAUSIBLE_AT_DOCUMENT` is not among the pair's reason codes (a known limit). A pair a hard conflict decides (for example `STATED_DOB_CONFLICT`) carries no hint: no code, no note, no explanation line. The hint is given when all of these hold:

- the customer's birth date is known to the day, and the Profile has no birth date of its own (else the [birth-date rules](#birth-date-tolerance) decide);
- an attached item of the Profile (a list Profile's news, or a received page by its publication date, [external sources](external-sources.md#matched-against-the-customer)) has a header date to the day: the media article's date line or a court release's 公告日 ([extraction](extraction-and-normalization.md#file-layouts)). Never a date in the body, a year-only or relative date, the vendor's review date or a page's update date (`UPDATE_DATE`); the Profile notes are the second source, below;
- the customer's oldest possible age that day is under 14 (`MinimumRoleAge`, the age of criminal responsibility; the role-table rule uses the same constant). A customer born after the header date (a negative age) is hinted too;
- the role is read locally, at the Profile person's own mentions in that item (`HardConflictGate.RoleAChildCannotHold`), under a [full name form](#two-year-conflict-on-stated-values) only:
  - basis: a suspect word (被告, 共同被告, 嫌犯, 犯嫌, 嫌疑人) or a company officer word (董事長, 副董事長, 董事, 監察人, 總經理, 負責人, 執行長; 實際負責人 and 常務董事 end in one; a 前 prefix counts) standing directly before a whole full-name mention. Nothing else: no event the news digest attributes (its actor falls back to the last name earlier in the sentence, so 檢察官張文華今天起訴一名詐欺嫌犯 would read as the person indicted), no role or party of an anonymised alias the digest folds into the person (張嫌 beside 被害人張文華), no model reading. So 判處張文華有期徒刑, with no word before the name, does not count;
  - whole mention: the name is followed by punctuation, whitespace, the end of the text, a particle that never starts a name or an organisation (涉, 遭, 被, 與, 及, 等, 因, 已, 則, 也, 亦, 稱, 說, 並, 坦, 供, 否, 辯, 均, 獲, 未, 不, 的) or an offence or penalty word of a court text (詐欺, 貪污, 違反, 侵占, 背信, 洗錢, 竊盜, 偽造, 有期徒刑, 無期徒刑, 拘役, 罰金, 死刑). 被告張文華宏 and 張文華國小 are other names. This boundedness applies to this rule only; the digest and the other readers do not use it;
  - disqualifiers: any mention of a full name directly after a victim, complainant or witness word (被害人, 受害人, 被害者, 受害者, 告訴人, 告發人, 檢舉人, 證人), a child word (少年, 少女, 男童, 女童, 兒童, 學童, 幼童, 學生) or an official word (主任檢察官, 檢察官, 審判長, 法官, 律師, 辯護人, 警員, 員警, 警官) makes the item silent, whatever its other mentions say (法官張文華 beside 被告張文華 gives no hint);
  - a minor or child context anywhere in the item makes the whole item silent, on purpose for the whole item (cheap, and it errs to silence; a juvenile defendant may be the young customer): 少年, 少女, 未成年, 兒童, 男童, 女童, 幼童, 學童, 孩童, 國小, 國中, 小學, 中學生, 學生 (so also 少年法庭, 國中生, 小學生), 高中, 國一, 國二, 國三, 幼稚園, 幼兒園, 就讀, 年幼, 年紀尚幼, 幼兒, 嬰兒, 稚子, 小六生, 未滿 N 歲, or any age under 18 stated anywhere (N 歲, 年僅 N);
  - a guardian or limited capacity anywhere makes it silent (法定代理人, 監護人, 監護宣告, 受監護, 輔助宣告, 無行為能力, 限制行為能力, 代為出席, 由父母), and so does a civil or inheritance matter (民事, 原告, 繼承, 遺產, 損害賠償, 連帶給付): a minor can be a civil defendant or an heir holding shares;
  - a kinship word directly before a full name or its role word makes it silent (兒子, 女兒, 孫子, 孫女, 子女, 幼子, 幼女);
  - a role word inside another word does not qualify (原被告, 準被告, 代位被告); the officer compounds 實際負責人, 副董事長, 副總經理, 獨立董事, 常務董事 are listed and qualify, and a case number before 被告 (第1234號被告) is not such a word;
  - a nominal officer or a name used by someone else anywhere in the item makes it silent (人頭, 掛名, 冒名, 冒用, 盜用, 借名, 名義負責人, 登記負責人): a minor so registered or whose name was used is the same person;
  - a mention does not qualify when its sentence holds a negation, mistaken-identity, misuse or harm cue (否認, 並非, 不是, 誤傳, 誤認, 誤植, 同名, 冒用, 冒名, 盜用, 人頭, 掛名, 名義, 被害, 受害, 遭詐, 受騙), or 非 stands directly before its word (非董事張文華); a qualifying mention in another sentence without such a cue still counts;
- the item does not state an age compatible with the customer's at that date, or the customer's birth date.

The second source is the Profile's own English notes (`HardConflictGate.ImplausibleAgeInNotes`), read only when no attached item grounds the hint. The vendor's record usually has no birth date but a narrative with dated acts (a charge date, the years of the acts); a bank reviewer concludes "not the same person: age" from exactly that. The hint is given when all of these hold:

- the customer's birth date is known to the day and the Profile has no birth date of its own (as above). An event dated to the year only suffices: the customer's oldest possible age is taken over every day the event can stand for (`MaxAgeAt` on the partial date), so a year is enough wherever the gap is that large; a customer birth date to the year or month only gives no hint, since a bank record without a full date is itself suspect;
- the notes date an event of the Profile person ([what is read and skipped](extraction-and-normalization.md#profile-notes)): an act word in a sentence that refers to the person (a whole Latin name form, a capitalised token of three letters or more, or he / she), with no relative, associate or company named anywhere in it and no victim, witness, investor or similar party before the act word, and no review, birth, negation, hypothesis or law named by year; and a date in a date context (a full date, a month and year, a span, or a year after in, since, during, by, until, before, after, around, early, mid or late; never a bare number such as an amount or a case number). At the earliest such event the customer's oldest possible age is under 14 (the same `MinimumRoleAge`), or the customer was not yet born, the more extreme case;
- the notes, read whole (every part and subject), state no age (its reference date is not read, so it may fit the customer) and no birth date within two years of the customer's (born, birth, Date of Birth, DOB). A birth date further off concludes nothing (the notes' DOB is not a Profile field) and the hint stays as it would otherwise;
- the notes hold at most 100 date mentions, and no pattern match takes over 200 ms; else they are silent.

Everything else is shared with the first source: the same code and evidence, stage 3 row 1 REVIEW, row 2 with the code alongside under an agreeing document, no hint on a pair a hard conflict decides, no score item. The hint is weighed before strong support that is not a document (row 3), so such a pair is REVIEW on row 1 while the scoring band, which gives the hint no item, is unaffected: the measurement-only score and the identity decision can then differ. The note names the source, the sentence, the event and the customer's age then, for example 「名單附註（名單資料_7900001.txt:14 Profile 第 2 句）記載名單人物「charged」於 2005，當時客戶最多 3 歲；未自動判定，請覆核」 or 「…當時客戶尚未出生…」, and the explanation names the conflict 名單附註事件時的年齡.

The word lists are in one place per source, `Identity/DocumentAgeWords.cs` and `Identity/NotesEventWords.cs`. The rule reads natural-language text with these closed lists, which are known to be incomplete. The reading is linear in the item: sentence ends are found once and each sentence's cues checked once; over 1,000 mentions of the Profile's names in one item the item is silent. No hint: an official only, a person merely mentioned, a masked or anonymised form, a two-character alias, a role not read. One qualifying item is enough for the hint, whatever another item says.

What the reviewer sees: the TEMPORAL_ROLE note names the item (its place in the attached order, from 1), its header date, the role read and the customer's oldest age that day, and the explanation opens the pair's 相符／衝突 sentence with it and names it first among the conflicts (附加新聞日期時的年齡), for example 「第 1 則附加新聞（報導日期 2017-05-09）稱名單人物為「董事」，當日客戶最多 10 歲；未自動判定，請覆核。」 Review priority follows the existing rules: row 1 REVIEW is LOW, HIGH when a Sanction condition is raised; so a pair that would otherwise sit on rows 3 to 5 (PROBABLE_MATCH at HIGH, or partial support at NORMAL) is row 1 at LOW with the hint, as with a stated age difference. The [score band](scoring-rule.md) gives it no item (0 points) and no veto.

Why it is advisory: it reads natural-language text with closed word lists known to be incomplete, and it is not measured on real labelled Cases with the bank's decisions. It becomes a concluding rule only on a measured error rate on such Cases ([open question](../plan/open-questions.md#data-and-source)). On the real-shape synthetic set the attached-item source is given to 6 pairs, all strangers, which a veto would conclude, and the notes source to 3 strangers and none of 9 same-person pairs built as traps; a concluding "not yet born" would decide 1 pair, "under 14" 3, all rightly ([measurement](../plan/poc-delivery-plan.md#real-shape-synthetic-set)). That checks the logic on chosen forms, not a rate.

Ways the hint can be wrong, were it a veto (not measured on real data): (a) a vendor Profile that merges namesakes: when the customer is one of them, an adult namesake's item on the same Profile hints against the customer, whatever the customer's own items say; (b) a compatible stated age in another item does not cancel the hint (only the qualifying item's own stated age does); (c) a wrong header date (a database's load date, a reprint); (d) a customer birth date entered wrongly at the bank; (e) a minor used as a nominal officer or account holder, or whose name was used without consent, is hinted although it is the same person when the item says nothing of it; (f) a sentence form the word lists do not know, such as a guardian, civil-suit or inheritance context written in other words than the listed ones. A minor of 14 or more who really is a defendant is outside the hint by the threshold, as wanted. From the notes, in addition: (g) the word lists are closed and English only; (h) the subject test is word-based at sentence level, not grammatical, so an unlisted co-accused ("Chen and Lin were charged in 2005") lends its date to the person, and an unlisted other party standing before the act word is not seen; (i) a vendor narrative may mix persons, and "he" is whoever the vendor meant; (j) a wrong year in the narrative, or a context year of a hearing or an appeal in a sentence with an act word, is read as the event; a date in a context the rule does not know is not read, which can only drop an event. Company officers take the same 14, not adulthood, by decision (the [impossible-role rule](#hard-conflict-gate) with adulthood stays pending).

## Decision policy

```text
Confirmed hard conflict
    → NO_MATCH

Approved unique or sufficiently strong combination, no hard conflict
    → MATCH once the scoring rule decides; today PROBABLE_MATCH / external REVIEW

Multiple strong supports but no unique identifier
    → PROBABLE_MATCH / external REVIEW

Insufficient or conflicting evidence
    → REVIEW
```

Today the identity decision is NO_MATCH, REVIEW or PROBABLE_MATCH; no MATCH is emitted. PROBABLE_MATCH is internal ([ADR-0001](../adr/0001-identity-adjudication.md)). The decided [scoring rule](scoring-rule.md) replaces the stage 3 table below once its weights are approved and its gates pass; until then its proposed weights are tried in measurement only (`ScoringRule`, computed by tests beside the table), the table decides and no score is part of a result. Automatic MATCH is allowed (user, 2026-11-26) in the scoring rule's MATCH band without a veto or cap, from the switch on, for every list type. Neither NO_MATCH nor MATCH is held for Sanction, PEP or RCA candidates: the identity decision gives the [combination matrix](data-contracts.md#combination-matrix)'s cell whatever the list type (no blanket human review, the user's decision of 2026-11-27). A wrong NO_MATCH on a sanctioned person is the most expensive error the system can make; the validated rules above and their downgrades are what stand against it, and they are not yet checked on a real Sanction or PEP Golden Case or against placeholder 1 January birth dates on lists. `FellegiSunter.Score` computes a calibration weight for Gate D only ([calibration weight](#calibration-weight)); the analysis does not run it, and it is outside the result and the explanation. The calibration tools are in place and are not wired to any decision: `FellegiSunter.Pattern` turns a pair's evidence into an agreement pattern, `EstimateParameters` estimates m, u per group and the match share from unlabelled patterns by expectation-maximization under conditional independence, `Weight` scores a pattern with the estimate, and `Calibrate` picks the highest weight threshold that keeps a required recall on labelled pairs, with the precision it gives.

### Support strength

Proposed, pending GC-06/GC-07 calibration. Levels follow the chance of an accidental agreement (u): rarer agreement, higher level.

| Group | Comparison | Strength | Rough u |
|---|---|---|---|
| DOCUMENT | comparable document key, EXACT | UNIQUE | ≈ 0 |
| DATE_OF_BIRTH | DAY precision on both sides, EXACT | HIGH | ≈ 1/22,000 |
| DATE_OF_BIRTH | MONTH precision agrees | MEDIUM | ≈ 1/700 |
| DATE_OF_BIRTH | YEAR only agrees | LOW | ≈ 1/60 |
| DATE_OF_BIRTH | not equal, within two years (`DOB_AGREES_WITHIN_TOLERANCE`) | LOW | ≈ 1/9 for a five-year window of births over some 45 years (not measured) |
| ADDRESS | same period, house-number level | MEDIUM | |
| ADDRESS | same period, district level only | LOW | |
| EMPLOYMENT, COMPANY_ASSOCIATION, RELATIONSHIP | explicit, same period | MEDIUM | |
| OTHER_IDENTIFIER | EXACT, nature unconfirmed | LOW | |
| NAME, NATIONALITY, GENDER | any | none: hit prerequisite | |

Age compatibility and temporal plausibility have no strength: they are not support.

### Stage 3 table

Applied only when no hard conflict fired. The first matching row wins. The table must reproduce every validated [Golden Case](../plan/golden-cases.md); a row that breaks one is wrong, not the case.

| # | Condition | Decision | Reason code |
|---|---|---|---|
| 1 | a `CONFLICT` in DOCUMENT; or, without UNIQUE support, the advisory `AGE_IMPLAUSIBLE_AT_DOCUMENT` (TEMPORAL_ROLE), `AGE_DIFFERENCE_AT_REPORT` or a `CONFLICT` in STATED_BIRTH_DATE (a DATE_OF_BIRTH conflict never reaches stage 3: it is the stage 1 rule) | REVIEW | the conflict's code, the first of `DOCUMENT_IDENTITY_CONFLICT`, `AGE_IMPLAUSIBLE_AT_DOCUMENT`, `AGE_DIFFERENCE_AT_REPORT`, `NEWS_BIRTH_DATE_DIFFERENCE` |
| 2 | UNIQUE support | MATCH when the release policy allows; Phase 1: PROBABLE_MATCH | `UNIQUE_IDENTIFIER_AGREEMENT` |
| 3 | HIGH support plus at least one other non-prerequisite support, and no `GENDER_CONFLICT`, STATED_GENDER conflict or `NATIVE_NAME_CONFLICT` | PROBABLE_MATCH | `STRONG_SUPPORT_COMBINATION` |
| 4 | every hit prerequisite is EXACT (name after normalization and disclosed relaxations, without `NATIVE_NAME_CONFLICT`; gender, nationality), DATE_OF_BIRTH is support at the finest precision both sides allow, and no DOCUMENT or EMPLOYMENT value is CONFLICT or UNCERTAIN other than `NOT_COMPARABLE` | PROBABLE_MATCH | `CONSISTENT_AT_AVAILABLE_PRECISION` |
| 5 | at least one non-prerequisite support | REVIEW | `PARTIAL_SUPPORT_ONLY` |
| 6 | none | REVIEW | `NO_NON_PREREQUISITE_SUPPORT` |

- Row 4 uses the prerequisites as preconditions, not as support: they never raise a pair on their own (row 6 still applies when nothing else agrees).
- Address and other location differences are weak ([comparison rules](#comparison-rules)); they never trigger row 1 and never block row 4.
- Employment is never a conflict (a different employer is not evidence against identity), so it never triggers row 1; an uncertain employment still blocks row 4.
- A DOB agreement recorded as `PROFILE_MULTIPLE_DOB` is not support, so it does not satisfy row 4.
- Agreement within tolerance (`DOB_AGREES_WITHIN_TOLERANCE`, LOW) satisfies row 4 like a birth year agreeing, and is a support for row 5; it is never the HIGH support row 3 needs.
- Opposite genders rule out rows 3 and 4; the pair falls to row 5 or 6 and `GENDER_CONFLICT` is recorded alongside. A gender in attached news that differs from the customer's does the same with `NEWS_GENDER_DIFFERENCE` ([birth date and gender stated in a source](#birth-date-and-gender-stated-in-a-source)).
- Romanised names that agree over Chinese-character names that differ ([native-script name conflict](#native-script-name-conflict)) rule out rows 3 and 4 the same way, with `NATIVE_NAME_CONFLICT` recorded alongside; with UNIQUE support the pair stays row 2 and the code is recorded beside it.
- A document number outranks what news states next to a name: with UNIQUE support, a stated-age or news birth date conflict does not make row 1; the pair is row 2 and the conflict's code is recorded alongside. A conflict in the Profile's own DOCUMENT still comes first.
- `NO_BILATERAL_DIRECT_IDENTIFIER` is recorded alongside whenever it applies.
- Review priority follows the row: 2, 3 and 4 first, then 5, then 1 and 6.

Golden Case check:

| Case | Evidence | Row | Result | Expected |
|---|---|---|---|---|
| GC-01 | impossible age at an explicitly attributed role | stage 1 | NO_MATCH | NO_MATCH |
| GC-02 | exact name and nationality only | 6 | REVIEW | REVIEW |
| GC-03 | normalized name, gender, nationality agree; birth year agrees, Profile has year only; cross-country address | 4 | PROBABLE_MATCH | PROBABLE_MATCH / REVIEW |
| GC-11 | name, gender, nationality agree; one of two Profile birth years agrees; Profile has no document, employment or address | 4 | PROBABLE_MATCH | PROBABLE_MATCH / REVIEW |
| GC-12 | related party: name and nationality agree, gender missing; Profile has no DOB; nothing else comparable | 6 | REVIEW | REVIEW |

The birth-date tolerance decides no Golden Case: GC-01 is the impossible-age veto, GC-03 and GC-11 agree on the birth year, GC-02 and GC-12 have no Profile DOB. Two augmented cases sit on its boundary ([augmented cases](../plan/golden-cases.md#augmented-cases)): AUG-02-02 (a year more than two years off) is stage 1 NO_MATCH `DOB_CONFLICT`, and AUG-11-05 (a full Profile DOB under two years from the customer's) is row 4 `DOB_AGREES_WITHIN_TOLERANCE`.

Row 4 makes common names with a shared birth year, or a birth date within two years, PROBABLE_MATCH. That only raises review priority; GC-07 (highly similar, final NO_MATCH) will show whether it needs narrowing.

### Calibration weight

`FellegiSunter.Score` sums one weight per compared group: log₂(m/u) when it agrees, log₂((1−m)/(1−u)) when it disagrees, where m is the share of same persons that agree and u the share of different persons that agree by chance. It never decides, so the hit prerequisites weigh here although they never support: without them a pair's pattern holds only DATE_OF_BIRTH and DOCUMENT, and expectation-maximization over that does not converge on list data. With them it reaches one answer from every start.

`Pattern` and `Score` read the evidence the same way, save a CONFLICT outside DATE_OF_BIRTH and DOCUMENT (a dated role, a stated age, birth date or gender, an entity identifier): `Pattern` marks it as disagreeing, `Score` gives it no weight, since no u is known for it.

| Evidence | Pattern | Weight |
|---|---|---|
| SUPPORT in any group | agrees | u from the strength (DATE_OF_BIRTH: its measured u below; others: the rough u of [support strength](#support-strength)) |
| CONFLICT in DATE_OF_BIRTH or DOCUMENT | disagrees | u of HIGH (DOB) or UNIQUE (document) |
| prerequisite EXACT | agrees | the group's u below |
| prerequisite CONFLICT (gender, nationality) or PARTIAL (name) | disagrees | the group's u below |
| PARTIAL name with `NOT_COMPARABLE_CROSS_SCRIPT`, a value missing on either side, anything NEUTRAL | left out | none |

A name has no CONFLICT: every compared name that is not EXACT is PARTIAL, similar or not, and row 4 reads it as not agreeing. So PARTIAL is the name's disagreement, and the name m below is the share of same persons whose names are EXACT. A romanization variant disagrees like any other PARTIAL name; a level of its own needs its own m and u from labelled pairs. Agreeing with any one of k Profile DOBs is k times as likely by chance, so the DOB u is multiplied by k.

Placeholders, uncalibrated (`ModelVersion` `fs-calibration-0.2`):

| Group | m | u | Where the numbers come from |
|---|---|---|---|
| NAME | 0.76 | 1/4,200 | m: EM. u: two occurrences of three-character full names in the annotated Chinese news corpus share the name at most 1 time in 4,200 (an upper bound: the same person recurring across reports counts); Latin-script list names agree far less often, about 1 in 100,000 across the two lists (a figure raised by people listed on both; within one list about 3 in 10,000,000) |
| GENDER | 0.99 | 0.5 | m: EM gives 1.00, kept below 1 so a disagreement stays finite. u: a general population (0.85 on the mostly male lists) |
| NATIONALITY | 0.99 | 0.1 | m: EM. u: 0.11 OFAC against itself, 0.018 UN against OFAC; it depends wholly on the customer population |
| DATE_OF_BIRTH | 0.91 | HIGH 1/13,700, MEDIUM 1/440, LOW 1/50 | m: EM (any agreement). u: 2,000,000 random pairs of different OFAC persons, the share that gives a support of that strength among the pairs whose precision allows it: HIGH counts a day-precise agreement only (any agreement of a day-precise pair, also on the year alone, is 1 in 7,350); MEDIUM rests on 44 agreements |
| DOCUMENT | 0.96 | 1e-7 | m: EM. u: no chance agreement in 12,810 comparable random pairs, which bounds it below 2.3e-4 and cannot confirm 1e-7 |
| any other group | 0.95 | the rough u of its strength | not estimated: list data has no support in other groups |

m comes from EM over 733 UN × 7,561 OFAC individuals with the prerequisites in the pattern (match share 1.08e-4, the same from three starts). The calibration run, its tool and its report stay outside the repository.

Limits:

- **Population.** A sanctions list is not a customer population: few nationalities, mostly male, and list dates cluster on placeholder days. The DOB u, measured on the list against itself, is an upper bound for a customer against a list; gender and nationality u depend on the customer population.
- **Script.** Almost every list name is Latin script, so EM's name m says nothing about Chinese-character names, variant folding or romanization; the name u is a Chinese-corpus upper bound, not a list measurement.
- **Upper bounds.** The name, DOB and document u are upper bounds or bounds from zero counts, not estimates; a weight from them is conservative on agreement.
- **The matches are the estimate's own.** No pair is labelled: the same persons are those EM takes for matches, and 99% of the pairs it is sure of have an exact name, so the name m rests on the roughly 140 estimated matches whose names differ, and same persons spelled differently across the lists are under-represented.
- **Independence.** EM assumes conditional independence between groups; the samples are too small to test name–nationality dependence.

## Reason code registry

Only codes in the code catalog may appear in decisions. The catalog (`Core/Codes/CodeCatalog.cs`, served by `GET /api/codes`) is the one registry: each code's name, meaning, emitter and the section of this or another spec that defines it. A new code is added there with the rule that emits it; this document defines the rules, not a copy of the list.

Comparison findings are evidence details, not decision reason codes; they explain an evidence status and never change a row on their own:

| Finding | Recorded on | Meaning |
|---|---|---|
| `NAME_ORDER_VARIANT` | NAME, EXACT | names agree with the surname moved |
| `DIACRITIC_VARIANT` | NAME, EXACT | names agree after diacritics are folded |
| `VARIANT_CHARACTERS` | NAME, EXACT | names agree after controlled variant characters are folded |
| `ROMANIZATION_VARIANT` | NAME, PARTIAL | romanized names read the same in different romanization systems |
| `NOT_COMPARABLE_CROSS_SCRIPT` | NAME, PARTIAL | no subject and Profile name share a script |
| `NATIVE_NAME_CONFLICT` | NAME, EXACT or PARTIAL `ROMANIZATION_VARIANT` | romanised names agree, complete Chinese-character names differ; also a reason code: it rules out rows 3 and 4 ([native-script name conflict](#native-script-name-conflict)) |
| `PROFILE_MULTIPLE_DOB` | DATE_OF_BIRTH, neutral | the agreeing DOB is one of many or scattered Profile values; not support |
| `NOT_COMPARABLE` | DOCUMENT or EMPLOYMENT | a value is present but cannot be compared (another document type or country; occupation category only); does not block row 4 |
| `OVERSIZED_GROUP` | Profile merge group | more than ten Profiles merged; inspect the edges |

## Explainability

Every decision records its evidence per group, its hard-conflict code or stage 3 row, and its reason codes. The hit explanation restates it as:

- supporting evidence;
- conflicts;
- unknown fields;
- hard conflict and stage 3 row;
- reason codes.

The revision's lineage records the input file hashes, the list version of each Profile used and the source-condition policy, code catalog, schema and app versions. The Fellegi-Sunter weight does not run in the analysis and so is not recorded ([ADR-0023](../adr/0023-business-api-data-and-workbook.md)). The identity rules have no version of their own; the app version identifies them. Retrieval scope is not recorded.

Every code a decision or explanation emits is in the code catalog, served by `GET /api/codes` with its meaning and the section that defines it.

### Contrastive explanation

`IdentityDecisionPolicy.Contrast` answers "what would change the decision": for a document of the same type and issuing country, and for a full DOB, each not yet comparable on both sides, it assumes agreement and conflict in turn and re-runs the [stage 3 table](#stage-3-table) with every other piece of evidence fixed. A full DOB more than two years off is the stage 1 rule: it is listed as `FULL_DOB_CONFLICT` with decision NO_MATCH and no row (`stage3Row` null; 若完整出生日期相差逾 2 年：身分判斷為第一階段硬衝突 FULL_DOB_CONFLICT), without re-checking the source-quality downgrade. An outcome on the current row is left out. For GC-03 (row 4): a document that agrees moves the pair to row 2 `UNIQUE_IDENTIFIER_AGREEMENT`, one that conflicts to row 1 `DOCUMENT_IDENTITY_CONFLICT`, and a full DOB more than two years off gives the stage 1 `FULL_DOB_CONFLICT`; a full DOB that agrees keeps row 4 and is not listed.

- It is empty for non-Person records, a pair with a hard conflict, rows 1 and 2, and a Profile whose DOBs are scattered (`PROFILE_MULTIPLE_DOB`).
- It names the row, the identity decision and the code only, never the external result: Phase 1 shows every PROBABLE_MATCH as REVIEW.
- Only stage 3 is re-run. A full DOB would in reality also reach the stage 1 checks (age at a dated role), which are not re-run.
- The wording of each line (`若同類證件相符：身分判斷移至第三階段第 2 列 …`) is on the business confirmation list; changing it touches the text only, not the rule.

A name similarity score is shown only for names located in a single source field. A full score means the names are identical after normalization; a missing score does not mean the source has no name. The score is never converted into a likelihood.
