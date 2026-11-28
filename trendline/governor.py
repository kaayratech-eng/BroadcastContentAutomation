#!/usr/bin/env python3
"""Usage governor for the Trendline Now pipeline (2026-11-23).

Free-tier API budgets are real and small (Groq 200K tokens/day, Azure
TTS 500K chars/month, OVHcloud images 2/min, Kilo 200 req/hour, ...).
Without a governor, a prep that hits a limit retries the same leg until
the driver gives up — burning the budget and the video. This helper is
the single source of truth for "can this provider be called now, and
when does its limit reset?"

State lives in trendline/usage-ledger.json (counters + cooldowns);
budgets live in trendline/governor-config.json. Both default to the
files next to this script; --config/--ledger override them (tests).

Design notes:
- Windows reset at midnight America/Toronto (daily), on the hour
  (hourly), on the 1st (monthly). A minute bucket enforces per-minute
  caps (Groq 8K TPM, OVHcloud images 2/min).
- `check` is read-only (does the provider have room for this call?).
  `acquire` is check + record in one locked step (one call is about to
  happen: it always counts 1 request plus the --tokens/--chars/--units
  passed). `record` adds usage after the fact (actuals). `mark-cooled`
  parks a provider until a reset after a 429/limit response — it is
  never retried in a tight loop.
- All file access is under an flock exclusive lock with atomic
  write-to-temp + os.replace, so concurrent preps cannot corrupt the
  ledger. If the ledger or config is missing/corrupt, the helper fails
  OPEN (exit 0, "fail_open": true) and logs to stderr: the governor
  must never halt the channel by itself.
- Exit codes: 0 = allowed / done, 1 = denied (cap hit / provider
  cooled / budget out). Every machine command prints one JSON object
  on stdout.

Usage:
  governor.py check --provider groq [--tokens N] [--requests N] [--chars N] [--units N]
  governor.py acquire --provider groq [--tokens N] [--chars N] [--units N]
  governor.py record --provider groq [--tokens N] [--requests N] [--chars N] [--units N]
  governor.py mark-cooled --provider groq --reason "HTTP 429" [--until midnight|next-hour|SECONDS]
  governor.py clear-cooled --provider groq
  governor.py video-may-start
  governor.py video-started --job job-20261123-150439
  governor.py status [--json]
"""
import argparse
import fcntl
import json
import os
import shutil
import sys
from datetime import datetime, timedelta, timezone
from zoneinfo import ZoneInfo

HERE = os.path.dirname(os.path.abspath(__file__))
DEFAULT_CONFIG = os.path.join(HERE, "governor-config.json")
DEFAULT_LEDGER = os.path.join(HERE, "usage-ledger.json")
TZ = ZoneInfo("America/Toronto")

# Built-in fallback when the config file is missing/corrupt: the daily
# video cap only, no per-provider numeric budgets (429-cooldowns still
# work). Fail-open, never fail-closed.
FALLBACK_CONFIG = {
    "timezone": "America/Toronto",
    "daily_video_cap": 3,
    "cooldown_defaults": {"after_429_seconds": 600, "after_timeout_seconds": 600},
    "providers": {},
    "stages": {"script": [], "image": [], "tts": []},
}

STAGE_PROVIDERS = {
    "script": ["groq", "gemini", "zai", "cloudflare", "openrouter", "kilo",
               "ovhcloud", "pollinations-text"],
    # Rebuilt image pool (2026-11-24 bake-off): FLUX first, Pexels #2,
    # OVH filler, then the dormant keyed legs. "cloudflare" is listed
    # here AND in the script stage on purpose — one shared 10k
    # neuron/day bucket feeds both. Pollinations anonymous + AI Horde
    # were discarded and deleted from the pool; they must never appear
    # here again.
    "image": ["cloudflare", "pexels", "ovhcloud-image",
              "pollinations-keyed", "leonardo-image"],
    "tts": ["azure-tts", "google-tts"],
}


def log(msg):
    print(f"[governor] {msg}", file=sys.stderr)


def now_toronto():
    return datetime.now(TZ)


def backup_corrupt(path):
    """Copy a corrupt governor file aside so the evidence survives the
    reset. Returns the backup path, or None if the copy itself failed
    (the reset still proceeds — the log line says the backup failed)."""
    stamp = now_toronto().strftime("%Y%m%dT%H%M%S")
    backup = f"{path}.corrupt-{stamp}"
    try:
        shutil.copy2(path, backup)
        return backup
    except Exception as e:
        log(f"governor: could not back up corrupt file {path}: {e}")
        return None


