#!/usr/bin/env python3
"""Scene-aligned chunk ranges from a Remotion props file.

Single source of truth for chunk boundaries, shared by
production/render-chunks.sh and production/finish-chunks.py (so the two
can never disagree about which frames a chunk covers).

Formula (Root.tsx calculateMetadata — the one source of truth for total
length, plus the scene-start formula validated empirically for Inanna):
  totalSeconds = introSeconds + sum(scene.durationInSeconds) + outroSeconds
  transitionFrames = round(fps * (isCalm ? 0.45 : 0.2))
  transitionCount = (1 if introSeconds > 0 else 0)
                  + (nScenes if outroSeconds > 0 else nScenes - 1)
  totalFrames = max(1, round(totalSeconds*fps) - transitionCount*transitionFrames)
  sceneStart[i] = round(introSeconds*fps)
                + sum(round(dur*fps) for scenes before i)
                - ((1 if introSeconds > 0 else 0) + i)*transitionFrames
Chunks group N consecutive scenes; a 1-scene tail is merged into the
previous chunk (81 scenes -> 16 chunks, the last holding 6).

The intro term matters: Trendline Now videos open with a 3.5s animated
intro sting (IntroSeconds=3.5, IntroLogoPath). The pre-2026-11-21 version
of this module ignored introSeconds, so for an intro-bearing composition
it computed a total 99 frames short (3.5s*30 - one 6-frame transition)
and scene starts ~3.3s early — chunk renders would have sliced the wrong
frames and the finish gate would have "verified" them against the same
wrong total. Fixed 2026-11-21 (trendline driver build).

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


def compute_ranges(props_path, scenes_per_chunk=5):
    with open(props_path) as f:
        props = json.load(f)
    scenes = props["scenes"]
    fps = props.get("fps", 30)
    outro = props.get("outroSeconds", 0)
    intro = props.get("introSeconds", 0)
    is_calm = props.get("isCalm", False)
    tf = round(fps * (0.45 if is_calm else 0.2))
    n = len(scenes)
    intro_frames = round(intro * fps)
    has_intro = 1 if intro > 0 else 0
    tcount = has_intro + (n if outro > 0 else max(0, n - 1))
    total_seconds = intro + sum(s["durationInSeconds"] for s in scenes) + outro
    total_frames = max(1, round(total_seconds * fps) - tcount * tf)
    frames = [round(s["durationInSeconds"] * fps) for s in scenes]
    starts = [0] * (n + 1)
    acc = 0
    for i in range(n):
        starts[i] = intro_frames + acc - (has_intro + i) * tf
        acc += frames[i]
    starts[n] = total_frames
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
    total, ranges = compute_ranges(sys.argv[1], int(sys.argv[2]) if len(sys.argv) > 2 else 5)
    print(f"TOTAL {total}")
    for name, start, end in ranges:
        print(f"{name} {start} {end}")
