#!/bin/bash
# production/driver.sh — the continuous-production state machine.
#
# Invoked by cron every 10 min (and by hand). One invocation = one tick:
# it looks at state.json, does the ONE safe next action for the current
# video, and exits in seconds. It NEVER blocks waiting for a render or a
# prep — long steps are launched detached (setsid, log to ~/workspace/tmp)
# and the NEXT tick checks on them. This is what makes it survive VM
# reboots: a reboot just kills the detached step; the next cron tick sees
# the dead process (or the missing output) and resumes from the last
# checkpoint. No background agent, no session, no heartbeat relaunch.
#
# Phases (state.json .current.phase):
#   idle            — nothing current; pick the next job (pre-prepped first,
#                     else start a new prep with the alternating profile).
#   prep            — a --prep is running (or was killed). If the process
#                     died: if the log shows a 429, back off 45 min, then
#                     --resume; if script.json exists, --resume; if only a
#                     partial exists, --resume still fails (open gap) so a
#                     fresh --prep is the recovery — but ONLY after 3
#                     failures escalate to Rishi.
#   prep_done       — script.json + all scene audio/art present; ready to
#                     render. (Detected from the job dir, not just the log.)
#   render_assemble — a driver-launched --assemble is running (vertical
#                     first, then landscape). If it dies after writing the
#                     vertical props, the vertical is chunk-recovered and
#                     the landscape is chunk-rendered (see render_chunks).
#   render_chunks   — a driver-launched chunk render (render-chunks.sh) is
#                     running. If it dies, re-run it: it skips valid chunks.
#   finish          — a driver-launched finish-chunks.py run (concat +
#                     loudnorm + SRT + sidecar) is running detached. When it
#                     exits, the driver re-checks that orientation.
#   verify          — both orientations rendered; the driver checks each
#                     final with finish-chunks.py --check-final-only and
#                     finishes (or re-renders) any that is missing/invalid.
#   done            — outputs verified. Record the completion, advance the
#                     queue, and (if the next job is pre-prepped) start it
#                     on the NEXT tick — never inside this tick, so a tick
#                     never does two videos' worth of work.
#
# Safety rules (hard):
#   * flock on production/driver.lock — only one driver tick at a time.
#   * Only processes the driver launched (tracked by pid in state.json,
#     driver_owned=true) are ever killed. A monitor-only job (the ducks
#     re-render, launched by the parent agent) is NEVER touched.
#   * Never re-render a finished output: chunk renders skip valid chunks,
#     and finish only runs when the final file is missing or invalid.
#   * Never upload anything. Sidecars are written Approved=false.
#   * A job that fails verification 3 times is escalated (state.json
#     failures>=3) and the driver stops touching it.
#   * Azure 429: back off 45 min before any --resume (never hammer F0).
set -u
PROD=~/workspace/BroadcastContentAutomation/production
REPO=~/workspace/BroadcastContentAutomation
STATE="$PROD/state.json"
LOG=~/workspace/tmp/production-driver.log
LOCK="$PROD/driver.lock"
CONFIG="$PROD/config.json"

export DOTNET_ROOT=~/.dotnet
export PATH="$HOME/.dotnet:$PATH"
export TMPDIR=~/workspace/tmp
export GIGGLE_ScriptProvider=groq-helper

log() { echo "$(date -Is) [driver] $*" | tee -a "$LOG"; }

