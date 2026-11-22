"""Which stage rules the unanimous vote needs: each rule unit of src/Aml.Core/Screening/Stage switched off in a temporary
copy of this checkout, built, and measured with tools/Aml.StageEval (SourceConditions.Determine, the ONNX classifier,
replayed language model answers). Prints per unit and development set the DECIDED / RIGHT deltas against the unchanged
copy and a verdict per unit: "precision falls" (the unanimous precision RIGHT / DECIDED falls on a set), "no effect"
(no set's DECIDED, RIGHT or rules count moves) or "may go". Held-back sets are never run. Counts only.

A unit is switched off by a source patch, never by a hook in src/:
  - every entry `new(StageBasis.X, ...)` of a decision list (RulingReading.Readers, LegalStageReading.Transitions) is
    found in the code and removed; the unit is X;
  - EXTRA below: the units outside a list, each an exact text that must occur once (else the unit is reported as not
    applying, and EXTRA is to be brought up to date with the code).

usage: prune.py <llm answers report> <llm answers court> [only=X,Y] [onnx=<dir>] [sets=<dir>]
       (the two answer folders and the options are Aml.StageEval's, save out=, which prune.py sets itself; the copy
       and its outputs are removed at the end)
"""
import os, re, shutil, subprocess, sys, tempfile

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
STAGE = "src/Aml.Core/Screening/Stage/"
DEV = ["dev", "dev2", "v9", "accept", "accept3", "accept4"]
ENTRY = re.compile(r"new\(StageBasis\.(\w+), [\w.]+\),?\s*")
EXTRA = {
    # ALLEGED from 偵辦 / 調查 when no stage was read (ReportRules.Closing)
    "UnderInvestigation": ("ReportRules.cs", "return stage == LegalStage.Unknown && underInvestigation ?",
                           "return stage == LegalStage.Unknown && underInvestigation && false ?"),
    # a judgment's non-defendant gets a stage only from the clause naming them (ReportRules.Bystander)
    "Bystander": ("ReportRules.cs", "internal static bool Bystander(RulingResult ruling) => ruling.Judgment && !ruling.Defendant;",
                  "internal static bool Bystander(RulingResult ruling) => false;"),
    # a sentence not naming the person may tell their dismissal or indictment (ReportForm.Scope)
    "Subjectless": ("ReportForm.cs", "return new(sentence, own, own || !bystander);", "return new(sentence, own, own);"),
}


def units(copy):
    """{unit: (file, old, new)} for every list entry found and every EXTRA unit."""
    found = {}
    for f in sorted(os.listdir(copy + STAGE)):
        text = open(copy + STAGE + f, encoding="utf-8", newline="").read()
        for m in ENTRY.finditer(text):
            found[m.group(1)] = (f, m.group(0), "")
    return {**found, **EXTRA}


def apply(copy, f, old, new):
    p = copy + STAGE + f
    text = open(p, encoding="utf-8", newline="").read()
    if text.count(old) != 1: return None
    open(p, "w", encoding="utf-8", newline="").write(text.replace(old, new))
    return text


def measure(copy, args):
    """{set: (n, RULES, DECIDED, RIGHT)} of the copy as it is, or the reason it failed."""
    proj = copy + "tools/Aml.StageEval/Aml.StageEval.csproj"
    # The copy is thrown away: a patch may leave an unused parameter, which is no reason to stop.
    b = subprocess.run(["dotnet", "build", proj, "-c", "Release", "-nologo", "-v", "q", "-p:TreatWarningsAsErrors=false"],
                       capture_output=True, text=True, encoding="utf-8", errors="replace")
    if b.returncode != 0:  # compiler messages hold code, never data
        return "build failed: " + "; ".join(sorted({l.strip() for l in b.stdout.splitlines() if " error " in l})[:3])
    r = subprocess.run(["dotnet", "run", "--no-build", "-c", "Release", "--project", proj, "--", *args, "out=" + copy + "eval-out"],
                       capture_output=True, text=True, encoding="utf-8", errors="replace")
    if r.returncode != 0: return "evaluator failed (exit %d)" % r.returncode
    res = {}
    for l in r.stdout.splitlines():
        f = l.split()
        if len(f) == 11 and f[0] in DEV and all(x.isdigit() for x in f[1:]):
            res[f[0]] = (int(f[1]), int(f[2]), int(f[9]), int(f[10]))
    return res if set(res) == set(DEV) else "evaluator printed no table"


