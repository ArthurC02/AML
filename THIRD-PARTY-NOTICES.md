# Third-party notices

`Aml.Core` embeds three public reference tables as resources (`src/Aml.Core/Normalization/Data/`). This file and the `licenses/` folder go with every copy of the source and every build that contains `Aml.Core`.

## 外交部領事事務局 護照外文姓名拼音對照表

- **File:** `romanization.txt`.
- **Attribution:** 外交部領事事務局 2026 護照外文姓名拼音對照表(發布 2026-05-28,更新 2026-05-29)。此開放資料依政府資料開放授權條款 (Open Government Data License) 進行公眾釋出，使用者於遵守本條款各項規定之前提下，得利用之。政府資料開放授權條款:https://data.gov.tw/license
- **Source:** https://www.boca.gov.tw/cp-2-4226-c0eff-1.html. The bureau's open data announcement (https://www.boca.gov.tw/cp-71-3817-6ceb8-1.html) releases its website content under the Open Government Data License, version 1.0 (OGDL-Taiwan-1.0).
- **Changes:**
  - The table is reformatted into one row per syllable with three columns: Hanyu Pinyin, Wade-Giles in passport form without apostrophes, and Tongyong.
  - Hanyu ü is written as V.
  - Where the table lists alternatives, they are kept.
- **Not an endorsement:** the Bureau of Consular Affairs has not reviewed or endorsed this software.

## OpenCC

- **File:** `hans-hant.txt`.
- **Source:** Open Chinese Convert (OpenCC), `data/dictionary/STCharacters.txt`, https://github.com/BYVoid/OpenCC.
- **License:** Apache License 2.0. The full text is in [licenses/Apache-2.0.txt](licenses/Apache-2.0.txt).
- **Changes:**
  - Only the one-to-one simplified → traditional entries are kept; one-to-many entries are dropped.
  - The file format is changed.

## Unicode Unihan

- **File:** `hanzi-pinyin.txt`.
- **Source:** the Unicode Character Database, Unihan 18.0.0 (`Unihan_Readings.txt`: `kMandarin`, `kTGHZ2013`, `kXHC1983`), https://www.unicode.org/Public/UCD/latest/ucd/Unihan.zip.
- **License:** Unicode License v3, Copyright © 1991-2026 Unicode, Inc. The full text, including the permission notice, is in [licenses/Unicode-3.0.txt](licenses/Unicode-3.0.txt).
- **Changes:**
  - Readings are toneless Hanyu Pinyin, with ü written as V.
  - The file is grouped by reading.
  - Only the CJK Unified Ideographs block and Extensions A and B are kept.