# Launch a long step detached (setsid, log to a file). Sets LAUNCH_PID.
# When PRODUCTION_DRY_RUN=1, nothing is launched — the command is only
# logged, and LAUNCH_PID is 0. Used by the dry-run tests (and by hand to
# see what the driver would do without starting any real work).
launch() { # $1 = command string (run via setsid bash -c)
  if [ "${PRODUCTION_DRY_RUN:-0}" = "1" ]; then
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
job_dir() { # $1 = "chronicleandchaos|gigglegarden" profile, $2 = job id
  echo "$REPO/work/$1/$2"
}
profile_of_job() { # $1 = job dir -> profile id from script.json (or dir parent)
  if [ -f "$1/script.json" ]; then
    jq -r '.Profile // empty' "$1/script.json"
  else
    basename "$(dirname "$1")"
  fi
}
prep_complete() { # $1 = job dir -> 0 if script.json + all scenes have mp3 + a visual
  # A scene's visual is the file named by script.json's ImagePath (scenes
  # share one art image per VisualGroup, so the file is named for the
  # group's first scene, not for this scene) or LandscapeImagePath, or a
  # stock file sceneNN-stock.mp4/.jpg discovered by filename. A per-scene
  # "sceneNN-art.*" name check is WRONG: it misses shared group art and
  # would call a fully prepped job incomplete.
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
    visual = False
    for key in ("ImagePath", "LandscapeImagePath"):
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
assemble_pid_alive() { # $1 = pid -> 0 if alive
  [ -n "$1" ] && [ "$1" != "null" ] && kill -0 "$1" 2>/dev/null
}
log_tail_has() { # $1 = log, $2 = pattern -> 0 if found in the last 200 lines
  [ -f "$1" ] && tail -200 "$1" 2>/dev/null | grep -q "$2"
}
extract_job_from_log() { # $1 = log -> prints "<profile> <job>" or nothing
  # --prep prints: Workspace: .../work/<profile>/<job-YYYYMMDD-HHMMSS>
  grep -m1 -o "work/[^ /]*/job-[0-9-]*" "$1" 2>/dev/null | \
    sed 's|^work/\([^ /]*\)/\(job-[0-9-]*\)$|\1 \2|'
}

# --- health check (read-only; never acts) ------------------------------------
"$PROD/healthcheck.sh" >> "$LOG" 2>&1 || true

# --- load state ---------------------------------------------------------------
if [ ! -f "$STATE" ]; then
  log "CRITICAL: state.json missing; refusing to act (create it first)"
  exit 1
fi
PHASE=$(jq -r '.current.phase // "idle"' "$STATE")
JOB_ID=$(jq -r '.current.job // empty' "$STATE")
JOB_PROFILE=$(jq -r '.current.profile // empty' "$STATE")
PID=$(jq -r '.current.pid // empty' "$STATE")
STEP_LOG=$(jq -r '.current.log // empty' "$STATE")
DRIVER_OWNED=$(jq -r '.current.driver_owned // false' "$STATE")
FAILURES=$(jq -r '.current.failures // 0' "$STATE")
NEXT_PROFILE=$(jq -r '.next_profile // "gigglegarden"' "$STATE")
MAX_FAILURES=$(jq -r '.health.max_consecutive_failures_before_escalation' "$CONFIG")
BACKOFF_MIN=$(jq -r '.tts.throttle_backoff_minutes' "$CONFIG")
STALL_MIN=$(jq -r '.render.chunk_log_silence_kill_minutes' "$CONFIG")

# A brand-new prep has no job id yet (the job id is only known once --prep
# prints its Workspace line). Extract it from the log and record it.
if [ -z "$JOB_ID" ] && [ "$PHASE" = "prep" ] && [ -n "$STEP_LOG" ] && [ -f "$STEP_LOG" ]; then
  read -r EXTRACTED_PROFILE EXTRACTED_JOB <<< "$(extract_job_from_log "$STEP_LOG")"
  if [ -n "$EXTRACTED_JOB" ]; then
    log "prep job id discovered from log: $EXTRACTED_JOB ($EXTRACTED_PROFILE)"
    JOB_ID="$EXTRACTED_JOB"
    JOB_PROFILE="$EXTRACTED_PROFILE"
    save_state ".current.job = \"$EXTRACTED_JOB\" | .current.profile = \"$EXTRACTED_PROFILE\""
  fi
fi

if [ "$FAILURES" -ge "$MAX_FAILURES" ]; then
  log "ESCALATED: current job has $FAILURES failures (>= $MAX_FAILURES) — driver is paused for this job; Rishi to decide"
  exit 0
fi

# Resolve the current job dir (if any).
JOB_DIR=""
[ -n "$JOB_ID" ] && [ -n "$JOB_PROFILE" ] && JOB_DIR=$(job_dir "$JOB_PROFILE" "$JOB_ID")

# --- phase: idle --------------------------------------------------------------
if [ "$PHASE" = "idle" ]; then
  # Prefer a pre-prepped job from the queue (FIFO).
  QUEUED=$(jq -r '.queue[0] // empty' "$STATE")
  if [ -n "$QUEUED" ]; then
    Q_PROFILE=$(jq -r '.queue[0].profile' "$STATE")
    Q_JOB=$(jq -r '.queue[0].job' "$STATE")
    Q_DIR=$(job_dir "$Q_PROFILE" "$Q_JOB")
    if prep_complete "$Q_DIR"; then
      log "idle: picking pre-prepped queued job $Q_JOB ($Q_PROFILE)"
      save_state "del(.queue[0]) | .current = {job: \"$Q_JOB\", profile: \"$Q_PROFILE\", phase: \"prep_done\", pid: null, log: null, driver_owned: true, failures: 0, started_at: \"$(date -Is)\", updated_at: \"$(date -Is)\"}"
      exit 0
    else
      log "idle: queued job $Q_JOB is NOT fully prepped (missing audio/art) — will resume its prep"
      save_state "del(.queue[0]) | .current = {job: \"$Q_JOB\", profile: \"$Q_PROFILE\", phase: \"prep\", pid: null, log: null, driver_owned: true, failures: 0, started_at: \"$(date -Is)\", updated_at: \"$(date -Is)\"}"
      exit 0
    fi
  fi
  # No queued job: start a new prep with the alternating profile.
  log "idle: starting a new prep (profile $NEXT_PROFILE, trending topic auto-picked)"
  NEW_LOG="$TMPDIR/prep-driver-$(date +%Y%m%d-%H%M%S).log"
  launch "cd '$REPO/BroadcastContentAutomation/VideoGen' && dotnet bin/Release/net8.0/VideoGen.dll --prep --long --profile $NEXT_PROFILE >> '$NEW_LOG' 2>&1"
  NEWPID=$LAUNCH_PID
  save_state ".current = {job: null, profile: \"$NEXT_PROFILE\", phase: \"prep\", pid: $NEWPID, log: \"$NEW_LOG\", driver_owned: true, failures: 0, started_at: \"$(date -Is)\", updated_at: \"$(date -Is)\"}"
  log "prep launched: pid $NEWPID log $NEW_LOG (job id unknown until --prep prints it; the next tick reads it from the log)"
  exit 0
fi

# From here a current job must exist — except a prep whose job id is not
# yet known (it is still running and has not printed its Workspace line).
if [ -z "$JOB_ID" ] || [ ! -d "$JOB_DIR" ]; then
  if [ "$PHASE" = "prep" ]; then
    if assemble_pid_alive "$PID"; then
      log "prep still running (pid $PID); job id not yet known — waiting"
      exit 0
    fi
    log "prep died before creating a job dir — relaunching a fresh --prep"
    NEW_LOG="$TMPDIR/prep-driver-$(date +%Y%m%d-%H%M%S).log"
    launch "cd '$REPO/BroadcastContentAutomation/VideoGen' && dotnet bin/Release/net8.0/VideoGen.dll --prep --long --profile $JOB_PROFILE >> '$NEW_LOG' 2>&1"
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
  if assemble_pid_alive "$PID"; then
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
      log "prep died during script generation (partial checkpoint exists, no script.json). --resume cannot use a partial — a fresh --prep is required (open design gap). Escalating after this failure."
      bump_failure
      exit 0
    else
      log "prep died before any script checkpoint — relaunching a fresh --prep"
    fi
  fi
  # Resume (or relaunch) the prep.
  RESUME_LOG="$TMPDIR/prep-driver-$(date +%Y%m%d-%H%M%S).log"
  if [ -f "$JOB_DIR/script.json" ]; then
    launch "cd '$REPO/BroadcastContentAutomation/VideoGen' && dotnet bin/Release/net8.0/VideoGen.dll --resume --job $JOB_ID >> '$RESUME_LOG' 2>&1"
  else
    launch "cd '$REPO/BroadcastContentAutomation/VideoGen' && dotnet bin/Release/net8.0/VideoGen.dll --prep --long --profile $JOB_PROFILE >> '$RESUME_LOG' 2>&1"
  fi
  NEWPID=$LAUNCH_PID
  save_state ".current.pid = $NEWPID | .current.log = \"$RESUME_LOG\" | .current.updated_at = \"$(date -Is)\""
  log "prep resumed/relaunched: pid $NEWPID log $RESUME_LOG"
  exit 0
fi

# --- phase: prep_done ---------------------------------------------------------
if [ "$PHASE" = "prep_done" ]; then
  # Is a render already running for this job (e.g. a previous --assemble)?
  if assemble_pid_alive "$PID"; then
    log "render already running for $JOB_ID (pid $PID); waiting"
    exit 0
  fi
  # Launch --assemble (vertical first, then landscape). The driver kills
  # it after the vertical finishes IF the landscape props appear and the
  # process is still running — see render_assemble handling below.
  ASSEMBLE_LOG="$TMPDIR/assemble-driver-$(date +%Y%m%d-%H%M%S).log"
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
  V_FINAL=$(python3 -c "import json,sys; s=json.load(open('$JOB_DIR/script.json')); print(s['Title'])" 2>/dev/null || echo "")
  if assemble_pid_alive "$PID"; then
    # Still running. If the vertical props exist and the vertical render
    # is done (the log says so) but the landscape props have NOT appeared
    # after a long time, the process may be stuck — but only kill it if
    # the log is silent for the stall threshold AND we own it.
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
    log "assemble died but landscape props exist — the vertical may be done; switching to chunk recovery for both orientations"
    set_phase "render_chunks" "null" "null"
    exit 0
  fi
  if [ -f "$V_PROPS" ]; then
    log "assemble died after writing vertical props (vertical may be partial) — switching to chunk recovery"
    set_phase "render_chunks" "null" "null"
    exit 0
  fi
  # Died before writing any props: likely a crash at startup or a 429 in
  # the loudness/measurement step. Check the log tail for a fatal marker.
  if log_tail_has "$STEP_LOG" "FATAL\|Unhandled exception\|OutOfMemoryException"; then
    log "assemble died before writing props (fatal marker in log) — retrying --assemble once"
    bump_failure
    set_phase "prep_done" "null" "null"
    exit 0
  fi
  log "assemble died before writing props (no fatal marker; possibly a VM reboot) — retrying --assemble"
  bump_failure
  set_phase "prep_done" "null" "null"
  exit 0
fi

# --- phase: render_chunks ------------------------------------------------------
# Ensure every chunk exists and is valid. A chunk render is only launched
# for an orientation whose final is NOT already done and whose chunks are
# NOT already valid. When nothing needs a chunk render, move to verify.
if [ "$PHASE" = "render_chunks" ]; then
  if assemble_pid_alive "$PID"; then
    log "chunk render still running (pid $PID, $JOB_ID)"
    exit 0
  fi
  # systemd service file lives in /etc (ephemeral — wiped on VM replacement).
  # If missing, reinstall from the persistent copy in the production dir.
  if [ ! -f /etc/systemd/system/kintaro-render.service ] && [ -f "$PROD/kintaro-render.service" ]; then
    cp "$PROD/kintaro-render.service" /etc/systemd/system/kintaro-render.service 2>/dev/null || sudo cp "$PROD/kintaro-render.service" /etc/systemd/system/kintaro-render.service
    systemctl daemon-reload 2>/dev/null || sudo systemctl daemon-reload
    systemctl enable kintaro-render.service 2>/dev/null || sudo systemctl enable kintaro-render.service
    systemctl start kintaro-render.service 2>/dev/null || sudo systemctl start kintaro-render.service
    log "kintaro-render.service reinstalled after VM replacement"
  fi
  # systemd-managed render: if kintaro-render.service is active, the render
  # runs under systemd (its MainPID changes on restart) — sync state.json to
  # the current MainPID and do NOT launch a duplicate.
  if systemctl is-active --quiet kintaro-render.service 2>/dev/null; then
    SYS_PID=$(systemctl show kintaro-render.service -p MainPID --value 2>/dev/null || echo "")
    if [ -n "$SYS_PID" ] && [ "$SYS_PID" != "0" ]; then
      save_state ".current.pid = $SYS_PID | .current.log = \"/home/hatch/workspace/tmp/chunks-kintaro-landscape-systemd-20261117.log\" | .current.render_orientation = \"landscape\" | .current.updated_at = \"$(date -Is)\""
      log "systemd render active (pid $SYS_PID) — state synced, no duplicate launch"
      exit 0
    fi
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
    CHUNK_LOG="$TMPDIR/chunks-driver-$(date +%Y%m%d-%H%M%S).log"
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
  if assemble_pid_alive "$PID"; then
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
      FINISH_LOG="$TMPDIR/finish-driver-$(date +%Y%m%d-%H%M%S).log"
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
    log "VIDEO COMPLETE: $JOB_ID ($JOB_PROFILE) — both orientations verified"
    # Record the completion and advance.
    TITLE=$(jq -r '.Title // empty' "$JOB_DIR/script.json")
    save_state ".completed += [{job: \"$JOB_ID\", profile: \"$JOB_PROFILE\", title: \"$TITLE\", completed_at: \"$(date -Is)\"}] | .next_profile = (if \"$JOB_PROFILE\" == \"chronicleandchaos\" then \"gigglegarden\" else \"chronicleandchaos\" end) | .current = {job: null, profile: null, phase: \"idle\", pid: null, log: null, driver_owned: false, failures: 0, updated_at: \"$(date -Is)\"}"
    log "advanced to idle; next profile is $(jq -r '.next_profile' "$STATE")"
    exit 0
  fi
  exit 0
fi

log "unknown phase: $PHASE — resetting to idle"
save_state '.current = {job: null, profile: null, phase: "idle", pid: null, log: null, driver_owned: false, failures: 0, updated_at: "'$(date -Is)'"}'
exit 0
