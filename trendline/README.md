# Trendline Now — continuous daily-video driver

One video at a time, continuously. Built 2026-11-21. Full documentation:
`~/workspace/goals/daily-trending-news-video-channel/README.md`.

## Files

- `config.json` — tunables (profile, env overrides, thresholds, paths).
- `state.json` — the state machine (current job/phase/pid, failures,
  completed list, paused gate). **Currently `.paused: true`** — the loop
  is gated until video #1 (job-20261121-085638) finishes and Rishi reviews it.
- `driver.sh` — one tick = one safe action; cron runs it every 10 min.
- `healthcheck.sh` — read-only health check; writes `health.json`.
- `health.json` — latest health snapshot.

## The loop (one tick per 10 min)

`idle` → start `--prep --long --profile trendlinenow` → `prep` (resume on
death; 45-min backoff on Azure 429) → `prep_done` → launch `--assemble`
→ `render_assemble` (vertical completes; landscape dies on the 180-min
timeout / VM reboot) → `render_chunks` (chunk recovery — the normal path
for landscape) → `finish` (concat + loudnorm + SRT + sidecar) →
`verify` (both finals must pass `--check-final-only`) → record completion,
clean up chunks, back to `idle` for the next video.

Nothing uploads automatically (sidecars `Approved: false`). The driver
never touches job-20261121-085638 (video #1, protected). The old
Chronicle/GiggleGarden production stays stopped and disabled.

## Start / stop

- Start (only after video #1 is reviewed): set `.paused: false` in
  `state.json`, enable cron `trendline-driver`, enable hook
  `trendline-heartbeat`.
- Stop: set `.paused: true` in `state.json`, disable the cron + hook.

## Monitor

- `jq .current state.json` — phase/job/failures.
- `jq . health.json` — status/problems/warnings/disk.
- `tail -30 ~/workspace/goals/daily-trending-news-video-channel/hidden_files/trendline-driver.log`
