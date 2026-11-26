#!/usr/bin/env python3
"""Unit tests for trendline/governor.py (stdlib unittest, no pytest).

Covers the ledger math the pipeline depends on: daily/hourly/monthly
rollovers, cooldown-until-reset, budget exhaustion, the min-interval
gate, video-cap counting (incl. idempotent video-started), and the
fail-open behaviour. All tests run against temp config/ledger files —
never the live ledger — and never touch a live provider.

Run: python3 test_governor.py   (from trendline/)
"""
import json
import os
import subprocess
import sys
import tempfile
import unittest
from datetime import datetime, timedelta
from zoneinfo import ZoneInfo

HERE = os.path.dirname(os.path.abspath(__file__))
GOVERNOR = os.path.join(HERE, "governor.py")
TZ = ZoneInfo("America/Toronto")

TEST_CONFIG = {
    "timezone": "America/Toronto",
    "daily_video_cap": 2,
    "cooldown_defaults": {"after_429_seconds": 600,
                          "after_timeout_seconds": 600},
    "providers": {
        "groq": {"budgets": {"tokens_per_day": 10000,
                             "tokens_per_minute": 8000,
                             "requests_per_day": 100},
                 "tokens_per_call_estimate": 7000},
        "kilo": {"budgets": {"requests_per_hour": 3}},
        "pollinations-image": {"budgets": {},
                               "min_interval_seconds": 45},
        "azure-tts": {"budgets": {"chars_per_month": 1000},
                      "soft_cap_fraction": 0.9},
        "google-tts": {"budgets": {"chars_per_month": 1000}},
        "ovhcloud-image": {"budgets": {"requests_per_minute": 2}},
        "pexels": {"budgets": {"requests_per_hour": 200}},
        # ONE shared Cloudflare bucket (2026-11-24 image-pool rebuild):
        # the script leg charges ~926 neurons/act and FLUX images ~173
        # each against the same 10,000/day entry — never two budgets.
        "cloudflare": {"budgets": {"units_per_day": 10000},
                       "units_per_call_estimate": 926,
                       "units_per_image_estimate": 173},
    },
    "stages": {"script": ["groq"],
               "image": ["ovhcloud-image", "pollinations-image", "pexels"],
               "tts": ["azure-tts", "google-tts"]},
}


def now():
    return datetime.now(TZ)


