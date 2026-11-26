#!/usr/bin/env bash
# trendline/laptop-render.sh — VM-side driver for laptop renders (Trendline Now).
#
# The Windows laptop (Hawkeye) is the ONLY renderer. The VM does code +
# orchestration only — never a render, never heavy compute. This helper is
# the VM side of that split: it ships a job package to the laptop, triggers
# the render there (detached, so it survives the SSH session ending AND a
# VM restart), polls for finished chunks, and pulls them back for the
# VM-side finish (concat + loudnorm + SRT + sidecar, which is light).
#
# Transport (see /opt/hatch/connectors/tailscale/CONNECTOR.md — follow it):
#   * The VM joins the tailnet as device "muse" via the built-in client
#     (/opt/hatch/bin/tailscale). Joining needs Rishi's approval; this
#     helper never runs `tailscale up` itself.
#   * The VM reaches the tailnet ONLY through the runtime tunnel proxy
#     (port 3130) via HTTP CONNECT. No direct networking to tailnet
#     addresses. Proxy credentials come from $HTTPS_PROXY at runtime and
#     are NEVER written to a file, a script, or a log.
#   * SSH uses ~/.ssh/id_ed25519_trendline (public key on the laptop's
#     authorized_keys) with BatchMode=yes (never a password prompt).
#   * The FIRST connection to the laptop's tailnet address needs Rishi's
#     approval; while it is pending the connection waits. This helper
#     bounds every network call with `timeout` so a driver tick never
#     hangs — a timeout during an approval wait exits 2 (WAITING, not a
#     failure) and the next tick retries after he approves.
#
# Why tar/scp and not rsync: stock Git for Windows does NOT ship rsync
# (verified 2026-11-23 — it must be installed separately from MSYS2), so
# the laptop cannot be assumed to have it. tar is in Git Bash and in
# Windows itself; scp ships with the Windows OpenSSH Server. Ship is a
# full package send (once per job, ~50 MB) and pull transfers only
# missing/invalid chunks (compared by name + validation), so nothing is
# re-shipped that does not need to be. If rsync is ever installed on the
# laptop, `has-rsync` (laptop-remote.sh) reports it and a future change
# can use it as a fast path — the state machine does not depend on it.
#
# State: per-job marker <job>/laptop-render.json on the VM (source of
# truth for what was shipped/triggered/pulled) plus a summary in
# state.json .current.laptop (written by the driver, not here). A VM
# restart loses nothing: the next tick re-reads the marker, re-polls the
# laptop, and resumes — never re-shipping finished chunks, never
# re-triggering a running render. The laptop enforces ONE renderer
# itself (render-chunks.sh mkdir lock, checked against the chunk log).
#
# Subcommands (all take --job <job-id>; trigger/poll/pull/step also take
# --orientation vertical|landscape):
#   test                          SSH echo + remote tool checks (first live test)
#   build-package --job <id>      assemble the shippable package in $TMPDIR
#   ship --job <id>               send the package (idempotent; never touches remote chunks)
#   trigger --job <id> --orientation <o>   start the render, detached (idempotent)
#   poll --job <id> --orientation <o>      one-line remote status
#   pull --job <id> --orientation <o>      bring finished chunks back + validate
#   step --job <id> --orientation <o>      one tick of the state machine (what the driver calls)
#   cleanup --job <id> --orientation <o>   remove the remote chunks dir (after verify; best-effort)
#
# Exit codes: 0 ok / waiting (render running, nothing to do), 1 error
# (the driver counts this as a failure), 2 unreachable or approval
# pending (the driver waits WITHOUT counting a failure).
set -u
REPO="${BROADCAST_REPO:-$HOME/workspace/BroadcastContentAutomation}"
TRENDLINE="$REPO/trendline"
PROD="$REPO/production"
CONFIG="${TRENDLINE_CONFIG:-$TRENDLINE/config.json}"
STATE="${TRENDLINE_STATE:-$TRENDLINE/state.json}"
HELPER_LOG_PREFIX="[laptop-render]"

log() { echo "$(date -Is) $HELPER_LOG_PREFIX $*"; }
err() { echo "$(date -Is) $HELPER_LOG_PREFIX ERROR: $*" >&2; }

# --- config: env > trendline/config.json laptop block > defaults ------------
LAPTOP_HOST="${LAPTOP_HOST:-$(jq -r '.laptop.host // "hawkeye"' "$CONFIG" 2>/dev/null || echo hawkeye)}"
LAPTOP_USER="${LAPTOP_USER:-$(jq -r '.laptop.user // "rishi"' "$CONFIG" 2>/dev/null || echo rishi)}"
LAPTOP_ROOT="${LAPTOP_ROOT:-$(jq -r '.laptop.root // "C:/trendline-render"' "$CONFIG" 2>/dev/null || echo "C:/trendline-render")}"
LAPTOP_IDENTITY="${LAPTOP_IDENTITY:-$(jq -r '.laptop.identity // "'"$HOME"'/.ssh/id_ed25519_trendline"' "$CONFIG" 2>/dev/null || echo "$HOME/.ssh/id_ed25519_trendline")}"
# LAPTOP_LOCAL_ROOT: local-test mode. When set, every "remote" operation
# runs against this local directory instead of SSH (no network, no
# Tailscale). Used by the dry-run harness; never set in production.
LAPTOP_LOCAL_ROOT="${LAPTOP_LOCAL_ROOT:-}"
if [ -n "$LAPTOP_LOCAL_ROOT" ] && [ ! -d "$LAPTOP_LOCAL_ROOT" ]; then
  err "LAPTOP_LOCAL_ROOT does not exist: $LAPTOP_LOCAL_ROOT"
  exit 2
