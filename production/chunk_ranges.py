#!/usr/bin/env python3
"""Scene-aligned chunk ranges from a Remotion props file.

Single source of truth for chunk boundaries, shared by
production/render-chunks.sh and production/finish-chunks.py (so the two
can never disagree about which frames a chunk covers).

Formula (Root.tsx calculateMetadata — the one source of truth for total
length, plus the scene-start formula validated empirically for Inanna):
  totalSeconds = sum(brief.durationInSeconds) + introSeconds
               + sum(scene.durationInSeconds) + outroSeconds
  transitionFrames = round(fps * (isCalm ? 0.45 : 0.2))
  transitionCount = nBrief
                  + (1 if introSeconds > 0 else 0)
                  + (nScenes if outroSeconds > 0 else nScenes - 1)
  totalFrames = max(1, round(totalSeconds*fps) - transitionCount*transitionFrames)
  briefStart[j] = sum(round(dur*fps) for brief scenes before j) - j*transitionFrames
  sceneStart[i] = sum(round(dur*fps) for brief scenes) + round(introSeconds*fps)
                + sum(round(dur*fps) for scenes before i)
                - (nBrief + (1 if introSeconds > 0 else 0) + i)*transitionFrames
Chunks group N consecutive scenes; a 1-scene tail is merged into the
previous chunk (81 scenes -> 16 chunks, the last holding 6).

The intro term matters: a Trendline Now long-form WITHOUT a brief opens
with a 3.5s animated intro sting (IntroSeconds=3.5, IntroLogoPath). When a
brief plays the sting is skipped (fix sequence #2 ITEM 3), so introSeconds
is 0 in the props - this module reads it from the props file either way,
it never assumes 3.5s. The pre-2026-11-21 version of this module ignored
it computed a total 99 frames short (3.5s*30 - one 6-frame transition)
and scene starts ~3.3s early — chunk renders would have sliced the wrong
frames and the finish gate would have "verified" them against the same
wrong total. Fixed 2026-11-21 (trendline driver build).

The brief term matters the same way: Trendline Now videos from #3 on
open with a 10-20s brief (briefScenes) FIRST - and the intro sting is
skipped whenever a brief plays, so brief + sting never stack. The
pre-2026-11-22 version of this module ignored briefScenes, so for a
brief-bearing composition it computed a total 427 frames short (video #3:
14.617s*30 - two 6-frame transitions) and scene starts 426 frames early —
the stitched landscape would have lost its entire outro plus the tail of
the last scene, and the finish gate would have "verified" it against the
same wrong total. Fixed 2026-11-22 (chunk render stopped mid-C02;
C01/C02 discarded, re-render after this fix).

Usage: chunk_ranges.py <props.json> [scenes_per_chunk]
Prints: "TOTAL <n>" then "Cxx <start> <end>" per chunk.
Validated:
  - reproduces the Inanna C01..C17 ranges exactly (no intro -> identical
    to the pre-fix output; Inanna landscape TOTAL 20059, C17 18978-20058).
  - Trendline Now video #1 landscape props (intro 3.5s, outro 7.494s,
    77 scenes): TOTAL 20960 — exactly the frame count the live --assemble
    render reports (Rendered x/20960).
"""
import json
import sys


