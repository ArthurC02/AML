# Golden Case samples

Input files and expected results for the [Golden Cases](../../docs/plan/golden-cases.md). Read-only test data.

| Folder | Case | Source |
|---|---|---|
| [GC-01](GC-01/) | same name, impossible age at an attributed role | synthetic |
| [GC-02](GC-02/) | fraud HIT, identity insufficient, human NO_MATCH | synthetic |
| [GC-03](GC-03/) | Vietnamese name with diacritics, official wanted list, human MATCH | synthetic |
| [GC-11](GC-11/) | Taiwanese customer on adverse media, year-only DOB, human MATCH | real case, anonymized |
| [GC-12](GC-12/) | related party with a common name, no comparable identifiers, unresolved | real case, anonymized |

Synthetic samples use fictional data built from a validated scenario, in the same export layout. Their `validationStatus` is `SYNTHETIC`; each is replaced by the anonymized real case when supplied, and its expected results must then still hold. Fields the real export has not shown yet (`職位`, `關鍵字`, `名單資訊`) are listed under `assumptions`.

## Anonymization

Every sample is anonymized before it enters the repository:

- names, national ID, customer number, address numbers, DOB day/month, account-opening date, branch code, Profile ID, bank, reporter, document IDs and URLs are replaced with fictional values;
- the news body is rewritten and truncated; no article text is reproduced;
- structural features that drive the rules are kept: script and character variants (鍾/鐘, simplified forms), romanization variants, old administrative names, full-width digits, multi-valued fields, missing-value markers, legacy decision columns.

`A123456789` is a widely used dummy Taiwan ID; `B123456780` is a dummy with a valid checksum, kept because GC-12 tests an ID-shaped reference number. Synthetic IDs that are not `A123456789` fail the checksum on purpose (`A223456788`) or use an obviously fake pattern (`AC12345678`). Phone numbers and e-mail addresses use `0912345678` and `example.com`. Real identifiers never appear in samples, tests, logs or commit history.

## Files per case

The file names and layout follow the supplied export (`.txt`). Whether these files or the original HTML are the production input is an [open question](../../docs/plan/open-questions.md#data-and-source).

- `名單資料_<profileId>.txt`: source Profile
- `客戶-背景資料.txt`, `客戶-聯絡資料.txt`: customer
- `客戶-關聯人背景資料.txt`: related party
- `案件資訊.txt`: case and candidate rows, including legacy decisions
- `新聞資訊_<profileId>.txt`: attached news
- `expected.yaml`: expected extraction, decisions and regression assertions
