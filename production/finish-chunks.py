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
     vertical title gets " #Shorts" (Text.Fit to 100), description is the
     ReelsCaption, caption overrides for IG/FB/TikTok; landscape title
     as-is, description + chapters (long-form only) + CommentPrompt,
     thumbnail path. Targets/MadeForKids come from production/config.json.
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
import json
import os
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from chunk_ranges import compute_ranges  # noqa: E402

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


def build_chapters(scenes):
    chapters, cursor, last_group = [], 0.0, None
    for sc in scenes:
        if sc.get("VisualGroup") != last_group:
            nar = sc["Narration"]
            label = (nar[:40].rstrip() + "…") if len(nar) > 40 else nar
            chapters.append(f"{int(cursor // 60)}:{int(cursor % 60):02d} - {label}")
            last_group = sc.get("VisualGroup")
        cursor += sc["DurationSeconds"] + 0.5
    return chapters


def dump_sidecar(path, sidecar):
    sidecar = {k: v for k, v in sidecar.items() if v is not None}
    text = json.dumps(sidecar, indent=2, ensure_ascii=True)
    # Match System.Text.Json's default escaping (& and ' as \u0026 / \u0027).
    text = text.replace("&", "\\u0026").replace("'", "\\u0027")
    with open(path, "w", encoding="utf-8") as f:
        f.write(text)


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
    w, h = (1080, 1920) if orientation == "vertical" else (1920, 1080)
    prefix = orientation
    props_path = os.path.join(job, f"remotion-props-{w}x{h}.json")
    if not os.path.exists(props_path):
        print(f"PROPS MISSING: {props_path}", file=sys.stderr)
        return 2
    with open(os.path.join(job, "script.json")) as f:
        script = json.load(f)
    with open(props_path) as f:
        props = json.load(f)
    profile_id = script.get("Profile") or "gigglegarden"
    prof = CONFIG["profiles"][profile_id]
    lang = script.get("Language", "en")
    out_dir = os.path.join(VIDEOS_DIR, profile_id)
    slug_base = slug(script["Title"])
    final = os.path.join(out_dir, f"{slug_base}-{lang}" + ("-short.mp4" if orientation == "vertical" else ".mp4"))
    chunks_dir = os.path.join(job, f"chunks-{prefix}")
    total_frames, ranges = compute_ranges(props_path, CONFIG["render"]["scenes_per_chunk"])
    prop_scenes = props["scenes"]

    print(f"job: {job}")
    print(f"orientation: {orientation} -> {final}")
    print(f"profile: {profile_id} (MadeForKids={prof['made_for_kids']}) total_frames={total_frames} chunks={len(ranges)}")

    # --check-final-only: is the orientation already finished by --assemble
    # (final + sidecar + SRT valid)? No chunks needed.
    if args.check_final_only:
        return check_final(final, w, h, total_frames)
    # --check-chunks-only: are all chunks present and valid (ready to finish)?
    if args.check_chunks_only:
        return check_chunks(chunks_dir, prefix, ranges)

    # 1. Verify chunks.
    rc = check_chunks(chunks_dir, prefix, ranges)
    if rc != 0:
        return rc
    print(f"All chunks valid; expected total {total_frames / 30.0:.1f}s")

    if args.check_only:
        if not os.path.exists(final):
            print(f"final file not present yet: {final}")
            return 1
        return check_final(final, w, h, total_frames)
    if args.dry_run:
        print("DRY RUN: would concatenate, loudnorm, write SRT/thumbnail/sidecar, verify.")
        print(f"  final: {final}")
        print(f"  SRT cues: {len(prop_scenes)} (from props scenes)")
        if orientation == "landscape":
            print(f"  thumbnail: 1280x720 from scene-0 landscape art")
        print(f"  sidecar: {os.path.splitext(final)[0] + '.json'} (Approved=false)")
        return 0

    os.makedirs(out_dir, exist_ok=True)

    # 2. Concatenate.
    print("Concatenating chunks...")
    joined = os.path.join(chunks_dir, f"{prefix}-joined.mp4")
    concat_path = os.path.join(chunks_dir, "concat.txt")
    with open(concat_path, "w") as f:
        for name, _, _ in ranges:
            f.write(f"file '{os.path.join(chunks_dir, prefix + '-' + name + '.mp4')}'\n")
    run(["ffmpeg", "-y", "-f", "concat", "-safe", "0", "-i", concat_path,
         "-c", "copy", joined])
    print(f"  joined: {probe_duration(joined):.2f}s")

    # 3. Delivery gate + loudness (NormalizeLoudnessAsync).
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
    tmp = final + ".loud.mp4"
    run(["ffmpeg", "-y", "-i", joined, "-af",
         "loudnorm=I=-14:TP=-1.5:LRA=11:linear=true"] + video_args +
        ["-c:a", "aac", "-b:a", "128k", "-ar", "48000", tmp])
    os.replace(tmp, final)
    print(f"  final: {final}")

    # 4. SRT from the props scenes (the rendered scene list). Cue boundaries
    # are the scenes' ACTUAL start frames in the composition (same formula
    # as chunk_ranges.scene starts): each transition's duration is shared
    # time, so a scene starts transitionFrames early and the nominal
    # durations would drift the cues late by 0.2s per scene (~15s by the
    # end). Scene 1 starts after the intro sting (introSeconds).
    print("Writing SRT...")
    fps = props.get("fps", 30)
    intro = float(props.get("introSeconds", 0))
    outro = float(props.get("outroSeconds", 0))
    is_calm = props.get("isCalm", False)
    tf = round(fps * (0.45 if is_calm else 0.2))
    intro_frames = round(intro * fps)
    has_intro = 1 if intro > 0 else 0
    starts = []
    acc = 0
    for sc in prop_scenes:
        starts.append(intro_frames + acc - (has_intro + len(starts)) * tf)
        acc += round(sc["durationInSeconds"] * fps)
    outro_start = total_frames - round(outro * fps)
    lines = []
    for i, sc in enumerate(prop_scenes):
        start = starts[i] / fps
        end = (starts[i + 1] if i + 1 < len(starts) else outro_start) / fps
        lines.append(f"{i + 1}\n{fmt_srt(start)} --> {fmt_srt(end)}\n{sc['captionText']}\n")
    srt_path = os.path.splitext(final)[0] + ".srt"
    with open(srt_path, "w", encoding="utf-8") as f:
        f.write("\n".join(lines))
    print(f"  {srt_path} ({len(prop_scenes)} cues)")

    # 5. Thumbnail (landscape only) + sidecar.
    thumb = None
    if orientation == "landscape":
        print("Building thumbnail...")
        thumb = os.path.splitext(final)[0] + ".thumb.jpg"
        # Thumbnail source (Rishi 2026-11-21): a MAJOR story picture, not the
        # scene-0 title card (video #1's title-card thumbnail was wrong).
        # First story scene (index >= 1) with landscape art wins; scene 0 is
        # the fallback, then a render frame.
        thumb_candidates = []
        for sc in (script.get("Scenes") or [])[1:]:
            p = sc.get("LandscapeImagePath")
            if p:
                thumb_candidates.append(p)
                break
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
            grab_at = min(3.0, max(0.5, script["Scenes"][0]["DurationSeconds"] * 0.6))
            print(f"  no scene-0 art; extracting frame at {grab_at}s from the render")
            frame = os.path.join(job, "thumbnail-frame.jpg")
            run(["ffmpeg", "-y", "-ss", str(grab_at), "-i", final,
                 "-frames:v", "1", "-q:v", "2", frame])
            run(["ffmpeg", "-y", "-i", frame, "-vf",
                 "scale=1280:720:force_original_aspect_ratio=increase,crop=1280:720",
                 "-frames:v", "1", "-q:v", "2", thumb])
        print(f"  {thumb}")

    print("Writing sidecar...")
    scenes = script["Scenes"]
    if orientation == "vertical":
        sidecar = {
            "Title": fit(script["Title"] + " #Shorts", 100),
            "Description": script["ReelsCaption"] + "\n\n#Shorts",
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
            "Publications": {},
            "CaptionOverrides": {
                "instagram": script["ReelsCaption"],
                "facebook": script["ReelsCaption"],
                "tiktok": script["TikTokCaption"],
            },
        }
    else:
        description = script["Description"]
        chapters = build_chapters(scenes) if script.get("IsLongForm") else None
        if chapters:
            description += "\n\nChapters:\n" + "\n".join(chapters)
        if script.get("CommentPrompt"):
            description += "\n\n" + script["CommentPrompt"]
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
            "Publications": {},
            "CaptionOverrides": {},
        }
    sidecar_path = os.path.splitext(final)[0] + ".json"
    dump_sidecar(sidecar_path, sidecar)
    print(f"  {sidecar_path}")

    # 6. Verify.
    return verify_final(final, w, h, total_frames)


