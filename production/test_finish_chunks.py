#!/usr/bin/env python3
"""Behavioral tests for the chunk-path sidecar writers in finish-chunks.py.

Covers fix #8 (chapters: <=12, first 0:00, first-sentence labels),
fix #9 (Short title: no " #Shorts" suffix) and fix #10 (canonical
hashtags from the long-form Description) as ported into the production
chunk path, plus the chunk-path SRT last-cue rule (fix #4), and fix
sequence #2 ITEM 6: R5 (chapter-label abbreviations incl. Sen./Rep./
Gov., quote-aware sentence end, BuildChapters re-fill to the 12 cap),
R6 (canonical hashtags prefer line 2, trailing-only strip, floor guard
keeps caption text) and B24 (boundary-only chunk invalidation hash in
chunk_ranges.py).

Run:  python3 test_finish_chunks.py        (from production/)
Exit code 0 = all pass. No network, no ffmpeg, no render — the tests
call the module's pure functions on video #3's real script.json and on
small synthetic inputs.

The C# suite (Tests/ChunkPathTests.cs) also runs this file and asserts
it exits 0, so these behaviours are pinned in the full regression run.
"""
import contextlib
import importlib.util
import io
import json
import os
import re
import shutil
import signal
import subprocess
import sys
import tempfile
import time
import unittest
from datetime import datetime, timezone
from types import SimpleNamespace

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

_spec = importlib.util.spec_from_file_location(
    "finish_chunks", os.path.join(HERE, "finish-chunks.py"))
assert _spec is not None and _spec.loader is not None
fc = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(fc)

VIDEO3 = "/home/hatch/workspace/BroadcastContentAutomation/work/trendlinenow/job-20261122-115430/script.json"
VIDEO3_PROPS = "/home/hatch/workspace/BroadcastContentAutomation/work/trendlinenow/job-20261122-115430/remotion-props-1920x1080.json"

EXPECTED_CANONICAL = [
    "#TrendlineNow", "#GTA6", "#RockstarGames", "#gaming",
    "#news", "#explained", "#trending", "#tech",
]


def load_video3():
    if not os.path.exists(VIDEO3):
        raise unittest.SkipTest(f"video #3 script.json not found at {VIDEO3}")
    with open(VIDEO3, encoding="utf-8") as f:
        script = json.load(f)
    with open(VIDEO3_PROPS, encoding="utf-8") as f:
        props = json.load(f)
    return script, props


def parse_chapter(chapter):
    dash = chapter.index(" - ")
    ts, label = chapter[:dash], chapter[dash + 3:]
    mins, secs = ts.split(":")
    return int(mins) * 60 + int(secs), label


class ChapterLabelTests(unittest.TestCase):
    def test_first_complete_sentence(self):
        self.assertEqual(
            fc.chapter_label("Rockstar finally released GTA 6, and it returns to Vice City! The rest follows."),
            "Rockstar finally released GTA 6, and it returns to Vice City!")

    def test_abbreviations_do_not_cut_label(self):
        self.assertEqual(
            fc.chapter_label("Mr. Smith said the U.S. plan works. The rest of the story follows."),
            "Mr. Smith said the U.S. plan works.")
        self.assertEqual(
            fc.chapter_label("Sen. Cruz warned the D.C. council today. More detail follows here."),
            "Sen. Cruz warned the D.C. council today.")

    def test_no_sentence_end_returns_whole_narration(self):
        self.assertEqual(
            fc.chapter_label("A narration with no sentence end at all"),
            "A narration with no sentence end at all")

    def test_empty_returns_untitled(self):
        self.assertEqual(fc.chapter_label(""), "Untitled")
        self.assertEqual(fc.chapter_label("   "), "Untitled")

    def test_long_first_sentence_cut_at_word_boundary_without_ellipsis(self):
        sentence = " ".join(["word"] * 50) + ". A short second sentence."
        label = fc.chapter_label(sentence)
        self.assertLessEqual(len(label), 200)
        self.assertNotIn("…", label)
        self.assertLess(len(label), len(sentence))
        self.assertEqual(" ", sentence[len(label)])

    def test_no_ellipsis_truncation(self):
        # The old chunk path truncated labels at 40 chars with "…".
        label = fc.chapter_label("After thirteen years since GTA V, Rockstar finally returned to Vice City with a brand-new map.")
        self.assertNotIn("…", label)
        self.assertTrue(label.endswith("."))

    def test_news_title_abbreviations_not_cut_short(self):
        # R5: the abbreviation set lacked the news-common titles, so
        # "Sen. Cruz warned..." was cut to just "Sen."
        self.assertEqual(
            fc.chapter_label("Sen. Cruz warned the Senate. Next sentence here."),
            "Sen. Cruz warned the Senate.")
        self.assertEqual(
            fc.chapter_label("Rep. Patel filed the bill. Next sentence here."),
            "Rep. Patel filed the bill.")
        self.assertEqual(
            fc.chapter_label("Gov. Lee signed the order. Next sentence here."),
            "Gov. Lee signed the order.")

    def test_sentence_end_after_closing_quote(self):
        # R5: a sentence end followed by a closing quote used to be
        # missed, falling back to the whole narration.
        self.assertEqual(
            fc.chapter_label('He said "It works." Next part follows here.'),
            'He said "It works."')
        self.assertEqual(
            fc.chapter_label('Something happened." And then more follows here.'),
            'Something happened."')


class BuildChaptersTests(unittest.TestCase):
    def test_video3_real_script_exactly_twelve_first_at_zero(self):
        script, props = load_video3()
        _, starts, _, fps, _ = fc.scene_starts(props)
        chapters = fc.build_chapters(script["Scenes"], starts, fps,
                                     brief_scenes=script.get("BriefScenes"))
        self.assertEqual(len(chapters), 12)
        seconds, _ = parse_chapter(chapters[0])
        self.assertEqual(seconds, 0)

    def test_video3_labels_are_complete_sentences(self):
        script, props = load_video3()
        _, starts, _, fps, _ = fc.scene_starts(props)
        chapters = fc.build_chapters(script["Scenes"], starts, fps,
                                     brief_scenes=script.get("BriefScenes"))
        for chapter in chapters:
            _, label = parse_chapter(chapter)
            self.assertNotIn("…", label)
            self.assertTrue(len(label) > 0)
            self.assertIn(label[-1], ".!?")

    def test_video3_every_gap_at_least_ten_seconds(self):
        script, props = load_video3()
        _, starts, _, fps, _ = fc.scene_starts(props)
        chapters = fc.build_chapters(script["Scenes"], starts, fps,
                                     brief_scenes=script.get("BriefScenes"))
        times = [parse_chapter(c)[0] for c in chapters]
        for a, b in zip(times, times[1:]):
            self.assertGreaterEqual(b - a, 10)

    def test_video3_first_chapter_labelled_from_brief(self):
        script, props = load_video3()
        _, starts, _, fps, _ = fc.scene_starts(props)
        chapters = fc.build_chapters(script["Scenes"], starts, fps,
                                     brief_scenes=script.get("BriefScenes"))
        _, label = parse_chapter(chapters[0])
        self.assertEqual(
            label,
            "Rockstar’s long‑awaited GTA\u202f6 landed in 2026, bringing a fresh take on Miami with a whole new criminal duo.")

    def test_no_brief_first_chapter_is_intro(self):
        scenes = [
            {"Narration": "First beat opens the story. The scene is set.", "VisualGroup": 0},
            {"Narration": "Second beat shifts the focus. A new angle emerges.", "VisualGroup": 1},
        ]
        starts = [0, 600, 1200]
        chapters = fc.build_chapters(scenes, starts, 30)
        self.assertEqual(chapters[0], "0:00 - Intro")

    def test_few_groups_keeps_all_boundaries(self):
        scenes = [
            {"Narration": "First beat opens the story. The scene is set.", "VisualGroup": 0},
            {"Narration": "Still the first beat. More context here.", "VisualGroup": 0},
            {"Narration": "Second beat shifts the focus. A new angle emerges.", "VisualGroup": 1},
            {"Narration": "Third beat brings the twist. Everything changes now.", "VisualGroup": 2},
        ]
        # 10s apart at 30fps, first scene starting at 20s (brief + intro
        # offset, as in a real render — a boundary at 0s would be dropped
        # by the 10s-gap rule against the 0:00 chapter).
        starts = [600, 900, 1200, 1500, 1800]
        brief = [{"Narration": "The teaser hooks you in. Watch what happens next."}]
        chapters = fc.build_chapters(scenes, starts, 30, brief_scenes=brief)
        self.assertEqual(len(chapters), 4)  # 0:00 + 3 boundaries

    def test_many_groups_caps_at_twelve(self):
        scenes = [
            {"Narration": f"Scene {i + 1} tells the next part of the story. More detail follows here.",
             "VisualGroup": i}
            for i in range(77)
        ]
        # 8s apart at 30fps; first scene starts after a 3.5s intro + brief.
        starts = [315 + i * 240 for i in range(77)] + [315 + 77 * 240]
        brief = [{"Narration": "The teaser hooks you in. Watch what happens next."}]
        chapters = fc.build_chapters(scenes, starts, 30, brief_scenes=brief)
        self.assertEqual(len(chapters), 12)

    def test_refills_after_gap_filter_to_full_twelve(self):
        # R5: 14 scenes x 30s, distinct groups — time-spacing picks 11 of
        # 14 boundaries, the gap filter drops the 0s one, and without a
        # re-fill the list would stop at 11 chapters with a slot wasted.
        # The re-fill must pull an unselected boundary back in (12 total).
        import chunk_ranges
        props = {
            "scenes": [{"durationInSeconds": 30} for _ in range(14)],
            "briefScenes": [], "fps": 30, "introSeconds": 0,
            "outroSeconds": 0, "isCalm": False,
        }
        _, starts, _, fps, _ = fc.scene_starts(props)
        scenes = [
            {"Narration": f"Scene {i + 1} happened here. More detail follows.",
             "VisualGroup": i + 1}
            for i in range(14)
        ]
        chapters = fc.build_chapters(scenes, starts, fps)
        self.assertEqual(len(chapters), 12)
        self.assertTrue(chapters[0].startswith("0:00 - "))
        times = [parse_chapter(c)[0] for c in chapters]
        for a, b in zip(times, times[1:]):
            self.assertGreaterEqual(b - a, 10)


