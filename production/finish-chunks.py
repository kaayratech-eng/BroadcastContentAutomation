#!/usr/bin/env python3
"""Finish one orientation of a job from rendered chunks.

Generalizes work/finish-landscape-inanna.py and
work/gigglegarden/finish-giggle-test2.py: replicates exactly what VideoGen's
--assemble does after the Remotion render for that orientation (see
Program.cs RunAssembleAsync), because the render was done in frame-exact
chunks via production/render-chunks.sh instead of one --assemble invocation:
  1. Verify every chunk (ffprobe duration vs the range from
     production/chunk_ranges.py — same module render-chunks.sh uses).
  2. Concatenate the chunks (ffmpeg concat demuxer, stream copy).
  3. Delivery gate + loudness (same as NormalizeLoudnessAsync): video must
     be limited-range yuv420p (re-encode with
     scale=in_range=pc:out_range=tv,format=yuv420p if not); audio loudnorm
     to -14 LUFS (TP -1.5, LRA 11), AAC 128k, -ar 48000 (loudnorm resamples
     to 96 kHz if -ar is omitted — the Medusa bug).
  4. Write the SRT. Cues come from the PROPS scenes (captionText +
     durationInSeconds), not script.json: for a trimmed long-form teaser
     the props hold the 8 rendered scenes while script.json holds all 81,
     and the old giggle finish script's script.json SRT would write cues
     past the end of the video.
  5. Landscape only: build the 1280x720 thumbnail, preferring
     script.Scenes[0].LandscapeImagePath (same as --assemble), then
     scene00-art.png / scene00-art-landscape.png, then a frame extracted
     from the finished landscape.
  6. Write the sidecar JSON (Approved False) exactly as --assemble does:
     vertical title is the plain script title (Text.Fit to 100, no
     " #Shorts" suffix — YouTube auto-detects Shorts from the vertical
     format), description is the ReelsCaption rebuilt with the
     canonical hashtags from the long-form Description, plus the
     #Shorts marker; caption overrides for IG/FB/TikTok use the
     canonical captions; landscape title as-is, description + chapters
     (long-form only, <=12, first 0:00, first-sentence labels) +
     CommentPrompt, thumbnail path. Targets/MadeForKids come from
     production/config.json.
  7. Verify the final file (ffprobe dims/duration/pix_fmt/sample rate,
     loudness measurement).

Usage:
  finish-chunks.py --job <jobdir> --orientation vertical|landscape [--dry-run] [--check-only]
--dry-run prints what it would do and writes nothing. --check-only verifies
the chunks and (if present) the final file, and writes nothing; exit 0
means the orientation is finished and valid. --check-final-only verifies
only the final file + sidecar + SRT (no chunks needed — the case where
--assemble itself rendered and finished the orientation). --check-chunks-only
verifies only the chunks (the case where the final is missing but the chunks
are ready to be finished). Exits non-zero if the checked thing is missing
or invalid.
"""
import argparse
from datetime import datetime, timedelta, timezone
import json
import os
import re
import shutil
import signal
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from chunk_ranges import compute_ranges, scene_starts  # noqa: E402


def sha256_file(path):
    import hashlib
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            h.update(block)
    return h.hexdigest()


def parse_srt(path):
    """Parse an SRT file, returning a list of (start_s, end_s, text).
    Raises ValueError if any cue block is malformed (bad index, bad
    timestamp line, or empty text). Used by check_final so a truncated
    SRT written by a killed finish cannot pass as valid."""
    import re
    with open(path, encoding="utf-8") as f:
        text = f.read()
    blocks = [b.strip() for b in re.split(r"\n\s*\n", text.strip()) if b.strip()]
    cues = []
    ts_re = re.compile(r"^(\d+):(\d+):(\d+),(\d+) --> (\d+):(\d+):(\d+),(\d+)$")
    for b in blocks:
        lines = b.splitlines()
        if len(lines) < 3:
            raise ValueError(f"malformed cue block: {b[:80]!r}")
        try:
            int(lines[0].strip())
        except ValueError:
            raise ValueError(f"cue index is not an integer: {lines[0]!r}")
        m = ts_re.match(lines[1].strip())
        if not m:
            raise ValueError(f"bad timestamp line: {lines[1]!r}")
        def to_s(h, mi, s, ms):
            return int(h) * 3600 + int(mi) * 60 + int(s) + int(ms) / 1000.0
        start = to_s(*m.groups()[:4])
        end = to_s(*m.groups()[4:])
        if end <= start:
            raise ValueError(f"cue ends before it starts: {lines[1]!r}")
        cues.append((start, end, "\n".join(lines[2:])))
    return cues

CONFIG = json.load(open(os.path.join(HERE, "config.json")))
POST_NARRATION_HOLD = CONFIG["queue_policy"]["post_narration_hold_seconds"]
VIDEOS_DIR = CONFIG["videos_dir"]


def run(cmd, check=True):
    r = subprocess.run(cmd, capture_output=True, text=True)
    if check and r.returncode != 0:
        print("FAILED:", " ".join(cmd), file=sys.stderr)
        print(r.stdout[-2000:], file=sys.stderr)
        print(r.stderr[-2000:], file=sys.stderr)
        sys.exit(1)
    return r


def probe_duration(path):
    r = run(["ffprobe", "-v", "error", "-show_entries", "format=duration",
             "-of", "csv=p=0", path])
    return float(r.stdout.strip())


def probe_video_format(path):
    """Return a dict {pix_fmt, color_range, width, height, codec_name}.
    Uses -of json: the csv writer emits stream fields in ffprobe's
    internal order (not the requested order), so positional parsing
    mislabels them."""
    r = run(["ffprobe", "-v", "error", "-select_streams", "v:0",
             "-show_entries", "stream=pix_fmt,color_range,width,height,codec_name",
             "-of", "json", path])
    streams = json.loads(r.stdout).get("streams", [])
    return streams[0] if streams else {}


def probe_audio_rate(path):
    """Return a dict {sample_rate, channels, codec_name} for stream a:0."""
    r = run(["ffprobe", "-v", "error", "-select_streams", "a:0",
             "-show_entries", "stream=sample_rate,channels,codec_name",
             "-of", "json", path])
    streams = json.loads(r.stdout).get("streams", [])
    return streams[0] if streams else {}


def fmt_srt(total_seconds):
    total_ms = int(total_seconds * 1000)
    h, rem = divmod(total_ms, 3600000)
    m, rem = divmod(rem, 60000)
    s, ms = divmod(rem, 1000)
    return f"{h:02d}:{m:02d}:{s:02d},{ms:03d}"


def slug(value, max_length=50):
    """Text.Slug (Shared/Text.cs): ASCII alphanumerics, runs of anything else
    collapse to one '-', trimmed, truncated to 50 chars."""
    if not value or not value.strip():
        return "video"
    out = []
    last_dash = False
    for ch in value.strip().lower():
        if ("a" <= ch <= "z") or ("0" <= ch <= "9"):
            out.append(ch)
            last_dash = False
        elif not last_dash and out:
            out.append("-")
            last_dash = True
    s = "".join(out).strip("-")
    if len(s) > max_length:
        s = s[:max_length].rstrip("-")
    return s or "video"


def utf16_len(s):
    return len(s.encode("utf-16-le")) // 2


def fit(value, max_length, ellipsis="…"):
    """Text.Fit (Shared/Text.cs): truncate to maxLength UTF-16 code units
    without splitting a surrogate pair or (preferably) a word."""
    if not value or utf16_len(value) <= max_length:
        return value or ""
    if max_length <= len(ellipsis):
        return value[:max_length]
    cut = max_length - len(ellipsis)
    # Don't split a surrogate pair (astral char = 2 UTF-16 units).
    while cut > 0 and utf16_len(value[:cut]) > cut:
        cut -= 1
    # Prefer a word boundary in the back two-thirds.
    last_space = value.rfind(" ", 0, max(0, cut - 1))
    if last_space > cut * 2 // 3:
        cut = last_space
    return value[:cut].rstrip() + ellipsis


def trim_tags(tags):
    kept, total, seen = [], 0, set()
    for tag in tags:
        low = tag.lower()
        if low in seen:
            continue
        seen.add(low)
        cost = len(tag) + 1
        if kept and total + cost > 500:
            break
        kept.append(tag)
        total += cost
    return kept


# Chapter rules (fix #8 port, mirroring VideoGen/Program.cs
# BuildChapters / ChapterLabel): at most 12 chapters, the first at
# 0:00, labels are the first complete sentence of the narration.
ABBREVIATIONS = {
    "mr", "mrs", "ms", "dr", "st", "jr", "sr", "vs", "etc", "e.g", "i.e",
    "u.s", "u.k", "d.c", "a.m", "p.m",
    # News-common titles (review #2 R5): "Sen. Cruz warned..." must not
    # be cut to just "Sen."
    "sen", "rep", "gov", "gen", "prof", "col",
}
MAX_CHAPTERS = 12
CHAPTER_MIN_GAP_SECONDS = 10.0
CHAPTER_LABEL_MAX_LENGTH = 200


def truncate_at_word(text, max_length):
    """Cut at a word boundary at <= max_length, never mid-word and never
    with an ellipsis — a safety net for a pathological first sentence."""
    if len(text) <= max_length:
        return text
    cut = text.rfind(" ", 0, max_length)
    return (text[:cut] if cut > 0 else text[:max_length]).rstrip()


def chapter_label(narration):
    """The first complete sentence of a scene's narration. A sentence
    ends at . ! or ? followed by a space and an uppercase letter (or
    the end of the string, optionally past a closing quote) — the word
    before the punctuation must not be an abbreviation, so "U.S." and
    "Mr." do not cut the label short."""
    text = (narration or "").strip()
    if not text:
        return "Untitled"
    for i, c in enumerate(text):
        if c not in ".!?":
            continue
        is_end = i == len(text) - 1
        # A closing quote right after the punctuation still ends the
        # sentence ("...works." then the end, or "...works." "Next...").
        k = i + 1
        if k < len(text) and text[k] in "\"'”’":
            k += 1
            if k == len(text):
                is_end = True
        next_is_space_upper = (
            k + 1 < len(text) and text[k] == " " and text[k + 1].isupper()
        )
        if not is_end and not next_is_space_upper:
            continue
        # Word immediately before this punctuation.
        word_start = text.rfind(" ", 0, i) + 1
        word = text[word_start:i]
        if len(word) == 1 and word.isupper():
            continue  # single-letter abbrev ("U.S.")
        if word.lower() in ABBREVIATIONS:
            continue
        return truncate_at_word(text[:k].strip(), CHAPTER_LABEL_MAX_LENGTH)
    return truncate_at_word(text, CHAPTER_LABEL_MAX_LENGTH)


