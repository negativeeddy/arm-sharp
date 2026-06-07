#!/usr/bin/env bash
# Start the disc watch script in background
# Usage: ./start-watch.sh

PID_FILE="/home/arm/scripts/.watch.pid"
SCRIPT="/workspaces/arm-sharp/scripts/watch-discs.sh"
LOG="/home/arm/logs/watch-discs.log"

if [ -f "$PID_FILE" ]; then
    pid=$(cat "$PID_FILE")
    if kill -0 "$pid" 2>/dev/null; then
        echo "Watch already running (PID $pid)"
        exit 0
    fi
    echo "Removing stale PID file"
    rm -f "$PID_FILE"
fi

nohup bash "$SCRIPT" > "$LOG" 2>&1 &
echo $! > "$PID_FILE"
echo "Watch started (PID $(cat $PID_FILE)), logging to $LOG"
