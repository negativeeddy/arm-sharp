#!/usr/bin/env bash
# stop-all.sh — Stop the ARM-sharp web service and any outstanding
# makemkvcon / ffmpeg processes.
#
# Usage:  ./stop-all.sh

set -euo pipefail

echo "==> Stopping ARM-sharp web service (dotnet)..."

# Kill any dotnet process running the WebUi project
DOTNET_PIDS=$(pgrep -f 'dotnet.*ArmRipper.WebUi' 2>/dev/null || true)
if [ -n "$DOTNET_PIDS" ]; then
    echo "    Sending SIGTERM to dotnet PIDs: $DOTNET_PIDS"
    kill -TERM $DOTNET_PIDS 2>/dev/null || true
    sleep 2
    # Still alive?  Use SIGKILL.
    DOTNET_PIDS=$(pgrep -f 'dotnet.*ArmRipper.WebUi' 2>/dev/null || true)
    if [ -n "$DOTNET_PIDS" ]; then
        echo "    Sending SIGKILL to dotnet PIDs: $DOTNET_PIDS"
        kill -KILL $DOTNET_PIDS 2>/dev/null || true
    fi
else
    echo "    (no dotnet WebUi process found)"
fi

# Also kill the compiled binary directly if it's still around
BIN_PIDS=$(pgrep -f 'ArmRipper.WebUi' 2>/dev/null || true)
if [ -n "$BIN_PIDS" ]; then
    echo "    Sending SIGTERM to ArmRipper.WebUi PIDs: $BIN_PIDS"
    kill -TERM $BIN_PIDS 2>/dev/null || true
    sleep 1
    BIN_PIDS=$(pgrep -f 'ArmRipper.WebUi' 2>/dev/null || true)
    if [ -n "$BIN_PIDS" ]; then
        echo "    Sending SIGKILL to ArmRipper.WebUi PIDs: $BIN_PIDS"
        kill -KILL $BIN_PIDS 2>/dev/null || true
    fi
fi

echo "==> Stopping makemkvcon..."

MAKEMKV_PIDS=$(pgrep -x 'makemkvcon' 2>/dev/null || true)
if [ -n "$MAKEMKV_PIDS" ]; then
    echo "    Sending SIGTERM to makemkvcon PIDs: $MAKEMKV_PIDS"
    kill -TERM $MAKEMKV_PIDS 2>/dev/null || true
    sleep 2
    MAKEMKV_PIDS=$(pgrep -x 'makemkvcon' 2>/dev/null || true)
    if [ -n "$MAKEMKV_PIDS" ]; then
        echo "    Sending SIGKILL to makemkvcon PIDs: $MAKEMKV_PIDS"
        kill -KILL $MAKEMKV_PIDS 2>/dev/null || true
    fi
else
    echo "    (no makemkvcon process found)"
fi

echo "==> Stopping ffmpeg..."

FFMPEG_PIDS=$(pgrep -x 'ffmpeg' 2>/dev/null || true)
if [ -n "$FFMPEG_PIDS" ]; then
    echo "    Sending SIGTERM to ffmpeg PIDs: $FFMPEG_PIDS"
    kill -TERM $FFMPEG_PIDS 2>/dev/null || true
    sleep 2
    FFMPEG_PIDS=$(pgrep -x 'ffmpeg' 2>/dev/null || true)
    if [ -n "$FFMPEG_PIDS" ]; then
        echo "    Sending SIGKILL to ffmpeg PIDs: $FFMPEG_PIDS"
        kill -KILL $FFMPEG_PIDS 2>/dev/null || true
    fi
else
    echo "    (no ffmpeg process found)"
fi

echo "==> Done."