class ShortTitleTests(unittest.TestCase):
    def test_video3_short_title_is_plain_title_no_suffix(self):
        script, _ = load_video3()
        short_title = fc.fit(script["Title"], 100)
        self.assertEqual(short_title, script["Title"])
        self.assertEqual(len(short_title), 61)
        self.assertNotIn("#Shorts", short_title)

    def test_long_title_fits_100_chars(self):
        title = "A very long title " + "word " * 30
        self.assertLessEqual(len(fc.fit(title, 100)), 100)
        self.assertNotIn("#Shorts", fc.fit(title, 100))


class CanonicalHashtagTests(unittest.TestCase):
    def test_video3_description_returns_eight_hashtags(self):
        script, _ = load_video3()
        self.assertEqual(fc.canonical_hashtags(script["Description"]), EXPECTED_CANONICAL)

    def test_reels_caption_hashtags_exactly_canonical(self):
        script, _ = load_video3()
        canonical = fc.canonical_hashtags(script["Description"])
        rebuilt = fc.with_canonical_hashtags(script["ReelsCaption"], canonical,
                                             max_hashtags=8, max_length=300)
        self.assertEqual(fc.extract_hashtags(rebuilt), EXPECTED_CANONICAL)
        self.assertLessEqual(len(rebuilt), 300)
        self.assertTrue(rebuilt.startswith("Rockstar"))

    def test_tiktok_caption_hashtags_are_first_four_canonical(self):
        script, _ = load_video3()
        canonical = fc.canonical_hashtags(script["Description"])
        rebuilt = fc.with_canonical_hashtags(script["TikTokCaption"], canonical,
                                             max_hashtags=4, max_length=150)
        self.assertEqual(fc.extract_hashtags(rebuilt), EXPECTED_CANONICAL[:4])
        self.assertLessEqual(len(rebuilt), 150)

    def test_empty_canonical_leaves_caption_as_written(self):
        caption = "Big news today, find out what happened next."
        self.assertEqual(
            fc.with_canonical_hashtags(caption, [], max_hashtags=8, max_length=300),
            caption)

    def test_strips_model_hashtags_before_appending(self):
        # R6 changed the strip to trailing-only: a model hashtag at the
        # END of the caption is replaced by the canonical set...
        canonical = ["#TrendlineNow", "#GTA6", "#gaming"]
        result = fc.with_canonical_hashtags(
            "Breaking news today, more to come. #WrongTag #OldTag", canonical,
            max_hashtags=3, max_length=300)
        self.assertNotIn("#WrongTag", result)
        self.assertNotIn("#OldTag", result)
        self.assertEqual(fc.extract_hashtags(result), canonical)

    def test_inline_hashtag_kept_not_stripped(self):
        # ...but an INLINE hashtag is part of the caption text and is
        # kept ("Watch #GTA6 gameplay now" keeps #GTA6 — the old regex
        # deleted the topic word, not just the #).
        canonical = ["#TrendlineNow", "#GTA6"]
        result = fc.with_canonical_hashtags(
            "Watch #GTA6 gameplay now #news #tech", canonical,
            max_hashtags=2, max_length=150)
        self.assertIn("#GTA6", result)
        self.assertIn("#TrendlineNow", result)
        self.assertNotIn("#news", result)
        self.assertNotIn("#tech", result)
        self.assertTrue(result.startswith("Watch #GTA6 gameplay now"))

    def test_floor_guard_keeps_caption_text(self):
        # Canonical tags longer than the caption budget used to wipe the
        # caption text entirely (hashtags only). The text must survive.
        big_tags = ["#averylongtopichashtag%d" % i for i in range(8)]
        result = fc.with_canonical_hashtags(
            "Watch the full story here.", big_tags,
            max_hashtags=8, max_length=100)
        self.assertIn("Watch the full story here.", result)

    def test_prefers_line_two_over_stuffing_line(self):
        # Line 2 is the intended hashtag line; a keyword-stuffing line
        # elsewhere must not win the max-count contest.
        description = ("Title line\n#TrendlineNow #GTA6\n"
                       "Stuff #a #b #c #d #e #f #g #h #i #j")
        self.assertEqual(fc.canonical_hashtags(description),
                         ["#TrendlineNow", "#GTA6"])

    def test_extract_hashtags_dedupes_case_insensitively(self):
        self.assertEqual(
            fc.extract_hashtags("#GTA6 rocks #gta6 and #Gaming"),
            ["#GTA6", "#Gaming"])


class BoundaryHashTests(unittest.TestCase):
    """B24: only frame-boundary properties feed the chunk-invalidation hash."""

    def _props(self):
        return {
            "fps": 30,
            "introSeconds": 3.5,
            "outroSeconds": 5.0,
            "isCalm": False,
            "briefScenes": [{"durationInSeconds": 6.0}, {"durationInSeconds": 5.0}],
            "scenes": [
                {"durationInSeconds": 12.0, "narration": "First scene here."},
                {"durationInSeconds": 9.5, "narration": "Second scene here."},
            ],
            "musicPath": "/music/track-a.mp3",
            "title": "Some Title",
        }

    def test_same_boundaries_reformatted_same_hash(self):
        import chunk_ranges
        a = self._props()
        b = dict(a)
        b["scenes"] = [dict(s, extraKey="ignored") for s in a["scenes"]]
        b["musicPath"] = "/music/track-b.mp3"  # non-boundary change
        b["title"] = "A Different Title"
        self.assertEqual(chunk_ranges.boundary_hash(a),
                         chunk_ranges.boundary_hash(b))

    def test_duration_change_changes_hash(self):
        import chunk_ranges
        a = self._props()
        b = dict(a)
        b["scenes"] = [dict(a["scenes"][0], durationInSeconds=13.0),
                       a["scenes"][1]]
        self.assertNotEqual(chunk_ranges.boundary_hash(a),
                            chunk_ranges.boundary_hash(b))

    def test_fps_intro_outro_and_scene_count_change_hash(self):
        import chunk_ranges
        a = self._props()
        for key, value in [("fps", 25), ("introSeconds", 0),
                           ("outroSeconds", 0), ("isCalm", True)]:
            b = dict(a)
            b[key] = value
            self.assertNotEqual(chunk_ranges.boundary_hash(a),
                                chunk_ranges.boundary_hash(b),
                                f"{key} change must change the hash")
        b = dict(a)
        b["scenes"] = a["scenes"] + [{"durationInSeconds": 4.0}]
        self.assertNotEqual(chunk_ranges.boundary_hash(a),
                            chunk_ranges.boundary_hash(b))

    def test_cli_boundary_hash(self):
        import subprocess
        import tempfile
        props = self._props()
        with tempfile.NamedTemporaryFile("w", suffix=".json", delete=False) as f:
            json.dump(props, f)
            path = f.name
        try:
            out = subprocess.run(
                [sys.executable, os.path.join(HERE, "chunk_ranges.py"),
                 "--boundary-hash", path],
                capture_output=True, text=True, check=True)
            self.assertEqual(out.stdout.strip(),
                             __import__("chunk_ranges").boundary_hash(props))
        finally:
            os.unlink(path)


