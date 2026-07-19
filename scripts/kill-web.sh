#!/bin/bash
# Kills the ArmRipper.WebUi development server.
# Looks for the dotnet run process launched by the watch/build task
# and the wrapping shell, then kills both.

WEBUI_PATTERN="[d]otnet.*run.*ArmRipper.WebUi"

PID=$(pgrep -f "$WEBUI_PATTERN" | head -1)
if [ -z "$PID" ]; then
    echo "ArmRipper.WebUi is not running."
    exit 0
fi

echo "Found ArmRipper.WebUi (PID $PID), stopping..."

# Try graceful kill first
kill "$PID" 2>/dev/null
sleep 1

# Force kill if still alive
if kill -0 "$PID" 2>/dev/null; then
    echo "Force killing PID $PID..."
    kill -9 "$PID" 2>/dev/null
fi

# Also kill the parent shell that wraps it (if any)
PARENT=$(pgrep -f "sh -c.*dotnet.*run.*ArmRipper.WebUi" | head -1)
if [ -n "$PARENT" ]; then
    kill -9 "$PARENT" 2>/dev/null
fi

echo "ArmRipper.WebUi stopped."
