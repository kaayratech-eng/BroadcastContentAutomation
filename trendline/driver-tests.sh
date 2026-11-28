#!/bin/bash
# driver-tests.sh — tests for trendline/driver.sh logging behaviour.
#
# L4 (save_state failure), L6 (exit codes), L36–L41 (driver failure
# lines). Runs the changed helpers — extracted verbatim from driver.sh,
# the same pattern Item6Tests uses for shell fixes — against scratch
# dirs; never touches the live state.json, the live log, or the live
# lock. Exit 0 = all pass. Run: bash trendline/driver-tests.sh
set -u
HERE=$(cd "$(dirname "$0")" && pwd)
PASS=0
FAIL=0
ok() { PASS=$((PASS + 1)); echo "PASS $1"; }
fail() { FAIL=$((FAIL + 1)); echo "FAIL $1: $2"; }

# extract_fn <name> <src> <out> — append the named function, verbatim,
# from a shell script to a file. Handles one-line functions too.
extract_fn() {
    local name="$1" src="$2" out="$3"
    local start end line
    start=$(grep -n "^${name}() {" "$src" | head -1 | cut -d: -f1)
    [ -n "$start" ] || { echo "extract_fn: $name not found in $src" >&2; return 1; }
    line=$(sed -n "${start}p" "$src")
    case "$line" in
        *"}") end="$start" ;;
        *) end=$(awk -v s="$start" 'NR > s && /^}$/ { print NR; exit }' "$src") ;;
    esac
    [ -n "$end" ] || { echo "extract_fn: end of $name not found" >&2; return 1; }
    sed -n "${start},${end}p" "$src" >> "$out"
}