fi
# LAPTOP_DRYRUN=1: print the commands instead of running them.
LAPTOP_DRYRUN="${LAPTOP_DRYRUN:-0}"

# --- tunnel proxy (from $HTTPS_PROXY at runtime; never logged) ---------------
# Prints shell assignments for the proxy host/user/pass (shlex-quoted).
# The tunnel proxy is the same host as the egress proxy, port 3130.
proxy_env() {
  python3 -c '
import os, shlex, urllib.parse
u = urllib.parse.urlparse(os.environ.get("HTTPS_PROXY", ""))
print("PROXY_HOST=" + shlex.quote(u.hostname or ""))
print("PROXY_USER=" + shlex.quote(urllib.parse.unquote(u.username or "")))
print("PROXY_PASS=" + shlex.quote(urllib.parse.unquote(u.password or "")))
'
}
SSH_OPTS=()
ssh_init() {
  [ -n "${SSH_OPTS_INIT:-}" ] && return 0
  SSH_OPTS_INIT=1
  if [ -z "$LAPTOP_LOCAL_ROOT" ]; then
    if [ -z "${HTTPS_PROXY:-}" ]; then
      err "HTTPS_PROXY is not set — the tailnet is unreachable without the runtime tunnel proxy"
      return 1
    fi
    eval "$(proxy_env)"
    if [ -z "$PROXY_HOST" ]; then err "could not parse a proxy host from HTTPS_PROXY"; return 1; fi
    local pc="socat - PROXY:${PROXY_HOST}:%h:%p,proxyport=3130"
    [ -n "$PROXY_USER" ] && pc="$pc,proxyauth=${PROXY_USER}:${PROXY_PASS}"
    SSH_OPTS=(-i "$LAPTOP_IDENTITY" -o BatchMode=yes -o ConnectTimeout=25 -o StrictHostKeyChecking=accept-new -o "ProxyCommand=$pc")
    # Remote commands always go through Git Bash explicitly (full path):
    # plain `bash` on Windows resolves to the WSL launcher (System32),
    # which fails with "no installed distributions" (seen 2026-11-23).
    LAPTOP_BASH='"C:\Program Files\Git\bin\bash.exe"'
  fi
  return 0
}
# The remote command line always uses the Git Bash path (not plain `bash`).
# remote_bash takes ONE plain invocation string (no double quotes, no $ or
# backticks inside — call sites pass "<script> <args>" only).
remote_bash() { # $1 = bash invocation, e.g. /c/.../laptop-remote.sh status job-XXX landscape
  local script="$1"
  if [ "$LAPTOP_DRYRUN" = "1" ]; then echo "DRYRUN remote_bash: $script"; return 0; fi
  if [ -n "$LAPTOP_LOCAL_ROOT" ]; then
    bash -c "${script//\/c\/trendline-render/$LAPTOP_LOCAL_ROOT}"
    return $?
  fi
  ssh_init || return 2
  timeout 240 ssh "${SSH_OPTS[@]}" "$LAPTOP_USER@$LAPTOP_HOST" "$LAPTOP_BASH -lc \"$script\""
}
# remote_ps runs the trigger .ps1 on the laptop. In local-test mode there
# is no PowerShell and no render must ever run on the VM, so the stub only
# reproduces the .ps1's contract: refuse (exit 2) when a render is already
# running for the job/orientation, otherwise record the launch (lockdir +
# log line + triggered marker) and return. The harness simulates the
# render itself by writing chunk files into the fake laptop root.
remote_trigger_ps() { # $1 job, $2 orientation
  local job="$1" ori="$2"
  if [ "$LAPTOP_DRYRUN" = "1" ]; then echo "DRYRUN remote_trigger_ps: $job $ori"; return 0; fi
  if [ -n "$LAPTOP_LOCAL_ROOT" ]; then
    local jobdir="$LAPTOP_LOCAL_ROOT/$job"
    local lockdir="$jobdir/chunks-$ori.lockdir"
    local log="$jobdir/chunks-$ori.log"
    local trig="$jobdir/chunks-$ori.triggered"
    mkdir -p "$jobdir"
    if [ -d "$lockdir" ] && [ -f "$log" ]; then
      local age=$(( ($(date +%s) - $(stat -c %Y "$log" 2>/dev/null || echo 0)) / 60 ))
      if [ "$age" -lt 30 ]; then
        echo "already running: $job/$ori (lockdir present, log $age min old)" >&2
        return 2
      fi
    fi
    mkdir -p "$lockdir"
    echo "$(date -Is) triggered $job $ori pid=$$ (local test mode)" >> "$log"
    echo "$$" > "$trig"
    echo "TRIGGERED pid=$$ job=$job orientation=$ori (local test mode)"
    return 0
  fi
  ssh_init || return 2
  timeout 120 ssh "${SSH_OPTS[@]}" "$LAPTOP_USER@$LAPTOP_HOST" "powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"C:/trendline-render/production/laptop-trigger.ps1\" -Job $job -Orientation $ori"
}
# scp one file from the laptop (remote C:/... path — the Windows OpenSSH
# scp understands forward slashes). Local-test mode is a plain cp.
remote_scp_get() { # $1 = remote rel path (job/chunks-<ori>/file), $2 = local dest
  local rel="$1" dest="$2"
  if [ "$LAPTOP_DRYRUN" = "1" ]; then echo "DRYRUN scp_get: $rel -> $dest"; return 0; fi
  if [ -n "$LAPTOP_LOCAL_ROOT" ]; then
    cp "$LAPTOP_LOCAL_ROOT/$rel" "$dest"
    return $?
  fi
  ssh_init || return 2
  timeout 600 scp "${SSH_OPTS[@]}" "$LAPTOP_USER@$LAPTOP_HOST:C:/trendline-render/$rel" "$dest"
}
# tar-extract a package stream on the laptop. Reads the tar from stdin.
# --no-same-owner: ownership recorded on the VM (root:nogroup) is
# meaningless on the laptop and restoring it fails there ("Cannot change
# ownership"); the files just take the extracting user's ownership.
remote_tar_extract() {
  if [ "$LAPTOP_DRYRUN" = "1" ]; then echo "DRYRUN tar_extract (consuming stdin)"; cat > /dev/null; return 0; fi
  if [ -n "$LAPTOP_LOCAL_ROOT" ]; then
    tar --no-same-owner -xzf - -C "$LAPTOP_LOCAL_ROOT"
    return $?
  fi
  ssh_init || return 2
  timeout 600 ssh "${SSH_OPTS[@]}" "$LAPTOP_USER@$LAPTOP_HOST" "$LAPTOP_BASH -lc \"tar --no-same-owner -xzf - -C /c/trendline-render\""
}

