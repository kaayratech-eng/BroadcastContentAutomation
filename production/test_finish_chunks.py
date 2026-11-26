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
import importlib.util
import json
import os
import re
import sys
import unittest

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


if __name__ == "__main__":
    unittest.main(verbosity=2)
