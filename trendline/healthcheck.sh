#!/bin/bash
# trendline/healthcheck.sh — health check for the Trendline Now loop.
# Runs every driver tick (10 min) and on demand. NEVER launches or kills
# anything, and never mutates jobs — with ONE exception: when it first sees
# an Azure 429/throttle marker in the current step log, it records
# .current.last_429_at in state.json so the driver's backoff can be
# measured from then (previously last_429_at was never set anywhere, so
# the backoff was dead code). The driver (driver.sh) is the only
# component that acts on the findings.
#
# Checks:
#   (a) Is a driver step currently running and making progress?
#       (process alive + its log advancing — the "stall" signal is log
#       silence, never absence of a process alone.)
#   (b) Did the last step stall, crash, or 429-throttle, and what is the
#       safe recovery (resume prep / relaunch chunk / back off TTS)?
#   (c) Are the external services reachable (Azure TTS voices probe,
#       Groq / Cloudflare / Pexels / YouTube API endpoint reachability)?
#   (d) Disk space, output-folder sanity, VideoGen DLL + intro logo present.
#
# Writes trendline/health.json (a snapshot) and appends one line per
# check to the driver log. Exit 0 = healthy or only warnings;
# exit 1 = a critical problem (disk critical, current step crashed with
# no recovery path, or a service the CURRENT step needs is down).
set -u
TRENDLINE=${TRENDLINE_DIR:-~/workspace/BroadcastContentAutomation/trendline}
STATE="${TRENDLINE_STATE:-$TRENDLINE/state.json}"
CONFIG="${TRENDLINE_CONFIG:-$TRENDLINE/config.json}"
LOG=$(jq -r '.driver_log' "$CONFIG" 2>/dev/null || echo ~/workspace/goals/daily-trending-news-video-channel/hidden_files/trendline-driver.log)

# Shared throttle-marker rule (review #2 B15): one pattern + whole-log
# check, so this script and driver.sh cannot drift apart again.
THROTTLE_SH="$TRENDLINE/throttle.sh"
[ -f "$THROTTLE_SH" ] && . "$THROTTLE_SH"
HEALTH="$TRENDLINE/health.json"
NOW=$(date -Is)
TS=$(date +%s)

note() { echo "$(date -Is) [trendline-healthcheck] $*"; }

# --- read state -------------------------------------------------------------
if [ ! -f "$STATE" ]; then
  note "CRITICAL: state.json missing"
  printf '{"timestamp":"%s","status":"critical","problems":["state.json missing"]}\n' "$NOW" > "$HEALTH"
  exit 1
fi
CURRENT_JOB=$(jq -r '.current.job // empty' "$STATE")
CURRENT_PHASE=$(jq -r '.current.phase // empty' "$STATE")
CURRENT_LOG=$(jq -r '.current.log // empty' "$STATE")
CURRENT_PID=$(jq -r '.current.pid // empty' "$STATE")
DRIVER_OWNED=$(jq -r '.current.driver_owned // false' "$STATE")
FAILURES=$(jq -r '.current.failures // 0' "$STATE")
LAST_429=$(jq -r '.current.last_429_at // empty' "$STATE")
PAUSED=$(jq -r '.paused // false' "$STATE")
DISK_WARN=$(jq -r '.health.disk_warn_gb' "$CONFIG")
DISK_CRIT=$(jq -r '.health.disk_critical_gb' "$CONFIG")
STALL_MIN=$(jq -r '.render.chunk_log_silence_kill_minutes' "$CONFIG")
LOG_STALL_MIN=$(jq -r '.health.render_log_silence_warn_minutes' "$CONFIG")
BACKOFF_MIN=$(jq -r '.tts.throttle_backoff_minutes' "$CONFIG")

PROBLEMS=()
WARNINGS=()
STATUS=ok

problem() { PROBLEMS+=("$1"); STATUS=critical; note "CRITICAL: $1"; }
warning() { WARNINGS+=("$1"); [ "$STATUS" = "ok" ] && STATUS=warning; note "WARNING: $1"; }

# --- (d) disk space + output sanity + pipeline files -------------------------
DISK_FREE_GB=$(df --output=avail -BG ~/workspace | tail -1 | tr -dc '0-9')
if [ "$DISK_FREE_GB" -lt "$DISK_CRIT" ]; then
  problem "disk free ${DISK_FREE_GB}GB < critical ${DISK_CRIT}GB — stop starting new work"
elif [ "$DISK_FREE_GB" -lt "$DISK_WARN" ]; then
  warning "disk free ${DISK_FREE_GB}GB < warn ${DISK_WARN}GB"
fi
VIDEOS_DIR=$(jq -r '.videos_dir' "$CONFIG")
if [ ! -d "$VIDEOS_DIR" ]; then
  problem "videos dir missing: $VIDEOS_DIR"
elif [ ! -d "$VIDEOS_DIR/trendlinenow" ]; then
  warning "videos dir missing trendlinenow subdir: $VIDEOS_DIR/trendlinenow"