def build_chapters(scenes, starts, fps, brief_scenes=None):
    """Chapter list from script scenes, mirroring VideoGen/Program.cs
    BuildChapters (fix #8). The first chapter is at 0:00 — it covers
    the brief teaser (+ intro sting when there is no brief — the sting is
    skipped whenever a brief plays) and is labelled from the brief's
    opening line, or "Intro" when there is no brief (YouTube ignores a
    chapter list whose first timestamp is not 0:00). Then one chapter
    per VisualGroup boundary, capped at MAX_CHAPTERS total: when there
    are more boundaries than the remaining slots, evenly-spaced ones
    are kept in TIME (divide the span from the first to the last
    boundary into equal slots and take the boundary nearest each slot),
    so the chapters still span the whole video. Every chapter must be
    at least 10s after the previous one, or YouTube ignores the whole
    list. Chapter times come from the same per-scene frame starts
    chunk_ranges.py uses (starts[i]/fps), so they match the rendered
    video. `brief_scenes` are the script's BriefScenes (dicts with
    "Narration"); `starts` is the per-scene start-frame list from
    chunk_ranges.scene_starts (same order as `scenes`, with
    starts[len(scenes)] == total_frames)."""
    chapters = []
    brief = brief_scenes or []
    intro_label = chapter_label(brief[0]["Narration"]) if brief else "Intro"
    chapters.append(f"0:00 - {intro_label}")

    # Visual-group boundaries in the main scenes.
    boundaries = []
    last_group = None
    for i, sc in enumerate(scenes):
        if sc.get("VisualGroup") != last_group:
            cursor = starts[i] / fps if i < len(starts) else 0.0
            boundaries.append((cursor, sc["Narration"]))
            last_group = sc.get("VisualGroup")

    # Cap the total at MAX_CHAPTERS. When there are more boundaries than
    # the remaining slots, keep ones time-spaced (evenly spaced in TIME,
    # not by index): time spacing keeps chapters comfortably apart
    # instead of bunching two group changes that happen to fall a few
    # seconds apart.
    remaining = MAX_CHAPTERS - len(chapters)
    if len(boundaries) <= remaining:
        selected = boundaries
    else:
        selected = []
        first_time = boundaries[0][0]
        last_time = boundaries[-1][0]
        span = last_time - first_time
        used = set()
        for k in range(remaining):
            target = first_time + span * k / (remaining - 1) if remaining > 1 else first_time
            best, best_dist = -1, float("inf")
            for j, (t, _) in enumerate(boundaries):
                if j in used:
                    continue
                dist = abs(t - target)
                if dist < best_dist:
                    best_dist, best = dist, j
            if best >= 0:
                used.add(best)
                selected.append(boundaries[best])
        selected.sort(key=lambda b: b[0])

    # YouTube requires every chapter to be at least 10 seconds long.
    # Drop a boundary that lands less than 10s after the previously
    # kept chapter (the 0:00 chapter is at 0.0). The time-based
    # selection above makes this a no-op in practice; it is a safety
    # net for a pathological script whose groups all bunch together.
    kept = []
    last_kept = 0.0
    for start_seconds, narration in selected:
        if start_seconds - last_kept < CHAPTER_MIN_GAP_SECONDS:
            continue
        kept.append((start_seconds, narration))
        last_kept = start_seconds
    # Re-fill (R5): the filter above can drop the final boundary — or the
    # first selected one — and leave the list short with no last-beat
    # chapter. The re-fill pool is every boundary that is not already kept
    # (a filter-dropped boundary can never qualify — it sits <10s from a
    # kept chapter by construction — but a boundary time-spacing did NOT
    # select can): walk them in time order and add back any that sits
    # >= 10s from the 0:00 chapter and from every chapter already kept,
    # until the cap is reached.
    refill_candidates = sorted(
        [b for b in boundaries if b not in kept], key=lambda b: b[0])
    for start_seconds, narration in refill_candidates:
        if len(chapters) + len(kept) >= MAX_CHAPTERS:
            break
        if start_seconds < CHAPTER_MIN_GAP_SECONDS:
            continue
        if all(abs(k[0] - start_seconds) >= CHAPTER_MIN_GAP_SECONDS for k in kept):
            kept.append((start_seconds, narration))
    for start_seconds, narration in sorted(kept, key=lambda b: b[0]):
        chapters.append(
            f"{int(start_seconds // 60)}:{int(start_seconds % 60):02d} - {chapter_label(narration)}")
    return chapters


def extract_hashtags(text):
    """All #word tokens in a string, deduped case-insensitively keeping
    first occurrence — mirrors Program.cs ExtractHashtags
    (#[letter/digit/underscore]+)."""
    seen, tags = set(), []
    for m in re.findall(r"#[\w]+", text or ""):
        if m.lower() not in seen:
            seen.add(m.lower())
            tags.append(m)
    return tags


def canonical_hashtags(description):
    """The long-form Description's hashtag line is the single source of
    truth for a video's hashtags: the description's line 2 (channel first
    then topic). Prefer line 2 outright (R6) — a keyword-stuffing line
    elsewhere used to win the max-count contest and beat the intended
    line. Fall back to the max-count line only when line 2 carries no
    hashtags. Mirrors Program.cs CanonicalHashtags."""
    lines = (description or "").split("\n")
    if len(lines) > 1:
        line2 = extract_hashtags(lines[1])
        if line2:
            return line2
    best = []
    for line in lines:
        tags = extract_hashtags(line)
        if len(tags) > len(best):
            best = tags
    return best


def with_canonical_hashtags(caption, canonical, max_hashtags, max_length):
    """Rebuild a caption with the canonical hashtags: strip only the
    TRAILING hashtags the model put in, append up to max_hashtags
    canonical ones (the first ones are the most specific — channel, then
    topic), and fit the whole thing to max_length. The text is fit first
    and the hashtags appended last, so the hashtags are never the part
    that gets cut. When there are no canonical hashtags, the caption is
    left exactly as the model wrote it. Inline hashtags are part of the
    caption text and are kept ("Watch #GTA6 gameplay now" keeps #GTA6) —
    the old regex stripped every hashtag anywhere in the caption (R6).
    Mirrors Program.cs WithCanonicalHashtags."""
    if not canonical:
        return fit(caption or "", max_length)
    text = re.sub(r"(\s+#[\w]+)+\s*$", "", caption or "")
    text = re.sub(r"\s{2,}", " ", text).strip()
    tags = canonical[:max_hashtags]
    if not tags:
        return fit(text, max_length)
    tag_str = " ".join(tags)
    room = max_length - len(tag_str) - 1
    # Floor guard (R6): if the canonical tags leave too little room for
    # any caption text, keep the text and drop the tags. The old code
    # clamped room to 0 and returned hashtags only, wiping the caption.
    min_caption_text_room = 20
    if room < min_caption_text_room:
        return fit(text, max_length)
    fitted = fit(text, room, ellipsis="")
    return f"{fitted} {tag_str}".strip()


# Second Short (fix #5 rework, 2026-11-27): payoff cut ASSEMBLED from
# the job's own assets — NOT cut from the landscape final.
#
# CORRECTION (verified by the Drake one-off build, 2026-11-27 18:35 EST):
# there is NO full-length vertical master in this pipeline. The vertical
# render only ever covers the main Short itself (76.1s, scenes 0-6 on
# Drake); chunks-vertical/vertical-joined.mp4 IS the Short, not a 458s
# video. The previous implementation cut the payoff from the LANDSCAPE
# final and verticalized it with a blurred pillarbox — that shipped
# landscape-framed, caption-baked footage in a vertical Short. The cut
# is now assembled the way the Drake one-off did it (see
# ~/workspace/tmp/drake-short2-fixed/build-short2.py, read-only):
# per-scene segments (zoompan on the scene's portrait art + its
# narration mp3), concatenated, plus the outro/end card cut from the
# Short itself (the vertical final being finished), with the music bed
# mixed under the scenes and ASS captions burned in. No re-render, no
# new images, no new TTS. The driver finishes vertical BEFORE
# landscape again (the landscape-final dependency is gone).
SHORT2_MAX_SECONDS = 59.5
SHORT2_MIN_SECONDS = 30
SHORT2_HOLD_HOURS = 12  # Rishi 2026-11-27: PublishAfterUtc = finish + 12h,
# a FALLBACK hold only. Cadence (Rishi 2026-11-27 18:19 EST): uploads
# alternate every 12h — long-form + main Short together, 2nd Short 12h
# later — so the GOVERNING hold is measured from the main Short going
# live, and the uploader anchors it off the sibling main Short's YouTube
# publication time when that exists. The finish can only stamp its own
# time, so finish + 12h stands in until the uploader re-anchors it.


def _is_closing_cta(narration):
    """True when a scene's narration is only a follow/subscribe prompt or
    the closing comment question — a CTA, not a story beat. The payoff
    window must open on a hook and end on a story beat, never on the CTA:
    a cut that ends "Follow @spotlightnowofficial..." / "What is your
    favourite moment? Tell us in the comments below." is an outro, not a
    payoff. Detects the follow prompt by its opening words and the
    closing question by a "?" that sends the viewer to the comments."""
    text = (narration or "").strip()
    if not text:
        return False
    low = text.lower()
    if low.startswith("follow @"):
        return True
    if "?" in text and "comment" in low:
        return True
    return False