class QualityGateTests(unittest.TestCase):
    """Quality gate finish-time checks (Rishi 2026-11-23): CC BY credit,
    loudness ±1, silence parsing. Pure functions only — no ffmpeg."""

    def test_ccby_credit_for_ccby(self):
        credit = fc.ccby_credit_for("uplifting-tech-01.mp3")
        self.assertIn("Rising Light", credit)
        self.assertIn("CC BY 4.0", credit)

    def test_ccby_credit_for_cc0_is_none(self):
        self.assertIsNone(fc.ccby_credit_for("tense-newsroom-01.mp3"))

    def test_ccby_credit_for_unknown_is_none(self):
        self.assertIsNone(fc.ccby_credit_for("random-track.mp3"))
        self.assertIsNone(fc.ccby_credit_for(None))
        self.assertIsNone(fc.ccby_credit_for(""))

    def test_ensure_ccby_credit_appends_when_missing(self):
        desc, changed = fc.ensure_ccby_credit("A news video.", "uplifting-tech-01.mp3")
        self.assertTrue(changed)
        self.assertIn("Rising Light", desc)
        self.assertTrue(desc.startswith("A news video."))

    def test_ensure_ccby_credit_idempotent(self):
        desc, _ = fc.ensure_ccby_credit("A news video.", "uplifting-tech-01.mp3")
        desc2, changed2 = fc.ensure_ccby_credit(desc, "uplifting-tech-01.mp3")
        self.assertFalse(changed2)
        self.assertEqual(desc, desc2)

    def test_ensure_ccby_credit_cc0_noop(self):
        desc, changed = fc.ensure_ccby_credit("A news video.", "tense-newsroom-01.mp3")
        self.assertFalse(changed)
        self.assertEqual("A news video.", desc)

    def test_loudness_ok(self):
        self.assertTrue(fc.loudness_ok(-14.0))
        self.assertTrue(fc.loudness_ok(-13.2))
        self.assertTrue(fc.loudness_ok(-15.0))
        self.assertFalse(fc.loudness_ok(-12.9))
        self.assertFalse(fc.loudness_ok(-15.1))
        self.assertFalse(fc.loudness_ok(None))
        self.assertFalse(fc.loudness_ok("not a number"))

    def test_parse_silence(self):
        sample = (
            "[silencedetect @ 0x123] silence_start: 0\n"
            "[silencedetect @ 0x123] silence_end: 2.34 | silence_duration: 2.34\n"
            "[silencedetect @ 0x123] silence_start: 30.5\n"
            "[silencedetect @ 0x123] silence_end: 31.0 | silence_duration: 0.5\n"
        )
        self.assertEqual(fc.parse_silence(sample), [(0.0, 2.34), (30.5, 0.5)])
        self.assertEqual(fc.parse_silence(""), [])
        self.assertEqual(fc.parse_silence(None), [])

    def test_leading_silence_error(self):
        self.assertEqual(fc.leading_silence_error([(0.0, 2.5)]), (0.0, 2.5))
        self.assertIsNone(fc.leading_silence_error([(0.0, 1.0)]))
        self.assertIsNone(fc.leading_silence_error([(30.5, 6.0)]))
        self.assertIsNone(fc.leading_silence_error([]))

    def test_verify_final_unparseable_loudness_fails(self):
        """L2: an unparseable loudnorm JSON block fails verification
        (return 1) and names the job/orientation + reason, instead of
        shipping with unverified loudness."""
        real_run, real_gate = fc.run, fc.gate_check

        def fake_run(cmd, check=True):
            if "loudnorm" in " ".join(cmd):
                # loudnorm ran but emitted no JSON block at all.
                return SimpleNamespace(
                    returncode=0, stdout="",
                    stderr="[Parsed_loudnorm @ 0x1] input_i: -14.0\n"
                           "[Parsed_loudnorm @ 0x1] loudnorm failed: no stats\n")
            return SimpleNamespace(returncode=0, stdout="", stderr="")

        fc.run = fake_run
        fc.gate_check = lambda *a, **k: True
        try:
            err = io.StringIO()
            with contextlib.redirect_stderr(err):
                rc = fc.verify_final("fake-final.mp4", 1920, 1080, 900,
                                     job="job-20261126-test",
                                     orientation="landscape")
            self.assertEqual(rc, 1)
            self.assertIn("loudness JSON unparseable", err.getvalue())
            self.assertIn("job-20261126-test", err.getvalue())
            self.assertIn("landscape", err.getvalue())
        finally:
            fc.run, fc.gate_check = real_run, real_gate


# L9: driver for test_finish_logs_interruption. Runs finish-chunks.py
# main() in a child process with the heavy steps stubbed; only the
# loudnorm call really stalls (30s sleep), so the parent can SIGTERM
# it mid-loudnorm and check the interruption is logged.
_INTERRUPT_DRIVER = '''
import importlib.util
import sys
import time
from types import SimpleNamespace

_spec = importlib.util.spec_from_file_location(
    "finish_chunks", "__PROD__/finish-chunks.py")
fc = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(fc)

fc.compute_ranges = lambda *a, **k: (900, [])
fc.scene_starts = lambda *a, **k: ([], [], 0, 0, 900)
fc.check_chunks = lambda *a, **k: 0
fc.probe_duration = lambda p: 10.0
fc.probe_video_format = lambda p: {"pix_fmt": "yuv420p", "color_range": "tv"}


def fake_run(cmd, check=True):
    if "loudnorm" in " ".join(str(c) for c in cmd):
        print("loudnorm-stub-sleeping", flush=True)
        time.sleep(30)
    return SimpleNamespace(returncode=0, stdout="", stderr="")


fc.run = fake_run
sys.argv = ["finish-chunks.py", "--job", "__JOB__", "--orientation", "landscape"]
sys.exit(fc.main())
'''


class InterruptionTests(unittest.TestCase):
    def test_finish_logs_interruption(self):
        # L9: SIGTERM mid-loudnorm must be logged with job, orientation, step.
        tmp = tempfile.mkdtemp(prefix="finish-interrupt-")
        try:
            job = os.path.join(tmp, "job-20261126-test")
            os.makedirs(os.path.join(job, "chunks-landscape"))
            with open(os.path.join(job, "script.json"), "w") as f:
                json.dump({"Title": "Test Video", "Profile": "trendlinenow",
                           "Language": "en"}, f)
            with open(os.path.join(job, "remotion-props-1920x1080.json"), "w") as f:
                json.dump({"scenes": [], "fps": 30}, f)
            driver = os.path.join(tmp, "driver.py")
            src = _INTERRUPT_DRIVER.replace("__PROD__", HERE).replace("__JOB__", job)
            with open(driver, "w") as f:
                f.write(src)
            proc = subprocess.Popen(
                [sys.executable, driver],
                stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                text=True, bufsize=1)
            seen = []
            saw_marker = False
            deadline = time.monotonic() + 20
            while time.monotonic() < deadline:
                line = proc.stdout.readline()
                if not line:
                    break
                seen.append(line)
                if "loudnorm-stub-sleeping" in line:
                    saw_marker = True
                    break
            self.assertTrue(saw_marker, "child never reached the loudnorm stall")
            proc.send_signal(signal.SIGTERM)
            out, err = proc.communicate(timeout=15)
            combined = "".join(seen) + out + err
            self.assertNotEqual(proc.returncode, 0)
            self.assertIn("interrupted", combined)
            self.assertIn("job-20261126-test", combined)
            self.assertIn("landscape", combined)
            self.assertIn("3-loudnorm", combined)
        finally:
            shutil.rmtree(tmp, ignore_errors=True)