def check_chunks(chunks_dir, prefix, ranges):
    print(f"Verifying {len(ranges)} chunks...")
    for name, start, end in ranges:
        path = os.path.join(chunks_dir, f"{prefix}-{name}.mp4")
        if not os.path.exists(path):
            print(f"MISSING chunk {name}: {path}", file=sys.stderr)
            return 1
        expected = (end - start + 1) / 30.0
        dur = probe_duration(path)
        if abs(dur - expected) >= 0.6:
            print(f"INVALID chunk {name}: duration {dur}, expected {expected}", file=sys.stderr)
            return 1
        print(f"  {name}: {dur:.2f}s OK (expected {expected:.2f}s)")
    return 0


def gate_check(final, w, h, total_frames):
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
    expected = total_frames / 30.0
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


def check_final(final, w, h, total_frames):
    """Is this orientation already finished (final + sidecar + SRT valid)?
    Used by the driver to decide whether --assemble already delivered it."""
    if not os.path.exists(final):
        print(f"final file not present: {final}")
        return 1
    print("Checking final file...")
    if not gate_check(final, w, h, total_frames):
        return 1
    for suffix in (".json", ".srt"):
        side = os.path.splitext(final)[0] + suffix
        if not os.path.exists(side):
            print(f"  FAIL: missing {side}", file=sys.stderr)
            return 1
    print("  final + sidecar + SRT all present and valid")
    return 0


def verify_final(final, w, h, total_frames):
    """Full verification after a finish: delivery gate + loudness."""
    print("Verifying final file...")
    ok = gate_check(final, w, h, total_frames)
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
        if abs(out_i + 14) > 2:
            print(f"  WARNING: measured loudness {out_i} LUFS is far from -14", file=sys.stderr)
    except Exception:
        print("  (could not parse loudness JSON; see output above)")
    if not ok:
        return 1
    print("DONE")
    return 0


if __name__ == "__main__":
    sys.exit(main())
