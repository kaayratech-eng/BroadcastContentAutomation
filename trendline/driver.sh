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
#     GIGGLE_ScriptProvider=groq-helper and GIGGLE_SceneArtProvider=cloudflare.
#     The config defaults are wrong for this channel (hybrid-helper routes
#     acts 3+ to depleted Gemini; pollinations is paid).
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
#   render_assemble — a driver-launched --assemble is running (vertical
#                     first, then landscape). If it dies after writing the
#                     vertical props (or the landscape props), the driver
#                     switches to chunk recovery. If its log is silent for
#                     30 min and the driver owns the process, the driver
#                     kills it and switches to chunk recovery.
#   render_chunks   — a driver-launched chunk render (render-chunks.sh) is
#                     running. If it dies, re-run it: it skips valid chunks.
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
#   * Never re-render a finished output: chunk renders skip valid chunks,
#     and finish only runs when the final file is missing or invalid.
#   * Never upload anything. Sidecars are written Approved=false.
#   * A job that fails verification 3 times is escalated (state.json
#     failures>=3) and the driver stops touching it.
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
export GIGGLE_ScriptProvider=groq-helper
export GIGGLE_SceneArtProvider=cloudflare

mkdir -p "$(dirname "$LOG")" 2>/dev/null || true
log() { echo "$(date -Is) [trendline-driver] $*" | tee -a "$LOG"; }

# Launch a long step detached (setsid, log to a file). Sets LAUNCH_PID.
# When TRENDLINE_DRY_RUN=1, nothing is launched — the command is only
# logged, and LAUNCH_PID is 0. Used by the dry-run tests (and by hand to
# see what the driver would do without starting any real work).
launch() { # $1 = command string (run via setsid bash -c)
  if [ "${TRENDLINE_DRY_RUN:-0}" = "1" ]; then
    log "DRY-RUN: would launch: $1"
    LAUNCH_PID=0
    return 0
  fi
  setsid bash -c "$1" &
  LAUNCH_PID=$!
}

# --- single-instance guard ---------------------------------------------------
exec 9>"$LOCK"
if ! flock -n 9; then
  log "another driver tick is running; exiting"
  exit 0
fi

