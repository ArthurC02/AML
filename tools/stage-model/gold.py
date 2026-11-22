"""Reference legal stage of an annotated suspect: a port of the aml-eval-r4 Program.cs `stage` mode (Gold + `mine`).

A suspect is a people entry with role PER-* and party Suspect. Its events are the document's event rows whose
`mention` is one of its forms (exact string; persons sharing a form share rows). Rows carrying "prior" are left
out whole; labels with a '-' (negated, requested, ...) are ignored; the highest step wins.

usage: gold.py verify         -> agreement with the evaluator on the development *-stage.jsonl files. Counts only.
       gold.py verify court   -> the same on measurements/court/accept, accept3, accept4.jsonl.
The held-back sets (v11, accept5) are never verified. verify is optional: it needs the .NET evaluator aml-eval-r4
built in AML_EVAL (bin/Release/net10.0/aml-eval.dll, plus its sg-<set>-r17.tsv files for the report sets). stage()
and person_stage() need nothing.
"""
import json, os, subprocess, sys, tempfile
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # python -I leaves the script folder off the path
import paths

STAGES = ["UNKNOWN", "ALLEGED", "WANTED", "INDICTED", "DISMISSED", "CONVICTED", "ACQUITTED"]
_ORDER = [("Conviction", "CONVICTED"), ("Acquittal", "ACQUITTED"), ("NonIndictment", "DISMISSED"),
          ("Indictment", "INDICTED"), ("WantedNotice", "WANTED")]


def stage(labels):
    l = {x for x in labels if "-" not in x and x != "prior"}
    return next((s for k, s in _ORDER if k in l), "ALLEGED" if l else "UNKNOWN")


def suspects(doc):
    return [p for p in doc["people"] if p["role"].startswith("PER-") and p["party"] == "Suspect"]


def person_stage(doc, p):
    ms = set(p["mentions"])
    return stage(x for e in doc["events"] if e["mention"] in ms and "prior" not in e["labels"] for x in e["labels"])


REPORT = paths.REPORT
EVAL = paths.EVAL
SETS = ["dev", "dev2", "v9", "v11"]


COURT = paths.COURT
COURT_SETS = ["accept", "accept3", "accept4", "accept5"]
HELD = {"v11", "accept5"}  # held-back sets: verify skips them


def verify(court=False):
    """Two references: (1) the evaluator run now on a copy of each file whose text is a neutral sentence, so the rules
    say Unknown for everyone and stageout lists every suspect whose reference is not Unknown; (2) sg-<set>-r17.tsv
    (the rows where the rules and the reference differed in the r17 run): its gold column (report sets only).
    court=True: the court press-release sets (measurements/court/<set>.jsonl), reference (1) only.
    The copies (they hold person mentions) live in a temporary folder removed on return."""
    with tempfile.TemporaryDirectory() as tmp:
        for s in [s for s in (COURT_SETS if court else SETS) if s not in HELD]:
            docs = [json.loads(l) for l in open(COURT + s + ".jsonl" if court else REPORT + s + "-stage.jsonl", encoding="utf-8")]
            mine = {(d["doc"], p["id"]): person_stage(d, p) for d in docs for p in suspects(d)}
            n_rows = sum(len(suspects(d)) for d in docs)
            blank, out = os.path.join(tmp, s + ".jsonl"), os.path.join(tmp, s + ".tsv")
            with open(blank, "w", encoding="utf-8") as w:
                for d in docs: w.write(json.dumps({**d, "text": "本案。"}, ensure_ascii=False) + "\n")
            con = subprocess.run(["dotnet", EVAL + "bin/Release/net10.0/aml-eval.dll", "none", "docs=" + blank, "stage", "stageout=" + out],
                                 capture_output=True, text=True, encoding="utf-8").stdout
            rule_not_unknown = sum(int(l.split()[-1]) for l in con.splitlines() if l.strip().startswith("gold ") and l.split()[3] != "Unknown")
            ev = {}
            with open(out, encoding="utf-8") as r:
                for l in r:
                    f = l.rstrip("\n").split("\t"); ev[(f[0], f[1])] = f[2].upper()
            ref = {k: ev.get(k, "UNKNOWN") for k in mine}
            a1 = sum(ref[k] == v for k, v in mine.items())
            if court:
                print(f"{s}: documents {len(docs)} suspects {len(mine)} (rows {n_rows}) | evaluator-now agrees {a1}/{len(mine)} (rule not Unknown on blank text: "
                      f"{rule_not_unknown}, listed {len(ev)}, listed but unknown to port {len(set(ev) - set(mine))})")
                continue
            sg = {}
            for l in open(EVAL + "sg-%s-r17.tsv" % s, encoding="utf-8"):
                f = l.rstrip("\n").split("\t"); sg[(f[0], f[1])] = f[2].upper()
            a2 = sum(mine.get(k) == v for k, v in sg.items())
            print(f"{s}: suspects {len(mine)} (rows {n_rows}) | evaluator-now agrees {a1}/{len(mine)} (rule not Unknown on blank text: {rule_not_unknown}, "
                  f"listed {len(ev)}, listed but unknown to port {len(set(ev) - set(mine))}) | sg-r17 gold agrees {a2}/{len(sg)}")


if __name__ == "__main__":
    assert stage(["Indictment", "Conviction-Negated"]) == "INDICTED" and stage(["prior"]) == "UNKNOWN" and stage(["Search"]) == "ALLEGED"
    if sys.argv[1:] == ["verify"]: verify()
    if sys.argv[1:] == ["verify", "court"]: verify(court=True)
