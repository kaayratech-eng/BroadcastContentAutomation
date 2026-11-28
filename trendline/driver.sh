#!/bin/bash
# trendline/driver.sh — the Trendline Now continuous daily-video driver.
#
# One video at a time, continuously: prep -> assemble -> chunk-recover ->
# finish -> verify -> next video. Cron runs it every 10 min; each tick does
# ONE safe next action and exits in seconds. Long steps (prep / assemble /
# chunk render / finish) are launched detached via setsid and checked on the
# next tick. A VM reboot just kills the detached step; the next cron tick
# sees the dead process (or the missing output) and resumes from the last
# checkpoint. No background agent, no session, no heartbeat relaunch.
#
# Adapted from production/driver.sh (the Chronicle/GiggleGarden loop, now
# stopped). Differences for Trendline Now:
#   * Single profile (trendlinenow), no alternation, no pre-prepped queue
#     (the queue field exists for a future manual job, FIFO).
#   * Prep is `--prep --long --profile trendlinenow` with the env overrides
#     GIGGLE_ScriptProvider=pool-helper and GIGGLE_SceneArtProvider=cloudflare.
#     The config defaults are wrong for this channel (hybrid-helper routes
#     acts 3+ to depleted Gemini; pollinations is paid). Env beats
#     appsettings.Local.json, so this export is the single source of truth
#     for the script provider: pool-helper tries Groq, then Cloudflare
#     Workers AI, then Pollinations, per act.
#   * The landscape render (~20960 frames, ~6h at ~1fps) ALWAYS exceeds the
#     180-min Remotion timeout and the ~2-4h VM lifetime, so chunk recovery
#     is the NORMAL path for landscape, not an exception. --assemble is
#     expected to deliver the vertical and die on the landscape; the driver
#     chunk-recovers the landscape from the props --assemble wrote.
#   * prep_complete() treats "visual" scenes (charts/callouts drawn by
#     Remotion from VisualSpec, nothing on disk) as complete, and counts
#     a scene resolved when its ImagePath, LandscapeImagePath or ClipPath
#     (a context scene reusing another scene's stock clip) exists on disk.
#     The old driver's check required a file for every scene and would have called
#     a fully-prepped Trendline Now job incomplete forever.
#   * chunk_ranges.py is intro-aware (Trendline Now opens with a 3.5s
#     intro sting). The old module ignored introSeconds and computed ranges
#     99 frames short for this channel.
#   * No systemd service: chunk renders run detached via setsid only.
#     /etc is wiped on VM replacement, so a systemd unit is a liability
#     here; the cron tick re-launches a dead chunk render just as well.
#   * A paused gate (state.json .paused): the driver does nothing while
#     paused. It starts paused until video #1 finishes and Rishi reviews it.
#   * A protected-job guard: job-20261121-085638 (video #1, owned by the
#     build subagent) is never touched by this driver.
#   * Laptop renders (2026-11-23, Rishi: the VM does code + orchestration
#     only, all heavy lifting on his laptop): when config .laptop.enabled
#     is true, prep_done writes props with --write-props (no render) and
#     render_chunks drives trendline/laptop-render.sh (ship / trigger /
#     poll / pull over Tailscale + SSH). The VM render path stays OFF
#     unless config .laptop.vm_render_fallback_enabled is true, so the
#     driver never silently renders on the VM while the laptop link is
#     being set up.
#
# Phases (state.json .current.phase):
#   idle            — nothing current; pick the next job (queued first,
#                     else start a new --prep --long --profile trendlinenow).
#   prep            — a --prep is running (or was killed). If the process
#                     died: if the log shows a 429, back off 45 min, then
#                     --resume; if script.json exists, --resume (skips scenes
#                     already on disk); if only a partial exists, --prep
#                     --job <job> resumes from the partial checkpoint
#                     (reuses the saved scenes verbatim).
#   prep_done       — script.json + all scene audio/art present; ready to
#                     render. (Detected from the job dir, not just the log.)
#                     Laptop mode: --write-props here, then render_chunks.
#   render_assemble — a driver-launched --assemble is running (vertical
#                     first, then landscape). If it dies after writing the
#                     vertical props (or the landscape props), the driver
#                     switches to chunk recovery. If its log is silent for
#                     30 min and the driver owns the process, the driver
#                     kills it and switches to chunk recovery. Never
#                     entered in laptop mode (guarded to render_chunks).
#   render_chunks   — a driver-launched chunk render (render-chunks.sh) is
#                     running. If it dies, re-run it: it skips valid chunks.
#                     Laptop mode: one laptop-render.sh step per orientation
#                     per tick instead (ship/trigger/poll/pull).
#   finish          — a driver-launched finish-chunks.py run (concat +
#                     loudnorm + SRT + sidecar) is running detached. When it
#                     exits, the driver re-checks that orientation.
#   verify          — both orientations rendered; the driver checks each
#                     final with finish-chunks.py --check-final-only and
#                     finishes (or re-renders) any that is missing/invalid.
#   done            — outputs verified. Record the completion, advance to
#                     idle — the next video starts on the NEXT tick, never
#                     inside this tick, so a tick never does two videos'
#                     worth of work.
#
# Safety rules (hard):
#   * flock on trendline/driver.lock — only one driver tick at a time.
#   * Only processes the driver launched (tracked by pid in state.json,
#     driver_owned=true) are ever killed. A monitor-only job (e.g. the
#     video #1 build, launched by the build subagent) is NEVER touched.
#   * Backpressure (B8): idle never starts new work while a completed video
#     waits for review (sidecar Approved:false in the watch dir).
#   * Collision guards (B25): every launch site also pgrep-checks for a
#     manual pipeline step for the same job — the recorded pid is not enough.
#   * Silent deaths (B6): memory context is logged before every --assemble
#     and again when one dies, with the last render frame and a FATAL line.
#   * Retention (B26): the 5 most recent job dirs are always kept; older
#     ones are pruned only when completed/abandoned and over 7 days old.
#   * state.json writes use per-call mktemp files and always refresh the
#     top-level .updated_at (B16); throttle markers use the single shared
#     whole-log rule in throttle.sh (B15).
#   * Never re-render a finished output: chunk renders skip valid chunks,
#     and finish only runs when the final file is missing or invalid.
#   * Never upload anything. Sidecars are written Approved=false.
#   * Failure limit (Rishi 2026-11-23): a job that hits failures>=3 is
#     AUTO-RESET, not parked — the driver clears the failure/retry
#     counters, kills orphaned pipeline processes for the job, and
#     restarts the current step from its checkpoint. At most 2
#     consecutive auto-resets with no progress (same progress marker);
#     the third no-progress hit escalates and the driver stops, as
#     before. A manual failures reset should also clear
#     .current.auto_resets (the driver self-heals a stale streak when it
#     next sees progress, but do not rely on that).
#   * Azure 429: back off 45 min before any --resume (never hammer F0).
#   * The driver never touches job-20261121-085638 (video #1).
set -u
TRENDLINE=${TRENDLINE_DIR:-~/workspace/BroadcastContentAutomation/trendline}
PROD=~/workspace/BroadcastContentAutomation/production
REPO=~/workspace/BroadcastContentAutomation
STATE="${TRENDLINE_STATE:-$TRENDLINE/state.json}"
LOG=$(jq -r '.driver_log' "${TRENDLINE_CONFIG:-$TRENDLINE/config.json}" 2>/dev/null || echo ~/workspace/goals/daily-trending-news-video-channel/hidden_files/trendline-driver.log)
LOCK="$TRENDLINE/driver.lock"
CONFIG="${TRENDLINE_CONFIG:-$TRENDLINE/config.json}"

export DOTNET_ROOT=~/.dotnet
export PATH="$HOME/.dotnet:$PATH"
export TMPDIR=~/workspace/tmp
export GIGGLE_ScriptProvider=pool-helper
export GIGGLE_SceneArtProvider=cloudflare

# Shared throttle-marker rule (review #2 B15): one pattern + whole-log
# check, sourced from throttle.sh so driver.sh and healthcheck.sh cannot
# drift apart again.
THROTTLE_SH="$TRENDLINE/throttle.sh"
[ -f "$THROTTLE_SH" ] && . "$THROTTLE_SH"

mkdir -p "$(dirname "$LOG")" 2>/dev/null || true
log() { echo "$(date -Is) [trendline-driver] $*" | tee -a "$LOG"; }

# Launch a long step detached (setsid, log to a file). Sets LAUNCH_PID and
# LAUNCH_PGID. When TRENDLINE_DRY_RUN=1, nothing is launched — the command
# is only logged, and LAUNCH_PID is 0. Used by the dry-run tests (and by
# hand to see what the driver would do without starting any real work).
# setsid makes the child its own session and process-group leader, so the
# PGID equals the PID. Both are recorded in state.json (.current.pid /
# .current.pgid) so a stall-kill can take the whole group (kill -- -$PGID)
# instead of orphaning the real dotnet/remotion child.
launch() { # $1 = command string (run via setsid bash -c)
  if [ "${TRENDLINE_DRY_RUN:-0}" = "1" ]; then
    log "DRY-RUN: would launch: $1"
    LAUNCH_PID=0
    LAUNCH_PGID=0
    return 0
  fi
  # Close the driver lock fd (9) in the child: a detached step otherwise
  # inherits it and holds the flock for hours, so every later tick exits
  # "another driver tick is running" while the step is alive (seen
  # 2026-11-23 13:04/13:14, when the running prep held the lock). flock is
  # released by the kernel when a process dies, so a dead process can
  # never hold a stale lock — the live-child inheritance is the real
  # stale-lock case, and closing the fd here removes it. (The auto-reset
  # also kills the job's orphaned processes, which releases any lock fd
  # an orphan inherited before this fix.)
  setsid bash -c "$1" 9>&- &
  LAUNCH_PID=$!
  LAUNCH_PGID=$LAUNCH_PID
}

# --- single-instance guard ---------------------------------------------------
exec 9>"$LOCK"
if ! flock -n 9; then
  # L6: exit 0 is deliberate here — a concurrent tick is normal (the
  # other tick is doing the work); this tick did nothing wrong.
  log "another driver tick is running; exiting"
  exit 0
fi

