"""Package a Colab job folder for run_job.sh and check the package. Prints counts and hashes only.

usage: colabjob.py package <job dir> <name> [--extra a,b] [--alive REGEX] [--milestones REGEX]
       colabjob.py check <name>

package: <job dir> must hold run.sh (LF line ends). Writes into AML_COLAB_HOME (default C:/tmp/colab/home, the folder
  mounted at /home/colab in the colab container):
    job<name>.tgz          the folder as job<name>/ (unpacked to /content/job<name> on the VM)
    j<name>.part.NN        the archive in 40 MiB parts (the upload limit)
    start<name>.py         joins the parts, checks the archive sha (embedded), starts run.sh detached
    peek<name>.py          done/alive/fail, last step line, LAST line, milestone lines (--milestones), log tails
    pack<name>.py          log.txt, out/, fail.txt, done.txt and the --extra files or folders, in 40 MiB parts
  --alive adds process patterns (pgrep -f) that count as alive besides job<name>/run.sh.
check: sha in start<name>.py = sha of job<name>.tgz = sha of the joined parts; job<name>/run.sh in the archive has no
  CR and passes bash -n; the three scripts compile. Exit 1 on any failure.
"""
import argparse, hashlib, os, pathlib, re, subprocess, sys, tarfile

HOME = pathlib.Path(os.environ.get("AML_COLAB_HOME", "C:/tmp/colab/home"))
TMPL = pathlib.Path(__file__).resolve().parent
PART = 40 * 1024 * 1024
MILESTONES = r"DONE|FAIL |ONNX_|epoch |saved |versions "


def bash():
    git = "C:/Program Files/Git/bin/bash.exe"  # on Windows a bare "bash" may be WSL's
    return os.environ.get("BASH") or (git if os.path.exists(git) else "bash")


def fill(tmpl, **kw):
    t = (TMPL / tmpl).read_text(encoding="utf-8")
    for k, v in kw.items(): t = t.replace("@%s@" % k, v)
    assert not re.search(r"@[A-Z]+@", t), tmpl
    return t


def package(job, name, extra, alive, milestones):
    job = pathlib.Path(job)
    run = (job / "run.sh").read_bytes()
    assert b"\r" not in run, "run.sh has CR line ends"
    HOME.mkdir(parents=True, exist_ok=True)
    archive = HOME / ("job%s.tgz" % name)
    with tarfile.open(archive, "w:gz") as t: t.add(job, arcname="job" + name)
    data = archive.read_bytes(); sha = hashlib.sha256(data).hexdigest()
    for old in HOME.glob("j%s.part.*" % name): old.unlink()
    n = 0
    for n, k in enumerate(range(0, len(data), PART)): (HOME / ("j%s.part.%02d" % (name, n))).write_bytes(data[k:k + PART])
    scripts = {"start": fill("start.py.tmpl", NAME=name, SHA=sha),
               "peek": fill("peek.py.tmpl", NAME=name, ALIVE=repr(alive), MILESTONES=repr(milestones)),
               "pack": fill("pack.py.tmpl", NAME=name, EXTRA=repr(extra))}
    for k, s in scripts.items(): (HOME / ("%s%s.py" % (k, name))).write_text(s, encoding="utf-8", newline="\n")
    print("packaged job%s: %d files, %d bytes, %d parts, sha256 %s, into %s" % (
        name, sum(1 for p in job.rglob("*") if p.is_file()), len(data), n + 1, sha, HOME))


def check(name):
    ok = True
    def c(cond, msg):
        nonlocal ok; ok &= bool(cond); print(("ok   " if cond else "FAIL ") + msg)
    archive = HOME / ("job%s.tgz" % name)
    sha = hashlib.sha256(archive.read_bytes()).hexdigest()
    parts = sorted(HOME.glob("j%s.part.*" % name))
    joined = hashlib.sha256(b"".join(p.read_bytes() for p in parts)).hexdigest()
    m = re.search(r"if got != '([0-9a-f]{64})':", (HOME / ("start%s.py" % name)).read_text(encoding="utf-8"))
    c(m and m.group(1) == sha, "start%s.py sha equals job%s.tgz sha" % (name, name))
    c(parts and joined == sha, "joined parts equal the archive (%d parts)" % len(parts))
    with tarfile.open(archive) as t:
        try: run = t.extractfile("job%s/run.sh" % name).read()
        except KeyError: run = None
    c(run is not None, "archive holds job%s/run.sh" % name)
    if run is not None:
        c(b"\r" not in run, "run.sh has no CR")
        p = subprocess.run([bash(), "-n"], input=run, capture_output=True)
        c(p.returncode == 0, "run.sh passes bash -n " + p.stderr.decode(errors="replace").strip()[:200])
    for k in ("start", "peek", "pack"):
        f = HOME / ("%s%s.py" % (k, name))
        try: compile(f.read_text(encoding="utf-8"), f.name, "exec"); c(True, "compiles " + f.name)
        except SyntaxError as e: c(False, "compiles %s: %s" % (f.name, e.msg))
    print("ALL OK" if ok else "CHECK FAILED")
    return ok


def main():
    a = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = a.add_subparsers(dest="cmd", required=True)
    p = sub.add_parser("package"); p.add_argument("job"); p.add_argument("name")
    p.add_argument("--extra", default="", help="comma-separated files or folders of the job folder to pack besides log.txt and out/")
    p.add_argument("--alive", default="", help="extra pgrep -f patterns, joined with |")
    p.add_argument("--milestones", default=MILESTONES, help="regex of log lines peek prints")
    k = sub.add_parser("check"); k.add_argument("name")
    args = a.parse_args()
    if not re.fullmatch(r"[A-Za-z0-9_-]+", args.name): a.error("name: letters, digits, _ and - only")
    if args.cmd == "package": package(args.job, args.name, [x for x in args.extra.split(",") if x], args.alive, args.milestones)
    else: sys.exit(0 if check(args.name) else 1)


if __name__ == "__main__":
    main()