def window_keys(now):
    """Window keys for the current Toronto time."""
    return {
        "day": now.strftime("%Y-%m-%d"),
        "hour": now.strftime("%Y-%m-%dT%H"),
        "minute": now.strftime("%Y-%m-%dT%H:%M"),
        "month": now.strftime("%Y-%m"),
    }


def next_boundary(now, window):
    """Next reset instant (Toronto time) for a window kind."""
    if window == "minute":
        return (now.replace(second=0, microsecond=0) + timedelta(minutes=1))
    if window == "hour":
        return now.replace(minute=0, second=0, microsecond=0) + timedelta(hours=1)
    if window == "day":
        return (now.replace(hour=0, minute=0, second=0, microsecond=0)
                + timedelta(days=1))
    if window == "month":
        first = now.replace(day=1, hour=0, minute=0, second=0, microsecond=0)
        nxt = first + timedelta(days=32)
        return nxt.replace(day=1)
    raise ValueError(f"unknown window: {window}")


def iso(dt):
    return dt.astimezone(TZ).isoformat()


def parse_iso(s):
    try:
        return datetime.fromisoformat(s)
    except (TypeError, ValueError):
        return None


def load_config(path):
    try:
        with open(path) as f:
            cfg = json.load(f)
        cfg.setdefault("daily_video_cap", 3)
        cfg.setdefault("cooldown_defaults", {})
        cfg.setdefault("providers", {})
        cfg.setdefault("stages", STAGE_PROVIDERS)
        return cfg
    except Exception as e:
        # L5: a corrupt config used to fall back silently (and the fallback
        # cap 3 is HIGHER than the live cap 2, so it silently raised the
        # cap). Back the corrupt file up and say so, with the fallback cap.
        backup = backup_corrupt(path)
        log(f"governor: config corrupt ({e}); backed up to {backup or 'BACKUP FAILED'}; using fallback (cap {FALLBACK_CONFIG['daily_video_cap']}, no numeric budgets); investigate the corrupt file before trusting today's limits")
        return dict(FALLBACK_CONFIG)


def fresh_provider_state():
    return {
        "day": "", "day_tokens": 0, "day_requests": 0, "day_units": 0,
        "hour": "", "hour_requests": 0,
        "minute": "", "minute_tokens": 0, "minute_requests": 0,
        "month": "", "month_requests": 0, "month_chars": 0,
        "last_request_at": None,
        "cooled_until": None, "cooled_reason": None,
    }


def fresh_ledger():
    return {"version": 1, "videos_today": {"date": "", "jobs": []},
            "providers": {}}


class Ledger:
    """The usage ledger, always accessed under an exclusive flock."""

    def __init__(self, path):
        self.path = path
        self.data = fresh_ledger()
        self._fd = None

    def __enter__(self):
        os.makedirs(os.path.dirname(self.path) or ".", exist_ok=True)
        self._fd = os.open(self.path, os.O_RDWR | os.O_CREAT, 0o644)
        fcntl.flock(self._fd, fcntl.LOCK_EX)
        try:
            with os.fdopen(os.dup(self._fd)) as f:
                f.seek(0)
                raw = f.read()
            if raw.strip():
                loaded = json.loads(raw)
                if isinstance(loaded, dict) and "providers" in loaded:
                    self.data = loaded
        except Exception as e:
            # L5: a corrupt ledger used to be silently discarded — the
            # videos_today jobs and all provider counters vanished with no
            # trace. Back the corrupt file up and say what was reset.
            backup = backup_corrupt(self.path)
            log(f"governor: ledger corrupt ({e}); backed up to {backup or 'BACKUP FAILED'}; starting fresh — videos_today and provider counters reset to 0; investigate the corrupt file before trusting today's counts")
            self.data = fresh_ledger()
        self.data.setdefault("version", 1)
        self.data.setdefault("videos_today", {"date": "", "jobs": []})
        self.data.setdefault("providers", {})
        return self

    def __exit__(self, *exc):
        try:
            tmp = f"{self.path}.tmp.{os.getpid()}"
            with open(tmp, "w") as f:
                json.dump(self.data, f, indent=2, sort_keys=True)
                f.write("\n")
                f.flush()
                os.fsync(f.fileno())
            os.replace(tmp, self.path)
        finally:
            fcntl.flock(self._fd, fcntl.LOCK_UN)
            os.close(self._fd)
            self._fd = None

    def provider(self, name):
        st = self.data["providers"].get(name)
        if not isinstance(st, dict):
            st = fresh_provider_state()
            self.data["providers"][name] = st
        for k, v in fresh_provider_state().items():
            st.setdefault(k, v)
        return st