# ------------------------------------------------------------------
# L4 — SaveStateFailure_LogsCriticalAndAbortsTick
# ------------------------------------------------------------------
test_save_state_failure() {
    local tmp state logf logdir helpers
    tmp=$(mktemp -d)
    mkdir -p "$tmp/log"
    state="$tmp/state.json"
    logf="$tmp/log/driver.log"
    helpers="$tmp/helpers.sh"
    printf '%s' '{"current":{"phase":"idle","job":null,"failures":0}}' > "$state"
    : > "$helpers"
    extract_fn log "$HERE/driver.sh" "$helpers" || { fail "SaveStateFailure_LogsCriticalAndAbortsTick" "extract log failed"; rm -rf "$tmp"; return; }
    extract_fn save_state "$HERE/driver.sh" "$helpers" || { fail "SaveStateFailure_LogsCriticalAndAbortsTick" "extract save_state failed"; rm -rf "$tmp"; return; }
    extract_fn set_phase "$HERE/driver.sh" "$helpers" || { fail "SaveStateFailure_LogsCriticalAndAbortsTick" "extract set_phase failed"; rm -rf "$tmp"; return; }

    # Happy path: the write succeeds and the phase advances.
    (
        export STATE="$state" LOG="$logf" JOB_ID="job-20261126-test" PHASE="idle"
        # shellcheck disable=SC1090
        source "$helpers"
        save_state '.current.failures = 1'
        [ $? -eq 0 ] || { echo "save_state rc != 0 on happy path"; exit 1; }
        set_phase "prep" "null" "null"
        [ "$(jq -r '.current.phase' "$state")" = "prep" ] || { echo "phase did not advance on happy path"; exit 1; }
    ) || { fail "SaveStateFailure_LogsCriticalAndAbortsTick" "happy path failed"; rm -rf "$tmp"; return; }

    # Failure paths. Note: this shell runs as root, so chmod-based
    # "unwritable" does not fail mktemp — instead the two real failure
    # branches are exercised: (a) the state dir does not exist, so mktemp
    # fails; (b) state.json is corrupt JSON, so jq fails. Both must log
    # CRITICAL, return 1, and make set_phase abort the tick with no
    # phase advance.
    local before
    before=$(cat "$state")

    # (a) mktemp failure: STATE in a non-existent directory.
    (
        export STATE="$tmp/no-such-dir/state.json" LOG="$logf" JOB_ID="job-20261126-test" PHASE="idle"
        # shellcheck disable=SC1090
        source "$helpers"
        save_state '.current.failures = 9'
        [ $? -eq 1 ] || { echo "save_state rc != 1 on mktemp failure"; exit 1; }
        grep -q "CRITICAL: state.json write failed" "$logf" || { echo "no CRITICAL line in log (mktemp)"; exit 1; }
        grep -q "job-20261126-test" "$logf" || { echo "CRITICAL line lacks job id (mktemp)"; exit 1; }
        grep -q "phase idle" "$logf" || { echo "CRITICAL line lacks phase (mktemp)"; exit 1; }
    )
    local rc1=$?
    (
        export STATE="$tmp/no-such-dir/state.json" LOG="$logf" JOB_ID="job-20261126-test" PHASE="idle"
        # shellcheck disable=SC1090
        source "$helpers"
        set_phase "prep" "null" "null"
        echo "set_phase returned instead of aborting (mktemp)"
        exit 2
    )
    local rc2=$?
    [ $rc1 -eq 0 ] || { fail "SaveStateFailure_LogsCriticalAndAbortsTick" "mktemp failure path rc=$rc1"; rm -rf "$tmp"; return; }
    [ $rc2 -eq 1 ] || { fail "SaveStateFailure_LogsCriticalAndAbortsTick" "set_phase did not abort the tick on mktemp failure (rc=$rc2)"; rm -rf "$tmp"; return; }
    [ "$(cat "$state")" = "$before" ] || { fail "SaveStateFailure_LogsCriticalAndAbortsTick" "state file changed despite failed write (mktemp)"; rm -rf "$tmp"; return; }

    # (b) jq failure: corrupt state.json.
    printf '%s' 'not json {' > "$state"
    (
        export STATE="$state" LOG="$logf" JOB_ID="job-20261126-test" PHASE="idle"
        # shellcheck disable=SC1090
        source "$helpers"
        save_state '.current.failures = 9'
        [ $? -eq 1 ] || { echo "save_state rc != 1 on jq failure"; exit 1; }
        grep -q "CRITICAL: state.json write failed" "$logf" || { echo "no CRITICAL line in log (jq)"; exit 1; }
    )
    local rc3=$?
    (
        export STATE="$state" LOG="$logf" JOB_ID="job-20261126-test" PHASE="idle"
        # shellcheck disable=SC1090
        source "$helpers"
        set_phase "prep" "null" "null"
        echo "set_phase returned instead of aborting (jq)"
        exit 2
    )
    local rc4=$?
    [ $rc3 -eq 0 ] || { fail "SaveStateFailure_LogsCriticalAndAbortsTick" "jq failure path rc=$rc3"; rm -rf "$tmp"; return; }
    [ $rc4 -eq 1 ] || { fail "SaveStateFailure_LogsCriticalAndAbortsTick" "set_phase did not abort the tick on jq failure (rc=$rc4)"; rm -rf "$tmp"; return; }
    [ "$(cat "$state")" = "not json {" ] || { fail "SaveStateFailure_LogsCriticalAndAbortsTick" "state file changed despite failed write (jq)"; rm -rf "$tmp"; return; }

    ok "SaveStateFailure_LogsCriticalAndAbortsTick"
    rm -rf "$tmp"
}

test_save_state_failure

# ------------------------------------------------------------------
# L6 — EscalatedTick_ExitsNonZero / HealthcheckCritical_IsNotMasked
# ------------------------------------------------------------------
# Runs the real driver.sh in a sandbox (TRENDLINE_DIR/TRENDLINE_CONFIG/
# TRENDLINE_STATE pointed at temp files; healthcheck.sh stubbed). The
# escalated tick exits before any phase dispatch, so no real work runs.
sandbox_driver_env() { # $1 = dir; writes config.json + throttle stub
    local dir="$1"
    cat > "$dir/config.json" <<JSON
{
  "driver_log": "$dir/driver.log",
  "protected_job": "job-20261121-085638",
  "health": {"max_consecutive_failures_before_escalation": 3, "disk_critical_gb": 1},
  "tts": {"throttle_backoff_minutes": 45},
  "render": {"chunk_log_silence_kill_minutes": 30, "max_assemble_retries": 2, "max_chunk_retries": 3},
  "laptop": {"enabled": false, "vm_render_fallback_enabled": false}
}
JSON
    : > "$dir/throttle.sh"
}