# --- marker: per-job laptop state on the VM ----------------------------------
# <job>/laptop-render.json is the source of truth for what was shipped /
# triggered / pulled. The driver mirrors a summary into state.json; this
# file is what a post-restart tick re-reads to resume.
job_dir() { echo "$REPO/work/trendlinenow/$1"; }
marker_file() { echo "$(job_dir "$1")/laptop-render.json"; }
marker_init() { # $1 job — create the marker if missing
  local mf; mf=$(marker_file "$1")
  [ -f "$mf" ] && return 0
  local tmp; tmp=$(mktemp "${mf}.tmp.XXXXXX") || return 1
  jq -n --arg job "$1" --arg host "$LAPTOP_HOST" \
    '{job: $job, host: $host, shipped_at: null, props_sha256: null, orientations: {}}' > "$tmp" \
    && mv "$tmp" "$mf"
}
marker_set() { # $1 job, $2 orientation ("" = job-level), $3 key, $4 value (string)
  local mf tmp; mf=$(marker_file "$1")
  tmp=$(mktemp "${mf}.tmp.XXXXXX") || return 1
  jq --arg ori "$2" --arg key "$3" --arg val "$4" \
    'if $ori == "" then .[$key] = $val else .orientations[$ori][$key] = $val end' \
    "$mf" > "$tmp" && mv "$tmp" "$mf"
}
marker_setn() { # $1 job, $2 orientation, $3 key, $4 value (number)
  local mf tmp; mf=$(marker_file "$1")
  tmp=$(mktemp "${mf}.tmp.XXXXXX") || return 1
  jq --arg ori "$2" --arg key "$3" --argjson val "$4" \
    'if $ori == "" then .[$key] = $val else .orientations[$ori][$key] = $val end' \
    "$mf" > "$tmp" && mv "$tmp" "$mf"
}
marker_get() { # $1 job, $2 jq query (e.g. .shipped_at) -> prints the value or empty
  local mf; mf=$(marker_file "$1")
  [ -f "$mf" ] || { echo ""; return 0; }
  jq -r "$2 // empty" "$mf" 2>/dev/null || echo ""
}
props_hash() { # $1 job dir -> sha256 of both props files (landscape then vertical)
  local jdir="$1"
  if [ -f "$jdir/remotion-props-1920x1080.json" ] && [ -f "$jdir/remotion-props-1080x1920.json" ]; then
    cat "$jdir/remotion-props-1920x1080.json" "$jdir/remotion-props-1080x1920.json" | sha256sum | awk '{print $1}'
  else
    echo ""
  fi
}
props_path_for() { # $1 job dir, $2 orientation -> props path
  if [ "$2" = "vertical" ]; then echo "$1/remotion-props-1080x1920.json"; else echo "$1/remotion-props-1920x1080.json"; fi
}