def rollover(st, now):
    """Zero the window counters whose window has turned over."""
    keys = window_keys(now)
    if st["day"] != keys["day"]:
        st["day"] = keys["day"]
        st["day_tokens"] = 0
        st["day_requests"] = 0
        st["day_units"] = 0
    if st["hour"] != keys["hour"]:
        st["hour"] = keys["hour"]
        st["hour_requests"] = 0
    if st["minute"] != keys["minute"]:
        st["minute"] = keys["minute"]
        st["minute_tokens"] = 0
        st["minute_requests"] = 0
    if st["month"] != keys["month"]:
        st["month"] = keys["month"]
        st["month_requests"] = 0
        st["month_chars"] = 0
    # An expired cooldown clears itself on first sight.
    if st.get("cooled_until"):
        until = parse_iso(st["cooled_until"])
        if until is not None and until <= now:
            st["cooled_until"] = None
            st["cooled_reason"] = None


def check_provider(cfg, st, name, amounts, now):
    """Pure decision: can this provider take a call of this size now?

    Returns (allowed, reason, resets_at_iso, usage_fraction).
    `amounts` maps counter names (tokens/requests/chars/units) to the
    size of the call being gated (0 = just asking "is it usable?").
    """
    pcfg = cfg["providers"].get(name, {})
    budgets = pcfg.get("budgets", {})

    if st.get("cooled_until"):
        until = parse_iso(st["cooled_until"])
        if until is not None and until > now:
            return (False, f"cooled: {st.get('cooled_reason') or 'rate limit'}",
                    iso(until), 1.0)

    interval = pcfg.get("min_interval_seconds")
    if interval and st.get("last_request_at"):
        last = parse_iso(st["last_request_at"])
        if last is not None:
            ready_at = last + timedelta(seconds=interval)
            if ready_at > now:
                return (False, f"min interval {interval}s between calls",
                        iso(ready_at), 1.0)

    # Budget checks: (counter key, requested amount, window for reset).
    checks = [
        ("tokens_per_day", "day_tokens", amounts.get("tokens", 0), "day"),
        ("tokens_per_minute", "minute_tokens", amounts.get("tokens", 0), "minute"),
        ("requests_per_day", "day_requests", amounts.get("requests", 0), "day"),
        ("requests_per_minute", "minute_requests", amounts.get("requests", 0), "minute"),
        ("requests_per_hour", "hour_requests", amounts.get("requests", 0), "hour"),
        ("requests_per_month", "month_requests", amounts.get("requests", 0), "month"),
        ("chars_per_month", "month_chars", amounts.get("chars", 0), "month"),
        ("units_per_day", "day_units", amounts.get("units", 0), "day"),
    ]
    fraction = 0.0
    for budget_key, counter, asked, window in checks:
        budget = budgets.get(budget_key)
        if not budget:
            continue
        used = st[counter] + asked
        fraction = max(fraction, used / budget)
        if used > budget:
            return (False, f"{budget_key} budget {budget} exceeded "
                           f"({st[counter]} used + {asked} asked)",
                    iso(next_boundary(now, window)), fraction)
    return (True, None, None, fraction)


def record_usage(st, amounts, now):
    """Add a call's usage to the (already rolled-over) counters."""
    st["day_tokens"] += amounts.get("tokens", 0)
    st["day_requests"] += amounts.get("requests", 0)
    st["day_units"] += amounts.get("units", 0)
    st["hour_requests"] += amounts.get("requests", 0)
    st["minute_tokens"] += amounts.get("tokens", 0)
    st["minute_requests"] += amounts.get("requests", 0)
    st["month_requests"] += amounts.get("requests", 0)
    st["month_chars"] += amounts.get("chars", 0)
    st["last_request_at"] = iso(now)


def emit(obj, code=0):
    print(json.dumps(obj))
    return code


def soft_cap_fraction(cfg, provider):
    """Monthly-budget soft cap: at this usage fraction the caller should
    switch to the next provider instead of burning the last of the
    budget (Azure TTS: 0.9 — preserve the final 10%)."""
    return cfg["providers"].get(provider, {}).get("soft_cap_fraction", 0.9)


