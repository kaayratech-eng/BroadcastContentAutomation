#!/usr/bin/env bash
# Generic restart-proof chunked Remotion render for one job + orientation.
#
# Generalizes the per-job drivers (work/render-landscape-chunks-inanna.sh,
# work/gigglegarden/render-giggle-test2-chunks.sh) that proved the pattern:
# a full landscape render (6-10h) cannot survive this VM's ~2-4h restarts,
# but ~25-35 min scene-aligned chunks can — each chunk is skipped when its
# output already exists and passes ffprobe validation, so re-running this
# script after any interruption resumes from the first missing chunk.
#
# Chunk boundaries are computed from the --assemble props file
# (<job>/remotion-props-<W>x<H>.json) using the same formula Root.tsx
# calculateMetadata uses (the single source of truth for total length):
#   totalSeconds = sum(brief.durationInSeconds) + introSeconds
#                + sum(scene.durationInSeconds) + outroSeconds
#   transitionFrames = round(fps * (isCalm ? 0.45 : 0.2))
#   transitionCount = nBrief + (introSeconds > 0 ? 1 : 0)
#                   + (outroSeconds > 0 ? nScenes : nScenes - 1)
#   totalFrames = max(1, round(totalSeconds*fps) - transitionCount*transitionFrames)
#   briefStart[j] = round(sum(brief durations before j) * fps) - j*transitionFrames
#   sceneStart[i] = round(sum(brief durations) * fps) + round(introSeconds*fps)
#                 + round(sum(scene durations before i) * fps)
#                 - (nBrief + (introSeconds > 0 ? 1 : 0) + i)*transitionFrames
# Chunks group N consecutive scenes (default 5, from production/config.json).
# Validated: this reproduces the Inanna C01..C16 ranges exactly from the
# Inanna landscape props (see production/README.md, dry-run evidence).
# The brief term was missing until 2026-11-22: a brief-bearing landscape
# was planned 427 frames short (video #3: TOTAL 9485 instead of 9912),
# which would have truncated the outro. Fixed in chunk_ranges.py.
#
# Usage:
#   render-chunks.sh --job <jobdir> --orientation vertical|landscape [--dry-run]
# Exits 0 when every chunk exists and validates, 1 when a chunk render
# fails (re-run to retry from that chunk), 2 when the props file is missing
# (the driver re-runs --assemble to regenerate it).
# Logs append to <job>/chunks-<orientation>.log (never truncated).
# --concurrency=1: chunk C14 OOMed twice at concurrency 2 (2026-11-16).
set -u
# Location-aware paths (2026-11-23, laptop automation): this one script runs
# in TWO layouts — the VM repo (production/ beside BroadcastContentAutomation/
# which holds remotion-assembler/) and the laptop package (production/
# beside remotion-assembler/ at the package root). Derive everything from
# this file's own location instead of hardcoding VM paths, so the two
# variants can never drift apart again (the sting divergence, 2026-11-23,
# came from exactly that drift). RENDER_DIR is picked by layout detection.
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROD_DIR="$SCRIPT_DIR"
REPO_ROOT="$(dirname "$SCRIPT_DIR")"
if [ -d "$REPO_ROOT/remotion-assembler" ]; then
  RENDER_DIR="$REPO_ROOT/remotion-assembler"          # laptop package layout
elif [ -d "$REPO_ROOT/BroadcastContentAutomation/remotion-assembler" ]; then
  RENDER_DIR="$REPO_ROOT/BroadcastContentAutomation/remotion-assembler"  # VM repo layout
else
  echo "cannot locate remotion-assembler under $REPO_ROOT" >&2; exit 2
fi
CONFIG_FILE="$PROD_DIR/config.json"
# npx lives in /opt/hatch-image/bin on the VM; that dir is normally on PATH
# already, but a bare invocation context may lack it. Add it when present
# (a no-op on the laptop, where the dir does not exist).
[ -d /opt/hatch-image/bin ] && case ":$PATH:" in *:/opt/hatch-image/bin:*) ;; *) PATH="/opt/hatch-image/bin:$PATH";; esac
# Python: prefer python3, but on Windows the `python3` name may be a
# Microsoft Store alias stub that opens the Store instead of running.
# Test that it actually runs; otherwise fall back to `python`.
if python3 --version >/dev/null 2>&1; then PY=python3
elif python --version >/dev/null 2>&1; then PY=python
else echo "Python not found: install Python 3.12+ and tick 'Add python.exe to PATH'" >&2; exit 2; fi
# Preflight: the tools the render needs must be on PATH.
for tool in npx ffprobe sha256sum; do
  command -v "$tool" >/dev/null 2>&1 || { echo "missing required tool: $tool (is it on PATH?)" >&2; exit 2; }