def scene_starts(props):
    """Per-scene start frames in the composition, mirroring Root.tsx
    calculateMetadata. Returns (brief_starts, starts, total_frames, fps, tf)
    where brief_starts[j] is the start frame of brief scene j, starts[i] is
    the start frame of main scene i, and starts[len(scenes)] == total_frames.
    finish-chunks.py imports this so SRT cues and chapters derive from the
    exact same frame starts as the chunk ranges (single source of truth)."""
    scenes = props["scenes"]
    brief = props.get("briefScenes") or []
    fps = props.get("fps", 30)
    outro = props.get("outroSeconds", 0)
    intro = props.get("introSeconds", 0)
    is_calm = props.get("isCalm", False)
    tf = round(fps * (0.45 if is_calm else 0.2))
    n = len(scenes)
    n_brief = len(brief)
    intro_frames = round(intro * fps)
    has_intro = 1 if intro > 0 else 0
    brief_frames = sum(round(b["durationInSeconds"] * fps) for b in brief)
    brief_seconds = sum(b["durationInSeconds"] for b in brief)
    tcount = n_brief + has_intro + (n if outro > 0 else max(0, n - 1))
    total_seconds = brief_seconds + intro + sum(s["durationInSeconds"] for s in scenes) + outro
    total_frames = max(1, round(total_seconds * fps) - tcount * tf)
    # Brief scenes run first, each sharing one transition with the next
    # segment; brief scene 0 starts at frame 0.
    brief_starts = []
    acc_b = 0
    for j in range(n_brief):
        brief_starts.append(acc_b - j * tf)
        acc_b += round(brief[j]["durationInSeconds"] * fps)
    frames = [round(s["durationInSeconds"] * fps) for s in scenes]
    starts = [0] * (n + 1)
    acc = 0
    for i in range(n):
        starts[i] = brief_frames + intro_frames + acc - (n_brief + has_intro + i) * tf
        acc += frames[i]
    starts[n] = total_frames
    return brief_starts, starts, total_frames, fps, tf


def boundary_hash(props):
    """SHA-256 of ONLY the props fields that affect chunk frame boundaries
    (review #2 B24): fps, introSeconds, outroSeconds, isCalm, and the
    per-scene durations of the brief + main scenes (count included — a
    scene added or removed changes the transition count). A --retts or
    re-assemble that rewrites byte-identical content with different
    formatting, or changes a field that does not move a boundary (music,
    thumbnail, captions), must NOT discard hours of valid chunks the way
    the old full-file sha256 did. Returns the hex digest."""
    import hashlib
    boundary = {
        "fps": props.get("fps", 30),
        "introSeconds": props.get("introSeconds", 0),
        "outroSeconds": props.get("outroSeconds", 0),
        "isCalm": props.get("isCalm", False),
        "briefDurations": [b["durationInSeconds"] for b in (props.get("briefScenes") or [])],
        "sceneDurations": [s["durationInSeconds"] for s in props["scenes"]],
    }
    canonical = json.dumps(boundary, sort_keys=True, separators=(",", ":"))
    return hashlib.sha256(canonical.encode("utf-8")).hexdigest()


def compute_ranges(props_path, scenes_per_chunk=5):
    with open(props_path) as f:
        props = json.load(f)
    scenes = props["scenes"]
    n = len(scenes)
    _, starts, total_frames, _, _ = scene_starts(props)
    bounds = list(range(0, n, scenes_per_chunk))
    if len(bounds) > 1 and bounds[-1] == n - 1:
        bounds.pop()
    ranges = []
    for k, a in enumerate(bounds):
        b = (bounds[k + 1] - 1) if k + 1 < len(bounds) else n - 1
        # The first chunk must start at frame 0 (the composition start), not
        # at the first scene's start: with an intro, scene 1 begins
        # transitionFrames early (frame 99 for a 3.5s intro), so starting the
        # first chunk at starts[0] would drop the intro's first
        # (introSeconds*fps - transitionFrames) frames. Without an intro
        # starts[0] is already 0, so this changes nothing there.
        start = 0 if k == 0 else starts[a]
        ranges.append((f"C{k + 1:02d}", start, starts[b + 1] - 1))
    return total_frames, ranges


if __name__ == "__main__":
    if len(sys.argv) > 2 and sys.argv[1] == "--boundary-hash":
        with open(sys.argv[2]) as f:
            print(boundary_hash(json.load(f)))
        sys.exit(0)
    total, ranges = compute_ranges(sys.argv[1], int(sys.argv[2]) if len(sys.argv) > 2 else 5)
    print(f"TOTAL {total}")
    for name, start, end in ranges:
        print(f"{name} {start} {end}")