def fail_open(reason, extra=None):
    """The governor must never halt the channel by itself."""
    log(f"fail-open: {reason}")
    out = {"allowed": True, "fail_open": True, "reason": reason}
    if extra:
        out.update(extra)
    return emit(out, 0)


def cmd_check(args, cfg):
    amounts = {"tokens": args.tokens, "requests": args.requests,
               "chars": args.chars, "units": args.units}
    now = now_toronto()
    try:
        with Ledger(args.ledger) as led:
            st = led.provider(args.provider)
            rollover(st, now)
            allowed, reason, resets_at, fraction = check_provider(
                cfg, st, args.provider, amounts, now)
    except Exception as e:
        return fail_open(f"check failed: {e}", {"provider": args.provider})
    if not allowed:
        # L14: a denial used to emit JSON on stdout only — no log line —
        # so a provider denial was visible only if the caller logged it.
        log(f"check: provider {args.provider} denied: {reason}, resets {resets_at}")
    return emit({"allowed": allowed, "provider": args.provider,
                 "reason": reason, "resets_at": resets_at,
                 "usage_fraction": round(fraction, 4),
                 "soft_cap_fraction": soft_cap_fraction(cfg, args.provider),
                 "cooled": reason is not None and reason.startswith("cooled")},
                0 if allowed else 1)


def cmd_acquire(args, cfg):
    # One call is about to happen: always count 1 request plus the
    # call's size (--tokens/--chars/--units).
    amounts = {"tokens": args.tokens, "requests": max(1, args.requests),
               "chars": args.chars, "units": args.units}
    now = now_toronto()
    try:
        with Ledger(args.ledger) as led:
            st = led.provider(args.provider)
            rollover(st, now)
            allowed, reason, resets_at, fraction = check_provider(
                cfg, st, args.provider, amounts, now)
            if allowed:
                record_usage(st, amounts, now)
    except Exception as e:
        return fail_open(f"acquire failed: {e}", {"provider": args.provider})
    if not allowed:
        # L14: see cmd_check — a denial needs a log line, not just JSON.
        log(f"acquire: provider {args.provider} denied: {reason}, resets {resets_at}")
    return emit({"allowed": allowed, "provider": args.provider,
                 "reason": reason, "resets_at": resets_at,
                 "usage_fraction": round(fraction, 4),
                 "soft_cap_fraction": soft_cap_fraction(cfg, args.provider),
                 "recorded": allowed,
                 "cooled": reason is not None and reason.startswith("cooled")},
                0 if allowed else 1)


def cmd_record(args, cfg):
    amounts = {"tokens": args.tokens, "requests": args.requests,
               "chars": args.chars, "units": args.units}
    now = now_toronto()
    try:
        with Ledger(args.ledger) as led:
            st = led.provider(args.provider)
            rollover(st, now)
            record_usage(st, amounts, now)
            totals = {k: st[k] for k in
                      ("day_tokens", "day_requests", "day_units",
                       "hour_requests", "minute_tokens", "minute_requests",
                       "month_requests", "month_chars")}
    except Exception as e:
        return fail_open(f"record failed: {e}", {"provider": args.provider})
    return emit({"recorded": True, "provider": args.provider,
                 "totals": totals}, 0)


def cmd_mark_cooled(args, cfg):
    now = now_toronto()
    pcfg = cfg["providers"].get(args.provider, {})
    defaults = cfg.get("cooldown_defaults", {})
    until = args.until or ""
    if until == "midnight":
        target = next_boundary(now, "day")
    elif until == "next-hour":
        target = next_boundary(now, "hour")
    elif until.isdigit():
        target = now + timedelta(seconds=int(until))
    else:
        secs = pcfg.get("cooldown_after_429_seconds",
                        defaults.get("after_429_seconds", 600))
        target = now + timedelta(seconds=secs)
    try:
        with Ledger(args.ledger) as led:
            st = led.provider(args.provider)
            rollover(st, now)
            # A longer existing cooldown wins over a shorter new one.
            cur = parse_iso(st.get("cooled_until") or "")
            if cur is None or target > cur:
                st["cooled_until"] = iso(target)
                st["cooled_reason"] = args.reason
    except Exception as e:
        return fail_open(f"mark-cooled failed: {e}",
                         {"provider": args.provider})
    log(f"{args.provider} cooled until {iso(target)} ({args.reason})")
    return emit({"cooled_until": iso(target), "provider": args.provider,
                 "reason": args.reason}, 0)


