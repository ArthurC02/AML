#!/bin/bash
# Upload, start, watch, download and stop one Colab job packaged by colabjob.py.
# usage: run_job.sh <name> <T4|L4> <download dir> [stall seconds] [max seconds] [--reuse] [--dry-run]
#   stall: stop when log.txt has no new last line for this long (default 2700 = 45 min)
#   max:   overall limit (default 43200 = 12 h); peeks every AML_COLAB_PEEK seconds (default 180)
#   --reuse: use an already open session aml<name>; without it the runner refuses to start (an old done.txt or out/
#            there would pass as new results)
#   --dry-run: print every docker command instead of running it, answer with simulated Colab output (no Colab needed);
#            AML_DRY_SIM=existing simulates an already open aml<name>, AML_DRY_SIM=nolist a `colab sessions` that
#            answers nothing after the first stop
# Every docker/colab call runs under timeout; any abnormal state stops the session (after downloading what exists
# when the VM still answers); the EXIT trap stops the session if still open and ends with `colab sessions`. A session
# counts as gone only when `colab sessions` succeeds with its normal listing and that listing lacks [aml<name>].
# AML_COLAB_HOME: the host folder mounted at /home/colab in the `colab` container (default C:/tmp/colab/home).
# Exit codes: 0 done, 1 no parts, no session listing, no session or upload failed, 2 start failed or job died,
# 3 fail.txt, 4 fetch failed, 5 session aml<name> already open (left untouched; --reuse to use it), 6 peeks failed,
# 7 session stop not confirmed: SESSION MAY STILL BE RUNNING (replaces any other code), 8 time limit, 9 stall,
# 64 usage, 130 interrupted.
export MSYS_NO_PATHCONV=1
DRY=0; REUSE=0; args=()
for a in "$@"; do case $a in --dry-run) DRY=1;; --reuse) REUSE=1;; *) args+=("$a");; esac; done
set -- "${args[@]}"
[ $# -ge 3 ] || { echo "usage: run_job.sh <name> <T4|L4> <download dir> [stall seconds] [max seconds] [--reuse] [--dry-run]"; exit 64; }
N=$1; GPU=$2; STALL=${4:-2700}; MAX=${5:-43200}; PEEK=${AML_COLAB_PEEK:-180}
[[ $N =~ ^[A-Za-z0-9_-]+$ ]] || { echo "name: letters, digits, _ and - only"; exit 64; }
case $GPU in T4|L4) ;; *) echo "gpu must be T4 or L4"; exit 64;; esac
[[ "$STALL$MAX$PEEK" =~ ^[0-9]+$ ]] || { echo "stall, max and AML_COLAB_PEEK are seconds"; exit 64; }
unix() { cygpath -u "$1" 2> /dev/null || echo "$1"; }  # tar reads C:/... as host:path
H=$(unix "${AML_COLAB_HOME:-C:/tmp/colab/home}"); DL=$(unix "$3")
S=aml$N

if [ $DRY = 0 ]; then
  cx() { timeout ${2:-900} docker exec -i -e OAUTHLIB_RELAX_TOKEN_SCOPE=1 -w /home/colab colab sh -c "$1 < /dev/null" 2>&1; }
