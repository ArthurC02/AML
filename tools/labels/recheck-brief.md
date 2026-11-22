# Task: recheck the outcome of listed persons in court press releases

You recheck acceptance labels. Read `stage-brief.md` (in this folder) first: its definitions of `stage` and its rules of conduct apply unchanged. Work only from the texts. Do not look at any label files, at the other pass's output, or at any model output.

Input: `<DIR>\persons.tsv`, one person a line: document id, person id, the person's written forms joined by `|`. Texts: `<RAW>\<doc>.main.txt` (UTF-8, may start with a BOM). <DIR>, <RAW> and <OUT> are given in your assignment.

Output: `<OUT>` (a single TSV file, UTF-8): one line per line of `persons.tsv`, in its order: document id, person id, stage (one of the seven codes of `stage-brief.md`), tab-separated, nothing else.

- Read the whole text of each document before judging its persons.
- The stage is the highest stage the text states for that person, as defined in `stage-brief.md`.
- In your final message report ONLY counts: lines written, persons per stage. Never a name or a quote.