def cmd_clear_cooled(args, cfg):
    # L14: the cleared cooldown used to vanish without a trace — capture
    # what it was before clearing so the log line names it.
    was_reason = None
    was_until = None
    try:
        with Ledger(args.ledger) as led:
            st = led.provider(args.provider)
            was_reason = st.get("cooled_reason")
            was_until = st.get("cooled_until")
            st["cooled_until"] = None
            st["cooled_reason"] = None
    except Exception as e:
        return fail_open(f"clear-cooled failed: {e}",
                         {"provider": args.provider})
    if was_reason or was_until:
        log(f"{args.provider} cooldown cleared (was {was_reason} until {was_until})")
    else:
        log(f"{args.provider} cooldown cleared (was not cooled)")
    return emit({"cleared": True, "provider": args.provider}, 0)


def stage_available(cfg, led, stage, now):
    """How many legs of a stage could take a call right now?"""
    names = cfg.get("stages", {}).get(stage) or STAGE_PROVIDERS.get(stage, [])
    n = 0
    for name in names:
        st = led.provider(name)
        rollover(st, now)
        allowed, _, _, _ = check_provider(cfg, st, name, {}, now)
        if allowed:
            n += 1
    return n, len(names)


def cmd_video_may_start(args, cfg):
    now = now_toronto()
    cap = cfg.get("daily_video_cap", 3)
    try:
        with Ledger(args.ledger) as led:
            vt = led.data["videos_today"]
            if vt.get("date") != window_keys(now)["day"]:
                vt["date"] = window_keys(now)["day"]
                vt["jobs"] = []
            count = len(vt["jobs"])
            script_n, script_total = stage_available(cfg, led, "script", now)
            image_n, image_total = stage_available(cfg, led, "image", now)
            tts_n, tts_total = stage_available(cfg, led, "tts", now)
    except Exception as e:
        return fail_open(f"video-may-start failed: {e}")
    if count >= cap:
        return emit({"allowed": False,
                     "reason": f"daily video cap reached ({count}/{cap})",
                     "resets_at": iso(next_boundary(now, "day")),
                     "videos_today": count, "cap": cap,
                     "script_legs_available": script_n,
                     "image_legs_available": image_n,
                     "tts_legs_available": tts_n}, 1)
    if script_n == 0:
        return emit({"allowed": False,
                     "reason": "all script legs cooled or over budget",
                     "resets_at": iso(next_boundary(now, "day")),
                     "videos_today": count, "cap": cap,
                     "script_legs_available": 0,
                     "image_legs_available": image_n,
                     "tts_legs_available": tts_n}, 1)
    if image_n == 0:
        return emit({"allowed": False,
                     "reason": "all image legs cooled or over budget",
                     "resets_at": iso(next_boundary(now, "day")),
                     "videos_today": count, "cap": cap,
                     "script_legs_available": script_n,
                     "image_legs_available": 0,
                     "tts_legs_available": tts_n}, 1)
    if tts_n == 0:
        return emit({"allowed": False,
                     "reason": "both TTS legs over monthly char budget",
                     "resets_at": iso(next_boundary(now, "month")),
                     "videos_today": count, "cap": cap,
                     "script_legs_available": script_n,
                     "image_legs_available": image_n,
                     "tts_legs_available": 0}, 1)
    return emit({"allowed": True, "reason": None, "resets_at": None,
                 "videos_today": count, "cap": cap,
                 "script_legs_available": script_n,
                 "image_legs_available": image_n,
                 "tts_legs_available": tts_n}, 0)


def cmd_video_started(args, cfg):
    now = now_toronto()
    cap = cfg.get("daily_video_cap", 3)
    try:
        with Ledger(args.ledger) as led:
            vt = led.data["videos_today"]
            if vt.get("date") != window_keys(now)["day"]:
                vt["date"] = window_keys(now)["day"]
                vt["jobs"] = []
            recorded = args.job not in vt["jobs"]
            if recorded:
                vt["jobs"].append(args.job)
            count = len(vt["jobs"])
    except Exception as e:
        return fail_open(f"video-started failed: {e}")
    # L40: a successful video-started used to be invisible — no log() call
    # — so the prep-counting behaviour (counted at prep discovery, when
    # the driver first learns the job id from the prep log, not at video
    # start/publish) left no trace, and a duplicate job (recorded=False)
    # was likewise silent. Logging only here: the counting behaviour
    # itself is NOT changed by this item.
    dup_note = "" if recorded else " (already counted — duplicate job, cap unchanged)"
    log(f"video-started: job {args.job} recorded={recorded} videos_today={count}/{cap} (counted at prep discovery; a failed prep still consumes the slot){dup_note}")
    return emit({"recorded": recorded, "job": args.job,
                 "videos_today": count, "cap": cap}, 0)