fi
DLL=~/workspace/BroadcastContentAutomation/BroadcastContentAutomation/VideoGen/bin/Release/net8.0/VideoGen.dll
[ -f "$DLL" ] || problem "VideoGen.dll missing: $DLL"
LOGO=~/workspace/BroadcastContentAutomation/BroadcastContentAutomation/VideoGen/assets/trendlinenow-logo.png
[ -f "$LOGO" ] || problem "intro logo missing: $LOGO (assemble would fail fast)"

# --- (a) current step alive + log advancing ---------------------------------
# Even when paused, check the recorded pid/log (monitor-only): a dead pid
# while paused is a warning, never a problem — the driver is gated and will
# not act, but Rishi should still see it. (Previously this whole block was
# skipped when paused, so health.json read "ok" while phase render_assemble
# had a long-dead pid.)
MONITOR_ONLY=false
if [ "$PAUSED" = "true" ]; then
  MONITOR_ONLY=true
  note "driver is paused (gated) — step checks are monitor-only"
fi
# step_problem: a step problem is CRITICAL only when there is no recovery
# path. A dead or stalled step the driver can resume/relaunch (every known
# phase) is a WARNING — flagging routine VM-restart deaths CRITICAL trained
# everyone to ignore health.json.
step_problem() { # $1 = message
  if [ "$MONITOR_ONLY" = "true" ]; then
    warning "monitor-only (paused): $1"
  else
    case "$CURRENT_PHASE" in
      prep|prep_done|render_assemble|render_chunks|finish|verify)
        warning "$1 (recoverable — driver will resume/relaunch on the next tick)" ;;
      *)
        problem "$1 (phase $CURRENT_PHASE has no recovery path)" ;;
    esac
  fi
}
if [ -n "$CURRENT_JOB" ] && [ "$CURRENT_PHASE" != "idle" ] && [ "$CURRENT_PHASE" != "done" ]; then
  if [ -n "$CURRENT_PID" ] && [ "$CURRENT_PID" != "null" ]; then
    if kill -0 "$CURRENT_PID" 2>/dev/null; then
      # Also verify the cmdline: a recorded pid can be reused by an
      # unrelated process after a VM restart (same markers as driver.sh
      # pid_alive).
      CMD=$(ps -o args= -p "$CURRENT_PID" 2>/dev/null || echo "")
      case "$CMD" in
        *VideoGen.dll*|*render-chunks.sh*|*finish-chunks.py*|*remotion*)
          note "current step alive: pid $CURRENT_PID ($CURRENT_PHASE, $CURRENT_JOB)" ;;
        *)
          step_problem "current step pid $CURRENT_PID is not a pipeline step (pid reused?) — treating as dead (phase $CURRENT_PHASE, job $CURRENT_JOB)" ;;
      esac
    else
      step_problem "current step process $CURRENT_PID is DEAD (phase $CURRENT_PHASE, job $CURRENT_JOB) — driver should recover (resume/relaunch)"
    fi
  else
    note "current step has no pid recorded (phase $CURRENT_PHASE, job $CURRENT_JOB) — monitor-only or not yet launched"
  fi
  if [ -n "$CURRENT_LOG" ] && [ "$CURRENT_LOG" != "null" ] && [ -f "$CURRENT_LOG" ]; then
    LOG_MTIME=$(stat -c %Y "$CURRENT_LOG" 2>/dev/null || echo 0)
    LOG_AGE_MIN=$(( (TS - LOG_MTIME) / 60 ))
    if [ "$LOG_AGE_MIN" -ge "$STALL_MIN" ]; then
      if [ "$DRIVER_OWNED" = "true" ]; then
        step_problem "current step log silent ${LOG_AGE_MIN}m (>= ${STALL_MIN}m stall threshold) — driver should kill + retry the step"
      else
        warning "monitor-only step log silent ${LOG_AGE_MIN}m (job $CURRENT_JOB) — do NOT kill; it was not launched by the driver"
      fi
    elif [ "$LOG_AGE_MIN" -ge "$LOG_STALL_MIN" ]; then
      warning "current step log silent ${LOG_AGE_MIN}m (watching; stall threshold ${STALL_MIN}m)"
    fi
    # --- (b) 429 / crash markers in the log --------------------------------
    # Match a real throttle, NOT the Remotion frame counter: the render log
    # contains "Rendered 429/2633" as a frame count, which a bare "429"
    # grep would mistake for an HTTP 429. The pattern and the whole-log
    # check live in throttle.sh (B15) — a 429 more than 200 lines back
    # used to be missed, and the two scripts disagreed on the pattern.
    if log_has_throttle "$CURRENT_LOG"; then
      if [ -z "$LAST_429" ] || [ "$LAST_429" = "null" ]; then
        # First sighting of this throttle: record it (the one sanctioned
        # write this check makes) so the driver's backoff can be measured
        # from now. Previously last_429_at was never set anywhere, so the
        # backoff was dead code.
        NEW_429=$(date -Is)
        # B16: per-call mktemp file, never the shared "$STATE.tmp" the
        # driver also writes, and refresh the top-level .updated_at.
        STATE_TMP=$(mktemp "$STATE.tmp.XXXXXX") || STATE_TMP=""
        if [ -n "$STATE_TMP" ] && jq --arg ts "$NEW_429" '.current.last_429_at = $ts | .updated_at = $ts' "$STATE" > "$STATE_TMP" && mv "$STATE_TMP" "$STATE"; then
          LAST_429="$NEW_429"
          note "Azure 429 marker first seen — recorded last_429_at=$NEW_429 (backoff ${BACKOFF_MIN}m)"
        else
          [ -n "$STATE_TMP" ] && rm -f "$STATE_TMP"
          warning "log mentions 429/throttle but could not record last_429_at — treat as backoff"
        fi
      fi
      if [ -n "$LAST_429" ] && [ "$LAST_429" != "null" ]; then
        BACKOFF_END=$(date -d "$LAST_429 + $BACKOFF_MIN minutes" +%s 2>/dev/null || echo 0)
        if [ "$TS" -lt "$BACKOFF_END" ]; then
          REMAIN=$(( (BACKOFF_END - TS) / 60 ))
          warning "Azure 429 backoff active: ${REMAIN}m remaining (do NOT resume TTS yet)"
        else
          warning "Azure 429 backoff expired — safe to --resume the prep"
        fi
      fi
    fi
    if tail -5 "$CURRENT_LOG" 2>/dev/null | grep -q "FATAL\|Unhandled exception\|OutOfMemoryException" ; then
      step_problem "log tail shows a fatal/crash marker — driver should recover from the last checkpoint"
    fi
  else
    warning "current step log missing or unreadable: ${CURRENT_LOG:-none}"
  fi