# --- chunk validation (mirrors render-chunks.sh's strict check) --------------
# A chunk is valid only if it has a video AND an audio stream, its frame
# count matches the range (when ffprobe reports it), and its duration is
# within 0.15s. Used by pull to decide what (re-)transfers.
if python3 --version >/dev/null 2>&1; then PY=python3; else PY=python; fi
ranges_for() { # $1 job dir, $2 orientation -> "TOTAL n" + "Cxx start end" lines
  local props spc
  props=$(props_path_for "$1" "$2")
  spc=$("$PY" -c "import json,sys; cfg=json.load(open(sys.argv[1]))['render']; print(cfg.get('scenes_per_chunk_landscape', cfg['scenes_per_chunk']) if sys.argv[2]=='landscape' else cfg['scenes_per_chunk'])" "$PROD/config.json" "$2" 2>/dev/null) || return 1
  "$PY" "$PROD/chunk_ranges.py" "$props" "$spc"
}
chunk_valid() { # $1 file, $2 expected frames, $3 expected duration -> 0 if valid
  "$PY" - "$1" "$2" "$3" <<'PYEOF' 2>/dev/null
import json, subprocess, sys
path, exp_frames, exp_dur = sys.argv[1], int(sys.argv[2]), float(sys.argv[3])
r = subprocess.run(["ffprobe", "-v", "error",
                    "-show_entries", "stream=codec_type,nb_frames",
                    "-show_entries", "format=duration",
                    "-of", "json", path], capture_output=True, text=True)
try:
    d = json.loads(r.stdout)
except Exception:
    sys.exit(1)
streams = d.get("streams", [])
nv = sum(1 for s in streams if s.get("codec_type") == "video")
na = sum(1 for s in streams if s.get("codec_type") == "audio")
if nv < 1 or na < 1:
    sys.exit(1)
nbf = None
for s in streams:
    if s.get("codec_type") == "video":
        raw = s.get("nb_frames")
        if raw not in (None, "N/A"):
            try:
                nbf = int(raw)
            except (TypeError, ValueError):
                nbf = None
        break
dur = float(d.get("format", {}).get("duration", 0))
if nbf is not None and abs(nbf - exp_frames) > 1:
    sys.exit(1)
sys.exit(0 if abs(dur - exp_dur) < 0.15 else 1)
PYEOF
}

# --- remote status parsing ---------------------------------------------------
# Parses laptop-remote.sh status output (key=value lines) into globals:
# ST_RUNNING, ST_STALE, ST_DONE_N, ST_TOTAL, ST_ALL_DONE, ST_LAST_LOG.
parse_status() { # $1 = status output
  ST_RUNNING=no; ST_STALE=no; ST_DONE_N=0; ST_TOTAL=0; ST_ALL_DONE=no; ST_LAST_LOG=""
  local line key val
  while IFS='=' read -r key val; do
    case "$key" in
      running) ST_RUNNING="$val" ;;
      stale) ST_STALE="$val" ;;
      chunks_done) ST_DONE_N="$val" ;;
      chunks_total) ST_TOTAL="$val" ;;
      all_done) ST_ALL_DONE="$val" ;;
      last_log) ST_LAST_LOG="$val" ;;
    esac
  done <<< "$1"
}
remote_status() { # $1 job, $2 orientation -> sets ST_* globals; rc 2 if unreachable
  # NOTE: call directly, never in $(...) — command substitution runs this
  # in a subshell and the ST_* globals would be lost to the caller.
  local out rc
  out=$(remote_bash "/c/trendline-render/production/laptop-remote.sh status $1 $2" 2>&1); rc=$?
  if [ $rc -ne 0 ]; then err "remote status failed for $1/$2 (rc $rc): $out"; return 2; fi
  parse_status "$out"
  return 0
}

# --- subcommands ---------------------------------------------------------------
cmd_test() {
  log "laptop test: host=$LAPTOP_HOST user=$LAPTOP_USER root=$LAPTOP_ROOT local_root=${LAPTOP_LOCAL_ROOT:-<none>}"
  if [ -z "$LAPTOP_LOCAL_ROOT" ]; then
    local ts
    ts=$(/opt/hatch/bin/tailscale status 2>&1 || echo "tailscale status failed")
    log "tailscale: $(echo "$ts" | head -1)"
    case "$ts" in
      *"Not connected"*|*"not connected"*)
        err "VM is not on the tailnet — approve the join link first (tailscale up)"
        return 2 ;;
    esac
    [ -f "$LAPTOP_IDENTITY" ] || { err "SSH identity missing: $LAPTOP_IDENTITY"; return 1; }
  fi
  local out rc
  out=$(remote_bash "echo LAPTOP-OK; hostname" 2>&1); rc=$?
  if [ $rc -ne 0 ]; then err "SSH echo to $LAPTOP_HOST failed (rc $rc): $out"; return 2; fi
  log "ssh echo: $out"
  out=$(remote_bash "/c/trendline-render/production/laptop-remote.sh has-rsync" 2>&1); rc=$?
  [ $rc -eq 0 ] && log "remote has-rsync: $out" || log "remote has-rsync check failed (rc $rc) — assuming no rsync, tar/scp path will be used"
  out=$(remote_bash "command -v node npm npx ffprobe python python3; test -d /c/trendline-render/remotion-assembler/node_modules && echo node_modules=yes || echo node_modules=no" 2>&1); rc=$?
  log "remote tools: $out"
  log "TEST OK: laptop reachable at $LAPTOP_HOST"
  return 0
}