class TestFinishThumbnailLogsPlainCrop(unittest.TestCase):
    # L80 — a landscape finish builds a plain 1280x720 crop, never the
    # fix-#3 variants. The log must say so (and must not claim variants
    # were built). Runs main() in-process with the heavy steps stubbed;
    # VIDEOS_DIR is patched to the temp dir so nothing lands in the real
    # watch dir.
    def test_finish_thumbnail_logs_plain_crop(self):
        tmp = tempfile.mkdtemp(prefix="finish-thumb-")
        try:
            job = os.path.join(tmp, "job-20261126-test")
            os.makedirs(os.path.join(job, "chunks-landscape"))
            art = os.path.join(job, "scene01-art-landscape.png")
            with open(art, "wb") as f:
                f.write(b"\x89PNG\r\n\x1a\n")
            with open(os.path.join(job, "script.json"), "w") as f:
                json.dump({
                    "Title": "Test Video", "Profile": "trendlinenow",
                    "Language": "en", "Description": "Test description.",
                    "Tags": ["test"],
                    "Scenes": [
                        {"Narration": "Scene zero.", "captionText": "Scene zero.",
                         "LandscapeImagePath": art},
                        {"Narration": "Scene one.", "captionText": "Scene one.",
                         "LandscapeImagePath": art},
                    ],
                }, f)
            with open(os.path.join(job, "remotion-props-1920x1080.json"), "w") as f:
                json.dump({"scenes": [{"durationInSeconds": 10,
                                       "captionText": "Scene zero."}],
                           "fps": 30}, f)

            real_run, real_compute, real_starts, real_check = (
                fc.run, fc.compute_ranges, fc.scene_starts, fc.check_chunks)
            real_probe_dur, real_probe_fmt, real_verify, real_videos = (
                fc.probe_duration, fc.probe_video_format,
                fc.verify_final, fc.VIDEOS_DIR)
            try:
                fc.compute_ranges = lambda *a, **k: (900, [])
                fc.scene_starts = lambda *a, **k: ([], [0, 900], 0, 0, 900)
                fc.check_chunks = lambda *a, **k: 0
                fc.probe_duration = lambda p: 10.0
                fc.probe_video_format = lambda p: {"pix_fmt": "yuv420p",
                                                   "color_range": "tv"}
                fc.verify_final = lambda *a, **k: 0
                fc.VIDEOS_DIR = tmp

                def fake_run(cmd, check=True):
                    if "loudnorm" in " ".join(str(c) for c in cmd):
                        # The loudnorm temp is shutil.move'd to the final.
                        open(cmd[-1], "wb").close()
                    return SimpleNamespace(returncode=0, stdout="", stderr="")

                fc.run = fake_run
                out = io.StringIO()
                old_argv = sys.argv
                try:
                    sys.argv = ["finish-chunks.py", "--job", job,
                                "--orientation", "landscape"]
                    with contextlib.redirect_stdout(out):
                        rc = fc.main()
                finally:
                    sys.argv = old_argv
                log = out.getvalue()
                self.assertEqual(rc, 0)
                self.assertIn("plain 1280x720 crop", log)
                # "variant" appears only in the disclaimer line itself —
                # no other line may claim variants were built.
                rest = "\n".join(
                    line for line in log.splitlines()
                    if "does not build fix-#3 variants" not in line)
                self.assertNotIn("variant", rest.lower())
            finally:
                (fc.run, fc.compute_ranges, fc.scene_starts,
                 fc.check_chunks) = (real_run, real_compute, real_starts,
                                     real_check)
                (fc.probe_duration, fc.probe_video_format,
                 fc.verify_final, fc.VIDEOS_DIR) = (
                    real_probe_dur, real_probe_fmt, real_verify, real_videos)
        finally:
            shutil.rmtree(tmp, ignore_errors=True)


class TestStoryCutFailureGoesToStderr(unittest.TestCase):
    # L81 — a failed story cut must be visible on stderr with the job id
    # (it used to go to stdout with no job id, so the driver could not
    # tell a missing cut happened without grepping). The cut is still
    # bonus-only: the finish exits 0 and the Short itself is unaffected.
    def test_story_cut_failure_goes_to_stderr_with_job(self):
        tmp = tempfile.mkdtemp(prefix="finish-storycut-")
        try:
            job = os.path.join(tmp, "job-20261126-test")
            os.makedirs(os.path.join(job, "chunks-vertical"))
            with open(os.path.join(job, "script.json"), "w") as f:
                json.dump({
                    "Title": "Test Video", "Profile": "trendlinenow",
                    "Language": "en", "Description": "Test description.",
                    "Tags": ["test"], "ReelsCaption": "Test reel.",
                    "TikTokCaption": "Test tok.",
                    "Scenes": [
                        {"Narration": "Scene zero.", "captionText": "Scene zero."},
                    ],
                }, f)
            with open(os.path.join(job, "remotion-props-1080x1920.json"), "w") as f:
                json.dump({"scenes": [{"durationInSeconds": 81,
                                       "captionText": "Scene zero."}],
                           "fps": 30}, f)

            real_run, real_compute, real_starts, real_check = (
                fc.run, fc.compute_ranges, fc.scene_starts, fc.check_chunks)
            real_probe_dur, real_probe_fmt, real_verify, real_videos = (
                fc.probe_duration, fc.probe_video_format,
                fc.verify_final, fc.VIDEOS_DIR)
            try:
                # 81s Short: the story cut is attempted.
                fc.compute_ranges = lambda *a, **k: (2430, [])
                fc.scene_starts = lambda *a, **k: ([], [0, 2430], 0, 0, 2430)
                fc.check_chunks = lambda *a, **k: 0
                fc.probe_duration = lambda p: 10.0
                fc.probe_video_format = lambda p: {"pix_fmt": "yuv420p",
                                                   "color_range": "tv"}
                fc.verify_final = lambda *a, **k: 0
                fc.VIDEOS_DIR = tmp

                def fake_run(cmd, check=True):
                    joined = " ".join(str(c) for c in cmd)
                    if "loudnorm" in joined:
                        open(cmd[-1], "wb").close()
                    if "-story60.mp4" in joined and "ffmpeg" in joined:
                        return SimpleNamespace(returncode=1, stdout="",
                                              stderr="ffmpeg boom")
                    return SimpleNamespace(returncode=0, stdout="", stderr="")

                fc.run = fake_run
                out, err = io.StringIO(), io.StringIO()
                old_argv = sys.argv
                try:
                    sys.argv = ["finish-chunks.py", "--job", job,
                                "--orientation", "vertical"]
                    with contextlib.redirect_stdout(out), \
                            contextlib.redirect_stderr(err):
                        rc = fc.main()
                finally:
                    sys.argv = old_argv
                self.assertEqual(rc, 0)
                stderr = err.getvalue()
                self.assertIn("job-20261126-test", stderr)
                self.assertIn("story cut", stderr)
                self.assertIn("exit 1", stderr)
                self.assertNotIn("story cut ffmpeg failed", out.getvalue())
            finally:
                (fc.run, fc.compute_ranges, fc.scene_starts,
                 fc.check_chunks) = (real_run, real_compute, real_starts,
                                     real_check)
                (fc.probe_duration, fc.probe_video_format,
                 fc.verify_final, fc.VIDEOS_DIR) = (
                    real_probe_dur, real_probe_fmt, real_verify, real_videos)
        finally:
            shutil.rmtree(tmp, ignore_errors=True)


class TestMainEarlyInputFailures(unittest.TestCase):
    # L58 — a corrupt script.json must fail as one clean FINISH FAILED
    # line (job + orientation + reason, return code 1), not a bare
    # traceback as the only record.
    def test_main_bad_script_json_logs_finish_failed(self):
        tmp = tempfile.mkdtemp(prefix="finish-badscript-")
        try:
            job = os.path.join(tmp, "job-20261126-test")
            os.makedirs(job)
            # Props must exist (missing props is the guarded return-2
            # path); the script is corrupt JSON.
            with open(os.path.join(job, "remotion-props-1920x1080.json"), "w") as f:
                f.write("{}")
            with open(os.path.join(job, "script.json"), "w") as f:
                f.write("{not valid json")
            argv = ["finish-chunks.py", "--job", job, "--orientation", "landscape"]
            err = io.StringIO()
            old_argv = sys.argv
            try:
                sys.argv = argv
                with contextlib.redirect_stderr(err):
                    rc = fc.main()
            finally:
                sys.argv = old_argv
            stderr = err.getvalue()
            self.assertEqual(rc, 1)
            self.assertIn("FINISH FAILED", stderr)
            self.assertIn(job, stderr)
            self.assertIn("landscape", stderr)
            self.assertNotIn("Traceback", stderr)
        finally:
            shutil.rmtree(tmp, ignore_errors=True)