fi

# --- (b) failure count -------------------------------------------------------
if [ "$FAILURES" -ge 3 ]; then
  problem "current job has $FAILURES failures (>= 3) — escalate to Rishi, do NOT keep retrying"
fi

# --- (c) external services ---------------------------------------------------
# Azure TTS: a real read-only probe (voices list). Expect HTTP 200.
# This is the only probe that uses a stored key, and it is a harmless read.
AZURE_HELPER=~/workspace/custom-connectors/custom.azure-speech/bin/azure_tts.py
if [ -f "$AZURE_HELPER" ]; then
  if python3 "$AZURE_HELPER" voices --region eastus >/dev/null 2>&1; then
    note "Azure TTS reachable (voices list OK)"
  else
    warning "Azure TTS voices probe failed — TTS may be down or the key expired (prep/--retts will fail; renders are unaffected)"
  fi
else
  warning "azure_tts.py not found — cannot probe Azure TTS"
fi
# Groq / Cloudflare / Pexels / YouTube API: endpoint reachability only.
# B22: only a genuine 2xx success counts as "reachable" — a 403 (dead key),
# 429 (throttled) or 500 used to be reported as reachable, which hid the
# two most likely prep killers until a prep died. The curl exit code is
# checked separately: previously a failed curl printed "000" AND the
# fallback echoed "000", producing the "HTTP 000000" artefact in the log.
# Quota is NOT probed here: no YouTube/Cloudflare/Azure quota endpoint
# reports remaining quota, and any authenticated probe would burn quota
# and need a key plumbed into this shell script — the uploader's
# per-run quota estimate (B31) is the quota signal instead.
for svc in groq cloudflare pexels youtube_api; do
  url=$(jq -r ".services.$svc.ping_url" "$CONFIG")
  code=$(curl -s -o /dev/null -w "%{http_code}" --max-time 15 "$url" 2>/dev/null)
  if [ $? -ne 0 ] || [ -z "$code" ]; then code="000"; fi
  case "$code" in
    2*) note "$svc endpoint reachable (HTTP $code)" ;;
    000) warning "$svc endpoint unreachable (curl failed) — prep may fail; renders unaffected" ;;
    *) warning "$svc endpoint returned HTTP $code (not a success) — treating as unreachable; prep may fail; renders unaffected" ;;
  esac
done

# --- write health.json -------------------------------------------------------
PROBLEMS_JSON=$(printf '%s\n' "${PROBLEMS[@]}" | jq -R . | jq -s . 2>/dev/null || echo '[]')
WARNINGS_JSON=$(printf '%s\n' "${WARNINGS[@]}" | jq -R . | jq -s . 2>/dev/null || echo '[]')
[ "${#PROBLEMS[@]}" -eq 0 ] && PROBLEMS_JSON='[]'
[ "${#WARNINGS[@]}" -eq 0 ] && WARNINGS_JSON='[]'
jq -n --arg ts "$NOW" --arg status "$STATUS" \
  --arg job "$CURRENT_JOB" --arg phase "$CURRENT_PHASE" \
  --argjson disk_free_gb "$DISK_FREE_GB" \
  --argjson problems "$PROBLEMS_JSON" --argjson warnings "$WARNINGS_JSON" \
  '{timestamp: $ts, status: $status, current_job: $job, current_phase: $phase,
    disk_free_gb: $disk_free_gb, problems: $problems, warnings: $warnings}' > "$HEALTH"

note "healthcheck done: status=$STATUS problems=${#PROBLEMS[@]} warnings=${#WARNINGS[@]} disk_free=${DISK_FREE_GB}GB"
[ "$STATUS" = "critical" ] && exit 1
exit 0