# build-package: assemble the shippable package in $TMPDIR/laptop-pkg-<job>/.
# Layout mirrors the manual video #4 package: remotion-assembler/ (no
# node_modules), production/ (the 6 render files), <job>/ (assets + props
# only — never script.json, never chunks, never logs). Fails fast when a
# file the props reference is missing or empty on the VM, so a bad package
# is caught here instead of as a cryptic laptop render failure.
staging_dir() { echo "${TMPDIR:-$HOME/workspace/tmp}/laptop-pkg-$1"; }
cmd_build_package() { # --job <id>
  local job="$1" jdir staging ori f
  jdir=$(job_dir "$job")
  [ -d "$jdir" ] || { err "no such job dir: $jdir"; return 1; }
  for ori in landscape vertical; do
    [ -f "$(props_path_for "$jdir" "$ori")" ] || { err "$ori props missing for $job — run --write-props first"; return 1; }
  done
  staging=$(staging_dir "$job")
  rm -rf "$staging"
  mkdir -p "$staging/production" "$staging/remotion-assembler" "$staging/$job"
  rsync -a --exclude node_modules/ "$REPO/BroadcastContentAutomation/remotion-assembler/" "$staging/remotion-assembler/" || { err "remotion-assembler copy failed"; return 1; }
  for f in chunk_ranges.py config.json finish-chunks.py render-chunks.sh laptop-remote.sh laptop-trigger.ps1; do
    [ -f "$PROD/$f" ] || { err "missing production file: $PROD/$f"; return 1; }
    cp "$PROD/$f" "$staging/production/"
  done
  # Job assets only: every file the render reads is named in the props or
  # is a scene/brief/mp3 asset. Everything else (script.json, chunks,
  # logs, raw provider JSON) stays on the VM. Both art derivations ship:
  # scene*-art-square.jpg (landscape) AND scene*-art.png (vertical —
  # the vertical props reference the portrait derivation, not the
  # square; a package without the PNGs fails the vertical render at
  # frame 0 with a 404 on scene00-art.png, seen 2026-11-24).
  (cd "$jdir" && find . -maxdepth 1 -type f -size +0c \( \
      -name 'scene[0-9]*.mp3' -o -name 'brief[0-9]*.mp3' -o \
      -name 'scene*-art*.jpg' -o -name 'scene*-art*.png' -o -name 'scene*-stock.mp4' -o \
      -name 'scene*-stock.jpg' -o -name 'remotion-props-*.json' -o \
      -name 'trendlinenow-logo.png' -o -name '*.mp3' \) -printf '%P\n') > "$staging/.job-files.txt"
  rsync -a --files-from="$staging/.job-files.txt" "$jdir/" "$staging/$job/" || { err "job asset copy failed"; return 1; }
  # Fail fast: every file the props reference must exist non-empty in the
  # STAGING copy — the copy that actually ships. Checking the VM job dir
  # instead passed while the package lacked the files (the vertical PNGs,
  # 2026-11-24), because the VM has files the package never included.
  "$PY" - "$staging/$job" <<'PYEOF' || return 1
import json, os, sys
job = sys.argv[1]
missing = []
for props_name in ("remotion-props-1920x1080.json", "remotion-props-1080x1920.json"):
    props = json.load(open(os.path.join(job, props_name)))
    refs = set()
    for key in ("outroAudioPath", "introLogoPath", "backgroundMusicPath"):
        if props.get(key):
            refs.add(props[key])
    for sc in props.get("scenes", []) + props.get("briefScenes", []):
        for key in ("path", "narrationAudioPath"):
            if sc.get(key):
                refs.add(sc[key])
    for name in sorted(refs):
        p = os.path.join(job, name)
        if not os.path.exists(p) or os.path.getsize(p) == 0:
            missing.append(f"{props_name}: {name}")
if missing:
    print("MISSING props-referenced files:\n" + "\n".join(missing), file=sys.stderr)
    sys.exit(1)
PYEOF
  local nfiles nbytes
  nfiles=$(find "$staging" -type f | wc -l); nfiles=${nfiles// /}
  nbytes=$(du -sb "$staging" | awk '{print $1}')
  log "PACKAGE built: $staging ($nfiles files, $((nbytes / 1024 / 1024)) MB)"
  # Sets BUILD_STAGING (no stdout capture — the caller reads the global;
  # command substitution would run this in a subshell and lose it).
  BUILD_STAGING="$staging"
  return 0
}

cmd_ship() { # --job <id>
  local job="$1" jdir staging cur_hash marked_hash shipped_at
  jdir=$(job_dir "$job")
  [ -d "$jdir" ] || { err "no such job dir: $jdir"; return 1; }
  cur_hash=$(props_hash "$jdir")
  [ -n "$cur_hash" ] || { err "props missing for $job — run --write-props first"; return 1; }
  marker_init "$job"
  shipped_at=$(marker_get "$job" '.shipped_at')
  marked_hash=$(marker_get "$job" '.props_sha256')
  if [ -n "$shipped_at" ] && [ "$marked_hash" = "$cur_hash" ]; then
    # Marker says shipped with identical props — confirm the remote props
    # are really there (the laptop may have been reset since). A missing
    # remote props file means re-ship; an unreachable laptop means wait.
    local chk rc
    chk=$(remote_bash "test -f /c/trendline-render/$job/remotion-props-1920x1080.json -a -f /c/trendline-render/$job/remotion-props-1080x1920.json && echo PROPS-OK" 2>&1); rc=$?
    if [ $rc -eq 0 ] && [ "$chk" = "PROPS-OK" ]; then
      log "SHIP already shipped $job (props $cur_hash, confirmed on laptop)"
      return 0
    fi
    if [ $rc -ne 0 ]; then err "cannot confirm remote props for $job (rc $rc): $chk"; return 2; fi
    log "SHIP marker says shipped but remote props are missing — re-shipping $job"
  fi
  cmd_build_package "$job" || return 1
  staging="$BUILD_STAGING"
  log "SHIP sending $job package to $LAPTOP_HOST ($LAPTOP_ROOT)..."
  if [ "$LAPTOP_DRYRUN" = "1" ]; then
    echo "DRYRUN ship: tar -czf - -C $staging . | remote_tar_extract"
    return 0
  fi
  tar -czf - -C "$staging" . | remote_tar_extract || { err "SHIP transfer failed for $job"; return 1; }
  # Verify the transfer before recording it: the props files must exist
  # on the laptop, or the ship did not land.
  local chk rc
  chk=$(remote_bash "test -f /c/trendline-render/$job/remotion-props-1920x1080.json -a -f /c/trendline-render/$job/remotion-props-1080x1920.json && echo PROPS-OK" 2>&1); rc=$?
  if [ $rc -ne 0 ]; then err "SHIP verification failed for $job (rc $rc): $chk"; return 2; fi
  [ "$chk" = "PROPS-OK" ] || { err "SHIP landed but remote props are missing for $job: $chk"; return 1; }
  marker_set "$job" "" shipped_at "$(date -Is)"
  marker_set "$job" "" props_sha256 "$cur_hash"
  log "SHIP OK $job (props $cur_hash)"
  return 0
}

cmd_trigger() { # --job <id> --orientation <o> [--force]
  # --force skips the VM-side status shortcut (all_done / running) and
  # goes straight to the remote launcher — used ONLY by step's
  # invalid-retrigger path, where the remote chunks are known bad. The
  # remote launcher still refuses (exit 2) if a render is genuinely
  # running, so --force can never start a second render.
  local job="$1" ori="$2" force=0
  [ "${3:-}" = "--force" ] && force=1
  local rc
  if [ "$force" = "0" ]; then
    remote_status "$job" "$ori" || return $?
    if [ "$ST_ALL_DONE" = "yes" ]; then log "TRIGGER $job/$ori already fully rendered on the laptop ($ST_DONE_N/$ST_TOTAL)"; return 0; fi
    if [ "$ST_RUNNING" = "yes" ]; then log "TRIGGER $job/$ori already running on the laptop ($ST_DONE_N/$ST_TOTAL chunks)"; return 0; fi
  else
    log "TRIGGER $job/$ori forced (remote chunks known bad — re-render)"
  fi
  log "TRIGGER starting $job/$ori on the laptop..."
  local out
  out=$(remote_trigger_ps "$job" "$ori" 2>&1); rc=$?
  if [ $rc -eq 2 ]; then log "TRIGGER $job/$ori already running (remote refused a duplicate)"; return 0; fi
  if [ $rc -ne 0 ]; then err "TRIGGER failed for $job/$ori (rc $rc): $out"; return 1; fi
  log "TRIGGER OK $job/$ori: $out"
  local pid; pid=$(echo "$out" | grep -o 'pid=[0-9]*' | head -1 | cut -d= -f2)
  marker_set "$job" "$ori" triggered_at "$(date -Is)"
  [ -n "$pid" ] && marker_setn "$job" "$ori" trigger_pid "$pid"
  marker_set "$job" "$ori" status "rendering"
  return 0
}

cmd_poll() { # --job <id> --orientation <o>
  local job="$1" ori="$2"
  remote_status "$job" "$ori" || return $?
  local status="idle"
  if [ "$ST_ALL_DONE" = "yes" ]; then status="done";
  elif [ "$ST_RUNNING" = "yes" ]; then status="rendering";
  elif [ "$ST_STALE" = "yes" ]; then status="stalled"; fi
  marker_set "$job" "$ori" last_poll "$(date -Is)"
  marker_setn "$job" "$ori" remote_done "${ST_DONE_N:-0}"
  marker_setn "$job" "$ori" remote_total "${ST_TOTAL:-0}"
  marker_set "$job" "$ori" status "$status"
  log "POLL $job/$ori status=$status chunks=$ST_DONE_N/$ST_TOTAL last_log=${ST_LAST_LOG:-}"
  return 0
}

cmd_pull() { # --job <id> --orientation <o>
  # Exit 0 = all chunks now valid locally. Exit 3 = pulled but local
  # validation failed (prints INVALID <name> lines; step decides whether
  # to re-trigger once). Exit 1 = transfer error. Exit 2 = unreachable.
  local job="$1" ori="$2" jdir chunks_dir
  jdir=$(job_dir "$job")
  chunks_dir="$jdir/chunks-$ori"
  mkdir -p "$chunks_dir"
  local list rc
  list=$(remote_bash "/c/trendline-render/production/laptop-remote.sh list $job $ori" 2>&1); rc=$?
  if [ $rc -ne 0 ]; then err "remote list failed for $job/$ori (rc $rc): $list"; return 2; fi
  [ -n "$list" ] || { log "PULL $job/$ori: no chunk files on the laptop yet"; return 0; }
  # Expected ranges (local props — same file the laptop rendered against).
  local ranges fps
  ranges=$(ranges_for "$jdir" "$ori") || { err "cannot compute chunk ranges for $job/$ori"; return 1; }
  fps=$("$PY" -c "import json,sys; print(json.load(open(sys.argv[1])).get('fps', 30))" "$(props_path_for "$jdir" "$ori")" 2>/dev/null || echo 30)
  local transferred=0 invalid=0 name size
  while read -r name size; do
    [ -n "$name" ] || continue
    local local="$chunks_dir/$name"
    local valid=0
    if [ -f "$local" ] && [ -s "$local" ]; then
      case "$name" in
        "$ori"-C[0-9]*.mp4)
          local cname start end frames dur
          cname="${name#$ori-}"; cname="${cname%.mp4}"
          read -r _ start end <<< "$(echo "$ranges" | awk -v c="$cname" '$1==c {print $1, $2, $3}')"
          if [ -n "$start" ]; then
            frames=$((end - start + 1))
            dur=$("$PY" -c "print(f'{$frames/$fps:.2f}')" 2>/dev/null || echo 0)
            chunk_valid "$local" "$frames" "$dur" && valid=1
          fi
          ;;
        *) valid=0 ;; # sidecar files (ranges.json, props hashes) are tiny:
          # always re-fetch, so a re-render's new hashes replace a stale
          # local copy instead of being skipped as "already present".
      esac
    fi
    if [ "$valid" = "1" ]; then continue; fi
    log "PULL $job/$ori fetching $name ($size bytes)"
    remote_scp_get "$job/chunks-$ori/$name" "$local" || { err "PULL transfer failed for $name"; return 1; }
    transferred=$((transferred + 1))
  done <<< "$list"
  # Final gate: the existing finish-chunks.py check logic decides whether
  # the orientation is really complete. It re-reads ranges from the props
  # (same formula as the render), so a boundary mismatch fails loudly
  # instead of shipping a short video.
  if "$PROD/finish-chunks.py" --job "$jdir" --orientation "$ori" --check-chunks-only >> /dev/null 2>&1; then
    local n; n=$(find "$chunks_dir" -maxdepth 1 -name "$ori-C[0-9]*.mp4" -size +0c 2>/dev/null | wc -l); n=${n// /}
    marker_setn "$job" "$ori" pulled "$n"
    marker_set "$job" "$ori" status "done"
    marker_set "$job" "$ori" invalid_retrigger "0"
    log "PULL OK $job/$ori: $n chunks valid locally (transferred $transferred this call)"
    return 0
  fi
  # Validation failed: name the invalid chunks so the step can decide.
  # A stale props.sha256 (props changed after the render) is named too —
  # it is the re-trigger signal, not a chunk problem.
  if [ -f "$chunks_dir/props.sha256" ] && [ -f "$(props_path_for "$jdir" "$ori")" ]; then
    local stored current
    stored=$(cat "$chunks_dir/props.sha256" 2>/dev/null)
    current=$(sha256sum "$(props_path_for "$jdir" "$ori")" | awk '{print $1}')
    if [ -n "$stored" ] && [ "$stored" != "$current" ]; then
      echo "INVALID props.sha256 (stale: rendered against older props)"
      invalid=$((invalid + 1))
    fi
  fi
  while read -r name size; do
    case "$name" in
      "$ori"-C[0-9]*.mp4)
        local cname start end frames dur local
        cname="${name#$ori-}"; cname="${cname%.mp4}"
        read -r _ start end <<< "$(echo "$ranges" | awk -v c="$cname" '$1==c {print $1, $2, $3}')"
        local="$chunks_dir/$name"
        if [ -n "$start" ] && [ -f "$local" ]; then
          frames=$((end - start + 1))
          dur=$("$PY" -c "print(f'{$frames/$fps:.2f}')" 2>/dev/null || echo 0)
          chunk_valid "$local" "$frames" "$dur" || { echo "INVALID $name"; invalid=$((invalid + 1)); }
        else
          echo "INVALID $name"; invalid=$((invalid + 1))
        fi
        ;;
    esac
  done <<< "$list"
  log "PULL $job/$ori: local validation FAILED ($invalid invalid chunk(s))"
  return 3
}