def falls(base, now):
    """Unanimous precision lower than the baseline's (exact fractions; no decision at all is no precision lost)."""
    (_, _, d0, r0), (_, _, d1, r1) = base, now
    return d1 > 0 and r1 * d0 < r0 * d1


def verdict(base, now):
    """'precision falls' on any set; 'no effect' when no set's DECIDED, RIGHT or rules count moves; else 'may go'."""
    if any(falls(base[s], now[s]) for s in DEV): return "precision falls"
    return "no effect" if all(now[s][1:] == base[s][1:] for s in DEV) else "may go"


def report(name, base, now):
    if isinstance(now, str):
        print(f"{name:<22} {now}"); return
    cells = []
    for s in DEV:
        n, ru, d, r = now[s]; _, ru0, d0, r0 = base[s]
        cells.append(f"{s} {d - d0:+d}/{r - r0:+d}{'!' if falls(base[s], now[s]) else ''} rules {ru - ru0:+d}")
    print(f"{name:<22} {verdict(base, now):<16} " + " | ".join(cells), flush=True)


def selfcheck():
    assert falls((10, 0, 10, 10), (10, 0, 11, 10)) and falls((10, 0, 10, 9), (10, 0, 9, 8)) and not falls((10, 0, 10, 9), (10, 0, 9, 9)) and not falls((10, 0, 5, 5), (10, 0, 0, 0))
    m = ENTRY.findall("[ new(StageBasis.Keyword, ReportRules.KeywordStage), new(StageBasis.WantedNotice, ReportRules.WantedWhileNoStage), ]")
    assert m == ["Keyword", "WantedNotice"], m
    base = {s: (10, 4, 8, 7) for s in DEV}
    assert verdict(base, dict(base)) == "no effect" and verdict(base, {**base, "v9": (10, 3, 8, 7)}) == "may go"
    assert verdict(base, {**base, "dev": (10, 4, 7, 7)}) == "may go" and verdict(base, {**base, "dev": (10, 4, 8, 6)}) == "precision falls"
    print("selfcheck OK")


def main(argv):
    if argv == ["--selfcheck"]: return selfcheck()
    only = next((a[5:].split(",") for a in argv if a.startswith("only=")), None)
    args = [a for a in argv if not a.startswith("only=")]
    if len([a for a in args if "=" not in a]) != 2 or "--heldback" in args: sys.exit(__doc__)
    if any(a.startswith("out=") for a in args): sys.exit("out= is prune.py's own (a folder in its temporary copy, removed at the end)")
    tmp = tempfile.mkdtemp(prefix="stage-prune-")
    try:
        copy = tmp.replace("\\", "/") + "/repo/"
        skip = shutil.ignore_patterns("bin", "obj")
        for d in ("src", "tools/Aml.StageEval", "licenses"): shutil.copytree(REPO + "/" + d, copy + d, ignore=skip)
        for f in ("Directory.Build.props", "Directory.Packages.props", "global.json", "nuget.config", "THIRD-PARTY-NOTICES.md"):
            if os.path.exists(REPO + "/" + f): shutil.copy(REPO + "/" + f, copy + f)
        todo = units(copy)
        if only:
            if set(only) - set(todo): sys.exit("no such unit: " + ", ".join(sorted(set(only) - set(todo))) + "; units: " + ", ".join(todo))
            todo = {k: v for k, v in todo.items() if k in only}
        base = measure(copy, args)
        if isinstance(base, str): sys.exit("baseline: " + base)
        print("baseline " + " | ".join(f"{s} n {base[s][0]} rules {base[s][1]} decided {base[s][2]} right {base[s][3]}" for s in DEV))
        print("per unit, set: DECIDED/RIGHT delta ('!' = unanimous precision falls), rules-alone delta", flush=True)
        for name, (f, old, new) in todo.items():
            text = apply(copy, f, old, new)
            if text is None: report(name, base, f"patch does not apply to {f}"); continue
            try: report(name, base, measure(copy, args))
            finally: open(copy + STAGE + f, "w", encoding="utf-8", newline="").write(text)
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


if __name__ == "__main__": main(sys.argv[1:])
