"""Self-check of textutil and of gold.stage / person_stage on made-up inputs. Needs no data, model or GPU.
usage: python -I selfcheck.py   -> "selfcheck OK" or an AssertionError"""
import os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import gold, textutil

# textutil.normalise: NFKC per character, one to one (a character whose NFKC form is longer stays as it is)
assert textutil.normalise("ＡＢ１２！？；") == "AB12!?;"
assert textutil.normalise("㍿") == "㍿" and len(textutil.normalise("陳志明㍿")) == 4
# textutil.occurrences: every start, overlaps included; an empty needle yields nothing
assert list(textutil.occurrences("陳志明與陳志明", "陳志明")) == [(0, 3), (4, 7)]
assert list(textutil.occurrences("aaa", "aa")) == [(0, 2), (1, 3)]
assert list(textutil.occurrences("abc", "")) == [] and list(textutil.occurrences("abc", "x")) == []
# textutil.event_label: type, with "-certainty" unless Stated
assert textutil.event_label({"type": "Conviction", "certainty": "Stated"}) == "Conviction"
assert textutil.event_label({"type": "Conviction", "certainty": "Negated"}) == "Conviction-Negated"

# gold.stage: dashed labels and "prior" ignored, the highest step wins, any other label is ALLEGED
assert gold.stage([]) == "UNKNOWN" and gold.stage(["prior"]) == "UNKNOWN" and gold.stage(["Indictment-Requested"]) == "UNKNOWN"
assert gold.stage(["Search"]) == "ALLEGED"
assert gold.stage(["WantedNotice", "Search"]) == "WANTED"
assert gold.stage(["Indictment", "Conviction-Negated"]) == "INDICTED"
assert gold.stage(["Indictment", "NonIndictment"]) == "DISMISSED"
assert gold.stage(["Acquittal", "Indictment"]) == "ACQUITTED"
assert gold.stage(["Acquittal", "Conviction"]) == "CONVICTED"

# gold.suspects / person_stage: rows by exact form; a row carrying "prior" is left out whole
doc = {"people": [
    {"id": "P1", "role": "PER-Suspect", "party": "Suspect", "mentions": ["陳志明", "陳某"]},
    {"id": "P2", "role": "PER-Suspect", "party": "Suspect", "mentions": ["李小四"]},
    {"id": "P3", "role": "PER-Other", "party": "Witness", "mentions": ["王大同"]},
    {"id": "O1", "role": "ORG-Suspect", "party": "Suspect", "mentions": ["某公司"]}],
    "events": [
    {"mention": "陳某", "labels": ["Indictment"]},
    {"mention": "陳志明", "labels": ["Conviction", "prior"]},
    {"mention": "李小四", "labels": ["Conviction-Negated", "Search"]},
    {"mention": "王大同", "labels": ["Conviction"]},
    {"mention": "某公司", "labels": ["Conviction"]}]}
assert [p["id"] for p in gold.suspects(doc)] == ["P1", "P2"]
assert {p["id"]: gold.person_stage(doc, p) for p in gold.suspects(doc)} == {"P1": "INDICTED", "P2": "ALLEGED"}
assert gold.person_stage({"people": [], "events": []}, {"mentions": ["陳志明"]}) == "UNKNOWN"
print("selfcheck OK")
