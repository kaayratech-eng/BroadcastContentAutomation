#!/usr/bin/env bash
# production/laptop-remote.sh — runs ON the laptop under Git Bash.
#
# Small status/list/cleanup helper for the VM-side laptop-render driver.
# It is shipped to the laptop with every job package (production/) and is
# invoked over SSH as:
#   "C:\Program Files\Git\bin\bash.exe" -lc "/c/trendline-render/production/laptop-remote.sh <cmd> <job> <orientation>"
# so its arguments are always simple words (job id, orientation) — no
# quoting, no spaces. All paths are derived from this file's own location.
#
# Subcommands (all print key=value lines on stdout, human-readable too):
#   status <job> <orientation>   running/done/stalled + chunk counts + log tail
#   list   <job> <orientation>   one "name size" line per chunk file + ranges.json + hashes
#   has-rsync                    yes/no (rsync is NOT in stock Git Bash)
#   cleanup <job> <orientation>  remove the remote chunks dir (after verify)
#
# "Running" is decided the same way render-chunks.sh decides its mkdir
# lock (see the flock fallback there): a lock dir exists AND the chunk log
# was written in the last STALL_MIN minutes means a live render. A lock
# with a stale log means the render died (VM restart, kill, crash) and the
# next trigger may take over. This is the ground truth the VM polls — it
# never trusts a pid, which does not survive a laptop reboot anyway.
set -u
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PKG_ROOT="$(dirname "$SCRIPT_DIR")"
STALL_MIN=30

if python3 --version >/dev/null 2>&1; then PY=python3
elif python --version >/dev/null 2>&1; then PY=python
else PY=""; fi

usage() { echo "usage: $0 status|list|has-rsync|cleanup <job> [orientation]" >&2; exit 2; }
[ $# -ge 1 ] || usage
CMD="$1"; JOB_ID="${2:-}"; ORI="${3:-landscape}"

case "$CMD" in
  has-rsync)
    if command -v rsync >/dev/null 2>&1; then echo "rsync=yes"; else echo "rsync=no"; fi
    exit 0
    ;;
esac

[ -n "$JOB_ID" ] || usage
JOB_DIR="$PKG_ROOT/$JOB_ID"
CHUNKS="$JOB_DIR/chunks-$ORI"
LOG="$JOB_DIR/chunks-$ORI.log"
LOCKDIR="$JOB_DIR/chunks-$ORI.lockdir"
RANGES_FILE="$CHUNKS/ranges.json"

case "$CMD" in
  status)
    total=0; done_n=0
    if [ -f "$RANGES_FILE" ] && [ -n "$PY" ]; then
      total=$("$PY" -c "import json; d=json.load(open('$RANGES_FILE')); print(len(d['ranges']))" 2>/dev/null || echo 0)
      total_frames=$("$PY" -c "import json; print(json.load(open('$RANGES_FILE'))['total_frames'])" 2>/dev/null || echo 0)
    else
      total=0; total_frames=0
    fi
    if [ -d "$CHUNKS" ]; then
      done_n=$(find "$CHUNKS" -maxdepth 1 -name "$ORI-C[0-9]*.mp4" -size +0c 2>/dev/null | wc -l)
      done_n=${done_n// /}
    fi
    running=no; stale=no
    if [ -d "$LOCKDIR" ]; then
      log_age=9999
      [ -f "$LOG" ] && log_age=$(( ($(date +%s) - $(stat -c %Y "$LOG" 2>/dev/null || echo 0)) / 60 ))
      if [ "$log_age" -lt "$STALL_MIN" ]; then running=yes; else stale=yes; fi
      echo "log_age_min=$log_age"
    fi
    all_done=no
    if [ "$total" -gt 0 ] && [ "$done_n" -ge "$total" ]; then all_done=yes; fi
    echo "job=$JOB_ID"
    echo "orientation=$ORI"
    echo "running=$running"
    echo "stale=$stale"
    echo "chunks_done=$done_n"
    echo "chunks_total=$total"
    echo "total_frames=$total_frames"
    echo "all_done=$all_done"
    if [ -f "$LOG" ]; then
      echo "last_log=$(tail -1 "$LOG" 2>/dev/null | tr -d '\r' | tail -c 200)"
    else
      echo "last_log="
    fi
    exit 0
    ;;
  list)
    [ -d "$CHUNKS" ] || exit 0
    # One "name size" line per file the VM needs back: chunk mp4s (only
    # non-empty ones — a zero-byte file is a torn write, not a chunk),
    # plus ranges.json and the props hashes the finish step re-checks.
    for f in "$CHUNKS"/"$ORI"-C[0-9]*.mp4 "$CHUNKS"/ranges.json "$CHUNKS"/props.sha256 "$CHUNKS"/props-boundary.sha256; do
      [ -f "$f" ] && [ -s "$f" ] || continue
      echo "$(basename "$f") $(stat -c %s "$f" 2>/dev/null || echo 0)"
    done
    exit 0
    ;;
  cleanup)
    rm -rf "$CHUNKS" "$LOCKDIR"
    echo "cleaned=$JOB_ID/chunks-$ORI"
    exit 0
    ;;
  *)
    usage
    ;;
esac