done

JOB=""
ORIENTATION=""
DRY_RUN=0
while [ $# -gt 0 ]; do
  case "$1" in
    --job) JOB="$2"; shift 2;;
    --orientation) ORIENTATION="$2"; shift 2;;
    --dry-run) DRY_RUN=1; shift;;
    *) echo "usage: $0 --job <jobdir> --orientation vertical|landscape [--dry-run]" >&2; exit 2;;
  esac
done
[ -n "$JOB" ] && [ -d "$JOB" ] || { echo "no such job dir: $JOB" >&2; exit 2; }
[ "$ORIENTATION" = "vertical" ] || [ "$ORIENTATION" = "landscape" ] || { echo "orientation must be vertical|landscape" >&2; exit 2; }

if [ "$ORIENTATION" = "vertical" ]; then W=1080; H=1920; else W=1920; H=1080; fi
PROPS="$JOB/remotion-props-${W}x${H}.json"
[ -f "$PROPS" ] || { echo "PROPS MISSING: $PROPS - run --assemble first to generate it" >&2; exit 2; }

CHUNKS="$JOB/chunks-$ORIENTATION"
LOG="$JOB/chunks-$ORIENTATION.log"
# Landscape chunks are shorter (2026-11-23): the 5-scene landscape chunk
# (903 frames) kept dying from memory at frame ~58, while the 5-scene
# vertical chunks (~725 frames) survived. scenes_per_chunk_landscape
# overrides the default for landscape only; vertical keeps 5 so its
# finished chunks stay valid. finish-chunks.py applies the same rule.
SCENES_PER_CHUNK=$("$PY" -c "import json,sys; cfg=json.load(open(sys.argv[1]))['render']; print(cfg.get('scenes_per_chunk_landscape', cfg['scenes_per_chunk']) if sys.argv[2]=='landscape' else cfg['scenes_per_chunk'])" "$CONFIG_FILE" "$ORIENTATION" 2>/dev/null) || { echo "could not read render.scenes_per_chunk from $CONFIG_FILE" >&2; exit 2; }
case "$SCENES_PER_CHUNK" in ''|*[!0-9]*)
  echo "scenes_per_chunk is not a positive integer: '$SCENES_PER_CHUNK'" >&2; exit 2;;
esac
[ "$SCENES_PER_CHUNK" -gt 0 ] || { echo "scenes_per_chunk must be > 0" >&2; exit 2; }

mkdir -p "$CHUNKS"
[ "$DRY_RUN" = "1" ] || touch "$LOG"

# One render per job+orientation at a time. A manual render and a driver
# render must never write the same chunk files (two writers to the same
# landscape-C0*.mp4, with duration-only validation, could stitch a torn
# file). flock is kernel-held, so a VM restart releases it automatically;
# a second instance exits at once instead of queuing.
LOCK="$JOB/chunks-$ORIENTATION.lock"
LOCKDIR="$JOB/chunks-$ORIENTATION.lockdir"
STALL_MIN=30
if command -v flock >/dev/null 2>&1; then
  exec 9>"$LOCK"
  if ! flock -n 9; then
    echo "another render-chunks.sh is already running for $JOB ($ORIENTATION); exiting" >&2
    exit 1
  fi
else
  # No flock (laptop Git Bash): mkdir is atomic, so it is the lock. A lock
  # left by a killed run goes stale: the chunk log stops advancing when the
  # render dies, so a lock whose chunk log has been silent for STALL_MIN
  # minutes is dead and may be taken over (the render resumes from the
  # first missing chunk). A fresh lock means a live render — exit. The lock
  # is removed on any exit this shell survives (trap EXIT); only a SIGKILL
  # / machine kill can leave it behind, which is exactly the stale case.
  # This enforces the ONE-renderer rule on the laptop itself, where the
  # driver cannot see the process table.
  if mkdir "$LOCKDIR" 2>/dev/null; then
    trap 'rmdir "$LOCKDIR" 2>/dev/null || true' EXIT
  else
    LOG_AGE_MIN=9999
    if [ -f "$LOG" ]; then
      LOG_AGE_MIN=$(( ($(date +%s) - $(stat -c %Y "$LOG" 2>/dev/null || echo 0)) / 60 ))
    fi
    if [ "$LOG_AGE_MIN" -ge "$STALL_MIN" ]; then
      echo "stale lock $LOCKDIR (chunk log silent ${LOG_AGE_MIN}m >= ${STALL_MIN}m) — previous render is dead; taking over" | tee -a "$LOG"
      rm -rf "$LOCKDIR"
      mkdir "$LOCKDIR" || { echo "could not take over stale lock $LOCKDIR" >&2; exit 1; }
      trap 'rmdir "$LOCKDIR" 2>/dev/null || true' EXIT
    else
      echo "another render-chunks.sh is already running for $JOB ($ORIENTATION) (lock $LOCKDIR, chunk log ${LOG_AGE_MIN}m old); exiting" >&2
      exit 1
    fi
  fi