else
  # Simulated VM: session state, a peek counter and a two-part fake output archive in a temp folder. The listing has
  # the real forms ("[<session>] <id> | Hardware: ..." per session, else "[colab] No active sessions found on server.");
  # before `colab new` it lists a session aml<name>0, which must not count as aml<name>.
  D=$(mktemp -d); mkdir -p "$D/vm/out" "$D/parts"; echo "dry run" > "$D/vm/log.txt"; echo DONE > "$D/vm/done.txt"; echo 1 > "$D/vm/out/r.txt"
  tar cf "$D/out.tar" -C "$D/vm" . && split -b 6000 -d "$D/out.tar" "$D/parts/out$N.part."
  [ "$AML_DRY_SIM" = existing ] && touch "$D/open" "$D/new"
  sleep() { :; }
  cx() {
    echo "DRY: timeout ${2:-900} docker exec -i -e OAUTHLIB_RELAX_TOKEN_SCOPE=1 -w /home/colab colab sh -c \"$1 < /dev/null\"" >&2
    case "$1" in
      "colab sessions"*) [ "$AML_DRY_SIM" = nolist ] && [ -f "$D/stopcalled" ] && return 124
        [ -f "$D/new" ] || echo "[${S}0] gpu-t4-s-dry0 | Hardware: T4 | Shape: Standard | Variant: GPU"
        [ -f "$D/open" ] && echo "[$S] gpu-${GPU,,}-s-dry | Hardware: $GPU | Shape: Standard | Variant: GPU"
        [ -f "$D/new" ] && [ ! -f "$D/open" ] && echo "[colab] No active sessions found on server."
        return 0 ;;
      "colab new "*) touch "$D/open" "$D/new"; echo "[colab] Session READY." ;;
      "colab stop "*) rm -f "$D/open"; touch "$D/stopcalled"; echo "[colab] Session terminated." ;;
      "colab upload "*) echo UPOK ;;
      *"-f start$N.py"*) echo "STARTED job$N" ;;
      # The first peek carries a log line with "done=True fail=True" in it: only the PEEK status line may count.
      *"-f peek$N.py"*) k=$(($(cat "$D/peeks" 2> /dev/null || echo 0) + 1)); echo $k > "$D/peeks"
        if [ $k -lt 2 ]; then printf 'PEEK done=False alive=True fail=False\nstep 1/2\nLAST 1 step 1/2\nepoch 1 note done=True fail=True alive=False\n'
        else printf 'PEEK done=True alive=False fail=False\nstep 2/2\nLAST 2 JOB_DONE\nJOB_DONE\n'; fi ;;
      *"-f pack$N.py"*) n=0; for f in "$D"/parts/*; do echo "PART $(basename $f) $(md5sum "$f" | cut -c1-32)"; n=$((n+1)); done
        echo "PACKED $(sha256sum "$D/out.tar" | cut -c1-64) $(wc -c < "$D/out.tar") $n" ;;
      "colab download "*) set -- $1; cp "$D/parts/$(basename $5)" "$H/$6" && echo DLOK ;;
    esac
  }
fi

# listed: 0 = aml<name> is listed, 1 = a normal listing without it, 2 = no normal listing (failed, timed out, other form)
listed() { local o; o=$(cx "colab sessions" 300) || return 2
  echo "$o" | awk -v s="[$S]" '$1 == s {f = 1} /^\[colab\] No active sessions found/ || / \| Hardware: / {ok = 1} END {exit f ? 0 : ok ? 1 : 2}'; }
stopped=0; unconfirmed=0
stop() { echo "STOPPING SESSION: $1"; stopped=1; local n r
  for n in 1 2 3 4 5; do
    cx "colab stop -s $S" | tail -1; listed; r=$?
    [ $r = 1 ] && { echo "session gone"; return 0; }
    [ $r = 2 ] && echo "colab sessions gave no normal listing (try $n)"
    sleep 20
  done
  unconfirmed=1; echo "SESSION MAY STILL BE RUNNING: $S (stop it by hand and check colab sessions)"; return 1; }
finish() { [ $stopped = 1 ] || stop "runner exited without stopping"; echo "== colab sessions $(date +%H:%M)"; cx "colab sessions" 300
  [ $DRY = 1 ] && rm -rf "$D"; [ $unconfirmed = 0 ] || { echo "exit 7: session stop not confirmed"; exit 7; }; }
trap finish EXIT
trap 'echo interrupted; exit 130' INT TERM HUP

ls "$H"/j$N.part.* > /dev/null 2>&1 || { echo "no parts j$N.part.* in $H (run colabjob.py package)"; stopped=1; exit 1; }
listed; r=$?
[ $r = 2 ] && { echo "colab sessions gave no normal listing: not starting"; stopped=1; exit 1; }
[ $r = 0 ] && [ $REUSE = 0 ] && { echo "session $S is already open: not starting and not stopping it (its old done.txt or out/ could pass as new results); stop it by hand, or pass --reuse"; stopped=1; exit 5; }
[ $r = 1 ] && cx "colab new -s $S --gpu $GPU" | tail -1
listed || { stop "no session after colab new"; exit 1; }
for p in $(cd "$H" && ls j$N.part.*); do
  ok=0; for n in 1 2 3 4 5; do cx "colab upload -s $S $p $p > /dev/null 2>&1 && echo UPOK" | grep -q UPOK && { ok=1; break; }; sleep 20; done
  [ $ok = 1 ] || { stop "upload failed $p"; exit 1; }
done
echo "uploaded $(date +%H:%M)"
st=$(cx "colab exec -s $S -f start$N.py --timeout 300" | grep "job$N" | tail -1); echo "$st"
echo "$st" | grep -qE "STARTED job$N|ALREADY job$N" || { stop "start failed"; exit 2; }

# pack and download in 40 MiB parts (md5 per part, sha256 of the joined archive), extract to the download dir
fetch() {
  packed=""; for n in 1 2 3; do packed=$(cx "colab exec -s $S -f pack$N.py --timeout 600" 700 | grep -E "^(PART|PACKED) "); echo "$packed" | grep -q "^PACKED " && break; packed=""; sleep 30; done
  [ -n "$packed" ] || { echo "pack failed"; return 1; }
  sha=$(echo "$packed" | grep "^PACKED " | tail -1 | awk '{print $2}'); np=$(echo "$packed" | grep "^PACKED " | tail -1 | awk '{print $4}')
  echo "packed $(echo "$packed" | grep "^PACKED " | tail -1 | awk '{print $3}') bytes in $np parts $sha"
  [ "$(echo "$packed" | grep -c "^PART ")" = "$np" ] || { echo "part list incomplete"; return 1; }
  rm -rf "$H/dl$N" && mkdir -p "$H/dl$N"
  for k in $(seq 0 $((np - 1))); do
    f=$(printf "out$N.part.%02d" $k); want=$(echo "$packed" | awk -v f=$f '$1 == "PART" && $2 == f {print $3}'); ok=0
    for t in 1 2 3 4 5; do
      rm -f "$H/dl$N/$f"
      cx "colab download -s $S /content/$f dl$N/$f > /dev/null 2>&1 && echo DLOK" 600 | grep -q DLOK && [ "$(md5sum "$H/dl$N/$f" | cut -c1-32)" = "$want" ] && { ok=1; break; }
      echo "part $f failed or md5 mismatch (try $t)"; sleep 20
    done
    [ $ok = 1 ] || { echo "download failed $f"; return 1; }
  done
  for k in $(seq 0 $((np - 1))); do cat "$H/dl$N/$(printf "out$N.part.%02d" $k)"; done > "$H/dl$N/out$N.tar"
  got=$(sha256sum "$H/dl$N/out$N.tar" | cut -c1-64)
  [ "$got" = "$sha" ] || { echo "sha mismatch of the joined archive"; return 1; }
  mkdir -p "$DL" && tar xf "$H/dl$N/out$N.tar" -C "$DL" && { echo "VERIFIED and extracted to $DL"; return 0; }
  echo "extract failed"; return 1
}

# Peek every $PEEK s until done; the time check ends the loop at $MAX s. Stall = the LAST line unchanged for $STALL s.
# done/alive/fail are read from the PEEK status line only (the rest of the output holds log lines and tails).
t0=$(date +%s); fails=0; last=""; tlast=$t0; out=""; st=""
for i in $(seq 1 $((MAX / (PEEK > 0 ? PEEK : 1) + 2))); do
  sleep $PEEK
  now=$(date +%s)
  [ $((now - t0)) -gt $MAX ] && break
  out=$(cx "colab exec -s $S -f peek$N.py --timeout 90" 200); st=$(echo "$out" | grep -m1 "^PEEK done=")
  if [ -z "$st" ]; then fails=$((fails+1)); echo "$(date +%H:%M) peek failed ($fails)"; [ $fails -ge 5 ] && { stop "five peeks failed in a row"; exit 6; }; continue; fi
  fails=0
  echo "$(date +%H:%M) $(echo "$out" | grep -m1 -A2 "^PEEK done=" | tr '\n' ' ' | cut -c1-240)"
  [[ $st == *" fail=True"* ]] && { echo "$out" | tail -15 | cut -c1-300; fetch; stop "job wrote fail.txt"; exit 3; }
  [[ $st == *"done=True"* ]] && break
  [[ $st == *" alive=False"* ]] && { echo "$out" | tail -15 | cut -c1-300; fetch; stop "job not alive and not done"; exit 2; }
  line=$(echo "$out" | grep -m1 "^LAST ")
  if [ "$line" != "$last" ]; then last=$line; tlast=$now
  elif [ $((now - tlast)) -gt $STALL ]; then echo "$out" | tail -10 | cut -c1-300; fetch; stop "no new log line for $STALL s"; exit 9; fi
done
if [[ $st != *"done=True"* ]]; then echo "time limit"; fetch; stop "not done after $MAX s"; exit 8; fi
echo "$out" | grep -v "^PEEK " | tail -40 | cut -c1-300
fetch || { stop "finished but fetch failed"; exit 4; }
stop "finished"