cmd_cleanup() { # --job <id> --orientation <o> — best-effort, never fails the tick
  local job="$1" ori="$2"
  remote_bash "rm -rf /c/trendline-render/$job/chunks-$ori /c/trendline-render/$job/chunks-$ori.lockdir; echo CLEANED" 2>&1 || log "CLEANUP $job/$ori: laptop unreachable — remote chunks left in place (harmless)"
  log "CLEANUP $job/$ori done"
  return 0
}

cmd_step() { # --job <id> --orientation <o> — one tick of the state machine
  local job="$1" ori="$2" jdir
  jdir=$(job_dir "$job")
  [ -f "$(props_path_for "$jdir" "$ori")" ] || { err "STEP $ori props missing for $job — run --write-props first"; return 1; }
  marker_init "$job"
  # 1. Ship when never shipped or the props changed since the last ship.
  local shipped_at marked_hash cur_hash
  shipped_at=$(marker_get "$job" '.shipped_at')
  marked_hash=$(marker_get "$job" '.props_sha256')
  cur_hash=$(props_hash "$jdir")
  if [ -z "$shipped_at" ] || [ "$marked_hash" != "$cur_hash" ]; then
    log "STEP $job/$ori: shipping package (first ship or props changed)"
    cmd_ship "$job" || return $?
  fi
  # 2. Status: done -> pull; running -> wait; idle/stalled -> trigger.
  remote_status "$job" "$ori" || return $?
  if [ "$ST_ALL_DONE" = "yes" ]; then
    local pull_out pull_rc
    pull_out=$(cmd_pull "$job" "$ori" 2>&1); pull_rc=$?
    echo "$pull_out"
    if [ $pull_rc -eq 0 ]; then log "STEP $job/$ori complete — all chunks pulled and valid"; return 0; fi
    if [ $pull_rc -eq 3 ]; then
      # Remote chunks exist but local validation failed: the laptop's
      # copy of a chunk is bad (torn write). Re-trigger ONCE — the render
      # script re-renders invalid chunks and resumes the rest. If it
      # fails again, escalate instead of looping.
      local flag; flag=$(marker_get "$job" ".orientations[\"$ori\"].invalid_retrigger // \"0\"")
      if [ "$flag" = "0" ]; then
        log "STEP $job/$ori: pulled chunks invalid — re-triggering once to re-render them"
        # Delete the bad chunks locally AND remotely, so the re-render
        # really re-renders them (a merely-present bad chunk would make
        # the status shortcut report all_done and the trigger no-op).
        # The props.sha256 file is NOT deleted remotely: the render
        # script reads it on start and discards chunks whose frame
        # boundaries moved — deleting it would defeat that check.
        echo "$pull_out" | grep '^INVALID ' | awk '{print $2}' | while read -r bad; do
          rm -f "$jdir/chunks-$ori/$bad"
          case "$bad" in
            *.mp4) remote_bash "rm -f /c/trendline-render/$job/chunks-$ori/$bad" 2>/dev/null || log "STEP $job/$ori: could not delete remote $bad (will rely on re-render validation)" ;;
          esac
        done
        marker_set "$job" "$ori" invalid_retrigger "1"
        cmd_trigger "$job" "$ori" --force || return $?
        return 0
      fi
      err "STEP $job/$ori: pulled chunks invalid twice — escalating"
      return 1
    fi
    return $pull_rc
  fi
  if [ "$ST_RUNNING" = "yes" ]; then
    log "STEP $job/$ori: render running on the laptop ($ST_DONE_N/$ST_TOTAL chunks)"
    return 0
  fi
  log "STEP $job/$ori: not running (stale=$ST_STALE) — triggering"
  cmd_trigger "$job" "$ori"
}

