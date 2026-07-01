#!/usr/bin/env bash
set -euo pipefail

SCRIPT="${ARM_WATCH_SCRIPT:-/opt/arm/scripts/watch-discs.sh}"
LOG="${ARM_LOG_FILE:-/home/arm/logs/watch-discs.log}"

log() { echo "$(date '+%Y-%m-%d %H:%M:%S') $1" >> "$LOG"; }

BACKOFF=1
log "SUPERVISOR: starting"
while true; do
    log "SUPERVISOR: launching watch-discs.sh"
    if bash "$SCRIPT"; then
        BACKOFF=1
        log "SUPERVISOR: watch-discs.sh exited cleanly"
    else
        rc=$?
        log "SUPERVISOR: watch-discs.sh crashed (exit $rc), restarting in ${BACKOFF}s"
        sleep "$BACKOFF"
        BACKOFF=$(( BACKOFF * 2 ))
        [ "$BACKOFF" -gt 60 ] && BACKOFF=60
    fi
done
