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
#   totalSeconds = sum(scene.durationInSeconds) + outroSeconds
#   transitionFrames = round(fps * (isCalm ? 0.45 : 0.2))
#   transitionCount = outroSeconds > 0 ? nScenes : nScenes - 1
#   totalFrames = max(1, round(totalSeconds*fps) - transitionCount*transitionFrames)
#   sceneStart[i] = round(sum(durations before i) * fps) - i*transitionFrames
# Chunks group N consecutive scenes (default 5, from production/config.json).
# Validated: this reproduces the Inanna C01..C16 ranges exactly from the
# Inanna landscape props (see production/README.md, dry-run evidence).
#
# Usage:
#   render-chunks.sh --job <jobdir> --orientation vertical|landscape [--dry-run]
# Exits 0 when every chunk exists and validates, 1 when a chunk render
# fails (re-run to retry from that chunk), 2 when the props file is missing
# (the driver re-runs --assemble to regenerate it).
# Logs append to <job>/chunks-<orientation>.log (never truncated).
# --concurrency=1: chunk C14 OOMed twice at concurrency 2 (2026-11-16).
set -u
export PATH="/home/hatch/.dotnet:/opt/hatch-image/bin:/usr/local/bin:/usr/bin:/bin:$PATH"
export TMPDIR=/home/hatch/workspace/tmp

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
RENDER_DIR=/home/hatch/workspace/BroadcastContentAutomation/BroadcastContentAutomation/remotion-assembler
SCENES_PER_CHUNK=$(python3 -c "import json; print(json.load(open('/home/hatch/workspace/BroadcastContentAutomation/production/config.json'))['render']['scenes_per_chunk'])")

mkdir -p "$CHUNKS"
[ "$DRY_RUN" = "1" ] || touch "$LOG"

# Compute chunk ranges from the props file via the shared module
# (production/chunk_ranges.py — same formula as Root.tsx + the validated
# Inanna scene-start formula). Output: "TOTAL n" + "Cxx start end" lines.
RANGES=$(python3 /home/hatch/workspace/BroadcastContentAutomation/production/chunk_ranges.py "$PROPS" "$SCENES_PER_CHUNK")
TOTAL=$(echo "$RANGES" | awk '$1=="TOTAL"{print $2}')
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

echo "$(date -Is) chunk render start: $ORIENTATION total_frames=$TOTAL" >> "$LOG"
ranges_file=$(mktemp)
echo "$RANGES" | grep -v "^TOTAL" > "$ranges_file"
while read -r name start end; do
  out="$CHUNKS/$ORIENTATION-$name.mp4"
  expected_frames=$((end - start + 1))
  expected_dur=$(python3 -c "print(f'{$expected_frames/30:.2f}')")
  if [ -f "$out" ]; then
    dur=$(ffprobe -v error -show_entries format=duration -of csv=p=0 "$out" 2>/dev/null || echo 0)
    ok=$(python3 -c "import sys; print('yes' if abs(float('$dur') - $expected_dur) < 0.6 else 'no')" 2>/dev/null || echo no)
    if [ "$ok" = "yes" ]; then
      echo "$(date -Is) $name already done (${dur}s, expected ${expected_dur}s), skipping" | tee -a "$LOG"
      continue
    fi
    echo "$(date -Is) $name exists but invalid (dur=${dur}, expected ${expected_dur}), re-rendering" | tee -a "$LOG"
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
