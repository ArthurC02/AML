# Extraction, Normalization and Data Quality

> Module: Extraction and Data Quality. Design baseline 2.0-PoC, 2026-11-02.

## Extraction principle

Extractors report what the source states. They do not decide identity or screening outcomes.

Data arrives through `ICustomerSource` / `IProfileSource`; the PoC implements HTML-file sources only ([ADR-0012](../adr/0012-source-acquisition-behind-ports.md)). HTML parsing lives in Infra (AngleSharp); normalization rules live in Core.

## Sources and refresh

Steps 1 to 3 of the [operating architecture](system-overview.md#operating-architecture) process list and public data only, never customer data:

| Source | Examples | Format |
|---|---|---|
| official lists | Sanction, PEP, RCA | PDF, HTML, API |
| public news and court rulings | adverse media, indictments, judgments | PDF, HTML |
| other public sources | registries, government sites | HTML, API |

- Each source gets its own adapter behind `IProfileSource` ([ADR-0012](../adr/0012-source-acquisition-behind-ports.md)); the PoC builds the `.txt` export reader, then the HTML adapter when samples arrive. PDF and API adapters follow when a source needs them.
- PDF text is read only from the text layer; scanned PDFs need OCR, which is out of scope ([system overview](system-overview.md#excluded-for-phase-1)).
- Structured parsing extracts person, event, role and legal status, with evidence locations.
- List data refreshes on a schedule or on an event (a source update). Each refresh keeps the previous snapshot; a changed Profile triggers re-analysis of the Cases that used it, producing a new revision ([case management](case-management.md)). Schedule and triggers are [open](../plan/open-questions.md#technology-governance).

## HTML processing

1. preserve raw source and content hash;
2. identify page type and source template version;
3. parse DOM, tables, rowspan/colspan and nested structures;
4. read actual selected values for select/radio/checkbox controls;
5. map source labels to canonical fields;
6. normalize by field type;
7. emit data-quality findings;
8. return typed snapshots and evidence locations.

Do not flatten the entire HTML to text before interpreting table and control relationships. Checkbox states, dropdown selections, input values, links and merged headers are all read.

## Input inventory

- Source case data is read-only.
- Every attachment is inventoried. File type is decided by content, not extension: a `.txt` may be page source, an extracted table or hand-copied text.
- An attachment the extractor cannot read (image, Word, unknown format) produces an `UNSUPPORTED_ATTACHMENT` finding. It is disclosed as unread, never treated as "no data"; opening an image does not mean its text was read.
- An archive over the file-count or extracted-size limit stops the whole batch; it is never truncated or partially accepted.

Customer, related party and Profile data are kept per person; one person's values are never attributed to another. Related parties arrive in their own file (e.g. `客戶-關聯人背景資料.txt`, `key ：` lines) with their role (e.g. 帳戶持有人的法定代理人) taken from the case table. The account holder's files are still read for a related-party hit and stored as case context, linked to the account holder, not to the related party.

## Source flags

PEP, blacklist and adverse-news flags and `Is RCA?` are read per candidate row:

| Source text | Value |
|---|---|
| 是、✓、✔、☑、V、Y、Yes、TRUE、1、checked、on, or a ticked checkbox | `YES` |
| 否、不是、X、✗、×、N、No、FALSE、0、unchecked、off | `NO` |
| blank, -, –, —, N/A, NA | `MISSING` |
| anything else | `UNPARSEABLE`: re-read the source; if still unreadable the case cannot be published |

`NO` is an explicit value, not unknown.

## Legacy review records

Earlier manual results in the input, such as "是否符合名單", "理由" and the 客戶檢核處理紀錄 section with its table, are extracted as ImportedDecision ([case management](case-management.md#decision-records)) and excluded from every evidence path. The exclusion ends where that table ends; if the heading has no table, or text after the table looks like a continuation, a `LEGACY_RECORD_BOUNDARY_UNCLEAR` finding asks the reviewer to confirm. Source flags are not decisions and stay usable.

## Name normalization

Preserve original name, language/script when known, components, source alias type and generated comparison variants.

Processing may include:

- Unicode NFKC;
- case, full-width/half-width and whitespace normalization;
- controlled Traditional/Simplified Chinese comparison variants;
- Vietnamese diacritic-preserving and diacritic-free forms;
- controlled given/family-name order variants;
- romanization variants of Chinese names for retrieval: Hanyu Pinyin, Wade-Giles, Tongyong, Taiwanese passport spellings (e.g. JENN, JEN, REN for 仁; JONG, JHONG, CHUNG, ZHONG for 鍾/鐘); every syllable must share a Pinyin reading from a controlled table (`Romanization`), and a match is PARTIAL with `ROMANIZATION_VARIANT`, never EXACT;
- variant characters with the same reading (鍾/鐘) as comparison variants;
- hyphen and punctuation variants.

Source aliases and generated search variants remain distinguishable.

Names keep their source characters and script:

- a romanized-only name is never back-converted to Chinese;
- pinyin or romanization similarity is a search variant, never a new source name;
- names recorded in separate fields are not joined into a full name the source never states;
- a Chinese–English name relationship is compared only when the source gives both forms for the same person.

## Missing values

A value that cannot be a valid form of its field, such as a single letter in a tax-ID field, is `AMBIGUOUS`, neither a value nor missing.

Only text whose sole meaning is "no data" is missing: 未提供、未記載、不詳、無、-、N/A and similar. Text with extra content, such as 「未提供（居住國臺灣）」, is a value. 無錫市, 無業 and 無國籍 are real values.

"No data" has six distinct causes, kept apart:

| Cause | Recorded as |
|---|---|
| field absent in the source | `MISSING`, detail `FIELD_ABSENT` |
| field present but blank | `MISSING`, detail `FIELD_BLANK` |
| format not supported | `UNSUPPORTED_ATTACHMENT` finding; not MISSING |
| parsing failed | `UNPARSEABLE` |
| lookup not completed (network/system) | verification `UNAVAILABLE` |
| lookup completed, nothing found | verification `NOT_FOUND`; not proof of a different person |

Only the last two belong to [verification records](review-and-disposition.md#verification-records). A fact missing from the extracted list does not mean the source lacks it.

## Date precision

```text
YEAR | MONTH | DAY | UNKNOWN
```

Never convert a year-only value such as `1991` to `1991-01-01`. Preserve multiple source dates.

Multi-valued and time-varying fields (several birth years, past offices, old and new addresses) keep every value with its time point and source.

## Ages in source text

An age is extracted only when stated explicitly for a named person:

- counted: Arabic or Chinese numerals (四十四歲、廿二歲), bracketed numbers, English forms (aged 44, 44-year-old); 足歲、周歲、週歲 mean 歲;
- not counted: approximations or ranges (七八十歲、20 多歲), age thresholds (未滿 18 歲), statistics (被害人平均年齡 65 歲);
- the age belongs to the person the sentence grammatically refers to, never by default to the Profile subject; 「甲、乙分別為 44 歲、45 歲」 yields one age per person in order;
- the stated age is preserved unchanged; names keep their source spelling.

Comparison rules are in [identity adjudication](identity-adjudication.md#age-stated-in-a-source).

## Country roles

Keep distinct:

```text
citizenship
residence
allegationCountry
documentIssuingCountry
addressCountry
```

Repeatedly seeing the same country in several roles does not create independent nationality evidence.

Nationality comes only from nationality-labelled fields (nationality, citizenship). Allegation and residence countries are not nationality. Only values that map unambiguously to a country or code are normalized; anything else is `AMBIGUOUS`.

## Documents

A comparable document identity key requires:

```text
documentType + issuingCountry/authority + documentNumber
```

Customer number, tax number, Profile ID and identity-document number must never be merged because their strings happen to match. Reference numbers and other non-document labels go to `OTHER_IDENTIFIER` with their original label.

## Taiwan address normalization

Parse when available:

```text
postalCode, city, district, village, neighborhood,
road, section, lane, alley, houseNumber, floor, unit
```

Handle `台/臺`, full-width digits, Chinese numeric forms, duplicated address lines and postal codes embedded in the address.

An address line holding only a postal code is `AMBIGUOUS` (`POSTAL_CODE_ONLY`); identical address lines are kept once with a `DUPLICATED_ADDRESS_LINE` finding.

Pre-2010 administrative names are mapped to current ones (e.g. 台北縣新莊市 → 新北市新莊區) with a versioned table; the raw value is kept and a `HISTORICAL_ADMIN_DIVISION` finding is recorded.

A country-field/address-text inconsistency produces `FIELD_CONFLICT`, preserving both source facts.

## Quality findings

- missing field;
- malformed date;
- unknown code;
- duplicated address line;
- country/address conflict;
- unsupported source-template version;
- ambiguous selected value;
- source Profile with multiple conflicting attributes;
- unsupported attachment;
- unreadable flag;
- unclear legacy-record boundary;
- historical administrative division;
- Profile with several DOB values (`PROFILE_MULTIPLE_DOB`).

Quality findings must remain visible to adjudication and explanations.

## Source text preservation

Pre-filled source text, career history, multiple birth years, relationships and notes are preserved even when not mapped to a field. "No related-party information" is shown only after confirming the source has none, never because parsing failed. Explanations may summarise, but names, compared values and quotations keep the source characters.
