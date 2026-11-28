#!/bin/bash
# healthcheck-tests.sh — tests for trendline/healthcheck.sh probe behaviour.
#
# L15 (Unauthenticated401_IsNotAWarning, EndpointDown_StillWarns). Runs the
# real script against scratch config/state and a local stub server; never
# touches the live state.json, health.json, or the live log. Exit 0 = all
# pass. Run: bash trendline/healthcheck-tests.sh
set -u
HERE=$(cd "$(dirname "$0")" && pwd)
PASS=0
FAIL=0
ok() { PASS=$((PASS + 1)); echo "PASS $1"; }
fail() { FAIL=$((FAIL + 1)); echo "FAIL $1: $2"; }

# free_port — ask the OS for an unused localhost port.
free_port() {
  python3 -c 'import socket; s = socket.socket(); s.bind(("127.0.0.1", 0)); print(s.getsockname()[1]); s.close()'
}

# make_sandbox <dir> <port> — config + state + videos dir for one run.
# The four ping URLs point at the local stub (or a closed port when no
# server runs there). The current step is idle so only the probe checks
# can add warnings.
make_sandbox() {
  local dir="$1" port="$2"
  mkdir -p "$dir/videos/trendlinenow"
  cat > "$dir/config.json" <<EOF
{
  "driver_log": "$dir/driver.log",
  "videos_dir": "$dir/videos",
  "health": {"disk_warn_gb": 20, "disk_critical_gb": 10, "render_log_silence_warn_minutes": 10},
  "render": {"chunk_log_silence_kill_minutes": 30},
  "tts": {"throttle_backoff_minutes": 45},
  "services": {
    "groq": {"ping_url": "http://127.0.0.1:$port/groq"},
    "cloudflare": {"ping_url": "http://127.0.0.1:$port/cloudflare"},
    "pexels": {"ping_url": "http://127.0.0.1:$port/pexels"},
    "youtube_api": {"ping_url": "http://127.0.0.1:$port/youtube_api"}
  }
}
EOF
  cat > "$dir/state.json" <<'EOF'
{"current": {"job": "", "phase": "idle", "log": "", "pid": "", "driver_owned": false, "failures": 0, "last_429_at": ""}, "paused": true}
EOF
}

