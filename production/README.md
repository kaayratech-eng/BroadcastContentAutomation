# Continuous Production

A cron-driven, self-restarting loop that produces one video after another
(prep → render → verify → next), alternating Chronicle long-form and
GiggleGarden short, with health checks and automatic crash recovery.
Uploads are **never** automatic: every sidecar is written `Approved: false`
and the uploader only touches approved videos.

## Files

| File | What it does |
|---|---|
| `production/config.json` | Profiles, paths, thresholds, queue policy. The one place to change a setting. |
| `production/state.json` | The single source of truth: current job + phase, queue, next profile, completed list. |
| `production/driver.sh` | The state machine. Cron runs it every 10 min; each tick does ONE safe next action and exits in seconds. Long steps (prep / assemble / chunk render / finish) are launched detached via `setsid` and checked on the next tick. |
| `production/healthcheck.sh` | Read-only health check (process alive? log advancing? 429? services reachable? disk?). Writes `production/health.json`, appends to the driver log. Never launches or kills anything. |
| `production/render-chunks.sh` | Generic scene-aligned chunk renderer (`--job <dir> --orientation vertical\|landscape`). Skips chunks that already exist and are valid (ffprobe duration ±0.6s). Ranges come from `chunk_ranges.py`. |
| `production/chunk_ranges.py` | The single source of truth for chunk boundaries (Root.tsx formula + the per-scene rounding validated against Inanna). Reproduces the Inanna C01..C16 ranges exactly. |
| `production/finish-chunks.py` | Generic finish for one orientation: verify chunks → concat → loudnorm to −14 LUFS (with the yuv420p delivery gate) → SRT from the **props** scenes → thumbnail (landscape) → sidecar (`Approved: false`) → verify. Also has `--check-final-only` (is --assemble's output already good?) and `--check-chunks-only` (are the chunks ready to finish?). |
| `~/workspace/tmp/production-driver.log` | The one log. Everything the driver and healthcheck do is appended here. |
| `~/hooks/scripts/production-heartbeat.sh` | Detect-only heartbeat (poll 600s). Wakes the main agent only when a video completes or the driver is stuck. Never relaunches anything. |

## How the loop works (one video)

1. **idle** — the driver picks the next job: a pre-prepped job from the
   queue first (FIFO), otherwise it starts a new `--prep --long` with the
   alternating profile (trending topic auto-picked). The job id is read
   from the prep log's `Workspace:` line once it appears.
2. **prep** — `--prep` runs detached. If it dies: if `script.json` exists
   but audio/art are incomplete → `--resume` (skips scenes already on
   disk). If the log shows an Azure 429 → back off 45 min, then `--resume`.
   If it died during script generation (only a partial checkpoint) →
   `--resume` cannot use a partial, so a fresh `--prep` is required — this
   is the one open design gap (see below); after 3 failures the driver
   escalates instead of retrying.
3. **prep_done** — script + all scene audio/art present. The driver
   launches `--assemble` (vertical first, then landscape).
4. **render_assemble** — `--assemble` runs detached. If it dies after
   writing the vertical props (or the landscape props), the driver switches
   to chunk recovery. If its log is silent for 30 min and the driver owns
   the process, the driver kills it and switches to chunk recovery. A
   monitor-only process (one the driver did not launch, e.g. the ducks
   re-render) is **never** killed.
5. **render_chunks** — for each orientation whose final is not already
   done and whose chunks are not already valid, `render-chunks.sh` runs
   detached (5 scenes per chunk, `--concurrency=1`). A reboot just kills
   the chunk render; the next tick re-runs it and it skips valid chunks.
6. **finish** — when an orientation's chunks are all valid,
   `finish-chunks.py` runs detached (concat → loudnorm → SRT → thumbnail
   → sidecar → verify).
7. **verify** — each orientation must pass `--check-final-only` (final
   file + sidecar + SRT, delivery gate). When both pass, the video is
   recorded in `state.json` (`.completed`), `next_profile` flips, and the
   driver returns to **idle** — the next video starts on the next tick.

## Failure modes

| Failure | Detection | Recovery |
|---|---|---|
| VM reboot mid-prep | Process dead on next tick | `--resume` (skips scenes already on disk); 429 → 45 min backoff first |
| VM reboot mid-render | Process dead / props present but no final | Chunk render from the first missing chunk (valid chunks skipped) |
| Azure 429 during TTS | `429` in the prep log | Back off 45 min, then `--resume`. Never hammer F0. |
| Render stall (no log progress) | Log silent ≥ 30 min | Driver-owned process: kill + chunk-recover. Monitor-only: warn only, never kill. |
| Prep died during script gen | `script-partial.json` exists, no `script.json` | Fresh `--prep` required (open gap); escalate after 3 failures |
| Verification fails 3 times | `.current.failures >= 3` | Driver pauses that job and escalates to Rishi; it never loops forever |
| Disk < 20 GB / < 10 GB | Healthcheck every tick | Warn / critical; the driver stops starting new work (critical) |
| Service down (Groq / Pollinations / Azure / Pexels) | Healthcheck probes | Warn; renders are unaffected (only prep needs the services) |

## Start / stop / monitor

- **Start:** the cron runs the driver every 10 min. Nothing else to start.
  To start it now by hand: `~/workspace/BroadcastContentAutomation/production/driver.sh`
- **Stop:** disable the cron (`cron` job `production-driver`) — the current
  detached step keeps running to its checkpoint, and the driver will not
  launch anything new. To stop a running step too, kill its pid (in
  `state.json` `.current.pid`) — but only a driver-owned one.
- **Monitor:** `tail -f ~/workspace/tmp/production-driver.log` and
  `cat ~/workspace/BroadcastContentAutomation/production/health.json`.
  Current job + phase: `jq .current ~/workspace/BroadcastContentAutomation/production/state.json`.
- **Dry-run tests:** `production/render-chunks.sh --job <job> --orientation <o> --dry-run`
  (ranges only, no render) and `production/finish-chunks.py --job <job> --orientation <o> --dry-run`.

## What is proven vs. what still needs a live run

Proven (dry-run / synthetic):
- Chunk ranges reproduce the Inanna C01..C16 ranges exactly.
- The finish pipeline runs end-to-end on a synthetic job (concat,
  loudnorm −14.03 LUFS, SRT 6 cues, sidecar with the correct vertical
  fields, delivery gate) and exits 0.
- The driver tick is a no-op (healthcheck only) while a monitor-only
  render is running.
- Healthcheck probes run (Azure voices, Groq/Pollinations/Pexels
  reachability, disk).

Still needs a live run (the driver has not yet launched a real prep or
render):
- A full cycle: new prep → assemble → chunk recovery → finish → verify →
  next video. The first live cycle should be watched, not assumed.
- The 429 backoff path (needs a real throttle to trigger).
- The stall-kill path (needs a real 30-min-silent log).

## Open decisions for Rishi

- Queue policy: alternate Chronicle long-form / GiggleGarden short (the
  current default). Confirm or change.
- How many videos per day, if any cap (the natural continuous rate is
  ~2–3/day alternating; Rishi said non-stop, so the default is no cap).
- Auto-start after a VM reboot is built in (the cron re-invokes the
  driver). Flag it explicitly: the loop restarts itself after a reboot.
- Whether the driver should ever auto-retry a video that fails
  verification 3+ times, or always escalate (current: escalate).
- The script-partial gap: if a prep dies during script generation,
  `--resume` cannot use the partial — a fresh `--prep` is the only
  recovery. Whether to build a `--prep --job` re-entry path (a design
  decision, not yet made).