class SelectPayoffScenesTests(unittest.TestCase):
    # Fix #5 rework — the 2nd Short is a payoff window ASSEMBLED from the
    # job's own scenes: a contiguous run near the END of the video,
    # strictly after the main Short's last scene, totalling
    # [30, 59.5]s including the outro, with trailing closing/CTA scenes
    # skipped so the window opens on a hook. Pure function — no ffmpeg,
    # no files.
    DRAKE_LANDSCAPE_PROPS = "/home/hatch/workspace/BroadcastContentAutomation/work/trendlinenow/job-20261127-193517/remotion-props-1920x1080.json"
    DRAKE_SCRIPT = "/home/hatch/workspace/BroadcastContentAutomation/work/trendlinenow/job-20261127-193517/script.json"
    DRAKE_VERTICAL_PROPS = "/home/hatch/workspace/BroadcastContentAutomation/work/trendlinenow/job-20261127-193517/remotion-props-1080x1920.json"

    def _drake_inputs(self):
        with open(self.DRAKE_LANDSCAPE_PROPS, encoding="utf-8") as f:
            landscape_props = json.load(f)
        with open(self.DRAKE_SCRIPT, encoding="utf-8") as f:
            script = json.load(f)
        with open(self.DRAKE_VERTICAL_PROPS, encoding="utf-8") as f:
            vertical_props = json.load(f)
        durations = [s["durationInSeconds"] for s in landscape_props["scenes"]]
        narrations = [s.get("Narration") for s in script["Scenes"]]
        return durations, vertical_props["outroSeconds"], narrations

    def test_drake_payoff_window(self):
        # Drake: 56 scenes, main Short covers vertical-props scenes
        # 0..6, outro 10.878s. Trailing scenes 54 ("Follow
        # @spotlightnowofficial...") and 55 (closing question + comments)
        # are CTA, so the walk starts at scene 53 and accumulates
        # backwards: 53+52+51+50+49 = 42.32s + 10.878s outro = 53.20s;
        # adding scene 48 would reach 62.05s > 59.5s, so the window
        # stops at 49..53 — disjoint from the main Short, self-contained,
        # and within duration.
        if not os.path.exists(self.DRAKE_LANDSCAPE_PROPS):
            raise unittest.SkipTest("Drake landscape props not found")
        durations, outro, narrations = self._drake_inputs()
        scenes, reason = fc.select_payoff_scenes(durations, outro, 6, narrations)
        self.assertIsNone(reason)
        self.assertEqual(scenes, [49, 50, 51, 52, 53])
        self.assertTrue(all(s > 6 for s in scenes))
        total = sum(durations[s] for s in scenes) + outro
        self.assertAlmostEqual(total, 53.20, places=2)
        self.assertGreaterEqual(total, 30)
        self.assertLessEqual(total, 59.5)

    def test_drake_window_opens_on_story_beat_not_cta(self):
        # The window must not end on the follow prompt / closing
        # question: scenes 54 and 55 are skipped even though they are
        # the last scenes in the video.
        if not os.path.exists(self.DRAKE_SCRIPT):
            raise unittest.SkipTest("Drake script not found")
        _, _, narrations = self._drake_inputs()
        self.assertTrue(fc._is_closing_cta(narrations[54]))
        self.assertTrue(fc._is_closing_cta(narrations[55]))
        self.assertFalse(fc._is_closing_cta(narrations[53]))

    def test_overlap_guard_blocks_window_when_main_short_covers_all(self):
        # 11 scenes x 7s, main Short covers 0..10: no scene is left.
        scenes, reason = fc.select_payoff_scenes([7.0] * 11, 10.0, 10)
        self.assertIsNone(scenes)
        self.assertIn("no scene is left", reason)

    def test_backwards_walk_stops_at_59_5s(self):
        # 20 scenes x 8s, outro 10s, main Short covers scene 0: the walk
        # takes 6 scenes (48s + 10s = 58s); a 7th would reach 66s.
        scenes, reason = fc.select_payoff_scenes([8.0] * 20, 10.0, 0)
        self.assertIsNone(reason)
        self.assertEqual(scenes, [14, 15, 16, 17, 18, 19])
        self.assertAlmostEqual(sum(8.0 for _ in scenes) + 10.0, 58.0)

    def test_window_too_short_is_no_cut(self):
        # Only 3 scenes after the main Short, 5s each: 15s + 10s outro
        # = 25s < 30s even taking every available scene.
        scenes, reason = fc.select_payoff_scenes([5.0] * 4, 10.0, 0)
        self.assertIsNone(scenes)
        self.assertIn("30", reason)

    def test_trailing_cta_scenes_skipped(self):
        # Scenes 6 (follow prompt) and 7 (closing question) are skipped;
        # the window is scenes 1..5 (40s + 10s outro = 50s).
        durations = [8.0] * 8
        narrations = ["Story beat %d opens here. More follows." % i for i in range(6)]
        narrations += ["Follow @spotlightnowofficial for a new star story every day.",
                       "What is your favourite moment so far? Tell us all about it in the comments below."]
        scenes, reason = fc.select_payoff_scenes(durations, 10.0, 0, narrations)
        self.assertIsNone(reason)
        self.assertEqual(scenes, [1, 2, 3, 4, 5])

    def test_without_narrations_cta_is_not_skipped(self):
        # The CTA skip needs narrations: without them the walk starts at
        # the last scene as-is.
        scenes, reason = fc.select_payoff_scenes([8.0] * 8, 10.0, 0)
        self.assertIsNone(reason)
        self.assertEqual(scenes, [2, 3, 4, 5, 6, 7])

    def test_all_remaining_scenes_are_cta_is_no_cut(self):
        durations = [8.0] * 4
        narrations = ["Story beat opens here. More follows.",
                      "Follow @spotlightnowofficial for more.",
                      "Follow @spotlightnowofficial for more.",
                      "What was your favourite part? Tell us in the comments below."]
        scenes, reason = fc.select_payoff_scenes(durations, 10.0, 0, narrations)
        self.assertIsNone(scenes)
        self.assertIn("closing/CTA", reason)

    def test_single_scene_plus_outro_over_59_5s_is_no_cut(self):
        scenes, reason = fc.select_payoff_scenes([8.0, 50.0], 10.0, 0)
        self.assertIsNone(scenes)
        self.assertIn("exceeds", reason)

    def test_empty_durations_is_no_cut(self):
        scenes, reason = fc.select_payoff_scenes([], 10.0, 0)
        self.assertIsNone(scenes)
        self.assertIn("no scenes", reason)

    def test_missing_outro_is_no_cut(self):
        scenes, reason = fc.select_payoff_scenes([8.0] * 10, 0, 0)
        self.assertIsNone(scenes)
        self.assertIn("outro", reason)


