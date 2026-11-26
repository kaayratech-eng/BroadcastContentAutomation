#!/bin/bash
# trendline/throttle.sh — ONE shared throttle-marker rule for the Trendline
# Now driver and healthcheck (review #2 B15).
#
# Both scripts used to define their own 429 pattern over the last 200 log
# lines, and they disagreed: the driver omitted "prepayment credits are
# depleted" (which the healthcheck matched), and a 429 more than 200 lines
# back was missed by both. A throttle marker is a state fact, not a
# recent-log fact, so the check greps the WHOLE log file.
#
# The pattern deliberately requires "HTTP 429" (not a bare "429"): the
# render log contains frame counters like "Rendered 429/2633", which a
# bare "429" grep would mistake for a throttle.
#
# Sourced by trendline/driver.sh and trendline/healthcheck.sh; no side
# effects on source.
THROTTLE_PATTERN="HTTP 429\|Too Many Requests\|Downstream Service Throttled\|prepayment credits are depleted\|FLUX_QUOTA_EXHAUSTED"

# log_has_throttle: 0 if the throttle pattern appears ANYWHERE in $1.
log_has_throttle() { # $1 = log path
  [ -n "$1" ] && [ -f "$1" ] && grep -q "$THROTTLE_PATTERN" "$1"
}