def select_payoff_scenes(scene_durations, outro_seconds, main_last_scene,
                         narrations=None):
    """The payoff window for the 2nd Short: a contiguous run of scenes
    near the END of the video, strictly after the main Short's last
    scene (zero overlap — the same predicate as
    SecondShortOverlapsMainShort), whose total (sum of the scenes'
    durationInSeconds + outroSeconds) is in [30, 59.5]s.
    Returns (scene_indices, None), or (None, reason) when no window
    satisfies all constraints. Pure: no ffmpeg, no files.
    `scene_durations` is the per-scene durationInSeconds list from the
    landscape props, in script order (the landscape props hold ALL
    scenes); `outro_seconds` is the outroSeconds from the VERTICAL
    props (the Short's own outro, which closes the cut);
    `main_last_scene` is (number of scenes in the vertical props) - 1.
    `narrations` (optional, script order) lets the rule skip trailing
    closing/CTA scenes: the walk starts at the last NON-CTA scene, so
    the window opens on a hook and ends on a story beat instead of
    ending on the follow prompt / closing question. Without narrations
    the walk starts at the last scene as-is.
    Rule, in order: (1) skip trailing closing/CTA scenes; (2) walk
    backwards from there, accumulating scenes while the total stays
    <= 59.5s — the walk prefers the payoff stretch NEAREST THE END;
    (3) the window must total >= 30s, else there is no cut (a later
    boundary can never help: scenes only add time, and the walk
    already took every scene back to the main Short)."""
    if not scene_durations:
        return (None, "no scenes in the landscape props")
    if outro_seconds is None or outro_seconds <= 0:
        return (None, f"outro is {outro_seconds}s — no outro to close the cut with")
    n = len(scene_durations)
    if main_last_scene >= n - 1:
        return (None, f"main Short already covers scenes 0..{main_last_scene} of {n} — no scene is left for a payoff cut")
    end = n - 1
    if narrations is not None:
        while end > main_last_scene and end < len(narrations) and _is_closing_cta(narrations[end]):
            end -= 1
        if end <= main_last_scene:
            return (None, "every scene after the main Short is a closing/CTA scene — no story beat left for a payoff cut")
    total = float(outro_seconds)
    chosen = []
    i = end
    while i > main_last_scene:
        d = float(scene_durations[i])
        if total + d > SHORT2_MAX_SECONDS:
            break
        chosen.append(i)
        total += d
        i -= 1
    if not chosen:
        return (None, f"no payoff window: scene {end} alone ({float(scene_durations[end]):.2f}s) + outro ({float(outro_seconds):.2f}s) already exceeds {SHORT2_MAX_SECONDS}s")
    chosen.reverse()
    if total < SHORT2_MIN_SECONDS:
        return (None, f"payoff window scenes {chosen[0]}..{chosen[-1]} is only {total:.2f}s (<{SHORT2_MIN_SECONDS}s) even taking every scene after the main Short")
    return (chosen, None)


def scene_composition_durations(starts, fps, outro_seconds):
    """Per-scene COMPOSITION durations in seconds from chunk_ranges'
    scene_starts output: starts[i+1] - starts[i] (a scene's slot in the
    composition is its duration minus one shared transition, tf/fps).
    The last scene runs to total_frames/fps minus the outro: its slot
    is starts[n] - starts[n-1] less the outro frames
    (round(outro_seconds*fps)), matching the SRT writer's outro_start.
    `starts` has n+1 entries with starts[n] == total_frames. Returns a
    list of n durations, rounded to 3 decimals (the ffmpeg -t values).
    Where the starts math is ambiguous, the props' durationInSeconds
    is the fallback — callers use these durations for the segment
    encodes and log them."""
    n = len(starts) - 1
    if n <= 0:
        return []
    outro_frames = round(float(outro_seconds or 0) * fps)
    durations = []
    for i in range(n):
        if i == n - 1:
            d = (starts[n] - starts[i] - outro_frames) / fps
        else:
            d = (starts[i + 1] - starts[i]) / fps
        durations.append(round(d, 3))
    return durations


def missing_short2_assets(job, scene_indices):
    """Scene art / narration files missing — or empty — for the given
    scenes. Returns the list of missing paths ([] means the cut can be
    built with no holes). A cut is never shipped with a hole in it: one
    missing file skips the whole 2nd Short."""
    missing = []
    for i in scene_indices:
        for name in (f"scene{i:02d}-art.png", f"scene{i:02d}.mp3"):
            path = os.path.join(job, name)
            if not os.path.exists(path) or os.path.getsize(path) == 0:
                missing.append(path)
    return missing


def find_music_bed(job, *candidates):
    """Resolve the music-bed path from the props: an absolute path, a
    path relative to the job dir, or a bare filename in the job dir
    (the props carry "tense-newsroom-02.mp3", which lives in the job
    dir). Returns the path, or None when no candidate exists on disk."""
    for cand in candidates:
        if not cand:
            continue
        if os.path.isabs(cand) and os.path.exists(cand):
            return cand
        p = os.path.join(job, cand)
        if os.path.exists(p):
            return p
        if os.path.exists(cand):
            return os.path.abspath(cand)
    return None


