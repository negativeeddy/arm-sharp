#!/usr/bin/env bash
set -euo pipefail

SCRIPT="${ARM_WATCH_SCRIPT:-/opt/arm/scripts/watch-discs.sh}"
LOG="${ARM_LOG_FILE:-/home/arm/logs/watch-discs.log}"

log() { echo "$(date '+%Y-%m-%d %H:%M:%S') $1" >> "$LOG"; }

log "SUPERVISOR: starting"
while true; do
    log "SUPERVISOR: launching watch-discs.sh"
    if bash "$SCRIPT"; then
        log "SUPERVISOR: watch-discs.sh exited cleanly"
    else
        rc=$?
        log "SUPERVISOR: watch-discs.sh crashed (exit $rc), restarting in 2s"
        sleep 2
    fi
done