test_escalated_tick_exits_nonzero() {
    local tmp rc
    tmp=$(mktemp -d)
    sandbox_driver_env "$tmp"
    printf '%s\n' '#!/bin/bash' 'exit 0' > "$tmp/healthcheck.sh"
    chmod +x "$tmp/healthcheck.sh"
    cat > "$tmp/state.json" <<JSON
{"paused": false, "current": {"job": "job-20261126-test", "phase": "idle", "pid": null, "pgid": null, "log": null, "driver_owned": false, "failures": 3, "auto_resets": 2, "auto_reset_progress": "phase=idle nojobdir"}}
JSON
    TRENDLINE_DIR="$tmp" TRENDLINE_CONFIG="$tmp/config.json" TRENDLINE_STATE="$tmp/state.json" TRENDLINE_DRY_RUN=1 bash "$HERE/driver.sh" > "$tmp/out.txt" 2>&1
    rc=$?
    [ $rc -ne 0 ] || { fail "EscalatedTick_ExitsNonZero" "driver exited 0 on an escalated stop"; rm -rf "$tmp"; return; }
    grep -q "ESCALATED" "$tmp/driver.log" || { fail "EscalatedTick_ExitsNonZero" "no ESCALATED line in driver log"; rm -rf "$tmp"; return; }
    ok "EscalatedTick_ExitsNonZero"
    rm -rf "$tmp"
}

test_healthcheck_critical_is_not_masked() {
    local tmp rc
    tmp=$(mktemp -d)
    sandbox_driver_env "$tmp"
    printf '%s\n' '#!/bin/bash' 'echo "stub healthcheck: critical"' 'exit 1' > "$tmp/healthcheck.sh"
    chmod +x "$tmp/healthcheck.sh"
    cat > "$tmp/state.json" <<JSON
{"paused": true, "pause_reason": "test pause", "current": {"job": null, "phase": "idle", "pid": null, "pgid": null, "log": null, "driver_owned": false, "failures": 0, "auto_resets": 0}}
JSON
    TRENDLINE_DIR="$tmp" TRENDLINE_CONFIG="$tmp/config.json" TRENDLINE_STATE="$tmp/state.json" TRENDLINE_DRY_RUN=1 bash "$HERE/driver.sh" > "$tmp/out.txt" 2>&1
    rc=$?
    grep -q "healthcheck critical (rc 1)" "$tmp/driver.log" || { fail "HealthcheckCritical_IsNotMasked" "driver log has no healthcheck-critical line (rc=$rc)"; rm -rf "$tmp"; return; }
    ok "HealthcheckCritical_IsNotMasked"
    rm -rf "$tmp"
}

test_escalated_tick_exits_nonzero
test_healthcheck_critical_is_not_masked

# ------------------------------------------------------------------
# L36 — BumpFailure_LogsJobPhaseReasonAndNewCount
# ------------------------------------------------------------------
test_bump_failure_logs_job_phase_reason_and_new_count() {
    local tmp state logf helpers
    tmp=$(mktemp -d)
    mkdir -p "$tmp/log"
    state="$tmp/state.json"
    logf="$tmp/log/driver.log"
    helpers="$tmp/helpers.sh"
    printf '%s' '{"current":{"phase":"prep","job":"job-20261126-test","failures":0}}' > "$state"
    : > "$helpers"
    extract_fn log "$HERE/driver.sh" "$helpers" || { fail "BumpFailure_LogsJobPhaseReasonAndNewCount" "extract log failed"; rm -rf "$tmp"; return; }
    extract_fn save_state "$HERE/driver.sh" "$helpers" || { fail "BumpFailure_LogsJobPhaseReasonAndNewCount" "extract save_state failed"; rm -rf "$tmp"; return; }
    extract_fn bump_failure "$HERE/driver.sh" "$helpers" || { fail "BumpFailure_LogsJobPhaseReasonAndNewCount" "extract bump_failure failed"; rm -rf "$tmp"; return; }
    (
        export STATE="$state" LOG="$logf" JOB_ID="job-20261126-test" PHASE="prep" MAX_FAILURES=3
        # shellcheck disable=SC1090
        source "$helpers"
        bump_failure "assemble died, no props"
        [ $? -eq 0 ] || { echo "bump_failure rc != 0"; exit 1; }
        [ "$(jq -r '.current.failures' "$state")" = "1" ] || { echo "failures did not increment to 1"; exit 1; }
        grep -q "failure job-20261126-test (prep): assemble died, no props — failures now 1/3" "$logf" || { echo "no bump log line with job/phase/reason/count:"; cat "$logf"; exit 1; }
    ) || { fail "BumpFailure_LogsJobPhaseReasonAndNewCount" "bump did not log job, phase, reason and new count"; rm -rf "$tmp"; return; }
    ok "BumpFailure_LogsJobPhaseReasonAndNewCount"
    rm -rf "$tmp"
}

