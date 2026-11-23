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

# prep_stage --nonsuspects: other named persons become UNKNOWN rows; a person sharing a form with a suspect, a Witness
# with a labelled event row and a person without a form in the text are left out; the cap keeps Victim/Other first
import prep_stage
prep_stage.row = lambda d, p: {"person": p["id"], "stage": "INDICTED" if p["id"] == "P5" else "UNKNOWN", "no_form": p["id"] == "P6"}
ns_doc = {"doc": "d", "people": doc["people"] + [
    {"id": "P4", "role": "PER-Victim", "party": "Victim", "mentions": ["陳某"]},
    {"id": "P5", "role": "PER-Official", "party": "Official", "mentions": ["張檢察官"]},
    {"id": "P6", "role": "PER-Victim", "party": "Victim", "mentions": ["林一"]},
    {"id": "P7", "role": "PER-Other", "party": "Witness", "mentions": ["趙二"]}],
    "events": doc["events"] + [{"mention": "趙二", "labels": []}]}
ns, left = prep_stage.nonsuspects([ns_doc])
assert [(k, r["person"], r["stage"]) for k, r in ns] == [("Official", "P5", "UNKNOWN"), ("Witness", "P7", "UNKNOWN")]
assert left == {"Witness with events": 1, "shares a form with a suspect": 1, "no form in text": 1, "kept, own events give a stage": 1}
assert [r for k, r in prep_stage.cap([("Official", 1), ("Victim", 2), ("Official", 3), ("Other", 4)], 3) if k != "Official"] == [2, 4]
assert [r for _, r in prep_stage.cap([("Official", 1), ("Victim", 2), ("Other", 3)], 2)] == [2, 3]
o = prep_stage.officials([("Official", 1), ("Witness", 2), ("Official", 3), ("Victim", 4), ("Official", 5)], 1)
assert len(o) == 3 and [r for k, r in o if k != "Official"] == [2, 4]
print("selfcheck OK")
