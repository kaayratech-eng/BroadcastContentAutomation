#!/bin/bash
# production/healthcheck.sh — read-only health check for the production loop.
# Runs every cron tick (10 min) and on demand. NEVER mutates jobs, NEVER
# launches or kills anything: it only reports. The driver (driver.sh) is
# the only component that acts on the findings.
#
# Checks:
#   (a) Is a production step currently running and making progress?
#       (process alive + its log advancing — the "stall" signal is log
#       silence, never absence of a process alone.)
#   (b) Did the last step stall, crash, or 429-throttle, and what is the
#       safe recovery (resume prep / relaunch chunk / back off TTS)?
#   (c) Are the external services reachable (Azure TTS voices probe,
#       Groq / Pollinations / Pexels endpoint reachability)?
#   (d) Disk space and output-folder sanity.
#
# Writes production/health.json (a snapshot) and appends one line per
# check to production/driver.log. Exit 0 = healthy or only warnings;
# exit 1 = a critical problem (disk critical, current step crashed with
# no recovery path, or a service the CURRENT step needs is down).
set -u
PROD=~/workspace/BroadcastContentAutomation/production
STATE="$PROD/state.json"
LOG=~/workspace/tmp/production-driver.log
HEALTH="$PROD/health.json"
CONFIG="$PROD/config.json"
NOW=$(date -Is)
TS=$(date +%s)

note() { echo "$(date -Is) [healthcheck] $*"; }

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

# --- (d) disk space + output sanity -----------------------------------------
DISK_FREE_GB=$(df --output=avail -BG ~/workspace | tail -1 | tr -dc '0-9')
if [ "$DISK_FREE_GB" -lt "$DISK_CRIT" ]; then
  problem "disk free ${DISK_FREE_GB}GB < critical ${DISK_CRIT}GB — stop starting new work"
elif [ "$DISK_FREE_GB" -lt "$DISK_WARN" ]; then
  warning "disk free ${DISK_FREE_GB}GB < warn ${DISK_WARN}GB"
fi
VIDEOS_DIR=$(jq -r '.videos_dir' "$CONFIG")
if [ ! -d "$VIDEOS_DIR" ]; then
  problem "videos dir missing: $VIDEOS_DIR"
fi
for profile in chronicleandchaos gigglegarden; do
  [ -d "$VIDEOS_DIR/$profile" ] || warning "videos dir missing profile subdir: $profile"
done

# --- (a) current step alive + log advancing ---------------------------------
if [ -n "$CURRENT_JOB" ] && [ "$CURRENT_PHASE" != "idle" ] && [ "$CURRENT_PHASE" != "done" ]; then
  if [ -n "$CURRENT_PID" ] && [ "$CURRENT_PID" != "null" ]; then
    if kill -0 "$CURRENT_PID" 2>/dev/null; then
      note "current step alive: pid $CURRENT_PID ($CURRENT_PHASE, $CURRENT_JOB)"
    else
      problem "current step process $CURRENT_PID is DEAD (phase $CURRENT_PHASE, job $CURRENT_JOB) — driver should recover (resume/relaunch)"
    fi
  else
    note "current step has no pid recorded (phase $CURRENT_PHASE, job $CURRENT_JOB) — monitor-only or not yet launched"
  fi
  if [ -n "$CURRENT_LOG" ] && [ "$CURRENT_LOG" != "null" ] && [ -f "$CURRENT_LOG" ]; then
    LOG_MTIME=$(stat -c %Y "$CURRENT_LOG" 2>/dev/null || echo 0)
    LOG_AGE_MIN=$(( (TS - LOG_MTIME) / 60 ))
    if [ "$LOG_AGE_MIN" -ge "$STALL_MIN" ]; then
      if [ "$DRIVER_OWNED" = "true" ]; then
        problem "current step log silent ${LOG_AGE_MIN}m (>= ${STALL_MIN}m stall threshold) — driver should kill + retry the step"
      else
        warning "monitor-only step log silent ${LOG_AGE_MIN}m (job $CURRENT_JOB) — do NOT kill; it was not launched by the driver"
      fi
    elif [ "$LOG_AGE_MIN" -ge "$LOG_STALL_MIN" ]; then
      warning "current step log silent ${LOG_AGE_MIN}m (watching; stall threshold ${STALL_MIN}m)"
    fi
    # --- (b) 429 / crash markers in the log --------------------------------
    # Match a real throttle, NOT the Remotion frame counter: the render log
    # contains "Rendered 429/2633" as a frame count, which a bare "429"
    # grep would mistake for an HTTP 429.
    if tail -200 "$CURRENT_LOG" 2>/dev/null | grep -q "HTTP 429\|Too Many Requests\|Downstream Service Throttled\|prepayment credits are depleted"; then
      if [ -n "$LAST_429" ] && [ "$LAST_429" != "null" ]; then
        BACKOFF_END=$(date -d "$LAST_429 + $BACKOFF_MIN minutes" +%s 2>/dev/null || echo 0)
        if [ "$TS" -lt "$BACKOFF_END" ]; then
          REMAIN=$(( (BACKOFF_END - TS) / 60 ))
          warning "Azure 429 backoff active: ${REMAIN}m remaining (do NOT resume TTS yet)"
        else
          warning "Azure 429 backoff expired — safe to --resume the prep"
        fi
      else
        warning "log mentions 429/throttle but no last_429_at in state — treat as backoff"
      fi
    fi
    if tail -5 "$CURRENT_LOG" 2>/dev/null | grep -q "FATAL\|Unhandled exception\|OutOfMemoryException" ; then
      problem "log tail shows a fatal/crash marker — driver should recover from the last checkpoint"
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
# Groq / Pollinations / Pexels: endpoint reachability only (any HTTP
# response, even 401, proves the endpoint is up). No quota is burned.
for svc in groq pollinations pexels; do
  url=$(jq -r ".services.$svc.ping_url" "$CONFIG")
  code=$(curl -s -o /dev/null -w "%{http_code}" --max-time 15 "$url" 2>/dev/null || echo "000")
  if [ "$code" = "000" ]; then
    warning "$svc endpoint unreachable (curl failed) — prep may fail; renders unaffected"
  else
    note "$svc endpoint reachable (HTTP $code)"
  fi
done

# --- write health.json -------------------------------------------------------
PROBLEMS_JSON=$(printf '%s\n' "${PROBLEMS[@]}" | jq -R . | jq -s . 2>/dev/null || echo '[]')
WARNINGS_JSON=$(printf '%s\n' "${WARNINGS[@]}" | jq -R . | jq -s . 2>/dev/null || echo '[]')
# jq -s on an empty input gives []; handle the empty-array case explicitly.
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