test_bump_failure_logs_job_phase_reason_and_new_count

# ------------------------------------------------------------------
# L37 — AutoReset_LogsFailureCauseAndClearedValues
# ------------------------------------------------------------------
# Runs the real driver.sh in a sandbox (same harness as the L6 tests).
# The job dir must live under the repo's work/ tree (driver.sh derives
# it from REPO, not TRENDLINE_DIR), so the test creates a scratch job
# dir there and removes it on return.
test_auto_reset_logs_failure_cause_and_cleared_values() {
    local tmp rc jobdir
    tmp=$(mktemp -d)
    jobdir="$HOME/workspace/BroadcastContentAutomation/work/trendlinenow/job-20261126-test"
    trap 'rm -rf "$jobdir" "$tmp"' RETURN
    mkdir -p "$jobdir"
    sandbox_driver_env "$tmp"
    printf '%s\n' '#!/bin/bash' 'exit 0' > "$tmp/healthcheck.sh"
    chmod +x "$tmp/healthcheck.sh"
    printf '%s\n' 'chunk render failed at scene 12' 'ffmpeg failed (exit 1): boom' > "$tmp/step.log"
    cat > "$tmp/state.json" <<JSON
{"paused": false, "current": {"job": "job-20261126-test", "phase": "render_assemble", "pid": null, "pgid": null, "log": "$tmp/step.log", "driver_owned": false, "failures": 3, "auto_resets": 0, "chunk_retries": 2, "assemble_retries": 1}}
JSON
    TRENDLINE_DIR="$tmp" TRENDLINE_CONFIG="$tmp/config.json" TRENDLINE_STATE="$tmp/state.json" TRENDLINE_DRY_RUN=1 bash "$HERE/driver.sh" > "$tmp/out.txt" 2>&1
    rc=$?
    local line
    line=$(grep "AUTO-RESET" "$tmp/driver.log" || true)
    [ -n "$line" ] || { fail "AutoReset_LogsFailureCauseAndClearedValues" "no AUTO-RESET line in driver log (rc=$rc)"; cat "$tmp/driver.log" "$tmp/out.txt"; return; }
    case "$line" in *"phase render_assemble"*) ;; *) fail "AutoReset_LogsFailureCauseAndClearedValues" "AUTO-RESET line lacks phase: $line"; return;; esac
    case "$line" in *"chunk render failed at scene 12"*"ffmpeg failed (exit 1)"*) ;; *) fail "AutoReset_LogsFailureCauseAndClearedValues" "AUTO-RESET line lacks last step log tail: $line"; return;; esac
    case "$line" in *"cleared failures=3 chunk_retries=2 assemble_retries=1"*) ;; *) fail "AutoReset_LogsFailureCauseAndClearedValues" "AUTO-RESET line lacks cleared counts: $line"; return;; esac
    case "$line" in *"stale pid=none"*) ;; *) fail "AutoReset_LogsFailureCauseAndClearedValues" "AUTO-RESET line lacks stale pid: $line"; return;; esac
    case "$line" in *"killed 0 orphan processes (none"*) ;; *) fail "AutoReset_LogsFailureCauseAndClearedValues" "AUTO-RESET line lacks orphan outcome: $line"; return;; esac
    ok "AutoReset_LogsFailureCauseAndClearedValues"
}

test_auto_reset_logs_failure_cause_and_cleared_values