# Stub server: /groq and /pexels answer 401, /cloudflare and
# /youtube_api answer 403 — the live services' unauthenticated codes.
write_stub() {
  cat > "$1" <<'PYEOF'
import http.server, sys
port = int(sys.argv[1])
CODES = {"/groq": 401, "/cloudflare": 403, "/pexels": 401, "/youtube_api": 403}
class H(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        code = CODES.get(self.path, 401)
        self.send_response(code)
        self.send_header("Content-Length", "2")
        self.end_headers()
        self.wfile.write(b"{}")
    def log_message(self, *a):
        pass
http.server.HTTPServer(("127.0.0.1", port), H).serve_forever()
PYEOF
}

run_healthcheck() {
  local dir="$1"
  TRENDLINE_DIR="$dir" TRENDLINE_CONFIG="$dir/config.json" TRENDLINE_STATE="$dir/state.json" \
    bash "$HERE/healthcheck.sh"
}

# ------------------------------------------------------------------
# L15 — Unauthenticated401_IsNotAWarning
# ------------------------------------------------------------------
test_unauthenticated_401_is_not_a_warning() {
  local tmp port stub srv out
  tmp=$(mktemp -d)
  port=$(free_port)
  stub="$tmp/stub.py"
  write_stub "$stub"
  make_sandbox "$tmp" "$port"
  python3 "$stub" "$port" & srv=$!
  # Wait for the stub to accept connections (max ~5s).
  for _ in $(seq 1 50); do
    curl -s -o /dev/null --max-time 2 "http://127.0.0.1:$port/groq" && break
    sleep 0.1
  done
  out=$(run_healthcheck "$tmp" 2>&1)
  local rc=$?
  kill "$srv" 2>/dev/null; wait "$srv" 2>/dev/null
  [ "$rc" -eq 0 ] || { fail "Unauthenticated401_IsNotAWarning" "healthcheck exited $rc: $out"; rm -rf "$tmp"; return; }
  [ "$(jq -r '.status' "$tmp/health.json")" = "ok" ] \
    || { fail "Unauthenticated401_IsNotAWarning" "status is $(jq -r '.status' "$tmp/health.json"), warnings: $(jq -c '.warnings' "$tmp/health.json")"; rm -rf "$tmp"; return; }
  [ "$(jq -r '.warnings | length' "$tmp/health.json")" -eq 0 ] \
    || { fail "Unauthenticated401_IsNotAWarning" "warnings not empty: $(jq -c '.warnings' "$tmp/health.json")"; rm -rf "$tmp"; return; }
  for svc in groq cloudflare pexels youtube_api; do
    [ "$(jq -r ".probe_auth.$svc" "$tmp/health.json")" = "unauthenticated" ] \
      || { fail "Unauthenticated401_IsNotAWarning" "probe_auth.$svc is not unauthenticated: $(jq -c '.probe_auth' "$tmp/health.json")"; rm -rf "$tmp"; return; }
  done
  [ "$(printf '%s' "$out" | grep -c "auth required")" -eq 4 ] \
    || { fail "Unauthenticated401_IsNotAWarning" "expected 4 'auth required' notes, got: $out"; rm -rf "$tmp"; return; }
  rm -rf "$tmp"
  ok "Unauthenticated401_IsNotAWarning"
}

# ------------------------------------------------------------------
# L15 — EndpointDown_StillWarns
# ------------------------------------------------------------------
test_endpoint_down_still_warns() {
  local tmp port out
  tmp=$(mktemp -d)
  # No server on this port: every curl fails (connection refused).
  port=$(free_port)
  make_sandbox "$tmp" "$port"
  out=$(run_healthcheck "$tmp" 2>&1)
  local rc=$?
  [ "$rc" -eq 0 ] || { fail "EndpointDown_StillWarns" "healthcheck exited $rc: $out"; rm -rf "$tmp"; return; }
  [ "$(jq -r '.status' "$tmp/health.json")" = "warning" ] \
    || { fail "EndpointDown_StillWarns" "status is $(jq -r '.status' "$tmp/health.json"), expected warning"; rm -rf "$tmp"; return; }
  local warnings
  warnings=$(jq -r '.warnings | join("\n")' "$tmp/health.json")
  for svc in groq cloudflare pexels youtube_api; do
    printf '%s' "$warnings" | grep -q "$svc endpoint unreachable" \
      || { fail "EndpointDown_StillWarns" "no unreachable warning for $svc: $warnings"; rm -rf "$tmp"; return; }
  done
  rm -rf "$tmp"
  ok "EndpointDown_StillWarns"
}

# ------------------------------------------------------------------
# L16 — DeadStepTwice_BecomesCritical
# ------------------------------------------------------------------
test_dead_step_twice_becomes_critical() {
  local tmp port stub srv out rc dead_since_1
  tmp=$(mktemp -d)
  port=$(free_port)
  stub="$tmp/stub.py"
  write_stub "$stub"
  make_sandbox "$tmp" "$port"
  # Dead step: phase prep, pid that can never be alive, a fresh step log
  # (so only the dead pid fires, not log silence), 1 failure recorded.
  : > "$tmp/step.log"
  cat > "$tmp/state.json" <<EOF
{"current": {"job": "job-20261126-test", "phase": "prep", "log": "$tmp/step.log", "pid": 999999, "driver_owned": true, "failures": 1, "last_429_at": ""}, "paused": false}
EOF
  python3 "$stub" "$port" & srv=$!
  for _ in $(seq 1 50); do
    curl -s -o /dev/null --max-time 2 "http://127.0.0.1:$port/groq" && break
    sleep 0.1
  done
  # Run 1: first sighting — warning only, but dead_since is recorded and
  # the snapshot carries the triage fields.
  out=$(run_healthcheck "$tmp" 2>&1); rc=$?
  [ "$rc" -eq 0 ] || { fail "DeadStepTwice_BecomesCritical" "run 1 exited $rc: $out"; kill "$srv" 2>/dev/null; rm -rf "$tmp"; return; }
  [ "$(jq -r '.status' "$tmp/health.json")" = "warning" ] \
    || { fail "DeadStepTwice_BecomesCritical" "run 1 status is $(jq -r '.status' "$tmp/health.json"), expected warning"; kill "$srv" 2>/dev/null; rm -rf "$tmp"; return; }
  [ "$(jq -r '.problems | length' "$tmp/health.json")" -eq 0 ] \
    || { fail "DeadStepTwice_BecomesCritical" "run 1 already has problems: $(jq -c '.problems' "$tmp/health.json")"; kill "$srv" 2>/dev/null; rm -rf "$tmp"; return; }
  [ "$(jq -r '.failures' "$tmp/health.json")" -eq 1 ] \
    || { fail "DeadStepTwice_BecomesCritical" "run 1 failures is not 1: $(jq -c '.failures' "$tmp/health.json")"; kill "$srv" 2>/dev/null; rm -rf "$tmp"; return; }
  [ "$(jq -r '.current_log' "$tmp/health.json")" = "$tmp/step.log" ] \
    || { fail "DeadStepTwice_BecomesCritical" "run 1 current_log is not the step log: $(jq -c '.current_log' "$tmp/health.json")"; kill "$srv" 2>/dev/null; rm -rf "$tmp"; return; }
  dead_since_1=$(jq -r '.dead_since // empty' "$tmp/health.json")
  [ -n "$dead_since_1" ] || { fail "DeadStepTwice_BecomesCritical" "run 1 dead_since is empty"; kill "$srv" 2>/dev/null; rm -rf "$tmp"; return; }
  # Run 2: still dead — escalates to critical, same dead_since.
  out=$(run_healthcheck "$tmp" 2>&1); rc=$?
  kill "$srv" 2>/dev/null; wait "$srv" 2>/dev/null
  [ "$rc" -eq 1 ] || { fail "DeadStepTwice_BecomesCritical" "run 2 exited $rc (expected 1 for critical): $out"; rm -rf "$tmp"; return; }
  [ "$(jq -r '.status' "$tmp/health.json")" = "critical" ] \
    || { fail "DeadStepTwice_BecomesCritical" "run 2 status is $(jq -r '.status' "$tmp/health.json"), expected critical"; rm -rf "$tmp"; return; }
  jq -r '.problems | join("\n")' "$tmp/health.json" | grep -q "2nd consecutive" \
    || { fail "DeadStepTwice_BecomesCritical" "run 2 problems lack the escalation: $(jq -c '.problems' "$tmp/health.json")"; rm -rf "$tmp"; return; }
  [ "$(jq -r '.dead_since // empty' "$tmp/health.json")" = "$dead_since_1" ] \
    || { fail "DeadStepTwice_BecomesCritical" "run 2 dead_since changed: $(jq -r '.dead_since' "$tmp/health.json") vs $dead_since_1"; rm -rf "$tmp"; return; }
  [ "$(jq -r '.failures' "$tmp/health.json")" -eq 1 ] \
    || { fail "DeadStepTwice_BecomesCritical" "run 2 failures is not 1"; rm -rf "$tmp"; return; }
  [ "$(jq -r '.current_log' "$tmp/health.json")" = "$tmp/step.log" ] \
    || { fail "DeadStepTwice_BecomesCritical" "run 2 current_log is not the step log"; rm -rf "$tmp"; return; }
  rm -rf "$tmp"
  ok "DeadStepTwice_BecomesCritical"
}

test_unauthenticated_401_is_not_a_warning
test_endpoint_down_still_warns
test_dead_step_twice_becomes_critical

echo "healthcheck-tests: $PASS passed, $FAIL failed"
[ "$FAIL" -eq 0 ]
