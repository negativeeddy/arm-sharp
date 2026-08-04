#!/bin/bash
# Kills any running ArmRipper app and then verifies it is fully stopped.
# Handles both the `dotnet run` wrapper process and the actual app binary,
# plus any parent shell. Verifies no processes remain and port 8080 is free.

set -u

APP_NAME="ArmRipper.WebUi"
RUN_PATTERN="[d]otnet.*run.*ArmRipper.WebUi"
BIN_PATTERN="[A]rmRipper.WebUi"
PORT=8080

echo "==> Searching for running ArmRipper app..."

# Collect PIDs of the dotnet run wrapper and the app binary.
# Match both the exact process name (comm) and the full command line,
# since some processes only carry the app name in argv[0].
RUN_PIDS=$(pgrep -f "$RUN_PATTERN" || true)
BIN_PIDS=$(pgrep -x "$APP_NAME" || true)
BIN_F_PIDS=$(pgrep -f "[A]rmRipper.WebUi" || true)
ALL_PIDS=$(printf "%s\n%s\n%s\n" "$RUN_PIDS" "$BIN_PIDS" "$BIN_F_PIDS" | grep -E '^[0-9]+$' | sort -un)

if [ -z "$ALL_PIDS" ]; then
    echo "    No ArmRipper app processes found."
else
    echo "    Found app PIDs: $(echo "$ALL_PIDS" | tr '\n' ' ')"
    echo "==> Sending SIGTERM (graceful)..."
    # shellcheck disable=SC2086
    kill $ALL_PIDS 2>/dev/null || true

    # Give it a moment to shut down gracefully
    sleep 2

    # Force-kill anything still alive
    STILL_ALIVE=""
    for pid in $ALL_PIDS; do
        if kill -0 "$pid" 2>/dev/null; then
            STILL_ALIVE="$STILL_ALIVE $pid"
        fi
    done

    if [ -n "$STILL_ALIVE" ]; then
        echo "    Still running (graceful shutdown timed out):$(echo "$STILL_ALIVE" | tr ' ' '\n' | sort -un | tr '\n' ' ')"
        echo "==> Sending SIGKILL (force)..."
        # shellcheck disable=SC2086
        kill -9 $STILL_ALIVE 2>/dev/null || true
        sleep 1
    fi
fi

# Also kill the parent shell that wraps the dotnet run (if any)
PARENT=$(pgrep -f "sh -c.*dotnet.*run.*ArmRipper.WebUi" | head -1 || true)
if [ -n "$PARENT" ]; then
    echo "==> Killing parent shell (PID $PARENT)..."
    kill -9 "$PARENT" 2>/dev/null || true
fi

echo "==> Verifying the app is stopped..."

VERIFY_FAIL=0

# 1. Check for any remaining app processes (same patterns used to find them)
if pgrep -f "$RUN_PATTERN" >/dev/null 2>&1 || pgrep -x "$APP_NAME" >/dev/null 2>&1 || pgrep -f "$BIN_PATTERN" >/dev/null 2>&1; then
    echo "    [FAIL] ArmRipper app process(es) still running:"
    ps aux | grep -E "$APP_NAME|dotnet run" | grep -v grep || true
    VERIFY_FAIL=1
else
    echo "    [OK] No ArmRipper app processes running."
fi

# 2. Check the web port is no longer bound.
#    Prefer ss/netstat if available; otherwise parse /proc/net/tcp directly
#    (no extra tools needed, and it does not consume connections like a probe).
port_in_use() {
    if command -v ss >/dev/null 2>&1; then
        ss -tln 2>/dev/null | grep -q ":$PORT "
    elif command -v netstat >/dev/null 2>&1; then
        netstat -tln 2>/dev/null | grep -q ":$PORT "
    else
        # Look for a LISTEN socket (state 0A) on the port in /proc/net/tcp[6]
        local hex
        hex=$(printf '%04X' "$PORT")
        if grep -E ":$hex [0-9A-F:]+ 0A " /proc/net/tcp /proc/net/tcp6 2>/dev/null | grep -q .; then
            return 0
        fi
        # Secondary signal: probe the port over TCP
        timeout 2 bash -c "echo > /dev/tcp/127.0.0.1/$PORT" 2>/dev/null
    fi
}

if port_in_use; then
    echo "    [FAIL] Port $PORT is still in use:"
    ss -tlnp 2>/dev/null | grep ":$PORT " || \
        netstat -tlnp 2>/dev/null | grep ":$PORT " || \
        echo "    (listener on $PORT detected, but no ss/netstat available to show details)"
    VERIFY_FAIL=1
else
    echo "    [OK] Port $PORT is free."
fi

if [ "$VERIFY_FAIL" -eq 0 ]; then
    echo ""
    echo "SUCCESS: ArmRipper app is fully stopped and verified."
    exit 0
else
    echo ""
    echo "WARNING: Some ArmRipper components could not be verified as stopped."
    exit 1
fi