# --- helpers -----------------------------------------------------------------
# B16: save_state writes via a per-call mktemp file (never the shared
# "$STATE.tmp" — healthcheck.sh used to write the same path with no lock,
# so a concurrent write could tear state.json) and always refreshes the
# top-level .updated_at, which used to sit stuck at 2026-11-21T19:45:53
# while .current.updated_at moved. Callers that set .current.updated_at
# themselves keep doing so; this is the file-level stamp.
save_state() { # $1 = jq filter (applied to state.json, atomic via tmp file)
  local tmp
  # L4: a failed state write used to be silent — the next tick re-read
  # stale state (failures, phase, auto-resets) with no trace. Log it as
  # CRITICAL with job/phase/filter and fail, so callers can abort the tick
  # instead of proceeding blind.
  tmp=$(mktemp "$STATE.tmp.XXXXXX") || {
    log "CRITICAL: state.json write failed (rc 1, mktemp failed) for ${JOB_ID:-none} phase ${PHASE:-unknown}: $1; state may be stale, not proceeding blind"
    return 1
  }
  jq "$1 | .updated_at = \"$(date -Is)\"" "$STATE" > "$tmp" && mv "$tmp" "$STATE"
  local rc=$?
  if [ $rc -ne 0 ]; then
    rm -f "$tmp"
    log "CRITICAL: state.json write failed (rc $rc) for ${JOB_ID:-none} phase ${PHASE:-unknown}: $1; state may be stale, not proceeding blind"
    return 1
  fi
  return 0
}
set_phase() { # $1 = phase, $2 = pid (or null), $3 = log path (or null)
  # The pid is a setsid group leader, so it is also the PGID (see launch()).
  local pgid="null"
  [ "$2" != "null" ] && pgid="$2"
  # L4: a phase transition that cannot be recorded must abort the tick —
  # continuing would run work under the wrong recorded phase.
  save_state ".current.phase = \"$1\" | .current.pid = $2 | .current.pgid = $pgid | .current.log = $3 | .current.updated_at = \"$(date -Is)\"" || exit 1
}
bump_failure() { # $1 = reason for the failure
  # L36: the failure count used to rise with no log line — callers were
  # relied on to have logged something, and several did not name the
  # reason. Log the job, phase, reason and the new count here, at the
  # increment site, so every bump is greppable.
  local reason="${1:-unspecified failure}"
  save_state ".current.failures = (.current.failures // 0) + 1 | .current.updated_at = \"$(date -Is)\"" || return 1
  local new_count
  new_count=$(jq -r '.current.failures // 0' "$STATE" 2>/dev/null || echo "?")
  log "failure ${JOB_ID:-none} (${PHASE:-unknown}): $reason — failures now $new_count/${MAX_FAILURES:-?}"
}
# progress_marker: a cheap fingerprint of how far the current job has got,
# used by the failure-limit auto-reset guard. A chunk file only appears
# after its chunk finished: render-chunks.sh renders ranges sequentially
# (--concurrency=1), so chunk N+1 only starts once chunk N is valid, and
# a half-written chunk reuses the same filename — the counts move only
# when a chunk actually completes. Prep progress is the non-empty scene
# mp3 count + script.json presence; a phase change also moves the marker.
# Caveat: a partially-written chunk file counts once, the same as a
# finished one for that chunk — the marker can only move when a NEW chunk
# file appears (or a phase/mp3 count changes), never from a chunk merely
# growing, so this cannot mask a stuck chunk. $1 = job dir (may be empty
# / missing for a prep that never created one).
progress_marker() { # $1 = job dir
  local dir="$1" m="phase=$PHASE"
  if [ -n "$dir" ] && [ -d "$dir" ]; then
    local script="no" mp3s=0 vch=0 lch=0
    [ -f "$dir/script.json" ] && script="yes"
    mp3s=$(find "$dir" -maxdepth 1 -name 'scene[0-9]*.mp3' -size +0c 2>/dev/null | wc -l)
    mp3s=${mp3s// /}
    [ -d "$dir/chunks-vertical" ] && vch=$(find "$dir/chunks-vertical" -maxdepth 1 -name 'vertical-C[0-9]*.mp4' -size +0c 2>/dev/null | wc -l)
    vch=${vch// /}
    [ -d "$dir/chunks-landscape" ] && lch=$(find "$dir/chunks-landscape" -maxdepth 1 -name 'landscape-C[0-9]*.mp4' -size +0c 2>/dev/null | wc -l)
    lch=${lch// /}
    m="$m script=$script mp3s=$mp3s vchunks=$vch lchunks=$lch"
  else
    m="$m nojobdir"
  fi
  echo "$m"
}
# abandon_current: move the current job to .abandoned with a reason, then
# reset .current to idle. Never silently discards a job's history (the old
# code reset to idle and lost the job, its phase, and its failure count).
# Shape matches the existing abandoned entries: {job, reason, abandoned_at,
# failures}.
abandon_current() { # $1 = reason
  local reason="$1"
  local now
  now=$(date -Is)
  if [ -n "$JOB_ID" ]; then
    local tmp
    tmp=$(mktemp "$STATE.tmp.XXXXXX") || return 1
    jq --arg job "$JOB_ID" --arg reason "$reason" --arg now "$now" \
      '.abandoned = ((.abandoned // []) + [{job: $job, reason: $reason, abandoned_at: $now, failures: (.current.failures // 0)}]) | .current = {job: null, profile: null, phase: "idle", pid: null, pgid: null, log: null, driver_owned: false, failures: 0, assemble_retries: 0, chunk_retries: 0, updated_at: $now} | .updated_at = $now' \
      "$STATE" > "$tmp" && mv "$tmp" "$STATE"
    log "abandoned job $JOB_ID: $reason"
  else
    save_state ".current = {job: null, profile: null, phase: \"idle\", pid: null, pgid: null, log: null, driver_owned: false, failures: 0, assemble_retries: 0, chunk_retries: 0, updated_at: \"$now\"}"
    log "reset to idle (no current job): $reason"
  fi
}
job_dir() { # $1 = job id (profile is always trendlinenow)
  echo "$REPO/work/trendlinenow/$1"
}
# prep_complete: 0 if script.json + every scene has its audio and visual.
# A "visual" scene (chart/table/callout) is drawn by Remotion from its
# VisualSpec at assemble time — nothing on disk — so it counts as complete
# when the spec is present. Every other scene needs sceneNN.mp3 plus a
# visual file (ImagePath/LandscapeImagePath from script.json, or a
# sceneNN-stock.mp4/.jpg). One fresh visual per scene (fix sequence #2
# ITEM 3): every scene has its own VisualGroup now, so the visual file is
# named for the scene itself, not shared across a group.
# Audio checks require the file to be non-empty: a truncated zero-byte mp3
# left by a kill mid-synthesis used to pass the existence-only check and
# ship as clipped narration. Brief audio is required whenever the script
# has brief scenes, or the landscape renders a silent brief.
prep_complete() { # $1 = job dir
  python3 - "$1" <<'PYEOF'
import glob, json, os, sys
job = sys.argv[1]
try:
    script = json.load(open(os.path.join(job, "script.json")))
except Exception:
    sys.exit(1)
def audio_ok(path):
    # Existence-only checks pass zero-byte (truncated) files; require bytes.
    return os.path.exists(path) and os.path.getsize(path) > 0
for i, sc in enumerate(script["Scenes"]):
    if not audio_ok(os.path.join(job, f"scene{i:02d}.mp3")):
        sys.exit(1)
    if sc.get("SceneKind") == "visual":
        if sc.get("VisualSpec"):
            continue
        sys.exit(1)
    if sc.get("VisualMissing"):
        # B12 (ITEM 4): a flagged scene has no visual by design — its
        # audio is still required (checked above), but the visual check is
        # waived so the prep can advance to prep_done for review instead
        # of stalling forever on a scene that will never get art.
        continue
    visual = False
    for key in ("ImagePath", "LandscapeImagePath", "ClipPath"):
        p = sc.get(key)
        if p and os.path.exists(p):
            visual = True
            break
    if not visual:
        for ext in (".mp4", ".jpg"):
            if os.path.exists(os.path.join(job, f"scene{i:02d}-stock{ext}")):
                visual = True
                break
    if not visual:
        sys.exit(1)
# Brief audio: a job with brief scenes needs its briefNN.mp3 files, or the
# landscape renders a silent brief (assemble voices the brief in TTS, so a
# missing/empty brief mp3 means the prep never finished it).
for i, b in enumerate(script.get("BriefScenes") or []):
    if not audio_ok(os.path.join(job, f"brief{i:02d}.mp3")):
        sys.exit(1)
# Outro audio: a shared cache (work/outro/trendlinenow-*.mp3) generated at
# assemble time, NOT by prep — so a missing cache file is fine (assemble
# synthesises it on demand) and must NOT fail prep (that would deadlock:
# prep never completes, assemble never runs, cache never created). But a
# zero-byte cache file IS a failure: assemble trusts File.Exists and would
# ship a silent outro. Fail only on a corrupt (empty) cache file.
outro_dir = os.path.join(os.path.dirname(os.path.dirname(job)), "outro")
for p in glob.glob(os.path.join(outro_dir, "trendlinenow-*.mp3")):
    if os.path.getsize(p) == 0:
        sys.exit(1)
sys.exit(0)
PYEOF
}
pid_alive() { # $1 = pid -> 0 if alive AND its cmdline is a pipeline step
  # A recorded pid can be reused by an unrelated process after a VM
  # restart, so kill -0 alone is not enough: require the cmdline to look
  # like a driver-launched step (a setsid bash whose command mentions a
  # pipeline entry point). All driver launches go through launch(), so
  # every legitimate recorded pid matches one of these markers.
  local pid="$1"
  [ -n "$pid" ] && [ "$pid" != "null" ] && [ "$pid" != "0" ] || return 1
  kill -0 "$pid" 2>/dev/null || return 1
  local cmd
  cmd=$(ps -o args= -p "$pid" 2>/dev/null || echo "")
  case "$cmd" in
    *VideoGen.dll*|*render-chunks.sh*|*finish-chunks.py*|*remotion*) return 0 ;;
    *) return 1 ;;
  esac
}
# kill_step_group: kill a whole step process group, not just the wrapper.
# launch() uses setsid, so the recorded pid IS the process-group leader
# (PGID == PID, also recorded as .current.pgid); killing the group takes
# the dotnet/remotion children with it instead of orphaning them (the old
# kill only took the bash wrapper, and the orphan kept rendering). Falls
# back to a single-pid kill if the pid is not a group leader.
kill_step_group() { # $1 = pid (group leader)
  local pid="$1"
  [ -n "$pid" ] && [ "$pid" != "null" ] && [ "$pid" != "0" ] || return 0
  local pgid
  pgid=$(ps -o pgid= -p "$pid" 2>/dev/null | tr -d ' ')
  if [ -n "$pgid" ] && [ "$pgid" = "$pid" ]; then
    kill -- "-$pid" 2>/dev/null || true
    sleep 2
    kill -9 -- "-$pid" 2>/dev/null || true
  else
    kill "$pid" 2>/dev/null || true
    sleep 2
    kill -9 "$pid" 2>/dev/null || true
  fi
}
# kill_job_orphans: kill leftover pipeline processes for this job whose
# wrapper is gone (e.g. a remotion render child that survived its
# --assemble / chunk-render wrapper dying — the known orphan case from
# the kill_step_group comment). Only ever runs for a driver-owned step
# (DRIVER_OWNED=true); a manual step's processes are never touched.
# Scoped to the job id, so other jobs' steps are safe. $1 = job id.
# L37: a successful kill used to log nothing (only the non-driver-owned
# skip was logged), so the auto-reset could never say how many orphans
# died. Counts the matching pids BEFORE killing and reports the outcome
# in ORPHAN_KILLED_COUNT / ORPHAN_KILLED_PIDS for the caller to log.
kill_job_orphans() { # $1 = job id
  local job="$1"
  ORPHAN_KILLED_COUNT=0
  ORPHAN_KILLED_PIDS="none"
  [ -n "$job" ] || return 0
  if [ "$DRIVER_OWNED" != "true" ]; then
    log "auto-reset: current step is not driver-owned — leaving any orphaned processes for $job alone"
    ORPHAN_KILLED_PIDS="none (not driver-owned — left alone)"
    return 0
  fi
  local pat pids all
  all=""
  for pat in "VideoGen.dll --assemble.*$job" "VideoGen.dll --prep.*$job" "VideoGen.dll --resume.*$job" "remotion render.*$job" "render-chunks.sh.*$job" "finish-chunks.py.*$job"; do
    pids=$(pgrep -f "$pat" 2>/dev/null || true)
    [ -n "$pids" ] && all="$all $pids"
  done
  # Deduplicate (one process can match several patterns) and count.
  all=$(printf '%s\n' $all | sort -u | tr '\n' ' ' | sed 's/^ *//; s/ *$//')
  if [ -n "$all" ]; then
    ORPHAN_KILLED_COUNT=$(printf '%s' "$all" | wc -w | tr -d ' ')
    ORPHAN_KILLED_PIDS=$(printf '%s' "$all" | tr ' ' ',')
  fi
  for pat in "VideoGen.dll --assemble.*$job" "VideoGen.dll --prep.*$job" "VideoGen.dll --resume.*$job" "remotion render.*$job" "render-chunks.sh.*$job" "finish-chunks.py.*$job"; do
    pkill -f "$pat" 2>/dev/null || true
  done
  sleep 2
  for pat in "VideoGen.dll --assemble.*$job" "VideoGen.dll --prep.*$job" "VideoGen.dll --resume.*$job" "remotion render.*$job" "render-chunks.sh.*$job" "finish-chunks.py.*$job"; do
    pkill -9 -f "$pat" 2>/dev/null || true
  done
  log "auto-reset: killed $ORPHAN_KILLED_COUNT orphan processes ($ORPHAN_KILLED_PIDS) for $job"
}
# step_log_age_min: minutes since the current step log was last written.
step_log_age_min() { # uses $STEP_LOG
  [ -n "$STEP_LOG" ] && [ -f "$STEP_LOG" ] || { echo 0; return 0; }
  local mtime
  mtime=$(stat -c %Y "$STEP_LOG" 2>/dev/null || echo 0)
  echo $(( ($(date +%s) - mtime) / 60 ))
}
# step_stalled: 0 if the current step log has been silent >= $1 minutes.
step_stalled() { # $1 = minutes
  local threshold="$1"
  [ -n "$STEP_LOG" ] && [ -f "$STEP_LOG" ] || return 1
  [ "$(step_log_age_min)" -ge "$threshold" ]
}
log_tail_has() { # $1 = log, $2 = pattern -> 0 if found in the last 200 lines
  [ -f "$1" ] && tail -200 "$1" 2>/dev/null | grep -q "$2"
}
extract_job_from_log() { # $1 = log -> prints "<job>" or nothing
  # --prep prints: Workspace: .../work/trendlinenow/<job-YYYYMMDD-HHMMSS>
  grep -m1 -o "work/trendlinenow/job-[0-9-]*" "$1" 2>/dev/null | \
    sed 's|^work/trendlinenow/\(job-[0-9-]*\)$|\1|'
}
# B6: memory/OOM context for the next silent --assemble death. The
# driver-launched --assemble that died at frame 129/2430 left no FATAL or
# SIGTERM line (SIGKILL/OOM cannot be caught in-process), so the driver
# itself records the memory picture before launching a render and again
# when one dies: free RAM, cgroup limits/usage and the OOM-killer count,
# plus any kernel OOM line. Call sites: prep_done launch, render_assemble
# death.
log_memory_context() { # $1 = tag
  local tag="$1"
  local mem_line
  mem_line=$(free -m 2>/dev/null | awk '/^Mem:/{print "mem total="$2"MB used="$3"MB free="$4"MB avail="$7"MB"}' || echo "mem unavailable")
  local cg=""
  if [ -f /sys/fs/cgroup/memory.max ]; then
    cg="cgroup max=$(cat /sys/fs/cgroup/memory.max 2>/dev/null) current=$(cat /sys/fs/cgroup/memory.current 2>/dev/null)"
    local oom
    oom=$(grep -o "oom_kill [0-9]*" /sys/fs/cgroup/memory.events 2>/dev/null || echo "oom_kill ?")
    cg="$cg $oom"
  fi
  log "memory[$tag]: $mem_line $cg"
  # Kernel OOM-killer evidence (best-effort: dmesg may be unreadable here).
  local oom_line
  oom_line=$(dmesg 2>/dev/null | grep -i "out of memory" | tail -1 || true)
  [ -n "$oom_line" ] && log "memory[$tag]: kernel OOM evidence: $oom_line"
  return 0 # the && above exits 1 when there is no OOM line; the caller must not read that as a failure
}
# --- memory gate ---------------------------------------------------------------
# Rishi 2026-11-23: renders die from memory pressure when launched while
# free RAM is low. Evidence: launches at ~150-250 MB free died at frame
# ~58-96 (16:54 + 17:04 chunk renders; 13:34-16:34 assembles); the vertical
# chunks that completed launched at ~782 MB free, ~40-55 min after the
# ~13:51 VM restart; and free decays to ~125 MB within ~30 min of boot
# (seen after the 18:05 restart). The gate blocks a RENDER launch
# (--assemble or a chunk render) when free RAM is below the threshold or
# the machine has been up too long — the tick becomes a no-op wait instead
# of another death, and the VM's own restart (every ~2-4h) re-opens the
# window. It never counts a failure, never touches state, and never kills
# a running step: it only guards launches. Prep launches are NOT gated
# (dotnet + API calls, no Chromium; prep deaths were provider/silent, not
# memory). Disabled by default (config .render.memory_gate_enabled) until
# Rishi switches it on. Thresholds are reasoned from the observed death
# point (~150-250 MB) vs success point (~782 MB) — unproven until a real
# gated launch succeeds. $1/$2 = free-MB / uptime-min overrides (harness
# only; a live call passes neither).
memory_gate_ok() {
  local enabled min_free max_uptime free_mb uptime_min
  enabled=$(jq -r '.render.memory_gate_enabled // false' "$CONFIG")
  [ "$enabled" = "true" ] || return 0
  min_free=$(jq -r '.render.memory_gate_min_free_mb // 400' "$CONFIG")
  max_uptime=$(jq -r '.render.memory_gate_max_uptime_minutes // 60' "$CONFIG")
  # $1/$2 override the live readings (harness only). An unreadable live
  # reading fails CLOSED: free_mb/uptime_min end up empty and the case
  # checks below block the launch, instead of silently defaulting to 0.
  if [ $# -ge 1 ]; then free_mb="$1"; else free_mb=$(free -m 2>/dev/null | awk '/^Mem:/{print $4}'); fi
  if [ $# -ge 2 ]; then
    uptime_min="$2"
  else
    local uptime_sec
    uptime_sec=$(awk '{print int($1)}' /proc/uptime 2>/dev/null) || uptime_sec=""
    if [ -z "$uptime_sec" ]; then uptime_min=""; else uptime_min=$((uptime_sec / 60)); fi
  fi
  case "$free_mb" in ''|*[!0-9]*) log "memory gate: cannot read free RAM — waiting (fail closed)"; return 1;; esac
  case "$uptime_min" in ''|*[!0-9]*) log "memory gate: cannot read uptime — waiting (fail closed)"; return 1;; esac
  if [ "$free_mb" -lt "$min_free" ]; then
    log "memory gate: free ${free_mb}MB < ${min_free}MB — not launching a render this tick (waiting for the VM restart to free memory)"
    return 1
  fi
  if [ "$uptime_min" -gt "$max_uptime" ]; then
    log "memory gate: uptime ${uptime_min}m > ${max_uptime}m (free ${free_mb}MB) — not launching a render this tick (memory decays with uptime; waiting for the VM restart)"
    return 1
  fi
  log "memory gate: pass (free ${free_mb}MB >= ${min_free}MB, uptime ${uptime_min}m <= ${max_uptime}m)"
  return 0
}
# Last Remotion frame counter in a step log ("129/2430") or "none".
last_render_frame() { # $1 = log
  local frame=""
  [ -n "$1" ] && [ -f "$1" ] && frame=$(grep -oE "Rendered [0-9]+/[0-9]+" "$1" 2>/dev/null | tail -1 | sed 's/Rendered //')
  echo "${frame:-none}"
}
# step_log_tail: last $2 (default 3) lines of a step log, joined with
# " | " into one log line; "no step log" / "step log empty" when there
# is nothing to show. L41: death lines must carry the underlying error,
# not just a symptom ("final still invalid").
step_log_tail() { # $1 = log, $2 = lines (default 3)
  local n="${2:-3}"
  if [ -z "${1:-}" ] || [ ! -f "$1" ]; then echo "no step log"; return 0; fi
  local tail_out
  tail_out=$(tail -n "$n" "$1" 2>/dev/null | awk '{printf "%s%s", (NR>1?" | ":""), $0} END{print ""}')
  [ -n "$tail_out" ] && echo "$tail_out" || echo "step log empty"
}
# step_exit_code: the step's exit code as recorded in its own log
# ("exit 137", "exit code 1", "ffmpeg failed (exit 1)"), or "unknown".
# A detached step has no wait status for the driver to read — the log
# is the only source, so "unknown" is honest when the log never says.
step_exit_code() { # $1 = log -> digits or "unknown"
  if [ -z "${1:-}" ] || [ ! -f "$1" ]; then echo "unknown"; return 0; fi
  local code
  code=$(grep -oE '\b(exit code|exit)[ :]*[0-9]+' "$1" 2>/dev/null | tail -1 | grep -oE '[0-9]+$' || true)
  [ -n "$code" ] && echo "$code" || echo "unknown"
}
# B8: how many completed videos are waiting for review — a final mp4 in
# the watch dir whose sidecar exists and is not Approved:true. A completed
# video only leaves the watch dir once Rishi approves it and the uploader
# publishes it (then it moves to done/), so this is exactly the unapproved
# backlog the driver must not grow. $1 overrides the videos dir (tests).
unapproved_completed_count() {
  local dir="${1:-$(jq -r '.videos_dir' "$CONFIG" 2>/dev/null)}"
  local n=0
  [ -n "$dir" ] && [ -d "$dir/trendlinenow" ] || { echo 0; return 0; }
  local f side approved
  for f in "$dir"/trendlinenow/*.mp4; do
    [ -e "$f" ] || continue
    side="${f%.mp4}.json"
    [ -f "$side" ] || continue
    approved=$(jq -r '.Approved // false' "$side" 2>/dev/null || echo false)
    [ "$approved" != "true" ] && n=$((n + 1))
  done
  echo "$n"
}
# B25: a manual render/prep/assemble is invisible to the recorded pid
# (exactly the manual-takeover pattern used for videos #2/#3), so every
# launch site also checks the process table for a live pipeline step for
# this job. The render_chunks phase already had its own pgrep guard; this
# is the same guard for the prep / prep_done / render_assemble launches.
pipeline_step_running() { # $1 = job id -> 0 if any pipeline step is live for it
  local job="$1"
  [ -n "$job" ] || return 1
  pgrep -f "VideoGen.dll --assemble.*$job" >/dev/null 2>&1 && return 0
  pgrep -f "VideoGen.dll --prep.*$job" >/dev/null 2>&1 && return 0
  pgrep -f "VideoGen.dll --resume.*$job" >/dev/null 2>&1 && return 0
  pgrep -f "remotion render.*$job" >/dev/null 2>&1 && return 0
  pgrep -f "render-chunks.sh.*$job" >/dev/null 2>&1 && return 0
  return 1
}
# B26 retention policy: keep the PRUNE_KEEP_JOBS most recent job dirs (by
# mtime) plus the protected job and the current job, always. Older dirs are
# pruned ONLY when the job is recorded completed/abandoned in state.json
# AND the dir is older than PRUNE_MIN_AGE_DAYS — a completed-but-unreviewed
# video gets a week before its source dir is pruned (the finals + sidecars
# in videos/ are the durable artifacts; the job dir is regenerable
# source). Jobs state.json does not know are never pruned. Stale driver
# logs in the tmp dir are pruned on the same age rule.
PRUNE_KEEP_JOBS=5
PRUNE_MIN_AGE_DAYS=7
prune_old_jobs() {
  local work="${REPO}/work/trendlinenow"
  [ -d "$work" ] || return 0
  local now_ts min_age_s kept dir job known mtime
  now_ts=$(date +%s)
  min_age_s=$((PRUNE_MIN_AGE_DAYS * 86400))
  kept=0
  while IFS= read -r dir; do
    job=$(basename "$dir")
    if [ "$job" = "$PROTECTED_JOB" ]; then log "prune: keeping protected job $job"; continue; fi
    if [ -n "${JOB_ID:-}" ] && [ "$job" = "$JOB_ID" ]; then log "prune: keeping current job $job"; continue; fi
    kept=$((kept + 1))
    if [ "$kept" -le "$PRUNE_KEEP_JOBS" ]; then continue; fi
    known=$(jq -r --arg j "$job" '((.completed // []) | map(.job) | index($j) != null) or ((.abandoned // []) | map(.job) | index($j) != null)' "$STATE" 2>/dev/null || echo false)
    if [ "$known" != "true" ]; then log "prune: keeping $job (not completed/abandoned in state.json)"; continue; fi
    mtime=$(stat -c %Y "$dir" 2>/dev/null || echo 0)
    if [ $((now_ts - mtime)) -lt $min_age_s ]; then log "prune: keeping $job (completed/abandoned but only $(( (now_ts - mtime) / 86400 ))d old, grace ${PRUNE_MIN_AGE_DAYS}d)"; continue; fi
    log "prune: deleting old job dir $dir (job $job, completed/abandoned, older than ${PRUNE_MIN_AGE_DAYS}d, outside the $PRUNE_KEEP_JOBS most recent)"
    rm -rf "$dir"
  done < <(ls -dt "$work"/job-* 2>/dev/null)
  find "${TMPDIR:-/tmp}" -maxdepth 1 -name "trendline-*.log" -mtime +$PRUNE_MIN_AGE_DAYS -delete 2>/dev/null || true
}
# preflight: returns 0 if the pipeline is ready to run a step, 1 if not.
# Checks the VideoGen DLL exists, the intro logo exists, disk space, and
# that no directory under the repo has a backslash in its name (MSB3552).
# A missing DLL is rebuilt once under a lock; a stale DLL (source newer
# than the DLL) only warns — the loop runs the last known-good build and
# the main agent rebuilds deliberately after code changes.
preflight() {
  local dll="$REPO/BroadcastContentAutomation/VideoGen/bin/Release/net8.0/VideoGen.dll"
  local logo="$REPO/BroadcastContentAutomation/VideoGen/assets/trendlinenow-logo.png"
  if [ ! -f "$dll" ]; then
    log "preflight: VideoGen.dll MISSING — attempting a locked rebuild"
    exec 8>"$TRENDLINE/build.lock"
    if flock -n 8; then
      BUILD_OK=0
      (cd "$REPO/BroadcastContentAutomation/VideoGen" && timeout 600 dotnet build -c Release >> "$LOG" 2>&1) || BUILD_OK=1
      exec 8>&-  # release the build lock now — the rebuild decision is made;
                 # holding it for the whole tick would block manual builds.
      if [ "$BUILD_OK" = "0" ]; then
        log "preflight: rebuild succeeded"
      else
        log "preflight: rebuild FAILED — escalating (not starting new work)"
        return 1
      fi
    else
      exec 8>&-
      log "preflight: a build is already running; skipping rebuild this tick"
      return 1
    fi
  fi
  if find "$REPO/BroadcastContentAutomation/VideoGen" -name '*.cs' -newer "$dll" 2>/dev/null | grep -q .; then
    log "preflight WARNING: VideoGen source is newer than the DLL — running the last known-good build; rebuild deliberately after code changes"
  fi
  if [ ! -f "$logo" ]; then
    log "preflight: intro logo MISSING ($logo) — --assemble would fail fast; escalating"
    return 1
  fi
  # Scoped to VideoGen/ (the .NET project MSB3552 actually breaks): the old
  # scan walked the whole repo, including work/ with hundreds of job dirs,
  # on every tick.
  if find "$REPO/BroadcastContentAutomation/VideoGen" -type d -name '*\\*' 2>/dev/null | grep -q .; then
    log "preflight: a directory with backslashes in its name exists under VideoGen/ — dotnet build would fail (MSB3552); escalating"
    return 1
  fi
  local disk_free
  disk_free=$(df --output=avail -BG ~/workspace | tail -1 | tr -dc '0-9')
  local disk_crit
  disk_crit=$(jq -r '.health.disk_critical_gb' "$CONFIG")
  if [ "$disk_free" -lt "$disk_crit" ]; then
    log "preflight: disk free ${disk_free}GB < critical ${disk_crit}GB — not starting new work"
    return 1
  fi
  return 0
}

# --- reset-failures subcommand (L38) ---------------------------------------
# Manual reset path: `driver.sh reset-failures --job <id> --reason <text>`
# logs who reset, which job, the old counts, and writes both fields
# atomically (failures + auto_resets). Previously a manual reset was a
# bare external `jq '.current.failures=0' state.json` with no log line
# at all — nothing said who reset, which job, the old count, or whether
# auto_resets was also cleared. Handled here, before the healthcheck and
# the tick dispatch, so it never runs a tick or launches work.
if [ "${1:-}" = "reset-failures" ]; then
  shift
  RESET_JOB=""
  RESET_REASON=""
  while [ $# -gt 0 ]; do
    case "$1" in
      --job)
        [ $# -ge 2 ] || { log "reset-failures: --job needs a value (usage: driver.sh reset-failures --job <id> --reason <text>)"; exit 1; }
        RESET_JOB="$2"; shift 2 ;;
      --reason)
        [ $# -ge 2 ] || { log "reset-failures: --reason needs a value (usage: driver.sh reset-failures --job <id> --reason <text>)"; exit 1; }
        RESET_REASON="$2"; shift 2 ;;
      *)
        log "reset-failures: unknown argument: $1 (usage: driver.sh reset-failures --job <id> --reason <text>)"
        exit 1 ;;
    esac
  done
  [ -n "$RESET_JOB" ] && [ -n "$RESET_REASON" ] || { log "reset-failures: --job and --reason are both required"; exit 1; }
  [ -f "$STATE" ] || { log "reset-failures: state.json missing ($STATE); refusing to act"; exit 1; }
  OLD_FAILURES=$(jq -r '.current.failures // 0' "$STATE" 2>/dev/null || echo 0)
  OLD_RESETS=$(jq -r '.current.auto_resets // 0' "$STATE" 2>/dev/null || echo 0)
  ACTOR="${USER:-$(whoami 2>/dev/null || echo unknown)}"
  JOB_ID="$RESET_JOB"
  PHASE=$(jq -r '.current.phase // "unknown"' "$STATE" 2>/dev/null || echo unknown)
  save_state ".current.failures = 0 | .current.auto_resets = 0 | .current.updated_at = \"$(date -Is)\"" || exit 1
  log "MANUAL RESET by $ACTOR: job $RESET_JOB failures $OLD_FAILURES→0 auto_resets $OLD_RESETS→0: $RESET_REASON"
  exit 0
fi

# --- pause / unpause subcommands (L76) ---------------------------------------
# Pausing used to be a bare external `jq '.paused=true' state.json` with
# no log line and no reason update — so state.json carried a stale
# pause_reason ("Unpaused 2026-11-25 ...") while paused=true, and an
# unpause was invisible entirely. Handled here, before the healthcheck
# and the tick dispatch, so it never runs a tick or launches work.
# pause_reason always describes the current transition: while paused it
# holds the pause reason; after an unpause it holds the "Unpaused <ts> —
# <reason>" stamp, matching the existing convention.
if [ "${1:-}" = "pause" ]; then
  shift
  PAUSE_REASON=""
  while [ $# -gt 0 ]; do
    case "$1" in
      --reason)
        [ $# -ge 2 ] || { log "pause: --reason needs a value (usage: driver.sh pause --reason <text>)"; exit 1; }
        PAUSE_REASON="$2"; shift 2 ;;
      *)
        log "pause: unknown argument: $1 (usage: driver.sh pause --reason <text>)"
        exit 1 ;;
    esac
  done
  [ -n "$PAUSE_REASON" ] || { log "pause: --reason is required"; exit 1; }
  [ -f "$STATE" ] || { log "pause: state.json missing ($STATE); refusing to act"; exit 1; }
  JOB_ID=$(jq -r '.current.job // empty' "$STATE" 2>/dev/null || echo "")
  PHASE=$(jq -r '.current.phase // "unknown"' "$STATE" 2>/dev/null || echo unknown)
  [ -n "$JOB_ID" ] || JOB_ID="none"
  ESCAPED_REASON=$(jq -n --arg r "$PAUSE_REASON" '$r')
  save_state ".paused = true | .pause_reason = $ESCAPED_REASON | .current.updated_at = \"$(date -Is)\"" || exit 1
  log "driver paused: job $JOB_ID phase $PHASE, reason: $PAUSE_REASON"
  exit 0
fi

if [ "${1:-}" = "unpause" ]; then
  shift
  UNPAUSE_REASON=""
  while [ $# -gt 0 ]; do
    case "$1" in
      --reason)
        [ $# -ge 2 ] || { log "unpause: --reason needs a value (usage: driver.sh unpause --reason <text>)"; exit 1; }
        UNPAUSE_REASON="$2"; shift 2 ;;
      *)
        log "unpause: unknown argument: $1 (usage: driver.sh unpause --reason <text>)"
        exit 1 ;;
    esac
  done
  [ -n "$UNPAUSE_REASON" ] || { log "unpause: --reason is required"; exit 1; }
  [ -f "$STATE" ] || { log "unpause: state.json missing ($STATE); refusing to act"; exit 1; }
  ESCAPED_REASON=$(jq -n --arg r "Unpaused $(date '+%Y-%m-%d %H:%M %Z') — $UNPAUSE_REASON" '$r')
  save_state ".paused = false | .pause_reason = $ESCAPED_REASON | .current.updated_at = \"$(date -Is)\"" || exit 1
  log "driver unpaused: $UNPAUSE_REASON"
  exit 0
fi

# --- health check (read-only; never acts) ------------------------------------
# L6: the healthcheck's exit 1 (critical) used to be masked by `|| true`,
# so a critical system looked like a healthy tick. Record it in the log.
"$TRENDLINE/healthcheck.sh" >> "$LOG" 2>&1
HC_RC=$?
[ $HC_RC -ne 0 ] && log "healthcheck critical (rc $HC_RC); see health.json"

# --- load state ---------------------------------------------------------------
if [ ! -f "$STATE" ]; then
  log "CRITICAL: state.json missing; refusing to act (create it first)"
  exit 1
fi
PAUSED=$(jq -r '.paused // false' "$STATE")
PAUSE_REASON=$(jq -r '.pause_reason // ""' "$STATE")
if [ "$PAUSED" = "true" ]; then
  # L6: exit 0 is deliberate — paused is a normal state, not a failure.
  log "PAUSED: $PAUSE_REASON"
  exit 0
fi
PHASE=$(jq -r '.current.phase // "idle"' "$STATE")
JOB_ID=$(jq -r '.current.job // empty' "$STATE")
PID=$(jq -r '.current.pid // empty' "$STATE")
STEP_LOG=$(jq -r '.current.log // empty' "$STATE")
DRIVER_OWNED=$(jq -r '.current.driver_owned // false' "$STATE")
FAILURES=$(jq -r '.current.failures // 0' "$STATE")
PROTECTED_JOB=$(jq -r '.protected_job // "job-20261121-085638"' "$CONFIG")
MAX_FAILURES=$(jq -r '.health.max_consecutive_failures_before_escalation' "$CONFIG")
BACKOFF_MIN=$(jq -r '.tts.throttle_backoff_minutes' "$CONFIG")
STALL_MIN=$(jq -r '.render.chunk_log_silence_kill_minutes' "$CONFIG")
# Per-phase retry caps (config .render.* — previously declared but never
# read, so a crashing assemble / chunk render retried forever). Enforced
# via .current.assemble_retries / .current.chunk_retries (job-scoped,
# reset on job start/completion).
MAX_ASSEMBLE_RETRIES=$(jq -r '.render.max_assemble_retries // 2' "$CONFIG")
MAX_CHUNK_RETRIES=$(jq -r '.render.max_chunk_retries // 3' "$CONFIG")
# Failure-limit auto-reset cap (Rishi 2026-11-23): at most this many
# consecutive auto-resets with NO progress before the driver escalates and
# stops. A shell constant, not config — the failure limit itself lives in
# config (.health.max_consecutive_failures_before_escalation); the cap is
# part of the reset policy.
MAX_AUTO_RESETS=2

# Laptop render mode (2026-11-23): Rishi's Windows laptop (Hawkeye) is the
# ONLY renderer. enabled=false until the first live link test passes.
# vm_render_fallback_enabled=false keeps the VM render path OFF even while
# laptop mode is off — the driver must never silently render on the VM
# while the link is being set up. The helper reads the same config block
# for host/user/root/identity (env overrides win).
LAPTOP_ENABLED=$(jq -r '.laptop.enabled // false' "$CONFIG")
LAPTOP_VM_FALLBACK=$(jq -r '.laptop.vm_render_fallback_enabled // false' "$CONFIG")
LAPTOP_HELPER="$TRENDLINE/laptop-render.sh"
laptop_mode() { [ "$LAPTOP_ENABLED" = "true" ]; }
vm_render_allowed() { [ "$LAPTOP_VM_FALLBACK" = "true" ]; }

# The protected job (video #1) is owned by the build subagent. Never touch it.
if [ -n "$JOB_ID" ] && [ "$JOB_ID" = "$PROTECTED_JOB" ]; then
  log "GUARD: current job is the protected video #1 ($PROTECTED_JOB) — refusing to act; resetting to idle"
  save_state '.current = {job: null, profile: null, phase: "idle", pid: null, pgid: null, log: null, driver_owned: false, failures: 0, assemble_retries: 0, chunk_retries: 0, updated_at: "'$(date -Is)'"}'
  exit 0
fi

# A brand-new prep has no job id yet (the job id is only known once --prep
# prints its Workspace line). Extract it from the log and record it.
if [ -z "$JOB_ID" ] && [ "$PHASE" = "prep" ] && [ -n "$STEP_LOG" ] && [ -f "$STEP_LOG" ]; then
  EXTRACTED_JOB=$(extract_job_from_log "$STEP_LOG")
  if [ -n "$EXTRACTED_JOB" ]; then
    log "prep job id discovered from log: $EXTRACTED_JOB"
    JOB_ID="$EXTRACTED_JOB"
    save_state ".current.job = \"$EXTRACTED_JOB\" | .current.profile = \"trendlinenow\""
    # Usage governor: count this video against the daily cap (idempotent).
    # L40: the JSON result used to be discarded and stderr never reached
    # the driver log, so a successful recording (and the prep-counting
    # behaviour behind it) was invisible. Capture and log the result line.
    GOV_STARTED_OUT=$("$TRENDLINE/governor-check.sh" video-started --job "$EXTRACTED_JOB" 2>&1)
    GOV_STARTED_RC=$?
    # Collapse to one log line (stdout JSON + stderr log line, if both).
    GOV_STARTED_ONE_LINE=$(printf '%s' "$GOV_STARTED_OUT" | tr '\n' ' ' | sed 's/  */ /g')
    if [ "$GOV_STARTED_RC" -ne 0 ]; then
      log "governor: video-started recording failed for $EXTRACTED_JOB (rc $GOV_STARTED_RC, output: $GOV_STARTED_ONE_LINE) (continuing)"
    else
      log "governor: video-started result for $EXTRACTED_JOB: $GOV_STARTED_ONE_LINE"
    fi
  fi
fi

# Failure limit (Rishi 2026-11-23 ~14:40 EST): hitting the limit no longer
# parks the job for a human decision — the driver resets the whole system
# and restarts the job from its checkpoint automatically. Guard: at most
# MAX_AUTO_RESETS consecutive auto-resets with NO progress (the progress
# marker below is unchanged since the last auto-reset); the next
# no-progress hit escalates and stops, as before, so a job that can never
# advance does not loop forever. The reset only clears counters and kills
# orphans — the actual restart is the normal phase dispatch below, which
# relaunches the current step from its checkpoint (chunk renders skip
# valid chunks; prep resumes via --resume / --prep --job from the saved
# partial). This block deliberately does NOT exit on the reset path: it
# falls through to the phase dispatch.
if [ "$FAILURES" -ge "$MAX_FAILURES" ]; then
  # A live step is still doing work — never kill it. Wait for it to finish
  # or die; the reset (if still needed) happens on a later tick.
  if [ -n "$PID" ] && [ "$PID" != "null" ] && pid_alive "$PID"; then
    log "at failure limit ($FAILURES >= $MAX_FAILURES) but a step is still running (pid $PID) — waiting for it; auto-reset will run on a later tick if it dies"
    exit 0
  fi
  RESET_JOB_DIR=""
  [ -n "$JOB_ID" ] && RESET_JOB_DIR=$(job_dir "$JOB_ID")
  MARKER=$(progress_marker "$RESET_JOB_DIR")
  PREV_MARKER=$(jq -r '.current.auto_reset_progress // empty' "$STATE")
  AUTO_RESETS=$(jq -r '.current.auto_resets // 0' "$STATE")
  if [ -n "$PREV_MARKER" ] && [ "$MARKER" = "$PREV_MARKER" ]; then
    AUTO_RESETS=$((AUTO_RESETS + 1))
  else
    # Progress since the last auto-reset (or the first one): fresh streak.
    AUTO_RESETS=1
  fi
  if [ "$AUTO_RESETS" -gt "$MAX_AUTO_RESETS" ]; then
    log "ESCALATED: job $JOB_ID hit the failure limit ($FAILURES >= $MAX_FAILURES) after $AUTO_RESETS auto-resets with no progress (marker unchanged: $MARKER) — driver is stopping for this job; Rishi to decide"
    # L4: if the escalation record cannot be written, abort the tick
    # instead of falling through with stale counters.
    save_state ".current.auto_resets = $AUTO_RESETS | .current.auto_reset_progress = \"$MARKER\"" || exit 1
    # L6: an escalated stop is not a healthy tick — exit non-zero so a
    # scheduler/monitor sees the driver stopped for this job.
    exit 1
  fi
  # Full reset: kill orphaned pipeline processes for this job, record the
  # memory picture, clear all failure/retry counters, clear the stale
  # step record (pid/pgid — the recorded step is dead, and clearing it
  # stops the next tick double-counting the same death as a fresh retry),
  # and store the reset count + progress marker. The last step log is
  # KEPT: the prep phase reads it for the Azure 429 throttle check.
  # L37: capture what is about to be cleared BEFORE the reset writes it
  # away — the old line said only "cleared failure counters, killed
  # orphans" with no phase, no last log tail, no cleared values, and no
  # orphan-kill outcome.
  RESET_FAILURES="$FAILURES"
  RESET_CHUNK_RETRIES=$(jq -r '.current.chunk_retries // 0' "$STATE" 2>/dev/null || echo 0)
  RESET_ASSEMBLE_RETRIES=$(jq -r '.current.assemble_retries // 0' "$STATE" 2>/dev/null || echo 0)
  RESET_STALE_PID="${PID:-none}"
  [ -n "$RESET_STALE_PID" ] && [ "$RESET_STALE_PID" != "null" ] || RESET_STALE_PID="none"
  if [ -n "$STEP_LOG" ] && [ -f "$STEP_LOG" ]; then
    RESET_LOG_TAIL=$(tail -n 2 "$STEP_LOG" 2>/dev/null | awk '{printf "%s%s", (NR>1?" | ":""), $0} END{print ""}')
    [ -n "$RESET_LOG_TAIL" ] || RESET_LOG_TAIL="step log empty"
  else
    RESET_LOG_TAIL="no step log"
  fi
  kill_job_orphans "$JOB_ID"
  log_memory_context "auto-reset $JOB_ID"
  # L4: if the reset itself cannot be written, abort the tick — falling
  # through would run the phase dispatch with the failure counters still
  # at the limit.
  save_state ".current.failures = 0 | .current.chunk_retries = 0 | .current.assemble_retries = 0 | .current.auto_resets = $AUTO_RESETS | .current.auto_reset_progress = \"$MARKER\" | .current.pid = null | .current.pgid = null" || exit 1
  log "AUTO-RESET #$AUTO_RESETS: job $JOB_ID hit the failure limit ($FAILURES >= $MAX_FAILURES) — failures were in phase $PHASE (last step log tail: $RESET_LOG_TAIL); cleared failures=$RESET_FAILURES chunk_retries=$RESET_CHUNK_RETRIES assemble_retries=$RESET_ASSEMBLE_RETRIES stale pid=$RESET_STALE_PID; killed $ORPHAN_KILLED_COUNT orphan processes ($ORPHAN_KILLED_PIDS); restarting from checkpoint (progress marker: $MARKER)"
  # fall through to the phase dispatch below (no exit here)
fi

# Resolve the current job dir (if any).
JOB_DIR=""
[ -n "$JOB_ID" ] && JOB_DIR=$(job_dir "$JOB_ID")

# Auto-reset streak self-heal: if the job has progressed since the last
# auto-reset (marker changed), the no-progress streak is over — clear it
# so a later failure episode gets its full MAX_AUTO_RESETS. This also
# heals a stale streak left by a manual failures reset (which should
# still clear .current.auto_resets itself, but the driver no longer
# depends on that). Runs only while failures are below the limit and a
# streak exists, so a normal tick costs nothing.
if [ "$FAILURES" -lt "$MAX_FAILURES" ] && [ -n "$JOB_DIR" ] && [ -d "$JOB_DIR" ]; then
  STORED_RESETS=$(jq -r '.current.auto_resets // 0' "$STATE")
  if [ "$STORED_RESETS" -gt 0 ]; then
    STORED_MARKER=$(jq -r '.current.auto_reset_progress // empty' "$STATE")
    NOW_MARKER=$(progress_marker "$JOB_DIR")
    if [ -n "$STORED_MARKER" ] && [ "$NOW_MARKER" != "$STORED_MARKER" ]; then
      save_state ".current.auto_resets = 0 | .current.auto_reset_progress = \"$NOW_MARKER\""
      log "auto-reset streak cleared: job $JOB_ID progressed since the last auto-reset (marker changed)"
    fi
  fi
fi

# --- phase: idle --------------------------------------------------------------
if [ "$PHASE" = "idle" ]; then
  # Retention (B26): prune old finished job dirs once per idle tick.
  prune_old_jobs
  # Backpressure (B8): never start new work while a completed video waits
  # for review — the approval gate is Rishi, and .completed would otherwise
  # grow unboundedly while unapproved videos pile up. A video leaves the
  # watch dir only once it is approved and published, so the unapproved
  # count is the backlog. One waiting video is enough to hold the line.
  UNAPPROVED=$(unapproved_completed_count)
  if [ "$UNAPPROVED" -ge 1 ]; then
    log "backpressure: $UNAPPROVED completed video(s) awaiting review (sidecar Approved:false) — not starting new work until one is approved/published"
    exit 0
  fi
  # Prefer a pre-prepped job from the queue (FIFO).
  QUEUED=$(jq -r '.queue[0] // empty' "$STATE")
  if [ -n "$QUEUED" ]; then
    Q_JOB=$(jq -r '.queue[0].job' "$STATE")
    Q_DIR=$(job_dir "$Q_JOB")
    if [ "$Q_JOB" = "$PROTECTED_JOB" ]; then
      log "idle: queued job is the protected video #1 — dropping it from the queue (owned by the build subagent)"
      save_state "del(.queue[0])"
      exit 0
    fi
    if prep_complete "$Q_DIR"; then
      log "idle: picking pre-prepped queued job $Q_JOB"
      # A forced next_topic belongs to the video it was set for, not to
      # this queued job (whose topic is already baked in) — clear it, or
      # it would also force the NEXT video after this one.
      save_state "del(.queue[0]) | .next_topic = null | .current = {job: \"$Q_JOB\", profile: \"trendlinenow\", phase: \"prep_done\", pid: null, pgid: null, log: null, driver_owned: true, failures: 0, assemble_retries: 0, chunk_retries: 0, started_at: \"$(date -Is)\", updated_at: \"$(date -Is)\"}"
      exit 0
    else
      log "idle: queued job $Q_JOB is NOT fully prepped (missing audio/art) — will resume its prep"
      save_state "del(.queue[0]) | .next_topic = null | .current = {job: \"$Q_JOB\", profile: \"trendlinenow\", phase: \"prep\", pid: null, pgid: null, log: null, driver_owned: true, failures: 0, assemble_retries: 0, chunk_retries: 0, started_at: \"$(date -Is)\", updated_at: \"$(date -Is)\"}"
      exit 0
    fi
  fi
  # No queued job: start a new prep. Preflight first.
  if ! preflight; then
    log "idle: preflight failed — not starting a new prep this tick"
    exit 0
  fi
  # Usage governor (2026-11-23): daily video cap (1 long-form/day, governor-config.json; fix #5) + cooled providers.
  # Refuses a new prep when the cap is hit or all legs are cooled — not a
  # failure, the driver just waits for the next window. Fail-open: a broken
  # governor exits 0 and never halts the channel.
  # L39: the denial used to be logged generically ("daily cap hit or all
  # legs cooled") — the governor's precise reason, count, cap and reset
  # time never reached the driver log. Capture may-start's stdout JSON
  # (+ its stderr reason line) and log them.
  GOV_OUT=$("$TRENDLINE/governor-check.sh" may-start 2>&1)
  GOV_RC=$?
  if [ "$GOV_RC" -ne 0 ]; then
    GOV_JSON=$(printf '%s\n' "$GOV_OUT" | grep -m1 '^{' || true)
    GOV_REASON=$(printf '%s' "$GOV_JSON" | jq -r '.reason // empty' 2>/dev/null || true)
    GOV_VIDEOS=$(printf '%s' "$GOV_JSON" | jq -r '.videos_today // empty' 2>/dev/null || true)
    GOV_CAP=$(printf '%s' "$GOV_JSON" | jq -r '.cap // empty' 2>/dev/null || true)
    GOV_RESETS=$(printf '%s' "$GOV_JSON" | jq -r '.resets_at // empty' 2>/dev/null || true)
    [ -n "$GOV_REASON" ] || GOV_REASON=$(printf '%s\n' "$GOV_OUT" | grep -m1 'governor-check: ' | sed 's/.*governor-check: //' || true)
    [ -n "$GOV_REASON" ] || GOV_REASON="daily cap hit or all legs cooled"
    if [ -n "$GOV_VIDEOS" ] && [ -n "$GOV_CAP" ]; then GOV_COUNT="$GOV_VIDEOS/$GOV_CAP"; else GOV_COUNT="unknown"; fi
    [ -n "$GOV_RESETS" ] || GOV_RESETS="unknown"
    log "governor: denied — $GOV_REASON (videos $GOV_COUNT, resets $GOV_RESETS); not starting a new prep, will retry next tick"
    exit 0
  fi
  # Forced next topic (Rishi 2026-11-21): state.json .next_topic, when set,
  # is passed as --topic to this prep. It is NOT cleared here - it is cleared
  # only when the prep completes (prep_done), so a died prep keeps the forced
  # topic across retries instead of the relaunch auto-picking the top story
  # (seen 2026-11-22: GTA 6 retries generated Trump/FBI-hack duplicates).
  NEXT_TOPIC=$(jq -r '.next_topic // empty' "$STATE" 2>/dev/null || true)
  if [ -n "$NEXT_TOPIC" ]; then
    log "idle: starting a new prep --prep --long --profile trendlinenow --topic \"$NEXT_TOPIC\" (forced next topic)"
    PREP_ARGS="--prep --long --profile trendlinenow --topic \"$NEXT_TOPIC\""
  else
    log "idle: starting a new prep --prep --long --profile trendlinenow (trending topic auto-picked)"
    PREP_ARGS="--prep --long --profile trendlinenow"
  fi
  NEW_LOG="$TMPDIR/trendline-prep-driver-$(date +%Y%m%d-%H%M%S).log"
  launch "cd '$REPO/BroadcastContentAutomation/VideoGen' && dotnet bin/Release/net8.0/VideoGen.dll $PREP_ARGS >> '$NEW_LOG' 2>&1"
  NEWPID=$LAUNCH_PID
  save_state ".current = {job: null, profile: \"trendlinenow\", phase: \"prep\", pid: $NEWPID, pgid: $LAUNCH_PGID, log: \"$NEW_LOG\", driver_owned: true, failures: 0, assemble_retries: 0, chunk_retries: 0, started_at: \"$(date -Is)\", updated_at: \"$(date -Is)\"}"
  log "prep launched: pid $NEWPID log $NEW_LOG (job id unknown until --prep prints it; the next tick reads it from the log)"
  exit 0
fi

# From here a current job must exist — except a prep whose job id is not
# yet known (it is still running and has not printed its Workspace line).
if [ -z "$JOB_ID" ] || [ ! -d "$JOB_DIR" ]; then
  if [ "$PHASE" = "prep" ]; then
    if pid_alive "$PID"; then
      log "prep still running (pid $PID); job id not yet known — waiting"
      exit 0
    fi
    log "prep died before creating a job dir — relaunching a fresh --prep; exit code $(step_exit_code "$STEP_LOG") (captured from the step's exit, not the driver's); last log lines: $(step_log_tail "$STEP_LOG" 3)"
    # A forced next_topic must survive the relaunch (same as the prep-phase
    # relaunch below), or it auto-picks the top story and duplicates the
    # previous video (seen 2026-11-22).
    RELAUNCH_TOPIC=$(jq -r '.next_topic // empty' "$STATE" 2>/dev/null || true)
    RELAUNCH_TOPIC_ARG=""
    [ -n "$RELAUNCH_TOPIC" ] && RELAUNCH_TOPIC_ARG="--topic \"$RELAUNCH_TOPIC\""
    # Recovery after a death: count it toward the escalation gate.
    bump_failure "prep died before creating a job dir"
    NEW_LOG="$TMPDIR/trendline-prep-driver-$(date +%Y%m%d-%H%M%S).log"
    launch "cd '$REPO/BroadcastContentAutomation/VideoGen' && dotnet bin/Release/net8.0/VideoGen.dll --prep --long --profile trendlinenow $RELAUNCH_TOPIC_ARG >> '$NEW_LOG' 2>&1"
    NEWPID=$LAUNCH_PID
    save_state ".current.pid = $NEWPID | .current.pgid = $LAUNCH_PGID | .current.log = \"$NEW_LOG\" | .current.updated_at = \"$(date -Is)\""
    log "prep relaunched: pid $NEWPID log $NEW_LOG"
    exit 0
  fi
  # No job dir for a non-prep phase: the job is gone (deleted/moved), not
  # merely unfinished — record it as abandoned instead of silently
  # discarding its history.
  abandon_current "phase=$PHASE but no current job dir ($JOB_ID / $JOB_DIR)"
  exit 0
fi

# --- phase: prep --------------------------------------------------------------
if [ "$PHASE" = "prep" ]; then
  if pid_alive "$PID"; then
    # Stall check: a prep whose log has been silent for the threshold is
    # hung (a Groq call, TTS, or art step that never returns) — kill the
    # whole process group; the next tick resumes from the checkpoint and
    # counts the failure when it relaunches.
    if [ "$DRIVER_OWNED" = "true" ] && step_stalled "$STALL_MIN"; then
      log "prep pid $PID log silent $(step_log_age_min)m (>= ${STALL_MIN}m) — killing the whole process group"
      kill_step_group "$PID"
      exit 0
    fi
    log "prep still running (pid $PID, $JOB_ID)"
    exit 0
  fi
  # The prep process is dead (or never had a pid). Is the prep actually
  # complete (script + audio + art)? Check first, so a finished prep is
  # not delayed by a stale 429 marker in its log.
  if [ -f "$JOB_DIR/script.json" ] && prep_complete "$JOB_DIR"; then
    log "prep complete for $JOB_ID (script + audio + art all present)"
    set_phase "prep_done" "null" "null"
    # Forced topic consumed only now that the prep is complete, so a died
    # prep keeps it across retries (see the idle-branch note above).
    save_state ".next_topic = null"
    exit 0
  fi
  # Incomplete prep. Was the death a 429 throttle? Check before ANY resume
  # — including the --resume path (script.json exists but audio/art are
  # incomplete, e.g. a 429 during TTS). Previously the backoff only ran
  # when script.json did not exist, so a TTS 429 went straight to --resume
  # with no backoff and hammered the F0 tier.
  if log_has_throttle "$STEP_LOG"; then
    LAST_429=$(jq -r '.current.last_429_at // empty' "$STATE")
    if [ -z "$LAST_429" ] || [ "$LAST_429" = "null" ]; then
      # First sighting of this throttle: record it so the backoff is
      # measured from now (previously last_429_at was never set anywhere,
      # so the backoff was dead code).
      save_state ".current.last_429_at = \"$(date -Is)\""
      LAST_429=$(jq -r '.current.last_429_at // empty' "$STATE")
    fi
    BACKOFF_END=$(date -d "$LAST_429 + $BACKOFF_MIN minutes" +%s 2>/dev/null || echo 0)
    NOW_TS=$(date +%s)
    if [ "$NOW_TS" -lt "$BACKOFF_END" ]; then
      log "Azure 429 backoff: $(( (BACKOFF_END - NOW_TS) / 60 ))m remaining; not resuming yet"
      exit 0
    fi
    log "Azure 429 backoff expired — resuming prep"
    save_state ".current.last_429_at = null"
  fi
  # Floor failure (B17/A5, ITEM 4): a script that failed the length/scene
  # floor was generated from a partial whose saved acts are the thin ones
  # that just failed — resuming it would fail the same floor again.
  # VideoGen deletes the partial before dying, but if it is still here
  # (killed mid-cleanup, old binary), drop it so the next attempt starts
  # a fresh prep instead of resuming the failure.
  if [ -f "$JOB_DIR/script-partial.json" ] && log_tail_has "$STEP_LOG" "Long-form script is far too short"; then
    rm -f "$JOB_DIR/script-partial.json" "$JOB_DIR/script-progress.json"
    log "floor failure in prep log (Long-form script is far too short) — deleted script-partial.json so the next attempt starts fresh"
  fi
  # Decide the recovery flavour (log only).
  if [ -f "$JOB_DIR/script.json" ]; then
    log "prep died with script.json present but audio/art incomplete — resuming"
  elif [ -f "$JOB_DIR/script-partial.json" ]; then
    log "prep died during script generation (partial checkpoint exists, no script.json) — resuming from the partial (saved scenes reused verbatim)"
  else
    log "prep died before any script checkpoint — relaunching a fresh --prep"
  fi
  # B25: a manual --prep/--resume/--assemble for this job is invisible to
  # the recorded pid — never launch a second step on top of it.
  if pipeline_step_running "$JOB_ID"; then
    log "a pipeline step is already running for $JOB_ID (not the recorded pid — possibly manual) — waiting"
    exit 0
  fi
  # Resume (or relaunch) the prep. This is a recovery after a death, so it
  # counts toward the escalation gate (previously prep deaths never did,
  # so the gate could never trip from the most failure-prone phase).
  bump_failure "prep died — resuming/relaunching prep"
  RESUME_LOG="$TMPDIR/trendline-prep-driver-$(date +%Y%m%d-%H%M%S).log"
  # A fresh relaunch (no checkpoint) must re-apply a forced next_topic, or it
  # auto-picks the top story and duplicates the previous video (seen 2026-11-22).
  RELAUNCH_TOPIC=$(jq -r '.next_topic // empty' "$STATE" 2>/dev/null || true)
  RELAUNCH_TOPIC_ARG=""
  [ -n "$RELAUNCH_TOPIC" ] && RELAUNCH_TOPIC_ARG="--topic \"$RELAUNCH_TOPIC\""
  if [ -f "$JOB_DIR/script.json" ]; then
    launch "cd '$REPO/BroadcastContentAutomation/VideoGen' && dotnet bin/Release/net8.0/VideoGen.dll --resume --job $JOB_ID >> '$RESUME_LOG' 2>&1"
  elif [ -f "$JOB_DIR/script-partial.json" ]; then
    launch "cd '$REPO/BroadcastContentAutomation/VideoGen' && dotnet bin/Release/net8.0/VideoGen.dll --prep --long --profile trendlinenow --job $JOB_ID >> '$RESUME_LOG' 2>&1"
  else
    launch "cd '$REPO/BroadcastContentAutomation/VideoGen' && dotnet bin/Release/net8.0/VideoGen.dll --prep --long --profile trendlinenow $RELAUNCH_TOPIC_ARG >> '$RESUME_LOG' 2>&1"
    # Fresh prep creates a new job dir: clear .current.job so the next tick
    # discovers the new job id from the log instead of reusing the stale one.
    save_state ".current.job = null"
  fi
  NEWPID=$LAUNCH_PID
  save_state ".current.pid = $NEWPID | .current.pgid = $LAUNCH_PGID | .current.log = \"$RESUME_LOG\" | .current.updated_at = \"$(date -Is)\""
  log "prep resumed/relaunched: pid $NEWPID log $RESUME_LOG"
  exit 0
fi

# --- phase: prep_done ---------------------------------------------------------
if [ "$PHASE" = "prep_done" ]; then
  # Is a render already running for this job (e.g. a previous --assemble)?
  if pid_alive "$PID"; then
    log "render already running for $JOB_ID (pid $PID); waiting"
    exit 0
  fi
  # B25: a manual --assemble for this job is invisible to the recorded pid
  # — never launch a second one on top of it.
  if pipeline_step_running "$JOB_ID"; then
    log "a pipeline step is already running for $JOB_ID (not the recorded pid — possibly manual) — waiting"
    exit 0
  fi
  if ! preflight; then
    log "prep_done: preflight failed — not launching --assemble this tick"
    exit 0
  fi
  if laptop_mode; then
    # Laptop mode: write both props files (light, inline — the outro comes
    # from the disk cache, no render, no heavy compute) and move to
    # render_chunks, where the helper ships/triggers/polls/pulls.
    # --assemble is never launched here.
    WRITE_PROPS_LOG="$TMPDIR/trendline-write-props-driver-$(date +%Y%m%d-%H%M%S).log"
    if [ "${TRENDLINE_DRY_RUN:-0}" = "1" ]; then
      log "DRY-RUN: would run --write-props for $JOB_ID (log $WRITE_PROPS_LOG)"
      set_phase "render_chunks" "null" "null"
      exit 0
    fi
    log "prep_done: laptop mode — writing props with --write-props (no VM render)"
    if (cd "$REPO/BroadcastContentAutomation/VideoGen" && dotnet bin/Release/net8.0/VideoGen.dll --write-props --job "$JOB_ID" >> "$WRITE_PROPS_LOG" 2>&1); then
      log "prep_done: props written — moving to render_chunks (laptop)"
      set_phase "render_chunks" "null" "null"
    else
      log "prep_done: --write-props FAILED (log $WRITE_PROPS_LOG) — counting a failure"
      bump_failure "prep_done --write-props failed"
    fi
    exit 0
  fi
  if ! memory_gate_ok; then
    log "prep_done: memory gate blocked --assemble this tick — waiting, not a failure"
    exit 0
  fi
  if ! vm_render_allowed; then
    # VM render is OFF and laptop mode is not on: the link is still being
    # set up. Wait here (not a failure) instead of silently rendering on
    # the VM.
    log "prep_done: VM render is OFF (laptop mode not enabled and vm_render_fallback_enabled=false) — waiting for the laptop link; not a failure"
    exit 0
  fi
  # B6: memory picture before the render, so a silent death afterwards has
  # a "before" to compare against.
  log_memory_context "before-assemble $JOB_ID"
  # Launch --assemble (vertical first, then landscape). The landscape will
  # almost certainly exceed the 180-min Remotion timeout or be killed by a
  # VM restart; the driver recovers it via chunk rendering (see
  # render_assemble / render_chunks below). The vertical is expected to
  # complete inside --assemble.
  ASSEMBLE_LOG="$TMPDIR/trendline-assemble-driver-$(date +%Y%m%d-%H%M%S).log"
  launch "cd '$REPO/BroadcastContentAutomation/VideoGen' && dotnet bin/Release/net8.0/VideoGen.dll --assemble --job $JOB_ID >> '$ASSEMBLE_LOG' 2>&1"
  NEWPID=$LAUNCH_PID
  set_phase "render_assemble" "$NEWPID" "\"$ASSEMBLE_LOG\""
  save_state ".current.driver_owned = true"
  log "assemble launched: pid $NEWPID log $ASSEMBLE_LOG (vertical first, then landscape)"
  exit 0
fi

# --- phase: render_assemble ---------------------------------------------------
if [ "$PHASE" = "render_assemble" ]; then
  if laptop_mode; then
    # Laptop mode never launches --assemble; a job found here was left by
    # the VM path before the switch. Move to chunk recovery (laptop).
    log "render_assemble: laptop mode — no VM assemble runs; moving to render_chunks (laptop)"
    set_phase "render_chunks" "null" "null"
    exit 0
  fi
  V_PROPS="$JOB_DIR/remotion-props-1080x1920.json"
  L_PROPS="$JOB_DIR/remotion-props-1920x1080.json"
  if pid_alive "$PID"; then
    # Still running. Only kill it if the log is silent for the stall
    # threshold AND we own it — a silent --assemble is stuck (Remotion
    # prints frame progress continuously while healthy).
    if [ "$DRIVER_OWNED" = "true" ] && [ -f "$STEP_LOG" ]; then
      LOG_AGE_MIN=$(step_log_age_min)
      if [ "$LOG_AGE_MIN" -ge "$STALL_MIN" ]; then
        log "assemble pid $PID log silent ${LOG_AGE_MIN}m (>= ${STALL_MIN}m) — killing the whole process group and chunk-recovering"
        kill_step_group "$PID"
        bump_failure "assemble log silent, killed process group"
        set_phase "render_chunks" "null" "null"
        exit 0
      fi
    fi
    log "assemble still running (pid $PID, $JOB_ID)"
    exit 0
  fi
  # The --assemble process is dead. What did it leave behind?
  # B6: make the death diagnosable even when the process left no FATAL or
  # SIGTERM line (OOM/SIGKILL/VM teardown cannot be caught in-process):
  # last Remotion frame, the memory picture now, and a FATAL line naming
  # the phase, pid, and exit evidence — the next silent death must leave
  # this line behind.
  log_memory_context "assemble-dead $JOB_ID"
  LAST_FRAME=$(last_render_frame "$STEP_LOG")
  log "FATAL: --assemble for $JOB_ID (pid $PID) died; last render frame $LAST_FRAME; log $STEP_LOG; exit code $(step_exit_code "$STEP_LOG") (captured from the step's exit, not the driver's); last log lines: $(step_log_tail "$STEP_LOG" 3)"
  if [ -f "$L_PROPS" ]; then
    log "assemble died but landscape props exist — switching to chunk recovery for both orientations"
    set_phase "render_chunks" "null" "null"
    exit 0
  fi
  if [ -f "$V_PROPS" ]; then
    log "assemble died after writing vertical props — switching to chunk recovery"
    set_phase "render_chunks" "null" "null"
    exit 0
  fi
  # Died before writing any props: likely a crash at startup. Retry, but
  # honour render.max_assemble_retries (config) — a repeatedly crashing
  # --assemble must escalate instead of looping forever.
  ASSEMBLE_RETRIES=$(jq -r '.current.assemble_retries // 0' "$STATE")
  ASSEMBLE_RETRIES=$((ASSEMBLE_RETRIES + 1))
  save_state ".current.assemble_retries = $ASSEMBLE_RETRIES"
  if [ "$ASSEMBLE_RETRIES" -gt "$MAX_ASSEMBLE_RETRIES" ]; then
    log "assemble died $ASSEMBLE_RETRIES times (max $MAX_ASSEMBLE_RETRIES retries) — escalating; Rishi to decide"
    save_state ".current.failures = $MAX_FAILURES | .current.updated_at = \"$(date -Is)\""
    exit 0
  fi
  # Check the log tail for a fatal marker.
  if log_tail_has "$STEP_LOG" "FATAL\|Unhandled exception\|OutOfMemoryException"; then
    log "assemble died before writing props (fatal marker in log) — retrying --assemble (attempt $ASSEMBLE_RETRIES of $MAX_ASSEMBLE_RETRIES)"
    bump_failure "assemble died before writing props (fatal marker in log)"
    set_phase "prep_done" "null" "null"
    exit 0
  fi
  log "assemble died before writing props (no fatal marker; possibly a VM reboot or the 180-min timeout) — retrying --assemble (attempt $ASSEMBLE_RETRIES of $MAX_ASSEMBLE_RETRIES)"
  bump_failure "assemble died before writing props (no fatal marker)"
  set_phase "prep_done" "null" "null"
  exit 0
fi

# --- phase: render_chunks ------------------------------------------------------
# Ensure every chunk exists and is valid. A chunk render is only launched
# for an orientation whose final is NOT already done and whose chunks are
# NOT already valid. When nothing needs a chunk render, move to verify.
if [ "$PHASE" = "render_chunks" ]; then
  if laptop_mode; then
    # Laptop mode: one helper `step` per orientation per tick. The helper
    # ships the package once, triggers the render detached on the laptop,
    # polls, and pulls finished chunks back. A VM restart loses nothing:
    # the next tick re-reads the marker and re-polls — never re-shipping
    # finished chunks, never re-triggering a running render. Helper exit
    # codes: 0 done/waiting, 1 error (count a failure), 2 unreachable or
    # approval pending (wait WITHOUT counting a failure).
    if [ "${TRENDLINE_DRY_RUN:-0}" = "1" ]; then
      log "DRY-RUN: would run laptop-render.sh step for $JOB_ID (first incomplete orientation)"
      exit 0
    fi
    LAPTOP_DONE=true
    for ori in vertical landscape; do
      if "$PROD/finish-chunks.py" --job "$JOB_DIR" --orientation "$ori" --check-final-only >> "$LOG" 2>&1; then
        log "laptop: $ori is already finished and valid for $JOB_ID"
        continue
      fi
      if "$PROD/finish-chunks.py" --job "$JOB_DIR" --orientation "$ori" --check-chunks-only >> "$LOG" 2>&1; then
        log "laptop: $ori chunks are all valid for $JOB_ID (ready to finish in the verify phase)"
        continue
      fi
      LAPTOP_DONE=false
      "$LAPTOP_HELPER" step --job "$JOB_ID" --orientation "$ori" >> "$LOG" 2>&1
      STEP_RC=$?
      save_state ".current.laptop = {orientation: \"$ori\", step_rc: $STEP_RC, updated_at: \"$(date -Is)\"}"
      if [ "$STEP_RC" -eq 2 ]; then
        log "laptop: $ori unreachable or awaiting approval — waiting, not a failure"
      elif [ "$STEP_RC" -ne 0 ]; then
        log "laptop: step for $ori failed (rc $STEP_RC) — counting a failure"
        bump_failure "laptop step for $ori failed (rc $STEP_RC)"
      else
        log "laptop: step for $ori OK (render running, waiting, or complete)"
      fi
      break
    done
    if [ "$LAPTOP_DONE" = "true" ]; then
      log "laptop: no chunk render needed — moving to verify"
      set_phase "verify" "null" "null"
    fi
    exit 0
  fi
  # Was the previous chunk render for this orientation a death (vs a first
  # launch, or a render that actually completed)? Only a same-orientation
  # relaunch after a death counts as a retry — a first launch for the other
  # orientation, after the first one completed, is not a failure.
  PREV_ORI=$(jq -r '.current.render_orientation // empty' "$STATE")
  PREV_DIED=false
  if [ -n "$PID" ] && [ "$PID" != "null" ] && ! pid_alive "$PID"; then
    PREV_DIED=true
  fi
  if pid_alive "$PID"; then
    # Stall check: a chunk render whose log has been silent for the
    # threshold is hung (Remotion prints frame progress continuously while
    # healthy) — kill the whole group; the next tick relaunches from the
    # last valid chunk and counts the failure then.
    if [ "$DRIVER_OWNED" = "true" ] && step_stalled "$STALL_MIN"; then
      log "chunk render pid $PID log silent $(step_log_age_min)m (>= ${STALL_MIN}m) — killing the whole process group"
      kill_step_group "$PID"
      exit 0
    fi
    log "chunk render still running (pid $PID, $JOB_ID)"
    exit 0
  fi
  # Guard: never start a second chunk render while ANY chunk render is
  # already running for this job. A manual render is invisible to the
  # recorded pid, and two writers to the same chunk files corrupt them.
  if pgrep -f "remotion render.*$JOB_ID" >/dev/null 2>&1 || pgrep -f "render-chunks.sh.*$JOB_ID" >/dev/null 2>&1; then
    log "a chunk render is already running for $JOB_ID (not the recorded pid — possibly manual) — waiting"
    exit 0
  fi
  LAUNCHED=""
  for ori in vertical landscape; do
    if "$PROD/finish-chunks.py" --job "$JOB_DIR" --orientation "$ori" --check-final-only >> "$LOG" 2>&1; then
      log "$ori is already finished and valid for $JOB_ID"
      continue
    fi
    if "$PROD/finish-chunks.py" --job "$JOB_DIR" --orientation "$ori" --check-chunks-only >> "$LOG" 2>&1; then
      log "$ori chunks are all valid for $JOB_ID (ready to finish in the verify phase)"
      continue
    fi
    # This orientation needs a chunk render. One per tick.
    # Guard: if the props file is missing, render-chunks.sh would exit 2
    # instantly and the driver would relaunch it forever. Send the job back
    # to prep_done instead — --assemble regenerates the props (TTS/art are
    # already on disk, so it only re-renders).
    ORI_PROPS="$JOB_DIR/remotion-props-$([ "$ori" = "vertical" ] && echo "1080x1920" || echo "1920x1080").json"
    if [ ! -f "$ORI_PROPS" ]; then
      log "$ori props missing ($ORI_PROPS) — returning to prep_done to regenerate them via --assemble"
      set_phase "prep_done" "null" "null"
      exit 0
    fi
    # Memory gate BEFORE the retry counting below: a blocked tick must not
    # burn a retry on the same death (the recorded pid stays dead, so the
    # next tick would count it again). When the gate passes, the counting
    # runs once and the launch follows.
    if ! memory_gate_ok; then
      log "render_chunks: memory gate blocked the $ori chunk render this tick — waiting, not a failure"
      exit 0
    fi
    if ! vm_render_allowed; then
      # VM render is OFF and laptop mode is not on: wait here (not a
      # failure, and NOT a retry — the launch below never runs).
      log "render_chunks: VM render is OFF (laptop mode not enabled and vm_render_fallback_enabled=false) — waiting for the laptop link; not a failure"
      exit 0
    fi
    # This launch retries a chunk render that died: count it toward the
    # escalation gate and the render.max_chunk_retries cap (previously a
    # dead chunk render was silently relaunched forever).
    if [ "$PREV_DIED" = "true" ] && [ "$ori" = "$PREV_ORI" ]; then
      bump_failure "chunk render for $ori died, retrying"
      CHUNK_RETRIES=$(jq -r '.current.chunk_retries // 0' "$STATE")
      CHUNK_RETRIES=$((CHUNK_RETRIES + 1))
      save_state ".current.chunk_retries = $CHUNK_RETRIES"
      if [ "$CHUNK_RETRIES" -gt "$MAX_CHUNK_RETRIES" ]; then
        log "chunk render for $ori died $CHUNK_RETRIES times (max $MAX_CHUNK_RETRIES retries) — escalating; Rishi to decide"
        save_state ".current.failures = $MAX_FAILURES | .current.updated_at = \"$(date -Is)\""
        exit 0
      fi
    fi
    CHUNK_LOG="$TMPDIR/trendline-chunks-driver-$(date +%Y%m%d-%H%M%S).log"
    launch "'$PROD/render-chunks.sh' --job '$JOB_DIR' --orientation $ori >> '$CHUNK_LOG' 2>&1"
    NEWPID=$LAUNCH_PID
    save_state ".current.pid = $NEWPID | .current.pgid = $LAUNCH_PGID | .current.log = \"$CHUNK_LOG\" | .current.render_orientation = \"$ori\" | .current.updated_at = \"$(date -Is)\""
    log "chunk render launched for $ori: pid $NEWPID log $CHUNK_LOG"
    LAUNCHED="$ori"
    break
  done
  if [ -z "$LAUNCHED" ]; then
    log "no chunk render needed — moving to verify"
    set_phase "verify" "null" "null"
  fi
  exit 0
fi

# --- phase: finish ------------------------------------------------------------
# A finish-chunks.py run (concat + loudnorm + SRT + sidecar) is in flight
# for one orientation. It runs detached because the loudnorm alone takes
# minutes on a long video. When it exits, re-check that orientation.
if [ "$PHASE" = "finish" ]; then
  ORI=$(jq -r '.current.finish_orientation // empty' "$STATE")
  if [ -z "$ORI" ]; then
    log "finish phase but no finish_orientation — resetting to verify"
    set_phase "verify" "null" "null"
    exit 0
  fi
  if pid_alive "$PID"; then
    # Stall check: a finish (concat + loudnorm) whose log has been silent
    # for the threshold is hung — kill the whole group; the next tick
    # re-checks the final and counts the failure if it is still invalid.
    if [ "$DRIVER_OWNED" = "true" ] && step_stalled "$STALL_MIN"; then
      log "finish pid $PID log silent $(step_log_age_min)m (>= ${STALL_MIN}m) — killing the whole process group"
      kill_step_group "$PID"
      exit 0
    fi
    log "finish still running for $ORI (pid $PID, $JOB_ID)"
    exit 0
  fi
  # L41: capture the check's own output instead of appending it straight
  # to the driver log — "final still invalid" is a symptom, not a reason,
  # and the finish step's exit code / log tail were never on the line.
  CHECK_OUT=$("$PROD/finish-chunks.py" --job "$JOB_DIR" --orientation "$ORI" --check-final-only 2>&1)
  CHECK_RC=$?
  [ -n "$CHECK_OUT" ] && printf '%s\n' "$CHECK_OUT" >> "$LOG"
  if [ "$CHECK_RC" -eq 0 ]; then
    log "$ORI finished OK for $JOB_ID"
    set_phase "verify" "null" "null"
    exit 0
  fi
  CHECK_TAIL=$(printf '%s\n' "$CHECK_OUT" | tail -n 3 | awk '{printf "%s%s", (NR>1?" | ":""), $0} END{print ""}')
  [ -n "$CHECK_TAIL" ] || CHECK_TAIL="no check-final-only output"
  log "$ORI finish FAILED (final still invalid) — returning to chunk recovery; exit code $(step_exit_code "$STEP_LOG") (captured from the step's exit, not the driver's); last log lines: $(step_log_tail "$STEP_LOG" 3); check-final-only output (rc $CHECK_RC): $CHECK_TAIL"
  bump_failure "finish FAILED for $ORI (final still invalid)"
  set_phase "render_chunks" "null" "null"
  exit 0
fi

# --- phase: verify ------------------------------------------------------------
# Both orientations must pass --check-final-only. If one does not, finish
# it (when its chunks are ready) or send it back to chunk rendering.
if [ "$PHASE" = "verify" ]; then
  ALL_OK=true
  for ori in vertical landscape; do
    if "$PROD/finish-chunks.py" --job "$JOB_DIR" --orientation "$ori" --check-final-only >> "$LOG" 2>&1; then
      log "$ori verified OK for $JOB_ID"
      continue
    fi
    ALL_OK=false
    if "$PROD/finish-chunks.py" --job "$JOB_DIR" --orientation "$ori" --check-chunks-only >> "$LOG" 2>&1; then
      log "$ori chunks are ready — launching finish for $ori"
      FINISH_LOG="$TMPDIR/trendline-finish-driver-$(date +%Y%m%d-%H%M%S).log"
      launch "'$PROD/finish-chunks.py' --job '$JOB_DIR' --orientation $ori >> '$FINISH_LOG' 2>&1"
      NEWPID=$LAUNCH_PID
      save_state ".current.phase = \"finish\" | .current.finish_orientation = \"$ori\" | .current.pid = $NEWPID | .current.pgid = $LAUNCH_PGID | .current.log = \"$FINISH_LOG\" | .current.updated_at = \"$(date -Is)\""
      log "finish launched for $ori: pid $NEWPID log $FINISH_LOG"
      exit 0
    fi
    log "$ori chunks are missing/invalid — returning to chunk rendering"
    set_phase "render_chunks" "null" "null"
    exit 0
  done
  if [ "$ALL_OK" = "true" ]; then
    log "VIDEO COMPLETE: $JOB_ID (trendlinenow) — both orientations verified. READY FOR REVIEW (sidecars Approved:false; nothing uploads without Rishi's OK)."
    TITLE=$(jq -r '.Title // empty' "$JOB_DIR/script.json")
    # Free the disk: chunk intermediates are no longer needed once the
    # finals verify. The finals + sidecars + SRTs are the durable output.
    for ori in vertical landscape; do
      if [ -d "$JOB_DIR/chunks-$ori" ]; then
        rm -rf "$JOB_DIR/chunks-$ori"
        log "cleaned up chunks-$ori for $JOB_ID"
      fi
    done
    if laptop_mode && [ "${TRENDLINE_DRY_RUN:-0}" != "1" ]; then
      # Best-effort: free the laptop's disk now that both finals verify.
      # Never fails the tick — the finals are already safe on the VM.
      for ori in vertical landscape; do
        "$LAPTOP_HELPER" cleanup --job "$JOB_ID" --orientation "$ori" >> "$LOG" 2>&1 || true
      done
      log "laptop: remote chunks cleaned up for $JOB_ID"
    fi
    save_state ".completed += [{job: \"$JOB_ID\", profile: \"trendlinenow\", title: \"$TITLE\", completed_at: \"$(date -Is)\"}] | .current = {job: null, profile: null, phase: \"idle\", pid: null, pgid: null, log: null, driver_owned: false, failures: 0, assemble_retries: 0, chunk_retries: 0, updated_at: \"$(date -Is)\"}"
    log "advanced to idle; next video starts on the next tick"
    exit 0
  fi
  exit 0
fi

# Unknown phase: the job is in a state the driver does not understand —
# record it as abandoned (with the phase as the reason) instead of
# silently discarding its history.
abandon_current "unknown phase: $PHASE"
exit 0
