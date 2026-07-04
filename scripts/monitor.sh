#!/bin/bash

MONITOR_URL="https://127.0.0.1:52323"

# Default categories if none provided
if [ $# -eq 0 ]; then
    CATEGORIES=("DatabaseSubmitService" "DiscPollingService")
else
    CATEGORIES=("$@")
fi

# Build category query string
CATEGORY_QUERY="category=none"
for CAT in "${CATEGORIES[@]}"; do
    CATEGORY_QUERY="$CATEGORY_QUERY&category=$CAT"
done

# Check if dotnet-monitor is running
if ! curl --silent --insecure $MONITOR_URL/processes >/dev/null 2>&1; then
    echo "dotnet-monitor is not running."
    exit 1
fi

# Extract PID of ArmRipper
PID=$(curl --silent --insecure $MONITOR_URL/processes \
  | jq -r '.[] | select(.name | test("ArmRipper"; "i")) | .pid' \
  | head -n 1)

if [ -z "$PID" ]; then
    echo "ArmRipper is not running."
    exit 1
fi

echo "Monitoring ArmRipper (PID: $PID)"
echo "Categories: ${CATEGORIES[*]}"
echo

# Color codes
RED=$(printf "\033[31m")
GREEN=$(printf "\033[32m")
YELLOW=$(printf "\033[33m")
BLUE=$(printf "\033[34m")
RESET=$(printf "\033[0m")

# Stream logs with proper tailing
stdbuf -oL curl --insecure -X POST \
  -H "Content-Type: application/json" \
  -d '{}' \
  "$MONITOR_URL/logs?pid=$PID&level=Debug&$CATEGORY_QUERY" \
  | stdbuf -oL awk -v blue="$BLUE" -v green="$GREEN" -v yellow="$YELLOW" -v red="$RED" -v reset="$RESET" '
    {
        # Convert UTC timestamps to local time (Dallas)
        if ($0 ~ /^[0-9]{4}-[0-9]{2}-[0-9]{2}T/) {
            cmd = "date -d \"" $0 "\" +\"%Y-%m-%d %H:%M:%S\""
            cmd | getline local
            close(cmd)
            print blue local reset
            next
        }

        # Colorize log levels
        if ($0 ~ /info:/) {
            print green $0 reset
        } else if ($0 ~ /dbug:/) {
            print blue $0 reset
        } else if ($0 ~ /warn:/) {
            print yellow $0 reset
        } else if ($0 ~ /fail:/ || $0 ~ /crit:/) {
            print red $0 reset
        } else {
            print $0
        }
    }
'