# ------------------------------------------------------------------
# L76 — PauseUnpause_BothLogged
# ------------------------------------------------------------------
test_pause_unpause_both_logged() {
    local tmp rc
    tmp=$(mktemp -d)
    trap 'rm -rf "$tmp"' RETURN
    sandbox_driver_env "$tmp"
    printf '%s\n' '#!/bin/bash' 'exit 0' > "$tmp/healthcheck.sh"
    chmod +x "$tmp/healthcheck.sh"
    cat > "$tmp/state.json" <<JSON
{"paused": false, "pause_reason": "", "current": {"job": "job-20261126-test", "phase": "prep", "pid": null, "pgid": null, "log": null, "driver_owned": false, "failures": 0, "auto_resets": 0}}
JSON
    TRENDLINE_DIR="$tmp" TRENDLINE_CONFIG="$tmp/config.json" TRENDLINE_STATE="$tmp/state.json" bash "$HERE/driver.sh" pause --reason "test pause for review" > "$tmp/out.txt" 2>&1
    rc=$?
    [ $rc -eq 0 ] || { fail "PauseUnpause_BothLogged" "pause exited $rc"; cat "$tmp/out.txt"; return; }
    grep -q "driver paused: job job-20261126-test phase prep, reason: test pause for review" "$tmp/driver.log" || { fail "PauseUnpause_BothLogged" "no driver-paused line with job/phase/reason"; cat "$tmp/driver.log"; return; }
    [ "$(jq -r '.paused' "$tmp/state.json")" = "true" ] || { fail "PauseUnpause_BothLogged" "paused not true after pause"; return; }
    [ "$(jq -r '.pause_reason' "$tmp/state.json")" = "test pause for review" ] || { fail "PauseUnpause_BothLogged" "pause_reason does not describe the current pause"; return; }
    TRENDLINE_DIR="$tmp" TRENDLINE_CONFIG="$tmp/config.json" TRENDLINE_STATE="$tmp/state.json" bash "$HERE/driver.sh" unpause --reason "test unpause, go ahead" > "$tmp/out2.txt" 2>&1
    rc=$?
    [ $rc -eq 0 ] || { fail "PauseUnpause_BothLogged" "unpause exited $rc"; cat "$tmp/out2.txt"; return; }
    grep -q "driver unpaused: test unpause, go ahead" "$tmp/driver.log" || { fail "PauseUnpause_BothLogged" "no driver-unpaused line with reason"; cat "$tmp/driver.log"; return; }
    [ "$(jq -r '.paused' "$tmp/state.json")" = "false" ] || { fail "PauseUnpause_BothLogged" "paused not false after unpause"; return; }
    case "$(jq -r '.pause_reason' "$tmp/state.json")" in
        "Unpaused "*"test unpause, go ahead") ;;
        *) fail "PauseUnpause_BothLogged" "pause_reason after unpause lacks the Unpaused stamp + reason: $(jq -r '.pause_reason' "$tmp/state.json")"; return ;;
    esac
    ok "PauseUnpause_BothLogged"
}

test_pause_unpause_both_logged

# ------------------------------------------------------------------
# L38 — ManualReset_LogsActorOldCountsAndReason
# ------------------------------------------------------------------
test_manual_reset_logs_actor_old_counts_and_reason() {
    local tmp rc
    tmp=$(mktemp -d)
    trap 'rm -rf "$tmp"' RETURN
    sandbox_driver_env "$tmp"
    printf '%s\n' '#!/bin/bash' 'exit 0' > "$tmp/healthcheck.sh"
    chmod +x "$tmp/healthcheck.sh"
    cat > "$tmp/state.json" <<JSON
{"paused": false, "current": {"job": "job-20261126-test", "phase": "prep", "pid": null, "pgid": null, "log": null, "driver_owned": false, "failures": 2, "auto_resets": 1}}
JSON
    TRENDLINE_DIR="$tmp" TRENDLINE_CONFIG="$tmp/config.json" TRENDLINE_STATE="$tmp/state.json" bash "$HERE/driver.sh" reset-failures --job job-20261126-test --reason "test manual reset" > "$tmp/out.txt" 2>&1
    rc=$?
    [ $rc -eq 0 ] || { fail "ManualReset_LogsActorOldCountsAndReason" "reset-failures exited $rc"; cat "$tmp/out.txt"; return; }
    grep -q "MANUAL RESET by .*: job job-20261126-test failures 2→0 auto_resets 1→0: test manual reset" "$tmp/driver.log" || { fail "ManualReset_LogsActorOldCountsAndReason" "no MANUAL RESET line with actor/old counts/reason in driver log"; cat "$tmp/driver.log" "$tmp/out.txt"; return; }
    [ "$(jq -r '.current.failures' "$tmp/state.json")" = "0" ] || { fail "ManualReset_LogsActorOldCountsAndReason" "failures not reset to 0"; return; }
    [ "$(jq -r '.current.auto_resets' "$tmp/state.json")" = "0" ] || { fail "ManualReset_LogsActorOldCountsAndReason" "auto_resets not reset to 0"; return; }
    ok "ManualReset_LogsActorOldCountsAndReason"
}

test_manual_reset_logs_actor_old_counts_and_reason