class Short2HelperTests(unittest.TestCase):
    # Pure helpers behind the assembled 2nd Short: composition
    # durations from scene starts, ASS caption building, asset checks,
    # CTA detection.

    def test_scene_composition_durations(self):
        # 2 scenes (10s, 8s), outro 5s, 30fps, no brief/intro: starts
        # 0 / 294 / 678; scene 0 = 9.8s (one 0.2s transition shared),
        # scene 1 = (678 - 294 - 150 outro frames) / 30 = 7.8s.
        props = {"scenes": [{"durationInSeconds": 10}, {"durationInSeconds": 8}],
                 "briefScenes": [], "fps": 30, "introSeconds": 0,
                 "outroSeconds": 5, "isCalm": False}
        _, starts, _, fps, _ = fc.scene_starts(props)
        self.assertEqual(fc.scene_composition_durations(starts, fps, 5), [9.8, 7.8])

    def test_scene_composition_durations_no_outro(self):
        # No outro: the last scene runs to total_frames, so it keeps its
        # full 8.0s (only the shared transition with scene 0 is gone
        # from scene 0's slot).
        props = {"scenes": [{"durationInSeconds": 10}, {"durationInSeconds": 8}],
                 "briefScenes": [], "fps": 30, "introSeconds": 0,
                 "outroSeconds": 0, "isCalm": False}
        _, starts, _, fps, _ = fc.scene_starts(props)
        self.assertEqual(fc.scene_composition_durations(starts, fps, 0), [9.8, 8.0])

    def test_build_short2_ass_cumulative_word_reveal(self):
        ass = fc.build_short2_ass(
            ["One two three four five six seven.", "Second scene here now."],
            [8.0, 4.0])
        self.assertIn("DejaVu Sans,52", ass)
        self.assertIn("PlayResY: 1920", ass)
        dialogues = [line for line in ass.splitlines() if line.startswith("Dialogue:")]
        self.assertEqual(len(dialogues), 3)  # 2 chunks + 1 chunk
        self.assertIn("One two three four", dialogues[0])
        self.assertIn("One two three four five six seven.", dialogues[1])
        self.assertIn("Second scene here now.", dialogues[2])
        # First chunk covers 0-4s of scene 0; second chunk 4-8s; scene 1
        # starts at 8s.
        self.assertIn("0:00:00.00,0:00:04.00", dialogues[0])
        self.assertIn("0:00:04.00,0:00:08.00", dialogues[1])
        self.assertIn("0:00:08.00,0:00:12.00", dialogues[2])

    def test_build_short2_ass_empty_narration_skipped(self):
        ass = fc.build_short2_ass(["", "Some words here now."], [5.0, 4.0])
        dialogues = [line for line in ass.splitlines() if line.startswith("Dialogue:")]
        self.assertEqual(len(dialogues), 1)
        self.assertIn("0:00:05.00,0:00:09.00", dialogues[0])

    def test_missing_short2_assets(self):
        tmp = tempfile.mkdtemp(prefix="short2-assets-")
        try:
            with open(os.path.join(tmp, "scene02-art.png"), "wb") as f:
                f.write(b"png")
            with open(os.path.join(tmp, "scene02.mp3"), "wb") as f:
                f.write(b"mp3")
            # scene02 complete; scene03 art present but mp3 missing.
            with open(os.path.join(tmp, "scene03-art.png"), "wb") as f:
                f.write(b"png")
            missing = fc.missing_short2_assets(tmp, [2, 3])
            self.assertEqual(missing, [os.path.join(tmp, "scene03.mp3")])
            self.assertEqual(fc.missing_short2_assets(tmp, [2]), [])
        finally:
            shutil.rmtree(tmp, ignore_errors=True)

    def test_missing_short2_assets_empty_file_counts_as_missing(self):
        tmp = tempfile.mkdtemp(prefix="short2-assets-empty-")
        try:
            with open(os.path.join(tmp, "scene04-art.png"), "wb"):
                pass  # zero-byte art is a hole, not an asset
            with open(os.path.join(tmp, "scene04.mp3"), "wb") as f:
                f.write(b"mp3")
            self.assertEqual(fc.missing_short2_assets(tmp, [4]),
                             [os.path.join(tmp, "scene04-art.png")])
        finally:
            shutil.rmtree(tmp, ignore_errors=True)

    def test_is_closing_cta(self):
        self.assertTrue(fc._is_closing_cta(
            "Follow @spotlightnowofficial for a new star story every day."))
        self.assertTrue(fc._is_closing_cta(
            "What is your favourite moment? Tell us in the comments below."))
        self.assertFalse(fc._is_closing_cta(
            "He has turned his entire life into a success story."))
        self.assertFalse(fc._is_closing_cta(""))
        self.assertFalse(fc._is_closing_cta(None))

    def test_find_music_bed(self):
        tmp = tempfile.mkdtemp(prefix="short2-bed-")
        try:
            bed = os.path.join(tmp, "tense-newsroom-02.mp3")
            with open(bed, "wb") as f:
                f.write(b"mp3")
            self.assertEqual(fc.find_music_bed(tmp, "tense-newsroom-02.mp3"), bed)
            self.assertEqual(fc.find_music_bed(tmp, None, bed), bed)
            self.assertIsNone(fc.find_music_bed(tmp, "no-such-track.mp3"))
        finally:
            shutil.rmtree(tmp, ignore_errors=True)


class PickSecondShortTitleTests(unittest.TestCase):
    # Fix #5 rework — the 2nd Short's own title: variant 3 when usable,
    # else derived from the payoff's opening narration, else the main
    # title with fallback True. Mirrors ShortRules.PickSecondShortTitle.

    def test_variant_3_used(self):
        title, fallback = fc.pick_second_short_title(
            "Main Title", ["V1", "V2", "Payoff Title Here"],
            "The payoff lands here. More follows.")
        self.assertEqual(title, "Payoff Title Here")
        self.assertFalse(fallback)

    def test_variant_3_whitespace_stripped(self):
        title, fallback = fc.pick_second_short_title(
            "Main Title", ["V1", "V2", "  Payoff Title Here  "], "")
        self.assertEqual(title, "Payoff Title Here")
        self.assertFalse(fallback)

    def test_variant_3_equal_to_main_title_falls_through_to_derivation(self):
        title, fallback = fc.pick_second_short_title(
            "Main Title", ["V1", "V2", "main title"],
            "The payoff lands here. More follows.")
        self.assertEqual(title, "The payoff lands here")
        self.assertFalse(fallback)

    def test_variant_3_empty_falls_through_to_derivation(self):
        title, fallback = fc.pick_second_short_title(
            "Main Title", ["V1", "V2", "   "],
            "The payoff lands here. More follows.")
        self.assertEqual(title, "The payoff lands here")
        self.assertFalse(fallback)

    def test_empty_variants_derive_from_narration(self):
        title, fallback = fc.pick_second_short_title(
            "Main Title", [], "The payoff lands here. More follows.")
        self.assertEqual(title, "The payoff lands here")
        self.assertFalse(fallback)

    def test_question_mark_kept_exclamation_kept_period_dropped(self):
        title, _ = fc.pick_second_short_title(
            "Main Title", [], "Who won the night? The answer surprises.")
        self.assertEqual(title, "Who won the night?")
        title, _ = fc.pick_second_short_title(
            "Main Title", [], "He did it again! Nobody saw it coming.")
        self.assertEqual(title, "He did it again!")

    def test_long_first_sentence_truncated_to_100_chars(self):
        narration = " ".join(["word"] * 40) + ". A second sentence follows."
        title, fallback = fc.pick_second_short_title("Main Title", [], narration)
        self.assertLessEqual(len(title), 100)
        self.assertFalse(fallback)

    def test_no_variant_no_narration_falls_back_to_main_title(self):
        err = io.StringIO()
        with contextlib.redirect_stderr(err):
            title, fallback = fc.pick_second_short_title("Main Title", [], "")
        self.assertEqual(title, "Main Title")
        self.assertTrue(fallback)
        self.assertIn("WARNING", err.getvalue())
        self.assertIn("Short2TitleFallback", err.getvalue())

    def test_no_variant_blank_narration_falls_back(self):
        title, fallback = fc.pick_second_short_title("Main Title", None, "   ")
        self.assertEqual(title, "Main Title")
        self.assertTrue(fallback)