fi

# Compute chunk ranges from the props file via the shared module
# (production/chunk_ranges.py — same formula as Root.tsx + the validated
# Inanna scene-start formula). Output: "TOTAL n" + "Cxx start end" lines.
# Fail loudly if the Python call fails or returns nothing usable: an empty
# range list used to print "ALL CHUNKS DONE" and exit 0 (the props file
# may be truncated JSON if --assemble was killed mid-write).
RANGES=$("$PY" "$PROD_DIR/chunk_ranges.py" "$PROPS" "$SCENES_PER_CHUNK") || { echo "chunk_ranges.py failed for $PROPS" >&2; exit 2; }
TOTAL=$(echo "$RANGES" | awk '$1=="TOTAL"{print $2}')
case "$TOTAL" in ''|*[!0-9]*)
  echo "chunk_ranges.py returned no valid TOTAL for $PROPS (props may be corrupt)" >&2; exit 2;;
esac
RANGE_COUNT=$(echo "$RANGES" | grep -vc "^TOTAL")
[ "$RANGE_COUNT" -ge 1 ] || { echo "chunk_ranges.py returned no chunk ranges for $PROPS" >&2; exit 2; }
echo "job: $JOB orientation: $ORIENTATION total_frames: $TOTAL scenes_per_chunk: $SCENES_PER_CHUNK"
if [ "$DRY_RUN" = "1" ]; then
  echo "$RANGES" | grep -v "^TOTAL"
  # Show which chunks already exist / would render, without rendering.
  echo "$RANGES" | grep -v "^TOTAL" | while read -r name start end; do
    out="$CHUNKS/$ORIENTATION-$name.mp4"
    if [ -f "$out" ]; then echo "$name $start-$end exists: $out"; else echo "$name $start-$end MISSING -> would render"; fi
  done
  exit 0
fi

# Props versioning: record what these chunks were rendered against. The
# boundary hash covers ONLY the fields that move a chunk boundary
# (fps / intro / outro / isCalm / per-scene durations — see
# chunk_ranges.boundary_hash, review #2 B24). The old full-file sha256
# discarded ALL chunks on any byte change, including a --retts that
# rewrote byte-identical content with different formatting. The full-file
# hash is still honoured as a fallback so jobs rendered before this change
# are not wiped on the first run after it: chunks are kept when EITHER
# hash matches, and discarded only when the boundaries actually moved.
# Read the stored hashes BEFORE overwriting them. (After the --dry-run
# branch above, so a dry run never deletes chunks or writes version files.)
STORED_HASH=$(cat "$CHUNKS/props.sha256" 2>/dev/null || echo "")
STORED_BOUNDARY_HASH=$(cat "$CHUNKS/props-boundary.sha256" 2>/dev/null || echo "")
PROPS_HASH=$(sha256sum "$PROPS" | awk '{print $1}')
PROPS_BOUNDARY_HASH=$("$PY" "$PROD_DIR/chunk_ranges.py" --boundary-hash "$PROPS") || { echo "chunk_ranges.py --boundary-hash failed for $PROPS" >&2; exit 2; }
if [ -n "$STORED_HASH" ] || [ -n "$STORED_BOUNDARY_HASH" ]; then
  if { [ -n "$STORED_HASH" ] && [ "$STORED_HASH" = "$PROPS_HASH" ]; } || \
     { [ -n "$STORED_BOUNDARY_HASH" ] && [ "$STORED_BOUNDARY_HASH" = "$PROPS_BOUNDARY_HASH" ]; }; then
    if [ -n "$STORED_HASH" ] && [ "$STORED_HASH" != "$PROPS_HASH" ]; then
      echo "props changed since these chunks were rendered, but the frame boundaries are identical (boundary hash $PROPS_BOUNDARY_HASH) — keeping all $ORIENTATION chunks" | tee -a "$LOG"
    fi
  else
    echo "props frame boundaries changed since these chunks were rendered (boundary hash $STORED_BOUNDARY_HASH -> $PROPS_BOUNDARY_HASH); discarding all $ORIENTATION chunks" | tee -a "$LOG"
    rm -f "$CHUNKS/$ORIENTATION"-C*.mp4
  fi
