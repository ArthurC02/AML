# Task: outcome per accused person in court press releases (acceptance labels)

You label Taiwanese court press releases (public government news releases). For each document in your list, read the whole text and write ONE JSON file. Work only from the text. Do not look at any other label files or at other agents' output directories.

Input text: `<RAW>\<doc>.main.txt` (UTF-8, may start with a BOM), where <RAW> is the directory given in your assignment.
Output: `<OUT>\<doc>.json` (UTF-8), where <OUT> is the directory given in your assignment.

## What to record

```json
{"doc": "jy-0000", "kind": "verdict | ruling | other",
 "persons": [{"forms": ["name exactly as written", "other written form of the same person"], "defendant": true, "stage": "CONVICTED"}]}
```

- `kind`: `verdict` if the release reports a judgment on guilt (first instance or appeal); `ruling` if it reports only a detention, bail, or other procedural ruling; `other` otherwise.
- `persons`: every natural person the text NAMES (a real or masked name such as 王○明, 王O明, a foreign name; not 甲女, A1, 被害人, titles alone) who is accused of an offence: the defendants of this ruling, and anyone else the text says committed or is suspected of an offence (co-offenders, people tried or wanted in another case). Do NOT list victims, witnesses, judges, prosecutors, lawyers, or companies.
- `forms`: every distinct way the text writes that person's name (copy exactly, no role word such as 被告). If a person has both a Chinese and a Latin form, list both.
- `defendant`: true if this ruling is about that person as defendant.
- `stage`: the highest stage THE TEXT STATES for that person, in this or any court instance it reports:
  - `CONVICTED`: the court (any instance) sentenced them or found them guilty, including when the release says the appeal is dismissed and recalls that the lower court sentenced them, and including a person convicted on some counts and acquitted on others.
  - `ACQUITTED`: found not guilty on everything the text reports about them.
  - `DISMISSED`: not indicted (不起訴處分).
  - `INDICTED`: the text says they were indicted / are a defendant, with no judgment on guilt stated (e.g. a detention ruling, or an appeal dismissed where the text does not say what the lower court decided).
  - `WANTED`: the text says they are wanted (通緝) and states no judicial stage.
  - `ALLEGED`: only suspected / under investigation (偵辦, 另案偵辦) with nothing more.
  - `UNKNOWN`: named as taking part in an offence, but the text states none of the above about them.
  A stage stated only about another case of the person (另案, 前案) counts too (e.g. 另案判處 = CONVICTED), since the question is what the text states.
- If a document gives defendants in a table (cells on separate lines), read the table carefully: each row's person gets that row's outcome.

## Rules of conduct
- Write the files; in your final message report ONLY counts: number of documents written, number of persons per stage. Never put a person's name or any quote from the texts in your message.
- Do not create any other files. Do not edit the input files.