# --- dispatch ------------------------------------------------------------------
cmd="${1:-}"; shift || true
JOB=""; ORI="landscape"; FORCE=""
while [ $# -gt 0 ]; do
  case "$1" in
    --job) JOB="$2"; shift 2;;
    --orientation) ORI="$2"; shift 2;;
    --force) FORCE="--force"; shift;;
    *) err "unknown argument: $1"; exit 2;;
  esac
done
case "$JOB" in
  ""|job-*) ;;
  *) # test takes no job; everything else needs a job id (no path traversal)
    err "bad job id: $JOB"; exit 2;;
esac
case "$JOB" in
  *..*|*/*) err "bad job id: $JOB"; exit 2;;
esac
case "$ORI" in
  vertical|landscape) ;;
  *) err "orientation must be vertical|landscape"; exit 2;;
esac
case "$cmd" in
  test) cmd_test ;;
  build-package) [ -n "$JOB" ] || { err "build-package needs --job"; exit 2; }; cmd_build_package "$JOB" ;;
  ship) [ -n "$JOB" ] || { err "ship needs --job"; exit 2; }; cmd_ship "$JOB" ;;
  trigger) [ -n "$JOB" ] || { err "trigger needs --job"; exit 2; }; cmd_trigger "$JOB" "$ORI" $FORCE ;;
  poll) [ -n "$JOB" ] || { err "poll needs --job"; exit 2; }; cmd_poll "$JOB" "$ORI" ;;
  pull) [ -n "$JOB" ] || { err "pull needs --job"; exit 2; }; cmd_pull "$JOB" "$ORI" ;;
  step) [ -n "$JOB" ] || { err "step needs --job"; exit 2; }; cmd_step "$JOB" "$ORI" ;;
  cleanup) [ -n "$JOB" ] || { err "cleanup needs --job"; exit 2; }; cmd_cleanup "$JOB" "$ORI" ;;
  *) err "usage: $0 test|build-package|ship|trigger|poll|pull|step|cleanup [--job <id>] [--orientation vertical|landscape] [--force]"; exit 2;;
esac
