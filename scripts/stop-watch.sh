#!/usr/bin/env bash
set -euo pipefail

PID_FILE="${ARM_STATE_DIR:-/opt/arm/scripts}/.watch.pid"

if [ -f "$PID_FILE" ]; then
    pid=$(cat "$PID_FILE")
    if kill -0 "$pid" 2>/dev/null; then
        kill "$pid"
        echo "Watch (PID $pid) stopped"
    else
        echo "Watch not running (stale PID)"
    fi
    rm -f "$PID_FILE"
else
    pkill -f watch-discs.sh 2>/dev/null && echo "Watch stopped via pkill" || echo "Watch not running"
fi
