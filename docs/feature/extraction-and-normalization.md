# Extraction, Normalization and Data Quality

> Module: Extraction and Data Quality.

## Extraction principle

Extractors report what the source states. They do not decide identity or screening outcomes.

Source acquisition sits behind replaceable adapters ([ADR-0012](../adr/0012-sources-and-web-information.md)). The PoC reads the bank's `.txt` exports and HTML pages of the same files with `Infra/Txt/TxtCaseReader` and `Infra/Html/HtmlExport` (AngleSharp); normalization rules live in Core (`Aml.Core.Normalization`: `NameComparison`, `Romanization`, `SourceValues`, `TaiwanAddresses`, `ChineseNumerals`).

## Sources and refresh

Steps 1 to 3 of the [operating architecture](system-overview.md#operating-architecture) process list and public data only, never customer data:

| Source | Examples | Format |
|---|---|---|
| official lists | Sanction, PEP, RCA | PDF, HTML, API |
| public news and court rulings | adverse media, indictments, judgments | PDF, HTML |
| other public sources | registries, government sites | HTML, API |

- Each source gets its own adapter. The PoC has the `.txt` export reader and the HTML conversion below; PDF and API adapters follow when a source needs them.
- PDF text is read only from the text layer; scanned PDFs need OCR, which is out of scope ([system overview](system-overview.md#excluded-for-phase-1)).
- Structured parsing extracts person, event, role and legal status, with evidence locations.
- List data refreshes on a schedule or on an event (a source update). Each refresh keeps the previous snapshot; a changed Profile triggers re-analysis of the Cases that used it, producing a new revision ([case management](case-management.md)). Schedule and triggers are [open](../plan/open-questions.md#technology-governance).

## Encoding

Input files are read as raw bytes, so the reader decides the encoding:

- an export `.txt` is decoded as UTF-16 when a UTF-16 byte order mark says so, else as strict UTF-8 (a BOM is dropped); bytes that are not valid UTF-8 are decoded as CP950 (Big5) and an `ENCODING_FALLBACK:<file>` finding is recorded;
- an HTML page is decoded by the same rules before it is parsed; its `<meta charset>` is not followed. The upload keeps the bytes but a stored copy, or a file sent as JSON, is text re-encoded as UTF-8, and both must read the same: a Big5 page saying `<meta charset=big5>` would otherwise be read as Big5 a second time and garbled;
- every input file's SHA-256 is taken on the bytes as uploaded, before any decoding or conversion, and kept as `CaseInput.SourceHashes`.

## HTML processing

An HTML source is processed in this order:

1. preserve raw source and content hash;
2. identify page type and source template version;
3. parse DOM, tables, rowspan/colspan and nested structures;
4. read actual selected values for select/radio/checkbox controls;
5. map source labels to canonical fields;
6. normalize by field type;
7. emit data-quality findings;
8. return typed snapshots and evidence locations.

Do not flatten the entire HTML to text before interpreting table and control relationships. Checkbox states, dropdown selections, input values, links and merged headers are all read.

In the PoC, `Infra/Html/HtmlExport` covers steps 3 and 4 by converting each page (`.html`/`.htm` in any letter case, or a `.txt` holding page source, under the export file names) into the export layout in memory, which the `.txt` reader then maps and normalizes (steps 5 to 8):

- a cell keeps its text and its control values in document order: a checkbox reads 是 or 否, a checked radio its label (else its value), a select its selected options, an input or textarea its value; the label text of a checkbox or radio is not repeated;
- `<br>` and block elements (`p`, `div`, `li`, table cells …) break lines;
- colspan repeats the cell across, rowspan carries it down;
- a page is refused as `MALFORMED` when it does not parse within 5 s (deep nesting parses in quadratic time; a 5 MB table page parses in about 1.3 s on one core), nests elements deeper than 256 levels (the read recurses per level, and a stack overflow cannot be caught), or its tables spread into more than 200,000 cells or 20 million characters across the page. At most one page per core is parsed at a time, and the wait is not counted, so pages parsed side by side (a list import, concurrent uploads) do not overrun the budget by sharing the cores. Reading the parsed page costs linear time: which label names a control and which elements hold a table or heading are worked out once per page;
- hidden content is not read: the `hidden` attribute, an inline `display:none` or `visibility:hidden` style, `script`, `style`, `template`, `noscript`, buttons and hidden inputs; hiding by stylesheet class is not detected;
- a blank field is written blank, never as `-`, so it is recorded as `FIELD_BLANK`, not `PLACEHOLDER_DASH` ([missing values](#missing-values)).

No HTML sample exists yet, so the pages are assumed to use the export's own labels; a label map per template version is added when samples arrive.

## Input inventory

- Source case data is read-only.
- Every attachment is inventoried. File type is decided by content, not extension: a `.txt` may be page source, an extracted table or hand-copied text.
- File extensions are matched in any letter case (`案件資訊.TXT`, `名單資料_1.HTML`). Two input files that read as the same export file (`名單資料_1.html` and `名單資料_1.txt`) are an input error; the upload is rejected.
- An attachment the extractor cannot read (image, Word, unknown format, or a `.txt` holding markup that is not an export page) produces an `UNSUPPORTED_ATTACHMENT:<file>` finding. It is disclosed as unread, never treated as "no data"; opening an image does not mean its text was read.
- An archive over the file-count or extracted-size limit stops the whole batch; it is never truncated or partially accepted.

Customer, related party and Profile data are kept per person; one person's values are never attributed to another. Related parties arrive in their own file (e.g. `客戶-關聯人背景資料.txt`, `key ：` lines) with their role (e.g. 帳戶持有人的法定代理人) taken from the case table. The account holder's files are still read for a related-party hit and stored as case context, linked to the account holder, not to the related party.

## File layouts

- Customer files (`客戶-背景資料`, `客戶-聯絡資料`): a label line followed by its value line. A value line that is itself one of the known labels means the value was blank (`FIELD_BLANK`).
- Related-party file: `key ：` or `key :` lines (full- or half-width colon), the value on the following line or lines, joined with a space when it spans several; a key followed directly by the next key has an empty value.
- Profile file (`名單資料_<id>`): `key:\tvalue` lines, or `key:` followed by a tab-separated table whose numbered rows are read. Row numbers may be full-width. A row with fewer cells than its table needs is left out with an `UNPARSEABLE:<table>` finding (e.g. `UNPARSEABLE:職位`); the rest of the Profile is still read.
- News file (`新聞資訊_<id>`): the publication date is a line such as `2026 年 9 月 6 日` or `2026年9月6日` (full-width digits too), read by the date parser, so an impossible date such as 13月 or 2月30日 leaves the date out; the publisher the line after it, the document ID the `文件 <id>` line.

## Candidate table

The 疑似符合的名單資料 section of 案件資訊 lists the candidate rows. A case table without this section is an input error.

- Columns are found by header label, not by position. The required columns are 編號, 姓名/名稱, 名單資料編號, 理由 and `Is RCA?`; a missing one is an input error. A full-width 名單資料編號 is read as half-width digits, so it still finds its Profile file.
- The header may span two rows. When the second row holds sub-labels only (the `.txt` export), each parent label with sub-labels is expanded in order: 是否符合名單？ → 全不符合, 全符合 and Name Property → PEP, 黑名單, 負面新聞. When the second row is as wide as the first (an HTML header with rowspan carried down), it is used as is. This expansion is an unconfirmed assumption: the samples show the header but no flag values ([open questions](../plan/open-questions.md#data-and-source)).
- Sub-labels other than the expected ones produce `UNPARSEABLE:candidate_header`, and every row's PEP, 黑名單 and 負面新聞 flags are `UNPARSEABLE`.
- Blank lines are skipped. A row with too few cells, or whose 編號 is not a number (full-width digits are read), is left out with `UNPARSEABLE:candidate_row:<line>` (1-based line in 案件資訊). A row with more cells than the header is kept with the same finding, and its three flags and `Is RCA?` are `UNPARSEABLE` because they cannot be placed.
- `Is RCA?` is kept as a flag value (YES, NO, MISSING or UNPARSEABLE), so a NO is told apart from a blank; it is not one of the three list flags.

## Source flags

PEP, blacklist and adverse-news flags and `Is RCA?` are read per candidate row:

| Source text | Value |
|---|---|
| 是、✓、✔、☑、V、Y、Yes、TRUE、1、checked、on, or a ticked checkbox | `YES` |
| 否、不是、X、✗、×、☐、N、No、FALSE、0、unchecked、off | `NO` |
| blank, -, –, —, －, N/A, NA | `MISSING` |
| anything else, or a flag the [candidate table](#candidate-table) cannot place | `UNPARSEABLE`: re-read the source; if still unreadable the case cannot be published |

Words are matched in any letter case. `NO` is an explicit value, not unknown.

## Legacy review records

Earlier manual results in the input, such as "是否符合名單", "理由" and the 客戶檢核處理紀錄 section with its table, are extracted as ImportedDecision ([case management](case-management.md#decision-records)) and excluded from every evidence path. The exclusion ends where that table ends; if the heading has no table, or text after the table looks like a continuation, a `LEGACY_RECORD_BOUNDARY_UNCLEAR` finding asks the reviewer to confirm. Source flags are not decisions and stay usable.

## Name normalization

Preserve original name, language/script when known, components, source alias type and generated comparison variants.

Processing includes:

- Unicode NFKC;
- case, full-width/half-width and whitespace normalization;
- controlled Traditional/Simplified Chinese comparison variants;
- Vietnamese diacritic-preserving and diacritic-free forms;
- controlled given/family-name order variants;
- romanization variants of Chinese names for retrieval and comparison: Hanyu Pinyin, Wade-Giles, Tongyong, Taiwanese passport spellings (e.g. JENN, JEN, REN for 仁; JONG, JHONG, CHUNG, ZHONG for 鍾/鐘); every syllable must share a Pinyin reading from a controlled table (`Romanization`), and a match is PARTIAL with `ROMANIZATION_VARIANT`, never EXACT;
- variant characters with the same reading (鍾/鐘) as comparison variants;
- hyphen and punctuation variants.

In the PoC (`NameComparison`, `Romanization`):

- The comparison key is built per Unicode code point, so characters outside the Basic Multilingual Plane count as one character. After NFKC, diacritics and case are folded and spaces and punctuation removed; letters, digits and private-use (造字) characters are kept. A character the source could not render (`?`, `*`, U+FFFD) is kept as an unknown character, and a key holding one is never EXACT.
- Name words come from NFKC and a split on any whitespace (including U+3000) and the middle dots · ‧ ・ ･; the order variants move the surname to either end.
- Simplified/Traditional folding uses the one-to-one entries of the OpenCC character table (`Data/hans-hant.txt`; one-to-many entries are left out) plus a controlled list, which wins. Characters that are distinct surnames (涂/塗, 云/雲, 余/餘, 后/後) are never folded.
- Romanization uses the Taiwan consular romanization table (`Data/romanization.txt`: Hanyu Pinyin, Wade-Giles passport form, Tongyong) plus a controlled list of passport spellings (LEE, JENN, JONG, SOONG); Chinese characters are read through the Unihan Mandarin readings (`Data/hanzi-pinyin.txt`) to give Pinyin retrieval keys across scripts. Each data file carries its attribution, source and license in its header lines ([third-party data](../plan/implementation-baseline.md#third-party-data)).

Source aliases and generated search variants remain distinguishable.

Names keep their source characters and script:

- a romanized-only name is never back-converted to Chinese;
- pinyin or romanization similarity is a search variant, never a new source name;
- names recorded in separate fields are not joined into a full name the source never states;
- a Chinese–English name relationship is compared only when the source gives both forms for the same person.

## Missing values

A value that cannot be a valid form of its field, such as a single letter in a tax-ID field, is `AMBIGUOUS`, neither a value nor missing.

Only text whose sole meaning is "no data" is missing (`SourceValues.IsPlaceholder`, whole value only): 未提供、未記載、不詳、無、N/A、NA、n.a.、none、null and the dashes -、–、—、－. Text with extra content, such as 「未提供（居住國臺灣）」, is a value. 無錫市, 無業 and 無國籍 are real values.

"No data" has these distinct causes, kept apart:

| Cause | Recorded as |
|---|---|
| field absent in the source | `MISSING`, finding `FIELD_ABSENT:<field>` |
| field present but blank | `MISSING`, finding `FIELD_BLANK:<field>` |
| field holding a dash (`-`, `–`, `—` or full-width `－`) | `MISSING`, finding `PLACEHOLDER_DASH:<field>` |
| field holding placeholder text (未提供, 不詳 …) | `MISSING`, finding `PLACEHOLDER_TEXT:<field>` |
| format not supported | `UNSUPPORTED_ATTACHMENT` finding; not MISSING |
| parsing failed | `UNPARSEABLE` |
| lookup not completed (network/system) | verification `UNAVAILABLE` |
| lookup completed, nothing found | verification `NOT_FOUND`; not proof of a different person |

The first four findings are recorded for the subject's identity fields (names, gender, DOB, nationality, document number) and shown in the explanation. Only the last two causes belong to [verification records](review-and-disposition.md#verification-records). A fact missing from the extracted list does not mean the source lacks it.

## Date precision

```text
YEAR | MONTH | DAY | UNKNOWN
```

Never convert a year-only value such as `1991` to `1991-01-01`. Preserve multiple source dates. A date that cannot be parsed is `UNPARSEABLE:<field>` (customer and related-party 出生日期, Profile Date of Birth and Date of Death), never a guessed value; an unparseable role start or end date is left empty with `UNPARSEABLE:職位`, except that an end date in a future year is an open term and records nothing.

Dates are read by `PartialDate.Parse`:

- accepted forms: `1973`, `1972/4`, `1990-07`, `1992/4/10`, and `民國62年6月12日` (an explicit 民國 year is converted by adding 1911); full-width digits and separators (`１９７３／６／１２`) read as half-width;
- a trailing time (`2026/9/10 下午 01:00:15`, `…T10:00`) is dropped;
- a month or day of `00` lowers the precision (`1990/00/00` is YEAR, `1990/07/00` is MONTH); a day with a `00` month is unparseable;
- the year must lie between 1800 and the current year, the month between 1 and 12, and the day within its month;
- a bare two- or three-digit year, such as a ROC year without 民國 (`62/6/12`), is never guessed and is `UNPARSEABLE`;
- a year after the current one is rejected, so a future date of birth is `UNPARSEABLE` and a role whose end date lies in the future reads as open.

Multi-valued and time-varying fields (several birth years, past offices, old and new addresses) keep every value with its time point and source.

## Ages in source text

An age is extracted only when stated explicitly for a named person:

- counted: Arabic or Chinese numerals (四十四歲、廿二歲, `ChineseNumerals.Parse`), bracketed numbers (陳志明（44歲）, `(44)`), English forms next to a romanized name (Chen Chih-Ming, 44; aged 44; 44-year-old); 足歲、周歲、週歲 mean 歲;
- not counted: approximations or ranges (七八十歲、20 多歲), age thresholds (未滿 18 歲), statistics (被害人平均年齡 65 歲);
- the age belongs to the person the sentence grammatically refers to, never by default to the Profile subject; 「甲、乙分別為 44 歲、45 歲」 yields one age per person in order, and nothing when the numbers of names and ages differ;
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

In the PoC, `SourceValues.Country` maps a value to an ISO 3166-1 alpha-2 code after NFKC (full-width letters and brackets) and whitespace collapsing. It accepts the alpha-2 or alpha-3 code (upper case only, so the words "No" or "in" are not codes), the ISO English short name, a controlled list of common English and Chinese names, and the Taiwan aliases (台灣, 臺灣, 中華民國, ROC, Republic of China, Chinese Taipei …). Names that fit more than one country, such as 韓國 or Korea, are not mapped. A value that does not map produces `AMBIGUOUS:<field>` (e.g. `AMBIGUOUS:國籍 1`, `AMBIGUOUS:Citizenship`) and is left out of comparison; placeholders stay missing. A country code is read before the placeholder check, so `NA` in a country field is Namibia, not "not available".

## Documents

A comparable document identity key requires:

```text
documentType + issuingCountry/authority + documentNumber
```

Customer number, tax number, Profile ID and identity-document number must never be merged because their strings happen to match. Reference numbers and other non-document labels go to `OTHER_IDENTIFIER` with their original label; on the Profile side they come from the `其他識別碼` table (layout assumed: 編號, 類型, 號碼). Profile documents come from a `身分證明` table (layout assumed: 編號, 類型, 號碼, 國家); no sample carries either table yet ([open questions](../plan/open-questions.md#identity-policy)).

## Taiwan address normalization

Parse when available:

```text
postalCode, city, district, village, neighborhood,
road, section, lane, alley, houseNumber, floor, unit
```

Handle `台/臺`, full-width digits, Chinese numerals before 段/巷/弄/號/樓/鄰 (turned into digits), duplicated address lines and postal codes embedded in the address.

An address line holding only a postal code is `AMBIGUOUS` (`POSTAL_CODE_ONLY`); identical address lines are kept once with a `DUPLICATED_ADDRESS_LINE` finding.

Administrative names from before the 2010 and 2014 county mergers are mapped to current ones (e.g. 台北縣新莊市 → 新北市新莊區, and the townships whose own name holds 市, 鄉 or 鎮: 桃園縣平鎮市 → 桃園市平鎮區, 臺南縣新市鄉 → 臺南市新市區, 臺南縣左鎮鄉 → 臺南市左鎮區) with the versioned table `tw-admin-2014-12-25`; the raw value is kept and a `HISTORICAL_ADMIN_DIVISION` finding is recorded.

An address's country comes from its own block's country field (永久地址(國家), 通訊位址(國家)), else from a Taiwan city or county in the text; otherwise it is unknown. A country-field/address-text inconsistency produces `FIELD_CONFLICT:<address role>`, preserving both source facts. It is a data-quality finding, never identity evidence.

## Quality findings

- missing field;
- malformed date;
- unknown code, or a country that maps to no country unambiguously (`AMBIGUOUS:<field>`);
- file not in UTF-8, read as CP950 (`ENCODING_FALLBACK:<file>`);
- unreadable candidate row or header (`UNPARSEABLE:candidate_row:<line>`, `UNPARSEABLE:candidate_header`);
- Profile table row too short to read (`UNPARSEABLE:<table>`);
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