def _ass_time(t):
    h = int(t // 3600)
    m = int((t % 3600) // 60)
    s = t % 60
    return f"{h}:{m:02d}:{s:05.2f}"


def build_short2_ass(narrations, durations):
    """ASS captions for the assembled 2nd Short, exactly like the Drake
    one-off: cumulative word-reveal, 4-word chunks, timed evenly across
    each scene's window. `narrations`/`durations` are aligned lists for
    the payoff scenes; windows are cumulative from 0. Captions cover
    the scenes portion only — the outro carries its own on-screen card.
    DejaVu Sans 52, bottom-center, as in the one-off. Pure: no ffmpeg,
    no files."""
    events = []
    offset = 0.0
    for narration, dur in zip(narrations, durations):
        words = (narration or "").split()
        if words:
            chunks = [words[j:j + 4] for j in range(0, len(words), 4)]
            count = len(chunks)
            for k in range(count):
                start = offset + (k / count) * dur
                end = offset + ((k + 1) / count) * dur if k < count - 1 else offset + dur
                shown = " ".join(words[:(k + 1) * 4])
                events.append((start, end, shown))
        offset += dur
    ass = """[Script Info]
ScriptType: v4.00+
PlayResX: 1080
PlayResY: 1920
ScaledBorderAndShadow: yes

[V4+ Styles]
Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
Style: Cap,DejaVu Sans,52,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,-1,0,0,0,100,100,0,0,1,2.5,0,2,60,60,170,1

[Events]
Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
"""
    for start, end, text in events:
        ass += f"Dialogue: 0,{_ass_time(start)},{_ass_time(end)},Cap,,0,0,0,,{text}\n"
    return ass


def assemble_second_short(job, job_id, scene_indices, comp_durations,
                          narrations, outro_seconds, short_duration,
                          short_path, music_path, music_volume, fps,
                          short2_path):
    """Assemble the 2nd Short from the job's own assets (the Drake
    one-off's stages, in production form). Returns
    (duration_seconds, None) on success, (None, reason) on failure.
    Bonus-only: never raises, every ffmpeg step is check=False, and a
    failed cut is discarded — the caller logs the reason with the job
    id and ships the main Short only.
    Stages: A — per-scene segments (zoompan on sceneNN-art.png +
    sceneNN.mp3, duration = the scene's composition duration); B —
    concat the scene segments (stream copy); C — outro cut from the
    Short itself (`short_path`, the vertical final being finished) at
    (short_duration - outro_seconds), re-encoded; D — concat scenes +
    outro; E/F — mix the music bed under the SCENES portion only
    (volume + fade-out in the last 2s of the scenes; no bed when the
    file is missing), burn the ASS captions, final encode libx264
    medium crf 20 yuv420p AAC, and loudness-normalize the final audio
    (loudnorm I=-14:TP=-1.5:LRA=11, linear=true) so the cut matches
    the pipeline's -14 LUFS target (the one-off did not do this).
    All intermediates go to the system temp dir (TMPDIR), never the
    watch dir; the final cut lands at `short2_path`. No SRT for the
    cut (burned-in captions are already in the picture)."""
    tmpdir = tempfile.mkdtemp(prefix="short2-")
    try:
        # Stage A: per-scene segments.
        seg_files = []
        for idx, dur in zip(scene_indices, comp_durations):
            if dur is None or dur <= 0:
                return (None, f"scene {idx} has no usable composition duration ({dur})")
            seg = os.path.join(tmpdir, f"seg{idx:02d}.mp4")
            seg_files.append(seg)
            frames = int(round(dur * fps))
            r = run(["ffmpeg", "-v", "error", "-y",
                     "-loop", "1", "-framerate", str(fps), "-i",
                     os.path.join(job, f"scene{idx:02d}-art.png"),
                     "-i", os.path.join(job, f"scene{idx:02d}.mp3"),
                     "-filter_complex",
                     f"[0:v]zoompan=z='1+0.10*on/{frames}':x='iw/2-(iw/zoom/2)':y='ih/2-(ih/zoom/2)':d=1:s=1080x1920:fps={fps},format=yuv420p[v];"
                     f"[1:a]apad,atrim=0:{dur}[a]",
                     "-map", "[v]", "-map", "[a]", "-t", str(dur),
                     "-c:v", "libx264", "-preset", "medium", "-crf", "20",
                     "-c:a", "aac", "-b:a", "160k", seg],
                    check=False)
            if r.returncode != 0:
                return (None, f"scene {idx} segment ffmpeg failed (exit {r.returncode})")
        # Stage B: concat the scene segments (stream copy).
        scenes_list = os.path.join(tmpdir, "concat-scenes.txt")
        atomic_write(scenes_list, "".join(f"file '{s}'\n" for s in seg_files))
        scenes_mp4 = os.path.join(tmpdir, "scenes.mp4")
        r = run(["ffmpeg", "-v", "error", "-y", "-f", "concat", "-safe", "0",
                 "-i", scenes_list, "-c", "copy", scenes_mp4], check=False)
        if r.returncode != 0:
            return (None, f"scene concat ffmpeg failed (exit {r.returncode})")
        # Stage C: outro cut from the Short itself (the vertical final).
        outro_start = short_duration - outro_seconds
        if outro_start < 0:
            return (None, f"outro start would be {outro_start:.2f}s (Short {short_duration:.2f}s, outro {outro_seconds:.2f}s)")
        outro_mp4 = os.path.join(tmpdir, "outro.mp4")
        r = run(["ffmpeg", "-v", "error", "-y",
                 "-ss", f"{outro_start:.2f}", "-i", short_path,
                 "-t", f"{outro_seconds:.2f}",
                 "-c:v", "libx264", "-preset", "medium", "-crf", "20",
                 "-color_range", "tv", "-colorspace", "bt709",
                 "-color_primaries", "bt709", "-color_trc", "bt709",
                 "-c:a", "aac", "-b:a", "160k", outro_mp4], check=False)
        if r.returncode != 0:
            return (None, f"outro cut ffmpeg failed (exit {r.returncode})")
        # Stage D: concat scenes + outro (stream copy).
        all_list = os.path.join(tmpdir, "concat-all.txt")
        atomic_write(all_list, f"file '{scenes_mp4}'\nfile '{outro_mp4}'\n")
        base_mp4 = os.path.join(tmpdir, "base.mp4")
        r = run(["ffmpeg", "-v", "error", "-y", "-f", "concat", "-safe", "0",
                 "-i", all_list, "-c", "copy", base_mp4], check=False)
        if r.returncode != 0:
            return (None, f"scenes+outro concat ffmpeg failed (exit {r.returncode})")
        # Stages E/F: music bed under the scenes + ASS captions + final
        # encode with loudness normalization to -14 LUFS.
        scenes_dur = round(sum(comp_durations), 3)
        ass_path = os.path.join(tmpdir, "captions.ass")
        atomic_write(ass_path, build_short2_ass(narrations, comp_durations))
        cmd = ["ffmpeg", "-v", "error", "-y", "-i", base_mp4]
        if music_path and os.path.exists(music_path):
            vol = music_volume if music_volume else 0.116
            cmd += ["-i", music_path]
            audio_filter = (
                f"[1:a]atrim=0:{scenes_dur},volume={vol},"
                f"afade=t=out:st={max(scenes_dur - 2, 0)}:d=2[bed];"
                f"[0:a][bed]amix=inputs=2:duration=first:normalize=0[mixed];"
                f"[mixed]loudnorm=I=-14:TP=-1.5:LRA=11:linear=true[aout]")
            print(f"  second Short music bed: {music_path} at volume {vol} under the scenes ({scenes_dur:.2f}s, fading out in the last 2s)")
        else:
            audio_filter = "[0:a]loudnorm=I=-14:TP=-1.5:LRA=11:linear=true[aout]"
            print("  second Short music bed: none (bed file missing — shipping narration only)")
        cmd += ["-filter_complex", audio_filter,
                "-map", "0:v", "-map", "[aout]",
                "-vf", f"subtitles={ass_path}",
                "-c:v", "libx264", "-preset", "medium", "-crf", "20",
                "-color_range", "tv", "-colorspace", "bt709",
                "-color_primaries", "bt709", "-color_trc", "bt709",
                "-c:a", "aac", "-b:a", "128k", "-ar", "48000", short2_path]
        r = run(cmd, check=False)
        if r.returncode != 0:
            if os.path.exists(short2_path):
                os.remove(short2_path)
            return (None, f"final encode ffmpeg failed (exit {r.returncode})")
        probe = run(["ffprobe", "-v", "error", "-show_entries",
                     "format=duration", "-of", "csv=p=0", short2_path],
                    check=False)
        try:
            cut_dur = float(probe.stdout.strip()) if probe.returncode == 0 else None
        except ValueError:
            cut_dur = None
        if cut_dur is None or not (SHORT2_MIN_SECONDS <= cut_dur <= 60.5):
            if os.path.exists(short2_path):
                os.remove(short2_path)
            return (None, f"assembled cut probed at {cut_dur}s (outside {SHORT2_MIN_SECONDS}..60.5s)")
        return (cut_dur, None)
    except Exception as ex:
        if os.path.exists(short2_path):
            try:
                os.remove(short2_path)
            except OSError:
                pass
        return (None, f"assembly raised {type(ex).__name__}: {ex}")
    finally:
        shutil.rmtree(tmpdir, ignore_errors=True)




def make_short2_thumbnail(job_id, final, short2_path):
    """Thumbnail still for the assembled cut: a frame from the cut at
    ~1s. It must differ from the main Short's first frame — an identical
    frame would repeat the duplicate-thumbnail defect. Retry at 3s when
    identical. Bonus only: a missing thumb never fails the finish.
    Returns the thumbnail path, or None."""
    thumb_path = os.path.splitext(short2_path)[0] + ".thumb.jpg"
    main_frame_tmp = os.path.join(
        tempfile.gettempdir(),
        f"{os.path.basename(short2_path)}.mainframe-{os.getpid()}.jpg")
    try:
        grab_main = run(["ffmpeg", "-y", "-ss", "1", "-i", final,
                         "-frames:v", "1", "-q:v", "2", main_frame_tmp],
                        check=False)
        grab_cut = run(["ffmpeg", "-y", "-ss", "1", "-i", short2_path,
                        "-frames:v", "1", "-q:v", "2", thumb_path],
                       check=False)
        if (grab_main.returncode != 0 or grab_cut.returncode != 0
                or not os.path.exists(thumb_path)
                or not os.path.exists(main_frame_tmp)):
            print(f"  WARNING [{job_id}]: second Short thumbnail frame extraction failed — continuing without a thumbnail still.", file=sys.stderr)
            if os.path.exists(thumb_path):
                os.remove(thumb_path)
            return None
        if sha256_file(main_frame_tmp) == sha256_file(thumb_path):
            print("  second Short thumbnail at 1s is identical to the main Short's first frame — retrying at 3s")
            grab_retry = run(["ffmpeg", "-y", "-ss", "3", "-i", short2_path,
                              "-frames:v", "1", "-q:v", "2", thumb_path],
                             check=False)
            if grab_retry.returncode != 0 or not os.path.exists(thumb_path):
                print(f"  WARNING [{job_id}]: second Short thumbnail retry at 3s failed — continuing without a thumbnail still.", file=sys.stderr)
                if os.path.exists(thumb_path):
                    os.remove(thumb_path)
                return None
            print(f"  second Short thumbnail: {thumb_path} (frame at 3s)")
        else:
            print(f"  second Short thumbnail: {thumb_path} (frame at 1s, differs from main Short first frame)")
        return thumb_path
    except Exception as ex:
        print(f"  WARNING [{job_id}]: second Short thumbnail failed ({type(ex).__name__}: {ex}) — continuing without a thumbnail still.", file=sys.stderr)
        if os.path.exists(thumb_path):
            try:
                os.remove(thumb_path)
            except OSError:
                pass
        return None
    finally:
        if os.path.exists(main_frame_tmp):
            try:
                os.remove(main_frame_tmp)
            except OSError:
                pass


def first_sentence_title(narration):
    """The first sentence of a narration, as a title: the text up to and
    including the first ". " / "? " / "! " boundary, or the whole text
    when it is a single sentence. A trailing "." is dropped (a title is
    not a sentence); a trailing "?" / "!" is kept. Truncated to 100
    chars (the YouTube title limit). Empty when the narration is null,
    blank, or yields no words. Mirrors VideoGen/ShortRules.cs
    FirstSentence so both sides of the pipeline derive the same title."""
    text = (narration or "").strip()
    if not text:
        return ""
    idx = -1
    for boundary in (". ", "? ", "! "):
        found = text.find(boundary)
        if found >= 0 and (idx < 0 or found < idx):
            idx = found
    sentence = text[:idx + 1].strip() if idx >= 0 else text
    if sentence.endswith("."):
        sentence = sentence[:-1].rstrip()
    if len(sentence) > 100:
        sentence = sentence[:100].rstrip()
    return sentence


def pick_second_short_title(main_title, variants, opening_narration):
    """The 2nd Short's own title. Variant 3 = variants[2] when present,
    non-empty, and different from the main title (case-insensitive,
    trimmed) — used as-is, fallback False. Otherwise a title derived
    from the opening narration's first sentence (first_sentence_title)
    — fallback False. Only when there is no usable variant AND no
    usable narration does it fall back to the main title, with
    fallback True and a loud stderr warning: a 2nd Short wearing the
    main Short's title is the defect this rule exists to prevent.
    Mirrors VideoGen/ShortRules.cs PickSecondShortTitle.
    Returns (title, used_fallback)."""
    main = (main_title or "").strip()
    variants = variants or []
    if len(variants) > 2:
        candidate = (variants[2] or "").strip()
        if candidate and candidate.lower() != main.lower():
            return (candidate, False)
    derived = first_sentence_title(opening_narration)
    if derived:
        return (derived, False)
    print(f"  WARNING: no usable title variant or opening narration for second Short — reusing main title {main_title!r} (Short2TitleFallback=true)", file=sys.stderr)
    return (main_title, True)


def atomic_write(path, text, encoding="utf-8"):
    """Write via temp file + os.replace so a kill mid-write never leaves a
    truncated file at the real path (a partial sidecar/SRT/concat-list used
    to pass the existence-only checks)."""
    tmp = path + ".tmp"
    with open(tmp, "w", encoding=encoding) as f:
        f.write(text)
    os.replace(tmp, path)


def dump_sidecar(path, sidecar):
    sidecar = {k: v for k, v in sidecar.items() if v is not None}
    text = json.dumps(sidecar, indent=2, ensure_ascii=True)
    # Match System.Text.Json's default escaping (& and ' as \u0026 / \u0027).
    text = text.replace("&", "\\u0026").replace("'", "\\u0027")
    atomic_write(path, text)


# L9 — interruption tracking. A VM reboot mid-finish used to leave no
# record of how far the finish got (concat done? loudnorm done? SRT /
# sidecar written?). _FINISH_CTX is the current step; the signal handler
# logs it and aborts via KeyboardInterrupt, which also kills the ffmpeg
# child mid-call and runs the loudnorm-temp finally cleanup.
_FINISH_CTX = {"job": None, "orientation": None, "step": "starting"}


def _finish_signal_handler(signum, frame):
    job = _FINISH_CTX.get("job") or "?"
    ori = _FINISH_CTX.get("orientation") or "?"
    step = _FINISH_CTX.get("step") or "?"
    name = ("SIGTERM" if signum == signal.SIGTERM else
            "SIGINT" if signum == signal.SIGINT else f"signal {signum}")
    print(f"finish interrupted at {step} (job {job} {ori}) — received {name}; partial outputs are untrusted, next run re-validates",
          file=sys.stderr, flush=True)
    raise KeyboardInterrupt


def _install_finish_signal_handlers():
    try:
        signal.signal(signal.SIGTERM, _finish_signal_handler)
        signal.signal(signal.SIGINT, _finish_signal_handler)
    except ValueError:
        # Not the main thread (e.g. imported by a test harness) — signal
        # handlers can only be installed there; the finish still runs.
        print("  WARNING: could not install finish signal handlers (not main thread)", file=sys.stderr)


def cleanup_orphaned_finish_temps(final, chunks_dir, prefix):
    """Remove temp files left by a finish killed mid-run (L9) and log
    them, so the next run notes the orphans instead of silently
    overwriting them. Loudnorm temps are per-pid: only a temp whose pid
    is dead is removed (a live pid means another finish is running).
    The joined file is recreated by concat on every run, so a stale one
    is safe to remove."""
    basename = os.path.basename(final)
    try:
        names = os.listdir(tempfile.gettempdir())
    except OSError:
        names = []
    for name in names:
        if not (name.startswith(basename + ".loud-") and name.endswith(".mp4")):
            continue
        path = os.path.join(tempfile.gettempdir(), name)
        pid = name[len(basename + ".loud-"):-len(".mp4")]
        try:
            os.kill(int(pid), 0)
            continue  # pid alive: another finish is using it
        except (ValueError, ProcessLookupError, PermissionError):
            pass
        print(f"  finish: removing orphaned loudnorm temp from interrupted run: {path}")
        try:
            os.remove(path)
        except OSError as ex:
            print(f"  WARNING: could not remove orphaned temp {path}: {ex}")
    joined = os.path.join(chunks_dir, f"{prefix}-joined.mp4")
    if os.path.exists(joined):
        print(f"  finish: removing orphaned joined file from interrupted run: {joined}")
        try:
            os.remove(joined)
        except OSError as ex:
            print(f"  WARNING: could not remove orphaned joined file {joined}: {ex}")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--job", required=True)
    ap.add_argument("--orientation", required=True, choices=["vertical", "landscape"])
    ap.add_argument("--dry-run", action="store_true")
    ap.add_argument("--check-only", action="store_true")
    ap.add_argument("--check-final-only", action="store_true")
    ap.add_argument("--check-chunks-only", action="store_true")
    args = ap.parse_args()

    job = os.path.abspath(args.job)
    orientation = args.orientation
    # L9: track the current step so a kill mid-finish is logged with the
    # step reached, not just a silent death.
    _FINISH_CTX["job"] = os.path.basename(job)
    _FINISH_CTX["orientation"] = orientation
    _FINISH_CTX["step"] = "1-verify-chunks"
    _install_finish_signal_handlers()
    print(f"finish started: job {_FINISH_CTX['job']} orientation {orientation} step 1-verify-chunks")
    w, h = (1080, 1920) if orientation == "vertical" else (1920, 1080)
    prefix = orientation
    props_path = os.path.join(job, f"remotion-props-{w}x{h}.json")
    if not os.path.exists(props_path):
        print(f"PROPS MISSING: {props_path}", file=sys.stderr)
        return 2
    # L58: a bad script.json / props, an unknown profile or a missing
    # title used to exit 1 via an uncaught traceback, with no
    # FINISH FAILED line naming the job — wrap the load/config block so
    # the failure is one clean log line (job + orientation + reason).
    try:
        with open(os.path.join(job, "script.json")) as f:
            script = json.load(f)
        with open(props_path) as f:
            props = json.load(f)
        profile_id = script.get("Profile") or "gigglegarden"
        prof = CONFIG["profiles"][profile_id]
        lang = script.get("Language", "en")
        out_dir = os.path.join(VIDEOS_DIR, profile_id)
        slug_base = slug(script["Title"])
    except Exception as ex:
        print(f"FINISH FAILED: job {job} orientation {orientation}: {type(ex).__name__}: {ex}", file=sys.stderr)
        return 1
    final = os.path.join(out_dir, f"{slug_base}-{lang}" + ("-short.mp4" if orientation == "vertical" else ".mp4"))
    chunks_dir = os.path.join(job, f"chunks-{prefix}")
    # Landscape chunks are shorter (2026-11-23): scenes_per_chunk_landscape
    # overrides the default for landscape only; vertical keeps 5 so its
    # finished chunks stay valid. render-chunks.sh applies the same rule.
    spc = CONFIG["render"].get("scenes_per_chunk_landscape", CONFIG["render"]["scenes_per_chunk"]) if orientation == "landscape" else CONFIG["render"]["scenes_per_chunk"]
    total_frames, ranges = compute_ranges(props_path, spc)
    prop_scenes = props["scenes"]
    fps = props.get("fps", 30)
    # Single source of truth for per-scene start frames (brief + intro
    # included) — shared by the SRT writer and build_chapters below, and
    # identical to what chunk_ranges.py used for the chunk ranges.
    brief_starts, starts, _, _, tf = scene_starts(props)

    print(f"job: {job}")
    print(f"orientation: {orientation} -> {final}")
    print(f"profile: {profile_id} (MadeForKids={prof['made_for_kids']}) total_frames={total_frames} chunks={len(ranges)}")

    # --check-final-only: is the orientation already finished by --assemble
    # (final + sidecar + SRT valid)? No chunks needed.
    if args.check_final_only:
        return check_final(final, w, h, total_frames, fps)
    # --check-chunks-only: are all chunks present and valid (ready to finish)?
    if args.check_chunks_only:
        return check_chunks(chunks_dir, prefix, ranges, props_path, fps)

    # 1. Verify chunks.
    rc = check_chunks(chunks_dir, prefix, ranges, props_path, fps)
    if rc != 0:
        return rc
    print(f"All chunks valid; expected total {total_frames / fps:.1f}s")

    if args.check_only:
        if not os.path.exists(final):
            print(f"final file not present yet: {final}")
            return 1
        return check_final(final, w, h, total_frames, fps)
    if args.dry_run:
        n_brief = len(props.get("briefScenes") or [])
        print("DRY RUN: would concatenate, loudnorm, write SRT/thumbnail/sidecar, verify.")
        print(f"  final: {final}")
        print(f"  SRT cues: {n_brief + len(prop_scenes)} ({n_brief} brief + {len(prop_scenes)} scenes)")
        if orientation == "landscape":
            print(f"  thumbnail: 1280x720 from first story scene (index>=1) landscape art")
        print(f"  sidecar: {os.path.splitext(final)[0] + '.json'} (Approved=false)")
        if orientation == "vertical":
            print("  second Short: payoff scenes assembled from job assets (-short2.mp4 + sidecar, own title + thumbnail, +12h publish hold as fallback) when the Short runs over 60s")
        return 0

    os.makedirs(out_dir, exist_ok=True)

    # L9: note + remove temps left by a finish killed mid-run.
    cleanup_orphaned_finish_temps(final, chunks_dir, prefix)

    # 2. Concatenate.
    _FINISH_CTX["step"] = "2-concat"
    print("Concatenating chunks...")
    joined = os.path.join(chunks_dir, f"{prefix}-joined.mp4")
    concat_path = os.path.join(chunks_dir, "concat.txt")
    concat_text = "".join(
        f"file '{os.path.join(chunks_dir, prefix + '-' + name + '.mp4')}'\n"
        for name, _, _ in ranges)
    atomic_write(concat_path, concat_text)
    run(["ffmpeg", "-y", "-f", "concat", "-safe", "0", "-i", concat_path,
         "-c", "copy", joined])
    print(f"  joined: {probe_duration(joined):.2f}s")

    # 3. Delivery gate + loudness (NormalizeLoudnessAsync).
    _FINISH_CTX["step"] = "3-loudnorm"
    print("Delivery gate + loudness-normalize to -14 LUFS...")
    vfmt = probe_video_format(joined)
    pix_fmt = vfmt.get("pix_fmt", "")
    color_range = vfmt.get("color_range", "")
    print(f"  joined video: pix_fmt={pix_fmt} color_range={color_range}")
    if pix_fmt == "yuv420p" and color_range == "tv":
        video_args = ["-c:v", "copy"]
    else:
        # Re-encode to limited-range yuv420p. The explicit colorspace /
        # primaries / trc flags force x264 to write the VUI block (which
        # carries the full_range flag); without them a source with no color
        # metadata (e.g. a test pattern) re-encodes to color_range=unknown
        # and fails the delivery gate even though the pixels are limited.
        video_args = ["-vf", "scale=in_range=pc:out_range=tv,format=yuv420p",
                      "-c:v", "libx264", "-preset", "medium", "-crf", "20",
                      "-color_range", "tv", "-colorspace", "bt709",
                      "-color_primaries", "bt709", "-color_trc", "bt709"]
        print("  -> re-encoding video to limited-range yuv420p")
    # B29: the loudnorm temp used to sit next to the final in the watch dir
    # (final + ".loud.mp4") — a kill mid-loudnorm left a partial that a
    # later uploader scan (age > 90s) treated as a no-sidecar video. Write
    # it to the system temp dir instead (TMPDIR, ~/workspace/tmp under the
    # driver) and remove it if the run fails, so nothing partial ever
    # appears in the watch dir.
    tmp = os.path.join(tempfile.gettempdir(),
                       f"{os.path.basename(final)}.loud-{os.getpid()}.mp4")
    try:
        run(["ffmpeg", "-y", "-i", joined, "-af",
             "loudnorm=I=-14:TP=-1.5:LRA=11:linear=true"] + video_args +
            ["-c:a", "aac", "-b:a", "128k", "-ar", "48000", tmp])
        # shutil.move, not os.replace: the loudnorm temp lives in
        # tempfile.gettempdir(), which is /tmp (a tmpfs) when TMPDIR is
        # unset — a different device from the watch dir, so os.replace
        # raises Errno 18 and the finally block deletes the finished
        # encode (seen 2026-11-24 on COP31 landscape). shutil.move
        # falls back to copy+delete across devices.
        shutil.move(tmp, final)
    finally:
        if os.path.exists(tmp):
            os.remove(tmp)
    print(f"  final: {final}")

    # 3b. Instagram Story cut (Rishi 2026-11-26): Stories are capped at
    # 60s and the Shorts this pipeline makes are 78-81s, so the Uploader
    # used to skip the instagram_story target outright. Cut the FIRST
    # 60s (the hook lives at the start) into a sibling
    # "<slug>-en-short-story60.mp4" for the Uploader to upload instead.
    # Re-encoded (not stream-copied) with the finish's own encode
    # settings, so the cut is exactly 59.5s — a nominal 60.00s cut can
    # probe at 60.0x and trip the 60s gate. Bonus only: the cut must
    # never fail the Short itself, so every step here is check=False and
    # a bad cut is discarded — a missing cut just means the Story target
    # is skipped as before. A stale cut from an earlier, longer finish
    # is removed when the Short now fits in 60s.
    _FINISH_CTX["step"] = "3b-story60"
    short2_path = None
    short2_duration = None
    short2_title = None
    short2_title_fallback = False
    short2_publish_after = None
    story60 = os.path.splitext(final)[0] + "-story60.mp4"
    if orientation == "vertical":
        if total_frames / fps > 60:
            cut = run(["ffmpeg", "-y", "-i", final, "-t", "59.5",
                       "-c:v", "libx264", "-preset", "medium", "-crf", "20",
                       "-color_range", "tv", "-colorspace", "bt709",
                       "-color_primaries", "bt709", "-color_trc", "bt709",
                       "-c:a", "aac", "-b:a", "128k", "-ar", "48000", story60],
                      check=False)
            if cut.returncode != 0:
                # L81: this warning used to go to stdout with no job id,
                # so the driver could not tell a missing cut happened
                # without grepping the log. Stderr + job id + exit code.
                print(f"  WARNING [{_FINISH_CTX['job']}]: story cut ffmpeg failed (exit {cut.returncode}) — continuing without one; the Story target will be skipped as before.", file=sys.stderr)
            else:
                probe = run(["ffprobe", "-v", "error", "-show_entries", "format=duration",
                             "-of", "csv=p=0", story60], check=False)
                try:
                    cut_dur = float(probe.stdout.strip()) if probe.returncode == 0 else None
                except ValueError:
                    cut_dur = None
                if cut_dur is not None and cut_dur <= 60:
                    print(f"  story cut: {story60} ({cut_dur:.2f}s)")
                else:
                    # L81: same as the ffmpeg-failure warning above —
                    # stderr + job id, so a discarded cut is machine-visible.
                    print(f"  WARNING [{_FINISH_CTX['job']}]: story cut probed at {cut_dur}s — discarding; the Story target will be skipped as before.", file=sys.stderr)
                    if os.path.exists(story60):
                        os.remove(story60)
        elif os.path.exists(story60):
            # Short now fits in 60s: a cut from an earlier longer finish
            # would be stale — remove it so the Uploader never ships it.
            print(f"  removing stale story cut (Short now fits in 60s): {story60}")
            os.remove(story60)

    # 3c. Second Short — payoff cut ASSEMBLED from the job's own assets
    # (fix #5 rework, Rishi 2026-11-27: the old tail cut of the vertical
    # master replayed the main Short's own scenes, and the landscape-cut
    # rework was wrong too — there is NO full-length vertical master;
    # the vertical render only ever covers the main Short itself).
    # The cut is assembled from the payoff scenes' own portrait art +
    # narration (zoompan segments), plus the outro/end card cut from
    # the Short itself (the vertical final being finished here), with
    # the music bed mixed under the scenes and ASS captions burned in
    # — see assemble_second_short. No re-render, no new images, no new
    # TTS, no landscape final needed: the vertical finish stands alone.
    # The 2nd Short gets its own title (variant 3, else derived from
    # the payoff's opening narration), its own thumbnail still, and a
    # 12h publish hold as a FALLBACK (PublishAfterUtc = finish + 12h):
    # the governing hold is measured from the main Short going live,
    # and the uploader re-anchors it off the sibling main Short's
    # YouTube publication time when that exists (cadence: uploads
    # alternate every 12h, Rishi 2026-11-27 18:19 EST).
    # Bonus only, like the story cut: every step here is check=False
    # and a failed cut never fails the Short. No SRT is written for
    # the cut: the burned-in captions are already in the picture.
    _FINISH_CTX["step"] = "3c-short2"
    if orientation == "vertical":
        duration_s = total_frames / fps
        if duration_s <= 60:
            # The main Short already fits in 60s: a payoff cut would be a
            # near-duplicate of it, not a second Short.
            print("  no second Short: main Short already fits in 60s")
        else:
            landscape_props_path = os.path.join(job, "remotion-props-1920x1080.json")
            if not os.path.exists(landscape_props_path):
                print(f"  WARNING [{_FINISH_CTX['job']}]: no second Short cut — landscape props not found ({landscape_props_path}); the video ships with its main Short only.", file=sys.stderr)
            else:
                landscape_starts = None
                try:
                    with open(landscape_props_path) as f:
                        landscape_props = json.load(f)
                    _, landscape_starts, _, landscape_fps, _ = scene_starts(landscape_props)
                    landscape_scenes = landscape_props.get("scenes") or []
                except Exception as ex:
                    print(f"  WARNING [{_FINISH_CTX['job']}]: no second Short cut — could not load landscape props ({landscape_props_path}): {type(ex).__name__}: {ex}; the video ships with its main Short only.", file=sys.stderr)
                if landscape_starts is not None:
                    outro_seconds = props.get("outroSeconds")
                    if not outro_seconds or outro_seconds <= 0:
                        print(f"  WARNING [{_FINISH_CTX['job']}]: no second Short cut — vertical props carry no outroSeconds (the Short's own outro closes the cut); the video ships with its main Short only.", file=sys.stderr)
                    else:
                        main_last_scene = len(prop_scenes) - 1
                        try:
                            scene_durations = [float(s["durationInSeconds"]) for s in landscape_scenes]
                        except (KeyError, TypeError, ValueError) as ex:
                            print(f"  WARNING [{_FINISH_CTX['job']}]: no second Short cut — landscape props carry an unusable scene duration ({type(ex).__name__}: {ex}); the video ships with its main Short only.", file=sys.stderr)
                            scene_durations = None
                        if scene_durations is not None:
                            script_scenes = script.get("Scenes") or []
                            narrations = ([s.get("Narration") for s in script_scenes]
                                          if len(script_scenes) == len(scene_durations) else None)
                            window, reason = select_payoff_scenes(
                                scene_durations, outro_seconds, main_last_scene, narrations)
                            if window is None:
                                print(f"  WARNING [{_FINISH_CTX['job']}]: no second Short cut — {reason} (main Short covers scenes 0..{main_last_scene}); the video ships with its main Short only.", file=sys.stderr)
                            else:
                                missing = missing_short2_assets(job, window)
                                if missing:
                                    shown = ", ".join(missing[:3])
                                    extra = f" (+{len(missing) - 3} more)" if len(missing) > 3 else ""
                                    print(f"  WARNING [{_FINISH_CTX['job']}]: no second Short cut — payoff scene assets missing ({shown}{extra}); a cut is never shipped with a hole in it; the video ships with its main Short only.", file=sys.stderr)
                                else:
                                    comp_all = scene_composition_durations(
                                        landscape_starts, landscape_fps,
                                        landscape_props.get("outroSeconds", 0))
                                    if len(comp_all) != len(scene_durations) or any(d is None or d <= 0 for d in comp_all):
                                        print(f"  WARNING [{_FINISH_CTX['job']}]: no second Short cut — composition durations unusable ({comp_all}); the video ships with its main Short only.", file=sys.stderr)
                                    else:
                                        comp_window = [comp_all[i] for i in window]
                                        payoff_narrations = [
                                            script_scenes[i].get("Narration") if i < len(script_scenes) else None
                                            for i in window]
                                        main_title = fit(script["Title"], 100)
                                        opening_narration = payoff_narrations[0] if payoff_narrations else None
                                        variants = script.get("TitleVariants") or []
                                        if (len(variants) > 2 and (variants[2] or "").strip()
                                                and (variants[2] or "").strip().lower() != main_title.strip().lower()):
                                            title_source = "title variant 3"
                                        elif first_sentence_title(opening_narration):
                                            title_source = "derived from payoff opening narration"
                                        else:
                                            title_source = "main title (fallback — no variant, no narration)"
                                        short2_title, short2_title_fallback = pick_second_short_title(
                                            main_title, variants, opening_narration)
                                        short2_publish_after = (datetime.now(timezone.utc) + timedelta(hours=SHORT2_HOLD_HOURS)).isoformat()
                                        short2_path = os.path.splitext(final)[0] + "2.mp4"
                                        print(f"  second Short: assembling payoff scenes {window[0]}..{window[-1]} from job assets (no re-render)")
                                        print(f"    scene composition durations (s): {', '.join(f'{i}:{d:.2f}' for i, d in zip(window, comp_window))}; outro {outro_seconds:.2f}s cut from the Short itself")
                                        probe_short = run(["ffprobe", "-v", "error", "-show_entries", "format=duration",
                                                           "-of", "csv=p=0", final], check=False)
                                        try:
                                            short_duration = float(probe_short.stdout.strip()) if probe_short.returncode == 0 else duration_s
                                        except ValueError:
                                            short_duration = duration_s
                                        music_path = find_music_bed(
                                            job, props.get("backgroundMusicPath"),
                                            landscape_props.get("backgroundMusicPath"))
                                        music_volume = (props.get("backgroundMusicVolume")
                                                        or landscape_props.get("backgroundMusicVolume"))
                                        cut_dur, asm_reason = assemble_second_short(
                                            job, _FINISH_CTX["job"], window, comp_window,
                                            payoff_narrations, outro_seconds, short_duration,
                                            final, music_path, music_volume, landscape_fps,
                                            short2_path)
                                        if cut_dur is None:
                                            # L81 pattern: stderr + job id + reason.
                                            print(f"  WARNING [{_FINISH_CTX['job']}]: second Short assembly failed — {asm_reason}; the video ships with its main Short only.", file=sys.stderr)
                                            if os.path.exists(short2_path):
                                                os.remove(short2_path)
                                            short2_path = None
                                            short2_title = None
                                            short2_publish_after = None
                                        else:
                                            short2_duration = cut_dur
                                            print(f"  second Short: {short2_path} ({cut_dur:.2f}s, payoff scenes {window[0]}..{window[-1]} assembled from job assets, no re-render)")
                                            print(f"    title: {short2_title!r} ({title_source}); PublishAfterUtc {short2_publish_after} (fallback hold, finish +{SHORT2_HOLD_HOURS}h — governing hold is sibling-anchored in the uploader)")
                                            make_short2_thumbnail(_FINISH_CTX["job"], final, short2_path)

    # 4. SRT from the props scenes (the rendered scene list). Cue boundaries
    _FINISH_CTX["step"] = "4-srt"
    # are the scenes' ACTUAL start frames in the composition — the shared
    # frame starts from chunk_ranges.scene_starts (brief + intro included),
    # so the SRT agrees with the chunk ranges and the rendered video.
    # Each transition's duration is shared time, so a scene starts
    # transitionFrames early. Brief scenes get cues too (they are captioned
    # in the render); the last brief cue ends where the segment after the
    # brief begins (intro start, or scene 0 if there is no intro).
    print("Writing SRT...")
    outro = float(props.get("outroSeconds", 0))
    outro_start = total_frames - round(outro * fps)
    brief = props.get("briefScenes") or []
    n_brief = len(brief)
    brief_frames = sum(round(b["durationInSeconds"] * fps) for b in brief)
    post_brief_start = brief_frames - n_brief * tf
    lines = []
    for j, b in enumerate(brief):
        start = brief_starts[j] / fps
        end = (brief_starts[j + 1] if j + 1 < n_brief else post_brief_start) / fps
        lines.append(f"{len(lines) + 1}\n{fmt_srt(start)} --> {fmt_srt(end)}\n{b['captionText']}\n")
    for i, sc in enumerate(prop_scenes):
        start = starts[i] / fps
        # `starts` has n+1 entries with starts[n] == total_frames, so
        # testing against len(starts) made the `else outro_start` branch
        # unreachable and the last cue ran to the video total, spanning
        # the outro (video #3's cue 42 ended at 330.4s = total). Test
        # against the scene count instead, mirroring the brief loop
        # above: the last cue ends where the outro begins. When there is
        # no outro, outro_start == total_frames, so this is a no-op.
        end = (starts[i + 1] if i + 1 < len(prop_scenes) else outro_start) / fps
        lines.append(f"{len(lines) + 1}\n{fmt_srt(start)} --> {fmt_srt(end)}\n{sc['captionText']}\n")
    srt_path = os.path.splitext(final)[0] + ".srt"
    atomic_write(srt_path, "\n".join(lines), encoding="utf-8")
    print(f"  {srt_path} ({len(lines)} cues: {n_brief} brief + {len(prop_scenes)} scenes)")

    # 5. Thumbnail (landscape only) + sidecar.
    _FINISH_CTX["step"] = "5-thumbnail-sidecar"
    thumb = None
    if orientation == "landscape":
        print("Building thumbnail...")
        # L80: the finish path builds a plain 1280x720 crop, never the
        # fix-#3 variants (real-photo source + overlay words + mobile
        # check) that --assemble builds. Say so, so the finish log
        # cannot be read as "variants built".
        print("  thumbnail: plain 1280x720 crop (finish path does not build fix-#3 variants)")
        thumb = os.path.splitext(final)[0] + ".thumb.jpg"
        # Thumbnail source (Rishi 2026-11-21): a MAJOR story picture, not the
        # scene-0 title card (video #1's title-card thumbnail was wrong).
        # First story scene (index >= 1) with landscape art wins; scene 0 is
        # the fallback, then a render frame. Candidates are filtered by
        # existence — the old code broke on the first scene>=1 that merely
        # HAD a LandscapeImagePath, even if the file did not exist, so later
        # story scenes with valid art were never considered.
        thumb_candidates = []
        for sc in (script.get("Scenes") or [])[1:]:
            p = sc.get("LandscapeImagePath")
            if p:
                thumb_candidates.append(p)
        sc0 = script["Scenes"][0] if script.get("Scenes") else {}
        thumb_candidates += [sc0.get("LandscapeImagePath"),
                             os.path.join(job, "scene00-art.png"),
                             os.path.join(job, "scene00-art-landscape.png")]
        art = None
        for cand in thumb_candidates:
            if cand and os.path.exists(cand):
                art = cand
                break
        if art:
            print(f"  source: {art}")
            run(["ffmpeg", "-y", "-i", art, "-vf",
                 "scale=1280:720:force_original_aspect_ratio=increase,crop=1280:720",
                 "-frames:v", "1", "-q:v", "2", thumb])
        else:
            # No art anywhere: grab a frame from inside scene 0. Scene 0
            # starts at starts[0] (brief + intro offset, 17.5s on video #3)
            # — the old grab_at (<=3s) landed inside the brief/intro, not
            # in scene 0 at all.
            scene0_start_s = starts[0] / fps if starts else 0.0
            scene0_dur_s = prop_scenes[0]["durationInSeconds"] if prop_scenes else 0.0
            grab_at = scene0_start_s + 0.6 * scene0_dur_s
            print(f"  no scene-0 art; extracting frame at {grab_at:.2f}s from the render")
            frame = os.path.join(job, "thumbnail-frame.jpg")
            run(["ffmpeg", "-y", "-ss", str(grab_at), "-i", final,
                 "-frames:v", "1", "-q:v", "2", frame])
            run(["ffmpeg", "-y", "-i", frame, "-vf",
                 "scale=1280:720:force_original_aspect_ratio=increase,crop=1280:720",
                 "-frames:v", "1", "-q:v", "2", thumb])
        print(f"  {thumb}")

    print("Writing sidecar...")
    # MusicTrack drives the uploader's CC BY credit (Program.cs:1650). The
    # --assemble path saves it into script.json, but the chunk path never
    # does — so fall back to the props' backgroundMusicPath, which is the
    # track actually rendered into the video. DurationSeconds is the final
    # video length (total frames / fps); the IG Story gate uses it instead
    # of ffprobing the file on every run. dump_sidecar strips None, so an
    # unknown track is omitted, not written as null.
    music_track = script.get("MusicTrack") or os.path.basename(props.get("backgroundMusicPath") or "") or None
    duration_seconds = total_frames / fps
    scenes = script["Scenes"]
    if orientation == "vertical":
        # Short title (fix #9 port): the plain script title, no
        # " #Shorts" suffix — YouTube auto-detects Shorts from the
        # vertical format. The description keeps its #Shorts hashtag
        # (the YouTube marker). Canonical hashtags (fix #10 port): the
        # Short's captions reuse the long-form Description's hashtag
        # line, so a video and its own Short never drift apart. Reels
        # keeps up to 8 tags / 300 chars, TikTok up to 4 tags / 150.
        canonical = canonical_hashtags(script["Description"])
        short_reels = with_canonical_hashtags(script["ReelsCaption"], canonical,
                                              max_hashtags=8, max_length=300)
        short_tiktok = with_canonical_hashtags(script["TikTokCaption"], canonical,
                                               max_hashtags=4, max_length=150)
        # Quality gate: a CC BY bed must be credited in the shipped
        # description (the uploader appends the same line idempotently
        # at upload time; the sidecar carries it already). The vertical
        # Description keeps its #Shorts hashtag (the YouTube marker).
        sidecar = {
            "Title": fit(script["Title"], 100),
            "Description": ensure_ccby_credit(short_reels + "\n\n#Shorts", music_track)[0],
            "Tags": trim_tags(list(script["Tags"][:12]) + ["shorts"]),
            "Approved": False,
            "Language": lang,
            "Aspect": "vertical",
            "MadeForKids": prof["made_for_kids"],
            "Channel": profile_id,
            "Targets": prof["vertical_targets"],
            "TitleVariants": script.get("TitleVariants"),
            "HookText": scenes[0]["Narration"] if scenes else None,
            "CommentPrompt": script.get("CommentPrompt") or None,
            "HookScore": script.get("HookScore"),
            "SeoScore": script.get("SeoScoreValue"),
            "LoudnessLUFS": -14,
            "MusicTrack": music_track,
            "DurationSeconds": duration_seconds,
            "Publications": {},
            "CaptionOverrides": {
                "instagram": short_reels,
                "facebook": short_reels,
                "tiktok": short_tiktok,
            },
        }
    else:
        description = script["Description"]
        chapters = build_chapters(scenes, starts, fps, brief_scenes=script.get("BriefScenes")) if script.get("IsLongForm") else None
        if chapters:
            description += "\n\nChapters:\n" + "\n".join(chapters)
        if script.get("CommentPrompt"):
            description += "\n\n" + script["CommentPrompt"]
        # Quality gate: CC BY credit (see the vertical branch above).
        description, _ = ensure_ccby_credit(description, music_track)
        sidecar = {
            "Title": script["Title"],
            "Description": description,
            "Tags": trim_tags(script["Tags"]),
            "Approved": False,
            "Language": lang,
            "Aspect": "landscape",
            "MadeForKids": prof["made_for_kids"],
            "Channel": profile_id,
            "Targets": prof["landscape_targets"],
            "TitleVariants": script.get("TitleVariants"),
            "HookText": scenes[0]["Narration"] if scenes else None,
            "CommentPrompt": script.get("CommentPrompt") or None,
            "Chapters": chapters,
            "ThumbnailPath": thumb,
            "HookScore": script.get("HookScore"),
            "SeoScore": script.get("SeoScoreValue"),
            "LoudnessLUFS": -14,
            "MusicTrack": music_track,
            "DurationSeconds": duration_seconds,
            "Publications": {},
            "CaptionOverrides": {},
        }
    sidecar_path = os.path.splitext(final)[0] + ".json"
    dump_sidecar(sidecar_path, sidecar)
    print(f"  {sidecar_path}")
    if orientation == "vertical" and short2_path is not None and os.path.exists(short2_path):
        # Fix #5 rework: the second Short is a distinct payoff moment,
        # so it carries its OWN title (variant 3 or derived from the
        # payoff's opening narration), a 12h publish hold as a FALLBACK
        # (PublishAfterUtc = finish + 12h — the governing hold is
        # measured from the main Short going live and the uploader
        # re-anchors it off the sibling main Short's YouTube
        # publication time when that exists), and Short2TitleFallback=true
        # only when the title fell back to the main title.
        # Description/tags/targets still reuse the main Short's packaging.
        # Its own sidecar (Approved=false)
        # makes it an independent video for the uploader, with its own
        # Publications; the "-short2" filename is what the uploader keys
        # the quota guard and the sibling lookup on.
        short2_sidecar = dict(sidecar)
        short2_sidecar["DurationSeconds"] = short2_duration
        short2_sidecar["Publications"] = {}
        if short2_title is not None:
            short2_sidecar["Title"] = short2_title
        if short2_publish_after is not None:
            short2_sidecar["PublishAfterUtc"] = short2_publish_after
        if short2_title_fallback:
            short2_sidecar["Short2TitleFallback"] = True
        short2_sidecar_path = os.path.splitext(short2_path)[0] + ".json"
        dump_sidecar(short2_sidecar_path, short2_sidecar)
        print(f"  {short2_sidecar_path}")

    # 6. Verify.
    _FINISH_CTX["step"] = "6-verify"
    return verify_final(final, w, h, total_frames, fps, job=job, orientation=orientation)


def probe_streams(path):
    """Return (n_video, n_audio, nb_frames, duration) for a chunk/final.
    nb_frames is None when ffprobe cannot report it (e.g. "N/A")."""
    r = run(["ffprobe", "-v", "error",
             "-show_entries", "stream=codec_type,nb_frames",
             "-show_entries", "format=duration",
             "-of", "json", path])
    data = json.loads(r.stdout)
    streams = data.get("streams", [])
    n_video = sum(1 for s in streams if s.get("codec_type") == "video")
    n_audio = sum(1 for s in streams if s.get("codec_type") == "audio")
    nb_frames = None
    for s in streams:
        if s.get("codec_type") == "video":
            raw = s.get("nb_frames")
            if raw not in (None, "N/A"):
                try:
                    nb_frames = int(raw)
                except (TypeError, ValueError):
                    nb_frames = None
            break
    dur = float(data.get("format", {}).get("duration", 0))
    return n_video, n_audio, nb_frames, dur


def check_chunks(chunks_dir, prefix, ranges, props_path=None, fps=30):
    """Verify every chunk: present, has both a video and an audio stream,
    frame count matches the range (when ffprobe reports it), and duration
    within 0.15s (~4 frames) of expected. Duration-only ±0.6s used to let
    audio-less, frozen, or up-to-17-frames-short chunks pass.
    If props_path is given and a props.sha256 file exists in the chunks
    dir, the stored hash must match the current props — otherwise the
    chunks were rendered against stale props and must be re-rendered."""
    if props_path is not None:
        hash_path = os.path.join(chunks_dir, "props.sha256")
        if os.path.exists(hash_path):
            with open(hash_path, encoding="utf-8") as f:
                stored = f.read().strip()
            current = sha256_file(props_path)
            if stored != current:
                print(f"STALE chunks: props.sha256 {stored[:12]}... != current props "
                      f"{current[:12]}... — props changed since these chunks were "
                      f"rendered; delete {chunks_dir} and re-render", file=sys.stderr)
                return 1
    print(f"Verifying {len(ranges)} chunks...")
    for name, start, end in ranges:
        path = os.path.join(chunks_dir, f"{prefix}-{name}.mp4")
        if not os.path.exists(path):
            print(f"MISSING chunk {name}: {path}", file=sys.stderr)
            return 1
        expected_frames = end - start + 1
        expected = expected_frames / fps
        n_video, n_audio, nb_frames, dur = probe_streams(path)
        if n_video < 1:
            print(f"INVALID chunk {name}: no video stream", file=sys.stderr)
            return 1
        if n_audio < 1:
            print(f"INVALID chunk {name}: no audio stream", file=sys.stderr)
            return 1
        if nb_frames is not None and abs(nb_frames - expected_frames) > 1:
            print(f"INVALID chunk {name}: {nb_frames} frames, expected {expected_frames}",
                  file=sys.stderr)
            return 1
        if abs(dur - expected) >= 0.15:
            print(f"INVALID chunk {name}: duration {dur:.2f}s, expected {expected:.2f}s",
                  file=sys.stderr)
            return 1
        print(f"  {name}: {dur:.2f}s {nb_frames if nb_frames is not None else '?'} frames "
              f"OK (expected {expected:.2f}s, {expected_frames}f)")
    return 0


def gate_check(final, w, h, total_frames, fps=30):
    """The delivery gate: dims, limited-range yuv420p, 48kHz audio,
    duration within tolerance. Returns True if the final file passes.
    (No loudness measurement here — that is slow and belongs in the full
    verify after a finish.)"""
    fmt = probe_video_format(final)
    pix_fmt = fmt.get("pix_fmt", "")
    color_range = fmt.get("color_range", "")
    width = fmt.get("width", 0)
    height = fmt.get("height", 0)
    rate = probe_audio_rate(final)
    sample_rate = str(rate.get("sample_rate", ""))
    dur = probe_duration(final)
    expected = total_frames / fps
    print(f"  {width}x{height} pix_fmt={pix_fmt} color_range={color_range} "
          f"audio_rate={sample_rate} duration={dur:.2f}s (expected {expected:.2f}s)")
    ok = True
    if int(width) != w or int(height) != h:
        print(f"  FAIL: dimensions {width}x{height} != {w}x{h}", file=sys.stderr)
        ok = False
    if pix_fmt != "yuv420p" or color_range != "tv":
        print("  FAIL: delivery gate — video not limited-range yuv420p", file=sys.stderr)
        ok = False
    if sample_rate != "48000":
        print(f"  FAIL: delivery gate — audio sample rate {sample_rate} != 48000", file=sys.stderr)
        ok = False
    if abs(dur - expected) > CONFIG["render"]["verify_duration_tolerance_seconds"]:
        print(f"  FAIL: duration {dur:.2f}s differs from expected {expected:.2f}s by more than "
              f"{CONFIG['render']['verify_duration_tolerance_seconds']}s", file=sys.stderr)
        ok = False
    return ok


def check_final(final, w, h, total_frames, fps=30):
    """Is this orientation already finished (final + sidecar + SRT valid)?
    Used by the driver to decide whether --assemble already delivered it.
    The sidecar must parse as JSON and the SRT must parse (cue structure),
    not merely exist — a truncated write by a killed finish used to pass
    the existence-only check."""
    if not os.path.exists(final):
        print(f"final file not present: {final}")
        return 1
    print("Checking final file...")
    if not gate_check(final, w, h, total_frames, fps):
        return 1
    sidecar_path = os.path.splitext(final)[0] + ".json"
    if not os.path.exists(sidecar_path):
        print(f"  FAIL: missing {sidecar_path}", file=sys.stderr)
        return 1
    try:
        with open(sidecar_path, encoding="utf-8") as f:
            json.load(f)
    except Exception as e:
        print(f"  FAIL: sidecar {sidecar_path} is not valid JSON: {e}", file=sys.stderr)
        return 1
    srt_path = os.path.splitext(final)[0] + ".srt"
    if not os.path.exists(srt_path):
        print(f"  FAIL: missing {srt_path}", file=sys.stderr)
        return 1
    try:
        cues = parse_srt(srt_path)
        if not cues:
            print(f"  FAIL: SRT {srt_path} has no cues", file=sys.stderr)
            return 1
    except Exception as e:
        print(f"  FAIL: SRT {srt_path} does not parse: {e}", file=sys.stderr)
        return 1
    print(f"  final + sidecar (valid JSON) + SRT ({len(cues)} cues) all present and valid")
    return 0


# Quality gate (Rishi 2026-11-23): the finish-time music checks.
# CC BY credit lines, verbatim from Uploader.MusicCredits (Program.cs) —
# the same strings QualityGate.ApprovedTracks carries in C#. A CC BY bed
# must be credited in the shipped description; the uploader appends the
# line idempotently at upload time, and the finish path appends it to
# the sidecar description first so the sidecar is already correct.
CCBY_CREDITS = {
    "uplifting-tech-01.mp3":
        'Music: "Rising Light (Uplifting Orchestral)" by AurosonMusic — https://freemusicarchive.org/music/aurosonmusic/single/rising-light-uplifting-orchestral/ — CC BY 4.0',
    "calm-neutral-01.mp3":
        'Music: "Old chantry" by Serat — https://freemusicarchive.org/music/serat/piano-textures/old-chantry-1/ — CC BY 4.0',
    "cinematic-epic-01.mp3":
        'Music: "A Hero for our Time" by Geoff Harvey (Purple Planet Music) — https://freemusicarchive.org/music/geoff-harvey-purple-planet-music/epic-missions/a-hero-for-our-timemp3/ — CC BY 4.0',
}


def ccby_credit_for(track):
    """The CC BY credit line for a track filename, or None when the
    track is CC0 / unknown (no credit owed)."""
    if not track:
        return None
    return CCBY_CREDITS.get(os.path.basename(track))


def ensure_ccby_credit(description, track):
    """Append the CC BY credit line to a description when the track
    requires one and the description does not already carry it.
    Returns (description, changed). Idempotent: a description that
    already contains the line is returned unchanged."""
    credit = ccby_credit_for(track)
    if credit is None:
        return description, False
    if credit in (description or ""):
        return description, False
    return (description or "") + "\n\n" + credit, True


def loudness_ok(output_i, target=-14.0, tolerance=1.0):
    """The finished mix must land within target ± tolerance LUFS
    (the channel standard is -14 ±1)."""
    try:
        return abs(float(output_i) - target) <= tolerance
    except (TypeError, ValueError):
        return False


def parse_silence(stderr):
    """Parse ffmpeg silencedetect output into [(start, duration)]
    pairs (same pairing as QualityGate.ParseSilence in C#). Pure:
    no ffmpeg."""
    starts = re.findall(r"silence_start:\s*(-?[\d.]+)", stderr or "")
    durations = re.findall(r"silence_duration:\s*([\d.]+)", stderr or "")
    gaps = []
    for s, d in zip(starts, durations):
        try:
            gaps.append((float(s), float(d)))
        except ValueError:
            continue
    return gaps


def leading_silence_error(gaps, min_seconds=2.0):
    """The leading-silence failure: a silent gap starting at ~0 that
    lasts >= min_seconds. Returns the gap or None. (A video that opens
    dead loses the viewer before the hook lands.)"""
    for start, dur in gaps:
        if start <= 0.1 and dur >= min_seconds:
            return (start, dur)
    return None


def verify_final(final, w, h, total_frames, fps=30, job=None, orientation=None):
    """Full verification after a finish: delivery gate + loudness +
    silence. A measured loudness more than 1 LUFS from -14 is a
    FAILURE (tightened from ±2: the channel standard is -14 ±1, and a
    mix 2 LUFS off sounds noticeably wrong next to the other videos).
    A silent opening >= 2s is a FAILURE; a silent gap >= 5s anywhere
    is a warning. Audio presence is covered by gate_check (48 kHz
    audio stream required) plus the loudnorm measurement."""
    print("Verifying final file...")
    ok = gate_check(final, w, h, total_frames, fps)
    print("Measuring loudness...")
    r = run(["ffmpeg", "-i", final, "-af",
             "loudnorm=I=-14:TP=-1.5:LRA=11:linear=true:print_format=json",
             "-f", "null", "-"], check=False)
    tail = r.stderr.strip().splitlines()
    print("\n".join(tail[-12:]))
    try:
        lines = r.stderr.strip().splitlines()
        start = next(i for i, ln in enumerate(lines) if ln.strip().startswith("{"))
        end = next(i for i, ln in enumerate(lines) if ln.strip() == "}")
        loud = json.loads("\n".join(lines[start:end + 1]))
        out_i = float(loud.get("output_i", -14))
        if not loudness_ok(out_i):
            print(f"  FAIL: measured loudness {out_i} LUFS is far from -14 (±1)", file=sys.stderr)
            ok = False
    except Exception as ex:
        ident = f"{job} {orientation}" if job else final
        print(f"  FAIL: verify_final: loudness JSON unparseable for {ident}: {ex} — failing verification", file=sys.stderr)
        ok = False
    print("Checking for silence...")
    s = run(["ffmpeg", "-i", final, "-af",
             "silencedetect=noise=-50dB:d=0.5", "-f", "null", "-"], check=False)
    gaps = parse_silence(s.stderr)
    lead = leading_silence_error(gaps)
    if lead is not None:
        print(f"  FAIL: file opens with {lead[1]:.1f}s of silence — the video starts dead", file=sys.stderr)
        ok = False
    for start, dur in gaps:
        if dur >= 5 and not (start <= 0.1 and dur >= 2):
            print(f"  warning: {dur:.1f}s silent gap at {start:.1f}s — dead air mid-video")
    if not ok:
        return 1
    print("DONE")
    return 0


if __name__ == "__main__":
    sys.exit(main())