class GovernorTest(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.config = os.path.join(self.tmp.name, "config.json")
        self.ledger = os.path.join(self.tmp.name, "ledger.json")
        with open(self.config, "w") as f:
            json.dump(TEST_CONFIG, f)

    def tearDown(self):
        self.tmp.cleanup()

    def gov(self, *args, expect=0):
        r = subprocess.run(
            [sys.executable, GOVERNOR, "--config", self.config,
             "--ledger", self.ledger, *args],
            capture_output=True, text=True, timeout=30)
        self.assertEqual(r.returncode, expect,
                         f"gov {args} -> rc {r.returncode}: {r.stdout} {r.stderr}")
        return json.loads(r.stdout) if r.stdout.strip() else {}

    def gov_raw(self, *args):
        return subprocess.run(
            [sys.executable, GOVERNOR, "--config", self.config,
             "--ledger", self.ledger, *args],
            capture_output=True, text=True, timeout=30)

    def read_ledger(self):
        with open(self.ledger) as f:
            return json.load(f)

    def write_ledger(self, data):
        with open(self.ledger, "w") as f:
            json.dump(data, f)

    # ---------------------------------------------------------- budgets ---
    def test_acquire_records_usage_and_denies_at_daily_cap(self):
        # Groq test budget: 10,000 tokens/day, 8,000/minute. Two 4,000-token
        # calls fit both windows (day 8,000, minute exactly 8,000).
        self.assertTrue(self.gov(
            "acquire", "--provider", "groq", "--tokens", "4000")["allowed"])
        self.assertTrue(self.gov(
            "acquire", "--provider", "groq", "--tokens", "4000")["allowed"])
        # A third 4,000-token call would take the day to 12,000 > 10,000.
        out = self.gov("acquire", "--provider", "groq", "--tokens", "4000",
                       expect=1)
        self.assertFalse(out["allowed"])
        self.assertIn("tokens_per_day", out["reason"])
        self.assertIsNotNone(out["resets_at"])  # next midnight Toronto
        # The minute bucket also binds: a 1,000-token call right now would
        # take the minute to 9,000 > 8,000, so it waits for the next minute.
        out = self.gov("acquire", "--provider", "groq", "--tokens", "1000",
                       expect=1)
        self.assertFalse(out["allowed"])
        self.assertIn("tokens_per_minute", out["reason"])
        # Once the minute turns over, the 1,000-token call fits the day
        # (8,000 + 1,000 <= 10,000) and the fresh minute.
        data = self.read_ledger()
        prev_minute = (now() - timedelta(minutes=1)).strftime("%Y-%m-%dT%H:%M")
        data["providers"]["groq"]["minute"] = prev_minute
        self.write_ledger(data)
        out = self.gov("acquire", "--provider", "groq", "--tokens", "1000")
        self.assertTrue(out["allowed"])

    def test_daily_rollover_zeroes_yesterday_counters(self):
        # Craft a ledger as if yesterday ended at 9,999/10,000 tokens.
        yday = (now() - timedelta(days=1)).strftime("%Y-%m-%d")
        led = {"version": 1, "videos_today": {"date": yday, "jobs": ["a"]},
               "providers": {"groq": {
                   "day": yday, "day_tokens": 9999, "day_requests": 99,
                   "day_units": 0, "hour": "", "hour_requests": 0,
                   "minute": "", "minute_tokens": 0, "minute_requests": 0,
                   "month": "", "month_requests": 0, "month_chars": 0,
                   "last_request_at": None, "cooled_until": None,
                   "cooled_reason": None}}}
        self.write_ledger(led)
        # Today the counters must have rolled over: a 7,000-token call fits.
        out = self.gov("acquire", "--provider", "groq", "--tokens", "7000")
        self.assertTrue(out["allowed"])
        data = self.read_ledger()
        self.assertEqual(data["providers"]["groq"]["day_tokens"], 7000)

    def test_monthly_rollover_zeroes_last_month_chars(self):
        last = (now().replace(day=1) - timedelta(days=1)).strftime("%Y-%m")
        led = {"version": 1, "videos_today": {"date": "", "jobs": []},
               "providers": {"azure-tts": {
                   "day": "", "day_tokens": 0, "day_requests": 0,
                   "day_units": 0, "hour": "", "hour_requests": 0,
                   "minute": "", "minute_tokens": 0, "minute_requests": 0,
                   "month": last, "month_requests": 0, "month_chars": 999,
                   "last_request_at": None, "cooled_until": None,
                   "cooled_reason": None}}}
        self.write_ledger(led)
        # Test budget is 1,000 chars/month; last month's 999 must not count.
        out = self.gov("acquire", "--provider", "azure-tts", "--chars", "500")
        self.assertTrue(out["allowed"])
        data = self.read_ledger()
        self.assertEqual(data["providers"]["azure-tts"]["month_chars"], 500)

    def test_hourly_window_kilo(self):
        # Kilo test budget: 3 requests/hour. Three fit, the fourth waits.
        for _ in range(3):
            self.gov("acquire", "--provider", "kilo")
        out = self.gov("acquire", "--provider", "kilo", expect=1)
        self.assertFalse(out["allowed"])
        self.assertIn("requests_per_hour", out["reason"])
        # A ledger stamped with last hour's window must roll over.
        data = self.read_ledger()
        prev_hour = (now() - timedelta(hours=1)).strftime("%Y-%m-%dT%H")
        data["providers"]["kilo"]["hour"] = prev_hour
        self.write_ledger(data)
        out = self.gov("acquire", "--provider", "kilo")
        self.assertTrue(out["allowed"])

    def test_min_interval_pollinations_image(self):
        # One call, then the next inside the 45s interval must wait.
        self.gov("acquire", "--provider", "pollinations-image")
        out = self.gov("acquire", "--provider", "pollinations-image",
                       expect=1)
        self.assertFalse(out["allowed"])
        self.assertIn("min interval 45s", out["reason"])
        self.assertIsNotNone(out["resets_at"])
        # 46s ago is fine again.
        data = self.read_ledger()
        past = (now() - timedelta(seconds=46)).isoformat()
        data["providers"]["pollinations-image"]["last_request_at"] = past
        self.write_ledger(data)
        out = self.gov("acquire", "--provider", "pollinations-image")
        self.assertTrue(out["allowed"])

    # -------------------------------------------------------- cooldowns ---
    def test_mark_cooled_until_midnight_then_clears(self):
        self.gov("mark-cooled", "--provider", "groq",
                 "--reason", "HTTP 429", "--until", "midnight")
        out = self.gov("check", "--provider", "groq", expect=1)
        self.assertFalse(out["allowed"])
        self.assertTrue(out["cooled"])
        self.assertIn("429", out["reason"])
        # Resets at next midnight Toronto (date part is tomorrow).
        tomorrow = (now() + timedelta(days=1)).strftime("%Y-%m-%d")
        self.assertTrue(out["resets_at"].startswith(tomorrow))
        # Clearing restores the leg.
        self.gov("clear-cooled", "--provider", "groq")
        out = self.gov("check", "--provider", "groq")
        self.assertTrue(out["allowed"])

    def test_mark_cooled_default_is_429_seconds(self):
        self.gov("mark-cooled", "--provider", "groq", "--reason", "HTTP 429")
        data = self.read_ledger()
        until = datetime.fromisoformat(
            data["providers"]["groq"]["cooled_until"])
        delta = (until - now()).total_seconds()
        self.assertGreater(delta, 500)  # ~600s from now
        self.assertLess(delta, 700)

    def test_expired_cooldown_clears_on_access(self):
        past = (now() - timedelta(seconds=60)).isoformat()
        led = {"version": 1, "videos_today": {"date": "", "jobs": []},
               "providers": {"groq": {
                   "day": "", "day_tokens": 0, "day_requests": 0,
                   "day_units": 0, "hour": "", "hour_requests": 0,
                   "minute": "", "minute_tokens": 0, "minute_requests": 0,
                   "month": "", "month_requests": 0, "month_chars": 0,
                   "last_request_at": None, "cooled_until": past,
                   "cooled_reason": "HTTP 429"}}}
        self.write_ledger(led)
        out = self.gov("check", "--provider", "groq")
        self.assertTrue(out["allowed"])
        data = self.read_ledger()
        self.assertIsNone(data["providers"]["groq"]["cooled_until"])

    # ------------------------------------------------------ daily video cap
    def test_video_cap_counts_distinct_jobs_and_resets_daily(self):
        self.assertTrue(self.gov("video-may-start")["allowed"])
        self.gov("video-started", "--job", "job-20261123-000001")
        # Same job again (resume/retry) does not consume the cap.
        out = self.gov("video-started", "--job", "job-20261123-000001")
        self.assertFalse(out["recorded"])
        self.assertEqual(out["videos_today"], 1)
        self.gov("video-started", "--job", "job-20261123-000002")
        # Test cap is 2: the next new video must wait for tomorrow.
        out = self.gov("video-may-start", expect=1)
        self.assertFalse(out["allowed"])
        self.assertIn("daily video cap", out["reason"])
        self.assertEqual(out["videos_today"], 2)
        # Yesterday's jobs do not count today.
        data = self.read_ledger()
        yday = (now() - timedelta(days=1)).strftime("%Y-%m-%d")
        data["videos_today"]["date"] = yday
        self.write_ledger(data)
        out = self.gov("video-may-start")
        self.assertTrue(out["allowed"])
        self.assertEqual(out["videos_today"], 0)

    def test_video_may_start_denies_when_all_script_legs_cooled(self):
        # The test stage has a single script leg (groq); cool it.
        self.gov("mark-cooled", "--provider", "groq",
                 "--reason", "HTTP 429", "--until", "midnight")
        out = self.gov("video-may-start", expect=1)
        self.assertFalse(out["allowed"])
        self.assertIn("script legs", out["reason"])

    def test_video_may_start_denies_when_all_image_legs_cooled(self):
        for leg in ("ovhcloud-image", "pollinations-image", "pexels"):
            self.gov("mark-cooled", "--provider", leg,
                     "--reason", "HTTP 429", "--until", "midnight")
        out = self.gov("video-may-start", expect=1)
        self.assertFalse(out["allowed"])
        self.assertIn("image legs", out["reason"])

    # --------------------------------------------------------- fail-open ---
    def test_fail_open_on_missing_config(self):
        # A missing config falls back to the built-in defaults (cap 4, no
        # numeric budgets) and the check still allows the call — the
        # governor never halts the channel by itself.
        r = subprocess.run(
            [sys.executable, GOVERNOR, "--config", "/nonexistent/cfg.json",
             "--ledger", self.ledger, "check", "--provider", "groq"],
            capture_output=True, text=True, timeout=30)
        self.assertEqual(r.returncode, 0)
        out = json.loads(r.stdout)
        self.assertTrue(out["allowed"])
        self.assertIn("fallback", r.stderr)

    def test_fail_open_on_corrupt_ledger(self):
        with open(self.ledger, "w") as f:
            f.write("{not json")
        out = self.gov("check", "--provider", "groq")
        self.assertTrue(out["allowed"])

    def test_status_json_reports_counts(self):
        self.gov("acquire", "--provider", "groq", "--tokens", "1000")
        self.gov("video-started", "--job", "job-20261123-000001")
        out = self.gov("status", "--json")
        self.assertEqual(out["videos_today"], 1)
        self.assertEqual(out["cap"], 2)
        groq = [p for p in out["providers"] if p["provider"] == "groq"][0]
        self.assertEqual(groq["day_tokens"], 1000)
        self.assertTrue(groq["allowed"])

    # ------------------------------------------- shared Cloudflare bucket ---
    def test_cloudflare_shared_bucket_script_plus_image(self):
        # Image-pool rebuild (2026-11-24): script acts (~926 neurons)
        # and FLUX images (~173 neurons) draw on the ONE "cloudflare"
        # 10,000/day bucket — a second 10k provider key would
        # double-spend the real bucket, so there is only one entry.
        # 10 script acts (9,260) + 4 images (692) = 9,952: allowed.
        for _ in range(10):
            self.assertTrue(self.gov(
                "acquire", "--provider", "cloudflare",
                "--units", "926")["allowed"])
        for _ in range(4):
            self.assertTrue(self.gov(
                "acquire", "--provider", "cloudflare",
                "--units", "173")["allowed"])
        # A 5th image (173) would take the day to 10,125 > 10,000.
        out = self.gov("acquire", "--provider", "cloudflare",
                       "--units", "173", expect=1)
        self.assertFalse(out["allowed"])
        self.assertIn("units_per_day", out["reason"])
        # The ledger holds the combined total on the one entry.
        self.assertEqual(
            self.read_ledger()["providers"]["cloudflare"]["day_units"], 9952)

    def test_cloudflare_image_stage_lists_shared_bucket(self):
        # The live config's image stage must include the shared
        # "cloudflare" key (FLUX is an image leg) and must not list
        # the discarded legs.
        with open(os.path.join(HERE, "governor-config.json")) as f:
            live = json.load(f)
        self.assertEqual(
            live["stages"]["image"],
            ["cloudflare", "pexels", "ovhcloud-image", "pollinations-keyed",
             "leonardo-image"])
        self.assertNotIn("ai-horde", live["providers"])
        self.assertNotIn("pollinations-image", live["providers"])
        for leg in ("pollinations-keyed", "leonardo-image"):
            self.assertIn(leg, live["providers"])


if __name__ == "__main__":
    unittest.main(verbosity=2)