fi
echo "$PROPS_HASH" > "$CHUNKS/props.sha256"
echo "$PROPS_BOUNDARY_HASH" > "$CHUNKS/props-boundary.sha256"
echo "$RANGES" | "$PY" -c "
import json, sys
lines = sys.stdin.read().strip().splitlines()
total = int([l.split()[1] for l in lines if l.startswith('TOTAL')][0])
ranges = [[p[0], int(p[1]), int(p[2])] for p in (l.split() for l in lines if not l.startswith('TOTAL'))]
json.dump({'total_frames': total, 'ranges': ranges}, open('$CHUNKS/ranges.json', 'w'), indent=2)
"

FPS=$("$PY" -c "import json,sys; print(json.load(open(sys.argv[1])).get('fps', 30))" "$PROPS" 2>/dev/null || echo 30)
echo "$(date -Is) chunk render start: $ORIENTATION total_frames=$TOTAL fps=$FPS" >> "$LOG"
ranges_file=$(mktemp)
echo "$RANGES" | grep -v "^TOTAL" > "$ranges_file"
while read -r name start end; do
  out="$CHUNKS/$ORIENTATION-$name.mp4"
  expected_frames=$((end - start + 1))
  expected_dur=$("$PY" -c "print(f'{$expected_frames/$FPS:.2f}')")
  if [ -f "$out" ]; then
    # Strict validation: both a video and an audio stream must be present,
    # the frame count must match the range (when ffprobe reports it), and
    # the duration must be within 0.15s (~4 frames). Duration-only ±0.6s
    # used to let audio-less, frozen, or up-to-17-frames-short chunks pass.
    valid=$("$PY" - "$out" "$expected_frames" "$expected_dur" <<'PYEOF' 2>/dev/null || echo no
import json, subprocess, sys
path, exp_frames, exp_dur = sys.argv[1], int(sys.argv[2]), float(sys.argv[3])
r = subprocess.run(["ffprobe", "-v", "error",
                    "-show_entries", "stream=codec_type,nb_frames",
                    "-show_entries", "format=duration",
                    "-of", "json", path], capture_output=True, text=True)
try:
    d = json.loads(r.stdout)
except Exception:
    print("no"); sys.exit()
streams = d.get("streams", [])
nv = sum(1 for s in streams if s.get("codec_type") == "video")
na = sum(1 for s in streams if s.get("codec_type") == "audio")
if nv < 1 or na < 1:
    print("no"); sys.exit()
nbf = None
for s in streams:
    if s.get("codec_type") == "video":
        raw = s.get("nb_frames")
        if raw not in (None, "N/A"):
            try:
                nbf = int(raw)
            except (TypeError, ValueError):
                nbf = None
        break
dur = float(d.get("format", {}).get("duration", 0))
if nbf is not None and abs(nbf - exp_frames) > 1:
    print("no"); sys.exit()
print("yes" if abs(dur - exp_dur) < 0.15 else "no")
PYEOF
)
    if [ "$valid" = "yes" ]; then
      echo "$(date -Is) $name already done (expected ${expected_dur}s, ${expected_frames}f), skipping" | tee -a "$LOG"
      continue
    fi
    echo "$(date -Is) $name exists but invalid (expected ${expected_dur}s/${expected_frames}f), re-rendering" | tee -a "$LOG"
    rm -f "$out"
  fi
  echo "$(date -Is) rendering $name frames $start-$end (${expected_frames} frames, ~${expected_dur}s)" | tee -a "$LOG"
  (cd "$RENDER_DIR" && npx remotion render src/index.ts Assembly "$out" --props="$PROPS" --public-dir="$JOB" --frames="$start-$end" --concurrency=1 >> "$LOG" 2>&1)
  rc=$?
  echo "$(date -Is) $name finished, exit $rc" | tee -a "$LOG"
  if [ $rc -ne 0 ]; then
    echo "$(date -Is) $name FAILED (exit $rc) - stopping; re-run to retry from $name" | tee -a "$LOG"
    rm -f "$ranges_file"
    exit 1
  fi
done < "$ranges_file"
rm -f "$ranges_file"
echo "$(date -Is) ALL $ORIENTATION CHUNKS DONE (total_frames=$TOTAL)" | tee -a "$LOG"
exit 0