class TestSecondShortCut(unittest.TestCase):
    # Fix #5 rework — the second Short is a payoff cut ASSEMBLED from the
    # job's own assets (step 3c): per-scene zoompan segments from the
    # payoff scenes' portrait art + narration, plus the outro/end card
    # cut from the Short itself, music bed under the scenes, ASS captions
    # burned in, loudness-normalized to -14 LUFS. No re-render, no new
    # images, no new TTS, no landscape final needed — and its own title
    # (variant 3, else derived), its own thumbnail still, and a +12h
    # publish hold as fallback. Runs main() in-process with the heavy
    # steps stubbed, like the L80/L81 tests; VIDEOS_DIR is patched to
    # the temp dir.
    #
    # Fixture: 79.5s vertical Short, 11 scenes, starts at 0/210/.../2100
    # frames (7s scenes) + total 2385 — main Short covers scenes 0..10.
    # Landscape props: 56 scenes x 8s, starts at 234*i frames + total
    # 13292 (8s scenes minus the 0.2s transition share), outro 6.27s.
    # Vertical outro 10.878s. Selection: walk back from scene 55,
    # 6 scenes x 8s + 10.878s outro = 58.878s — window scenes 50..55,
    # strictly after the main Short's scenes 0..10.
    STARTS_79S = [0, 210, 420, 630, 840, 1050, 1260, 1470, 1680, 1890, 2100, 2385]
    LANDSCAPE_STARTS = [234 * i for i in range(56)] + [13292]

    def _write_job(self, tmp, title_variants=None, landscape_props=True,
                   missing_art=None):
        job = os.path.join(tmp, "job-20261126-test")
        os.makedirs(os.path.join(job, "chunks-vertical"))
        with open(os.path.join(job, "script.json"), "w") as f:
            json.dump({
                "Title": "Test Video", "Profile": "trendlinenow",
                "Language": "en", "Description": "Test description.",
                "Tags": ["test"], "ReelsCaption": "Test reel.",
                "TikTokCaption": "Test tok.",
                "TitleVariants": title_variants if title_variants is not None else [],
                "Scenes": [
                    {"Narration": f"Scene {i} opens the next beat. More detail follows here.",
                     "captionText": f"Scene {i}."}
                    for i in range(56)
                ],
            }, f)
        with open(os.path.join(job, "remotion-props-1080x1920.json"), "w") as f:
            json.dump({"scenes": [{"durationInSeconds": 7,
                                   "captionText": f"Scene {i}."}
                                  for i in range(11)],
                       "fps": 30, "outroSeconds": 10.878,
                       "backgroundMusicPath": "tense-newsroom-02.mp3",
                       "backgroundMusicVolume": 0.116}, f)
        if landscape_props:
            with open(os.path.join(job, "remotion-props-1920x1080.json"), "w") as f:
                json.dump({"scenes": [{"durationInSeconds": 8,
                                       "captionText": f"Scene {i}."}
                                      for i in range(56)],
                           "fps": 30, "outroSeconds": 6.27,
                           "backgroundMusicPath": "tense-newsroom-02.mp3",
                           "backgroundMusicVolume": 0.116}, f)
        # Music bed (bare filename in the props, lives in the job dir)
        # and the payoff scenes' portrait art + narration mp3s — the
        # assets the assembly builds from.
        with open(os.path.join(job, "tense-newsroom-02.mp3"), "wb") as f:
            f.write(b"bed")
        for i in range(50, 56):
            if missing_art is not None and i == missing_art:
                continue
            with open(os.path.join(job, f"scene{i:02d}-art.png"), "wb") as f:
                f.write(b"png")
            with open(os.path.join(job, f"scene{i:02d}.mp3"), "wb") as f:
                f.write(b"mp3")
        return job

    def _stub(self, total_frames=2385, starts=None):
        real_run, real_compute, real_starts, real_check = (
            fc.run, fc.compute_ranges, fc.scene_starts, fc.check_chunks)
        real_probe_dur, real_probe_fmt, real_verify, real_videos = (
            fc.probe_duration, fc.probe_video_format,
            fc.verify_final, fc.VIDEOS_DIR)
        fc.compute_ranges = lambda *a, **k: (total_frames, [])
        if starts is None:
            # Dispatch on the props' scene count: the vertical props
            # (11 scenes) get the vertical starts, the landscape props
            # (56 scenes) get the landscape starts.
            def _starts(props):
                if len((props or {}).get("scenes") or []) == 56:
                    return ([], list(self.LANDSCAPE_STARTS), 0, 30, 13292)
                return ([], list(self.STARTS_79S), 0, 30, total_frames)
            fc.scene_starts = _starts
        else:
            fc.scene_starts = lambda *a, **k: ([], list(starts), 0, 30, total_frames)
        fc.check_chunks = lambda *a, **k: 0
        fc.probe_duration = lambda p: 10.0
        fc.probe_video_format = lambda p: {"pix_fmt": "yuv420p",
                                           "color_range": "tv"}
        fc.verify_final = lambda *a, **k: 0
        return (real_run, real_compute, real_starts, real_check,
                real_probe_dur, real_probe_fmt, real_verify, real_videos)

    def _unstub(self, saved):
        (real_run, real_compute, real_starts, real_check,
         real_probe_dur, real_probe_fmt, real_verify, real_videos) = saved
        (fc.run, fc.compute_ranges, fc.scene_starts,
         fc.check_chunks) = (real_run, real_compute, real_starts, real_check)
        (fc.probe_duration, fc.probe_video_format,
         fc.verify_final, fc.VIDEOS_DIR) = (
            real_probe_dur, real_probe_fmt, real_verify, real_videos)

    def _fake_run(self, short2_rc=0, short_duration="79.5",
                  thumb_mode="distinct"):
        thumb_grabs = {"n": 0}

        def fake_run(cmd, check=True):
            joined = " ".join(str(c) for c in cmd)
            if "ffprobe" in joined:
                if "-short2.mp4" in joined:
                    return SimpleNamespace(returncode=0, stdout="54.8",
                                          stderr="")
                if "-story60.mp4" in joined:
                    return SimpleNamespace(returncode=0, stdout="59.5",
                                          stderr="")
                if joined.endswith("test-video-en-short.mp4"):
                    return SimpleNamespace(returncode=0,
                                          stdout=short_duration,
                                          stderr="")
                return SimpleNamespace(returncode=0, stdout="10.0",
                                      stderr="")
            elif "ffmpeg" in joined:
                if "-frames:v" in joined:
                    # Thumbnail frame grabs: the main Short's first
                    # frame vs the short2 frame. Distinct bytes, so the
                    # md5 check passes — unless thumb_mode asks for an
                    # identical first grab (retry test).
                    if "mainframe" in joined:
                        content = b"MAINFRAME"
                    else:
                        if thumb_mode == "identical_first" and thumb_grabs["n"] == 0:
                            content = b"MAINFRAME"
                        else:
                            content = b"SHORT2FRAME"
                        thumb_grabs["n"] += 1
                    with open(cmd[-1], "wb") as f:
                        f.write(content)
                elif joined.endswith("-short2.mp4"):
                    # The final-encode step of the assembly — the only
                    # ffmpeg call whose output is the cut itself. (It
                    # also contains "loudnorm" in its filter graph, so
                    # this check must come before the loudnorm branch.)
                    if short2_rc != 0:
                        return SimpleNamespace(returncode=short2_rc,
                                              stdout="", stderr="ffmpeg boom")
                    open(cmd[-1], "wb").close()
                elif "loudnorm" in joined:
                    open(cmd[-1], "wb").close()
                elif cmd[-1].endswith(".mp4"):
                    # Every other ffmpeg step (story60 cut, per-scene
                    # segments, concats, outro cut) writes its output
                    # as the last argument.
                    open(cmd[-1], "wb").close()
            return SimpleNamespace(returncode=0, stdout="", stderr="")
        return fake_run

    def _run_main(self, job, fc_run, total_frames=2385, starts=None):
        saved = self._stub(total_frames, starts)
        tmp = os.path.dirname(job)
        fc.VIDEOS_DIR = tmp
        fc.run = fc_run
        out, err = io.StringIO(), io.StringIO()
        old_argv = sys.argv
        try:
            sys.argv = ["finish-chunks.py", "--job", job,
                        "--orientation", "vertical"]
            with contextlib.redirect_stdout(out), \
                    contextlib.redirect_stderr(err):
                rc = fc.main()
        finally:
            sys.argv = old_argv
            self._unstub(saved)
        return rc, out.getvalue(), err.getvalue()

    def _short2_sidecar(self, tmp):
        with open(os.path.join(tmp, "trendlinenow",
                               "test-video-en-short2.json")) as f:
            return json.load(f)

    def test_vertical_over_60s_assembles_payoff_short2_from_job_assets(self):
        tmp = tempfile.mkdtemp(prefix="finish-short2-")
        try:
            job = self._write_job(tmp)
            rc, log, stderr = self._run_main(job, self._fake_run())
            self.assertEqual(rc, 0)
            slug = "test-video"
            short2 = os.path.join(tmp, "trendlinenow", slug + "-en-short2.mp4")
            self.assertTrue(os.path.exists(short2),
                            f"short2 file missing; log:\n{log}\nstderr:\n{stderr}")
            self.assertIn("second Short", log)
            self.assertIn("payoff scenes 50..55", log)
            self.assertIn("assembled from job assets", log)
            self.assertIn("PublishAfterUtc", log)
            self.assertIn("fallback hold", log)
            self.assertIn("sibling-anchored", log)
            # No trace of the old landscape-cut implementation.
            self.assertNotIn("verticalized", log)
            self.assertNotIn("from landscape final", log)
            self.assertNotIn("second Short assembly failed", stderr)
            # Own thumbnail still next to the cut.
            self.assertTrue(os.path.exists(
                os.path.join(tmp, "trendlinenow",
                             slug + "-en-short2.thumb.jpg")))
            # Own sidecar: own title (derived — no variants), 12h fallback hold,
            # no fallback flag; description/targets still the main Short's.
            with open(os.path.join(tmp, "trendlinenow",
                                   slug + "-en-short.json")) as f:
                main_sidecar = json.load(f)
            short2_sidecar_path = os.path.join(
                tmp, "trendlinenow", slug + "-en-short2.json")
            self.assertTrue(os.path.exists(short2_sidecar_path))
            short2_sidecar = self._short2_sidecar(tmp)
            self.assertEqual(short2_sidecar["Title"],
                             "Scene 50 opens the next beat")
            self.assertNotEqual(short2_sidecar["Title"],
                                main_sidecar["Title"])
            self.assertNotIn("Short2TitleFallback", short2_sidecar)
            self.assertEqual(short2_sidecar["Description"],
                             main_sidecar["Description"])
            self.assertEqual(short2_sidecar["Aspect"], "vertical")
            self.assertFalse(short2_sidecar["Approved"])
            self.assertAlmostEqual(short2_sidecar["DurationSeconds"], 54.8)
            # 12h publish hold as fallback (Rishi 2026-11-27 correction:
            # 12h, not 24h; governing hold is sibling-anchored in the
            # uploader) — the test still asserts the fallback value.
            hold = datetime.fromisoformat(short2_sidecar["PublishAfterUtc"])
            delta_h = (hold - datetime.now(timezone.utc)).total_seconds() / 3600
            self.assertAlmostEqual(delta_h, 12.0, delta=0.1)
        finally:
            shutil.rmtree(tmp, ignore_errors=True)

    def test_variant_3_used_for_short2_title(self):
        tmp = tempfile.mkdtemp(prefix="finish-short2-variant-")
        try:
            job = self._write_job(
                tmp, title_variants=["First variant", "Second variant",
                                     "Payoff Title Here"])
            rc, log, stderr = self._run_main(job, self._fake_run())
            self.assertEqual(rc, 0)
            short2_sidecar = self._short2_sidecar(tmp)
            self.assertEqual(short2_sidecar["Title"], "Payoff Title Here")
            self.assertNotIn("Short2TitleFallback", short2_sidecar)
            self.assertIn("title variant 3", log)
            hold = datetime.fromisoformat(short2_sidecar["PublishAfterUtc"])
            delta_h = (hold - datetime.now(timezone.utc)).total_seconds() / 3600
            self.assertAlmostEqual(delta_h, 12.0, delta=0.1)
        finally:
            shutil.rmtree(tmp, ignore_errors=True)

    def test_missing_landscape_props_skips_short2(self):
        tmp = tempfile.mkdtemp(prefix="finish-short2-noland-")
        try:
            job = self._write_job(tmp, landscape_props=False)
            rc, log, stderr = self._run_main(job, self._fake_run())
            self.assertEqual(rc, 0)
            self.assertIn("job-20261126-test", stderr)
            self.assertIn("landscape props not found", stderr)
            self.assertFalse(os.path.exists(
                os.path.join(tmp, "trendlinenow",
                             "test-video-en-short2.mp4")))
            self.assertFalse(os.path.exists(
                os.path.join(tmp, "trendlinenow",
                             "test-video-en-short2.json")))
            # The main Short itself is unaffected.
            self.assertTrue(os.path.exists(
                os.path.join(tmp, "trendlinenow",
                             "test-video-en-short.mp4")))
        finally:
            shutil.rmtree(tmp, ignore_errors=True)

    def test_missing_scene_asset_skips_short2(self):
        # One payoff scene's art is missing: no cut with a hole in it —
        # the whole 2nd Short is skipped with a logged reason, rc 0.
        tmp = tempfile.mkdtemp(prefix="finish-short2-noasset-")
        try:
            job = self._write_job(tmp, missing_art=52)
            rc, log, stderr = self._run_main(job, self._fake_run())
            self.assertEqual(rc, 0)
            self.assertIn("job-20261126-test", stderr)
            self.assertIn("scene assets missing", stderr)
            self.assertIn("scene52-art.png", stderr)
            self.assertFalse(os.path.exists(
                os.path.join(tmp, "trendlinenow",
                             "test-video-en-short2.mp4")))
            self.assertFalse(os.path.exists(
                os.path.join(tmp, "trendlinenow",
                             "test-video-en-short2.json")))
            self.assertTrue(os.path.exists(
                os.path.join(tmp, "trendlinenow",
                             "test-video-en-short.mp4")))
        finally:
            shutil.rmtree(tmp, ignore_errors=True)

    def test_thumbnail_retry_when_first_frame_identical(self):
        tmp = tempfile.mkdtemp(prefix="finish-short2-thumb-")
        try:
            job = self._write_job(tmp)
            rc, log, stderr = self._run_main(
                job, self._fake_run(thumb_mode="identical_first"))
            self.assertEqual(rc, 0)
            self.assertIn("retrying at 3s", log)
            self.assertTrue(os.path.exists(
                os.path.join(tmp, "trendlinenow",
                             "test-video-en-short2.thumb.jpg")))
        finally:
            shutil.rmtree(tmp, ignore_errors=True)

    def test_vertical_60s_or_less_skips_short2(self):
        tmp = tempfile.mkdtemp(prefix="finish-short2-short-")
        try:
            job = os.path.join(tmp, "job-20261126-test")
            os.makedirs(os.path.join(job, "chunks-vertical"))
            with open(os.path.join(job, "script.json"), "w") as f:
                json.dump({
                    "Title": "Test Video", "Profile": "trendlinenow",
                    "Language": "en", "Description": "Test description.",
                    "Tags": ["test"], "ReelsCaption": "Test reel.",
                    "TikTokCaption": "Test tok.",
                    "Scenes": [
                        {"Narration": f"Scene {i}.", "captionText": f"Scene {i}."}
                        for i in range(2)
                    ],
                }, f)
            with open(os.path.join(job, "remotion-props-1080x1920.json"), "w") as f:
                json.dump({"scenes": [{"durationInSeconds": 22.5,
                                       "captionText": f"Scene {i}."}
                                      for i in range(2)],
                           "fps": 30}, f)
            rc, log, stderr = self._run_main(
                job, self._fake_run(),
                total_frames=1350, starts=[0, 675, 1350])
            self.assertEqual(rc, 0)
            self.assertIn("no second Short: main Short already fits in 60s",
                          log)
            self.assertFalse(os.path.exists(
                os.path.join(tmp, "trendlinenow",
                             "test-video-en-short2.mp4")))
        finally:
            shutil.rmtree(tmp, ignore_errors=True)

    def test_short2_ffmpeg_failure_warns_and_keeps_short(self):
        tmp = tempfile.mkdtemp(prefix="finish-short2-fail-")
        try:
            job = self._write_job(tmp)
            rc, log, stderr = self._run_main(job, self._fake_run(short2_rc=1))
            self.assertEqual(rc, 0)
            self.assertIn("job-20261126-test", stderr)
            self.assertIn("second Short assembly failed", stderr)
            self.assertIn("exit 1", stderr)
            self.assertNotIn("second Short assembly failed", log)
            # The main Short itself is unaffected.
            self.assertTrue(os.path.exists(
                os.path.join(tmp, "trendlinenow",
                             "test-video-en-short.mp4")))
            self.assertFalse(os.path.exists(
                os.path.join(tmp, "trendlinenow",
                             "test-video-en-short2.mp4")))
        finally:
            shutil.rmtree(tmp, ignore_errors=True)


if __name__ == "__main__":
    unittest.main(verbosity=2)
