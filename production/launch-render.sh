#!/bin/bash
# Double-fork launcher for render-chunks.sh.
# Fully detaches the render from the launching exec session so the
# runtime's ~11-min SIGTERM on long background execs cannot kill it.
# Usage: launch-render.sh <job_dir> <orientation> <log_file>
# The render-chunks.sh is exec'd by the grandchild (reparented to init).
set -u
JOB="$1"
ORIENTATION="$2"
LOG="$3"
cd /home/hatch/workspace/BroadcastContentAutomation/production || exit 1
# First fork: child detaches into a new session, then forks again so the
# grandchild is reparented to init and no longer in our process tree.
(
  setsid bash -c '
    ./render-chunks.sh --job "$1" --orientation "$2" >> "$3" 2>&1 < /dev/null &
    disown
    exit 0
  ' _ "$JOB" "$ORIENTATION" "$LOG"
) &
disown
exit 0
