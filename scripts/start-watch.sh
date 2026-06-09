#!/usr/bin/env bash
set -euo pipefail

PID_FILE="${ARM_STATE_DIR:-/opt/arm/scripts}/.watch.pid"
SCRIPT="${ARM_WATCH_SCRIPT:-/opt/arm/scripts/watch-discs.sh}"
LOG="${ARM_LOG_FILE:-/home/arm/logs/watch-discs.log}"

if [ -f "$PID_FILE" ]; then
    pid=$(cat "$PID_FILE")
    if kill -0 "$pid" 2>/dev/null; then
        echo "Watch already running (PID $pid)"
        exit 0
    fi
    echo "Removing stale PID file"
    rm -f "$PID_FILE"
fi

mkdir -p "$(dirname "$LOG")"
nohup bash "$SCRIPT" > "$LOG" 2>&1 &
echo $! > "$PID_FILE"
echo "Watch started (PID $(cat $PID_FILE)), logging to $LOG"