# --- helpers -----------------------------------------------------------------
save_state() { # $1 = jq filter (applied to state.json, atomic via tmp file)
  jq "$1" "$STATE" > "$STATE.tmp" && mv "$STATE.tmp" "$STATE"
}
set_phase() { # $1 = phase, $2 = pid (or null), $3 = log path (or null)
  save_state ".current.phase = \"$1\" | .current.pid = $2 | .current.log = $3 | .current.updated_at = \"$(date -Is)\""
}
bump_failure() {
  save_state ".current.failures = (.current.failures // 0) + 1 | .current.updated_at = \"$(date -Is)\""
}
job_dir() { # $1 = job id (profile is always trendlinenow)
  echo "$REPO/work/trendlinenow/$1"
}
# prep_complete: 0 if script.json + every scene has its audio and visual.
# A "visual" scene (chart/table/callout) is drawn by Remotion from its
# VisualSpec at assemble time — nothing on disk — so it counts as complete
# when the spec is present. Every other scene needs sceneNN.mp3 plus a
# visual file (ImagePath/LandscapeImagePath from script.json, or a
# sceneNN-stock.mp4/.jpg). A per-scene "sceneNN-art.*" name check is WRONG:
# scenes share one art image per VisualGroup, so the file is named for the
# group's first scene, not for this scene.
prep_complete() { # $1 = job dir
  python3 - "$1" <<'PYEOF'
import json, os, sys
job = sys.argv[1]
try:
    script = json.load(open(os.path.join(job, "script.json")))
except Exception:
    sys.exit(1)
for i, sc in enumerate(script["Scenes"]):
    if not os.path.exists(os.path.join(job, f"scene{i:02d}.mp3")):
        sys.exit(1)
    if sc.get("SceneKind") == "visual":
        if sc.get("VisualSpec"):
            continue
        sys.exit(1)
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
sys.exit(0)
PYEOF
}
pid_alive() { # $1 = pid -> 0 if alive
  [ -n "$1" ] && [ "$1" != "null" ] && kill -0 "$1" 2>/dev/null
}
log_tail_has() { # $1 = log, $2 = pattern -> 0 if found in the last 200 lines
  [ -f "$1" ] && tail -200 "$1" 2>/dev/null | grep -q "$2"
}
extract_job_from_log() { # $1 = log -> prints "<job>" or nothing
  # --prep prints: Workspace: .../work/trendlinenow/<job-YYYYMMDD-HHMMSS>
  grep -m1 -o "work/trendlinenow/job-[0-9-]*" "$1" 2>/dev/null | \
    sed 's|^work/trendlinenow/\(job-[0-9-]*\)$|\1|'
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
      if (cd "$REPO/BroadcastContentAutomation/VideoGen" && timeout 600 dotnet build -c Release >> "$LOG" 2>&1); then
        log "preflight: rebuild succeeded"
      else
        log "preflight: rebuild FAILED — escalating (not starting new work)"
        return 1
      fi
    else
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
  if find "$REPO" -type d -name '*\\*' 2>/dev/null | grep -q .; then
    log "preflight: a directory with backslashes in its name exists under the repo — dotnet build would fail (MSB3552); escalating"
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

# --- health check (read-only; never acts) ------------------------------------
"$TRENDLINE/healthcheck.sh" >> "$LOG" 2>&1 || true

# --- load state ---------------------------------------------------------------
if [ ! -f "$STATE" ]; then
  log "CRITICAL: state.json missing; refusing to act (create it first)"
  exit 1
fi
PAUSED=$(jq -r '.paused // false' "$STATE")
PAUSE_REASON=$(jq -r '.pause_reason // ""' "$STATE")
if [ "$PAUSED" = "true" ]; then
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

# The protected job (video #1) is owned by the build subagent. Never touch it.
if [ -n "$JOB_ID" ] && [ "$JOB_ID" = "$PROTECTED_JOB" ]; then
  log "GUARD: current job is the protected video #1 ($PROTECTED_JOB) — refusing to act; resetting to idle"
  save_state '.current = {job: null, profile: null, phase: "idle", pid: null, log: null, driver_owned: false, failures: 0, updated_at: "'$(date -Is)'"}'
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
  fi
fi

if [ "$FAILURES" -ge "$MAX_FAILURES" ]; then
  log "ESCALATED: current job has $FAILURES failures (>= $MAX_FAILURES) — driver is paused for this job; Rishi to decide"
  exit 0
fi

# Resolve the current job dir (if any).
JOB_DIR=""
[ -n "$JOB_ID" ] && JOB_DIR=$(job_dir "$JOB_ID")

# --- phase: idle --------------------------------------------------------------
if [ "$PHASE" = "idle" ]; then
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
      save_state "del(.queue[0]) | .current = {job: \"$Q_JOB\", profile: \"trendlinenow\", phase: \"prep_done\", pid: null, log: null, driver_owned: true, failures: 0, started_at: \"$(date -Is)\", updated_at: \"$(date -Is)\"}"
      exit 0
    else
      log "idle: queued job $Q_JOB is NOT fully prepped (missing audio/art) — will resume its prep"
      save_state "del(.queue[0]) | .current = {job: \"$Q_JOB\", profile: \"trendlinenow\", phase: \"prep\", pid: null, log: null, driver_owned: true, failures: 0, started_at: \"$(date -Is)\", updated_at: \"$(date -Is)\"}"
      exit 0
    fi
  fi
  # No queued job: start a new prep. Preflight first.
  if ! preflight; then
    log "idle: preflight failed — not starting a new prep this tick"
    exit 0
  fi
  log "idle: starting a new prep --prep --long --profile trendlinenow (trending topic auto-picked)"
  NEW_LOG="$TMPDIR/trendline-prep-driver-$(date +%Y%m%d-%H%M%S).log"
  launch "cd '$REPO/BroadcastContentAutomation/VideoGen' && dotnet bin/Release/net8.0/VideoGen.dll --prep --long --profile trendlinenow >> '$NEW_LOG' 2>&1"
  NEWPID=$LAUNCH_PID
  save_state ".current = {job: null, profile: \"trendlinenow\", phase: \"prep\", pid: $NEWPID, log: \"$NEW_LOG\", driver_owned: true, failures: 0, started_at: \"$(date -Is)\", updated_at: \"$(date -Is)\"}"
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
    log "prep died before creating a job dir — relaunching a fresh --prep"
    NEW_LOG="$TMPDIR/trendline-prep-driver-$(date +%Y%m%d-%H%M%S).log"
    launch "cd '$REPO/BroadcastContentAutomation/VideoGen' && dotnet bin/Release/net8.0/VideoGen.dll --prep --long --profile trendlinenow >> '$NEW_LOG' 2>&1"
    NEWPID=$LAUNCH_PID
    save_state ".current.pid = $NEWPID | .current.log = \"$NEW_LOG\" | .current.updated_at = \"$(date -Is)\""
    log "prep relaunched: pid $NEWPID log $NEW_LOG"
    exit 0
  fi
  log "phase=$PHASE but no current job dir ($JOB_ID / $JOB_DIR) — resetting to idle"
  save_state '.current = {job: null, profile: null, phase: "idle", pid: null, log: null, driver_owned: false, failures: 0, updated_at: "'$(date -Is)'"}'
  exit 0
fi

# --- phase: prep --------------------------------------------------------------
if [ "$PHASE" = "prep" ]; then
  if pid_alive "$PID"; then
    log "prep still running (pid $PID, $JOB_ID)"
    exit 0
  fi
  # The prep process is dead (or never had a pid). Decide the recovery.
  if [ -f "$JOB_DIR/script.json" ]; then
    # Script exists: is the prep actually complete (audio + art)?
    if prep_complete "$JOB_DIR"; then
      log "prep complete for $JOB_ID (script + audio + art all present)"
      set_phase "prep_done" "null" "null"
      exit 0
    fi
    log "prep died with script.json present but audio/art incomplete — resuming"
  else
    # No script.json yet. Was it a 429 (throttle) or a script-gen failure?
    if log_tail_has "$STEP_LOG" "HTTP 429\|Too Many Requests\|Downstream Service Throttled"; then
      LAST_429=$(jq -r '.current.last_429_at // empty' "$STATE")
      if [ -n "$LAST_429" ] && [ "$LAST_429" != "null" ]; then
        BACKOFF_END=$(date -d "$LAST_429 + $BACKOFF_MIN minutes" +%s 2>/dev/null || echo 0)
        NOW_TS=$(date +%s)
        if [ "$NOW_TS" -lt "$BACKOFF_END" ]; then
          log "Azure 429 backoff: $(( (BACKOFF_END - NOW_TS) / 60 ))m remaining; not resuming yet"
          exit 0
        fi
      fi
      log "Azure 429 backoff expired — resuming prep"
      save_state ".current.last_429_at = null"
    elif [ -f "$JOB_DIR/script-partial.json" ]; then
      log "prep died during script generation (partial checkpoint exists, no script.json) — resuming from the partial (saved scenes reused verbatim)"
    else
      log "prep died before any script checkpoint — relaunching a fresh --prep"
    fi
  fi
  # Resume (or relaunch) the prep.
  RESUME_LOG="$TMPDIR/trendline-prep-driver-$(date +%Y%m%d-%H%M%S).log"
  if [ -f "$JOB_DIR/script.json" ]; then
    launch "cd '$REPO/BroadcastContentAutomation/VideoGen' && dotnet bin/Release/net8.0/VideoGen.dll --resume --job $JOB_ID >> '$RESUME_LOG' 2>&1"
  elif [ -f "$JOB_DIR/script-partial.json" ]; then
    launch "cd '$REPO/BroadcastContentAutomation/VideoGen' && dotnet bin/Release/net8.0/VideoGen.dll --prep --long --profile trendlinenow --job $JOB_ID >> '$RESUME_LOG' 2>&1"
  else
    launch "cd '$REPO/BroadcastContentAutomation/VideoGen' && dotnet bin/Release/net8.0/VideoGen.dll --prep --long --profile trendlinenow >> '$RESUME_LOG' 2>&1"
  fi
  NEWPID=$LAUNCH_PID
  save_state ".current.pid = $NEWPID | .current.log = \"$RESUME_LOG\" | .current.updated_at = \"$(date -Is)\""
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
  if ! preflight; then
    log "prep_done: preflight failed — not launching --assemble this tick"
    exit 0
  fi
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
  V_PROPS="$JOB_DIR/remotion-props-1080x1920.json"
  L_PROPS="$JOB_DIR/remotion-props-1920x1080.json"
  if pid_alive "$PID"; then
    # Still running. Only kill it if the log is silent for the stall
    # threshold AND we own it — a silent --assemble is stuck (Remotion
    # prints frame progress continuously while healthy).
    if [ "$DRIVER_OWNED" = "true" ] && [ -f "$STEP_LOG" ]; then
      LOG_MTIME=$(stat -c %Y "$STEP_LOG" 2>/dev/null || echo 0)
      LOG_AGE_MIN=$(( ($(date +%s) - LOG_MTIME) / 60 ))
      if [ "$LOG_AGE_MIN" -ge "$STALL_MIN" ]; then
        log "assemble pid $PID log silent ${LOG_AGE_MIN}m (>= ${STALL_MIN}m) — killing and chunk-recovering"
        kill "$PID" 2>/dev/null || true
        sleep 2
        kill -9 "$PID" 2>/dev/null || true
        bump_failure
        set_phase "render_chunks" "null" "null"
        exit 0
      fi
    fi
    log "assemble still running (pid $PID, $JOB_ID)"
    exit 0
  fi
  # The --assemble process is dead. What did it leave behind?
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
  # Died before writing any props: likely a crash at startup. Check the log
  # tail for a fatal marker.
  if log_tail_has "$STEP_LOG" "FATAL\|Unhandled exception\|OutOfMemoryException"; then
    log "assemble died before writing props (fatal marker in log) — retrying --assemble once"
    bump_failure
    set_phase "prep_done" "null" "null"
    exit 0
  fi
  log "assemble died before writing props (no fatal marker; possibly a VM reboot or the 180-min timeout) — retrying --assemble"
  bump_failure
  set_phase "prep_done" "null" "null"
  exit 0
fi

# --- phase: render_chunks ------------------------------------------------------
# Ensure every chunk exists and is valid. A chunk render is only launched
# for an orientation whose final is NOT already done and whose chunks are
# NOT already valid. When nothing needs a chunk render, move to verify.
if [ "$PHASE" = "render_chunks" ]; then
  if pid_alive "$PID"; then
    log "chunk render still running (pid $PID, $JOB_ID)"
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
    CHUNK_LOG="$TMPDIR/trendline-chunks-driver-$(date +%Y%m%d-%H%M%S).log"
    launch "'$PROD/render-chunks.sh' --job '$JOB_DIR' --orientation $ori >> '$CHUNK_LOG' 2>&1"
    NEWPID=$LAUNCH_PID
    save_state ".current.pid = $NEWPID | .current.log = \"$CHUNK_LOG\" | .current.render_orientation = \"$ori\" | .current.updated_at = \"$(date -Is)\""
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
    log "finish still running for $ORI (pid $PID, $JOB_ID)"
    exit 0
  fi
  if "$PROD/finish-chunks.py" --job "$JOB_DIR" --orientation "$ORI" --check-final-only >> "$LOG" 2>&1; then
    log "$ORI finished OK for $JOB_ID"
    set_phase "verify" "null" "null"
    exit 0
  fi
  log "$ORI finish FAILED (final still invalid) — returning to chunk recovery"
  bump_failure
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
      save_state ".current.phase = \"finish\" | .current.finish_orientation = \"$ori\" | .current.pid = $NEWPID | .current.log = \"$FINISH_LOG\" | .current.updated_at = \"$(date -Is)\""
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
    save_state ".completed += [{job: \"$JOB_ID\", profile: \"trendlinenow\", title: \"$TITLE\", completed_at: \"$(date -Is)\"}] | .current = {job: null, profile: null, phase: \"idle\", pid: null, log: null, driver_owned: false, failures: 0, updated_at: \"$(date -Is)\"}"
    log "advanced to idle; next video starts on the next tick"
    exit 0
  fi
  exit 0
fi

log "unknown phase: $PHASE — resetting to idle"
save_state '.current = {job: null, profile: null, phase: "idle", pid: null, log: null, driver_owned: false, failures: 0, updated_at: "'$(date -Is)'"}'
exit 0