# ------------------------------------------------------------------
# L39 — GovernorDenial_LogsReasonCountCapAndReset
# ------------------------------------------------------------------
test_governor_denial_logs_reason_count_cap_and_reset() {
    local tmp rc
    tmp=$(mktemp -d)
    trap 'rm -rf "$tmp"' RETURN
    sandbox_driver_env "$tmp"
    printf '%s\n' '#!/bin/bash' 'exit 0' > "$tmp/healthcheck.sh"
    chmod +x "$tmp/healthcheck.sh"
    cat > "$tmp/governor-check.sh" <<'EOS'
#!/bin/bash
echo '{"allowed": false, "reason": "daily video cap reached (2/2)", "resets_at": "2026-11-27T00:00:00-05:00", "videos_today": 2, "cap": 2}'
exit 1
EOS
    chmod +x "$tmp/governor-check.sh"
    cat > "$tmp/state.json" <<JSON
{"paused": false, "current": {"job": null, "phase": "idle", "pid": null, "pgid": null, "log": null, "driver_owned": false, "failures": 0, "auto_resets": 0}}
JSON
    TRENDLINE_DIR="$tmp" TRENDLINE_CONFIG="$tmp/config.json" TRENDLINE_STATE="$tmp/state.json" TRENDLINE_DRY_RUN=1 bash "$HERE/driver.sh" > "$tmp/out.txt" 2>&1
    rc=$?
    [ $rc -eq 0 ] || { fail "GovernorDenial_LogsReasonCountCapAndReset" "driver exited $rc on a governor denial (should wait, exit 0)"; cat "$tmp/out.txt"; return; }
    grep -q "governor: denied — daily video cap reached (2/2) (videos 2/2, resets 2026-11-27T00:00:00-05:00); not starting a new prep, will retry next tick" "$tmp/driver.log" || { fail "GovernorDenial_LogsReasonCountCapAndReset" "no governor-denied line with reason/count/reset in driver log"; cat "$tmp/driver.log" "$tmp/out.txt"; return; }
    ok "GovernorDenial_LogsReasonCountCapAndReset"
}

test_governor_denial_logs_reason_count_cap_and_reset

# ------------------------------------------------------------------
# L41 — AssembleDeath_LogIncludesExitCodeAndTail
# ------------------------------------------------------------------
test_assemble_death_log_includes_exit_code_and_tail() {
    local tmp rc jobdir
    tmp=$(mktemp -d)
    jobdir="$HOME/workspace/BroadcastContentAutomation/work/trendlinenow/job-20261126-test"
    trap 'rm -rf "$jobdir" "$tmp"' RETURN
    mkdir -p "$jobdir"
    sandbox_driver_env "$tmp"
    printf '%s\n' '#!/bin/bash' 'exit 0' > "$tmp/healthcheck.sh"
    chmod +x "$tmp/healthcheck.sh"
    printf '%s\n' 'Rendered 129/2430' 'ffmpeg failed (exit 137): out of memory' 'remotion render aborted' > "$tmp/step.log"
    cat > "$tmp/state.json" <<JSON
{"paused": false, "current": {"job": "job-20261126-test", "phase": "render_assemble", "pid": null, "pgid": null, "log": "$tmp/step.log", "driver_owned": false, "failures": 0, "auto_resets": 0, "assemble_retries": 0}}
JSON
    TRENDLINE_DIR="$tmp" TRENDLINE_CONFIG="$tmp/config.json" TRENDLINE_STATE="$tmp/state.json" TRENDLINE_DRY_RUN=1 bash "$HERE/driver.sh" > "$tmp/out.txt" 2>&1
    rc=$?
    local line
    line=$(grep "FATAL: --assemble" "$tmp/driver.log" || true)
    [ -n "$line" ] || { fail "AssembleDeath_LogIncludesExitCodeAndTail" "no assemble FATAL line in driver log (rc=$rc)"; cat "$tmp/driver.log" "$tmp/out.txt"; return; }
    case "$line" in *"exit code 137"*) ;; *) fail "AssembleDeath_LogIncludesExitCodeAndTail" "FATAL line lacks exit code 137: $line"; return;; esac
    case "$line" in *"ffmpeg failed (exit 137): out of memory"*"remotion render aborted"*) ;; *) fail "AssembleDeath_LogIncludesExitCodeAndTail" "FATAL line lacks last log lines: $line"; return;; esac
    case "$line" in *"last render frame 129/2430"*) ;; *) fail "AssembleDeath_LogIncludesExitCodeAndTail" "FATAL line lost the last render frame: $line"; return;; esac
    ok "AssembleDeath_LogIncludesExitCodeAndTail"
}

test_assemble_death_log_includes_exit_code_and_tail

echo "driver-tests: $PASS passed, $FAIL failed"
[ "$FAIL" -eq 0 ]