def cmd_status(args, cfg):
    now = now_toronto()
    try:
        with Ledger(args.ledger) as led:
            vt = led.data["videos_today"]
            if vt.get("date") != window_keys(now)["day"]:
                vt["date"] = window_keys(now)["day"]
                vt["jobs"] = []
            rows = []
            for name in sorted(cfg["providers"]):
                st = led.provider(name)
                rollover(st, now)
                allowed, reason, resets_at, fraction = check_provider(
                    cfg, st, name, {}, now)
                rows.append({"provider": name, "allowed": allowed,
                             "reason": reason, "resets_at": resets_at,
                             "usage_fraction": round(fraction, 4),
                             "day_tokens": st["day_tokens"],
                             "day_requests": st["day_requests"],
                             "month_chars": st["month_chars"],
                             "cooled_until": st.get("cooled_until")})
            out = {"videos_today": len(vt["jobs"]),
                   "cap": cfg.get("daily_video_cap", 3),
                   "date": vt["date"], "providers": rows}
    except Exception as e:
        return fail_open(f"status failed: {e}")
    if args.json:
        return emit(out, 0)
    print(f"videos today: {out['videos_today']}/{out['cap']} ({out['date']})")
    print(f"{'provider':<20} {'ok':<4} {'use%':<6} {'day tok':<9} "
          f"{'day req':<8} {'mo chars':<9} {'cooled until'}")
    for r in rows:
        print(f"{r['provider']:<20} {'Y' if r['allowed'] else 'N':<4} "
              f"{r['usage_fraction'] * 100:>5.0f}% {r['day_tokens']:<9} "
              f"{r['day_requests']:<8} {r['month_chars']:<9} "
              f"{r['cooled_until'] or ''}")
        if r["reason"]:
            print(f"{'':<20} -> {r['reason']}"
                  + (f" (resets {r['resets_at']})" if r["resets_at"] else ""))
    return 0


def main(argv):
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--config", default=DEFAULT_CONFIG)
    p.add_argument("--ledger", default=DEFAULT_LEDGER)
    sub = p.add_subparsers(dest="cmd", required=True)

    for name in ("check", "acquire", "record"):
        sp = sub.add_parser(name)
        sp.add_argument("--provider", required=True)
        sp.add_argument("--tokens", type=int, default=0)
        sp.add_argument("--requests", type=int, default=0)
        sp.add_argument("--chars", type=int, default=0)
        sp.add_argument("--units", type=int, default=0)

    sp = sub.add_parser("mark-cooled")
    sp.add_argument("--provider", required=True)
    sp.add_argument("--reason", required=True)
    sp.add_argument("--until", default="")

    sp = sub.add_parser("clear-cooled")
    sp.add_argument("--provider", required=True)

    sub.add_parser("video-may-start")

    sp = sub.add_parser("video-started")
    sp.add_argument("--job", required=True)

    sp = sub.add_parser("status")
    sp.add_argument("--json", action="store_true")

    args = p.parse_args(argv)
    cfg = load_config(args.config)
    try:
        if args.cmd == "check":
            return cmd_check(args, cfg)
        if args.cmd == "acquire":
            return cmd_acquire(args, cfg)
        if args.cmd == "record":
            return cmd_record(args, cfg)
        if args.cmd == "mark-cooled":
            return cmd_mark_cooled(args, cfg)
        if args.cmd == "clear-cooled":
            return cmd_clear_cooled(args, cfg)
        if args.cmd == "video-may-start":
            return cmd_video_may_start(args, cfg)
        if args.cmd == "video-started":
            return cmd_video_started(args, cfg)
        if args.cmd == "status":
            return cmd_status(args, cfg)
    except Exception as e:
        # Last-resort fail-open: never let the governor itself crash a run.
        return fail_open(f"unexpected error: {e}")
    return 2


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
