#!/usr/bin/env bash
# Usage-governor gate for the Trendline Now driver (2026-11-23).
#
# The driver calls this before starting a NEW prep (idle phase, after
# preflight, before the launch) and after learning the new job id:
#
#   # Gate: refuse to start when the daily video cap is hit or every leg
#   # of a stage is cooled / over budget. Exit 0 = may start, 1 = wait.
#   if ! "$REPO/trendline/governor-check.sh" may-start; then
#     log "governor: daily cap hit or all legs cooled — not starting a new prep"
#     exit 0
#   fi
#
#   # Record (once the prep's job id is known from its Workspace line):
#   "$REPO/trendline/governor-check.sh" video-started --job "$EXTRACTED_JOB"
#
# Both commands are idempotent and fail OPEN: if the governor itself is
# broken (missing files, bad JSON), may-start prints a warning and exits
# 0, so a governor bug can never halt the channel. The reason is logged
# either way. See hidden_files/governor.md.
set -u
DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
GOVERNOR="$DIR/governor.py"

if [ ! -f "$GOVERNOR" ]; then
  echo "governor-check: $GOVERNOR missing — fail-open, allowing" >&2
  exit 0
fi

# may-start prints one JSON line; the human reason goes to stderr via
# the governor's log prefix, and the driver logs the denial itself.
if [ "${1:-}" = "may-start" ]; then
  OUT=$(python3 "$GOVERNOR" video-may-start 2>"$DIR/.governor-check.err")
  RC=$?
  if [ $RC -ne 0 ]; then
    REASON=$(printf '%s' "$OUT" | python3 -c "import json,sys; print(json.load(sys.stdin).get('reason',''))" 2>/dev/null || echo "governor denied")
    echo "governor-check: $REASON" >&2
  fi
  rm -f "$DIR/.governor-check.err"
  exit $RC
fi

# Any other subcommand passes straight through to governor.py.
exec python3 "$GOVERNOR" "$@"
