#!/bin/bash

MONITOR_URL="https://127.0.0.1:52323"

# Default categories if none provided
if [ $# -eq 0 ]; then
    CATEGORIES=("DatabaseSubmitService" "DiscPollingService")
else
    CATEGORIES=("$@")
fi

# Build regex supporting BOTH wildcards and raw regex
FILTER_REGEX=""
for CAT in "${CATEGORIES[@]}"; do
    if [[ "$CAT" == *"*"* ]]; then
        # wildcard → regex
        REGEX_CAT=$(echo "$CAT" | sed 's/\*/.*/g')
    else
        # treat as raw regex
        REGEX_CAT="$CAT"
    fi
    FILTER_REGEX="$FILTER_REGEX|$REGEX_CAT"
done

# Remove leading |
FILTER_REGEX="${FILTER_REGEX:1}"

# Colors
RED=$(printf "\033[31m")
GREEN=$(printf "\033[32m")
YELLOW=$(printf "\033[33m")
BLUE=$(printf "\033[34m")
RESET=$(printf "\033[0m")

echo "Monitoring ArmRipper"
echo "Category filters (regex): $FILTER_REGEX"
echo

while true; do
    # Get PID fresh each loop (in case ripper restarts)
    PID=$(curl --silent --insecure $MONITOR_URL/processes \
      | jq -r '.[] | select(.name | test("ArmRipper"; "i")) | .pid' \
      | head -n 1)

    if [ -z "$PID" ]; then
        echo "ArmRipper not running, retrying..."
        sleep 1
        continue
    fi

    echo "Connected to ArmRipper (PID: $PID)"
    echo

    # Stream logs
    stdbuf -oL curl --insecure -X POST \
      -H "Content-Type: application/json" \
      -d '{}' \
      "$MONITOR_URL/logs?pid=$PID&level=Debug" \
      | stdbuf -oL awk -v blue="$BLUE" -v green="$GREEN" -v yellow="$YELLOW" -v red="$RED" -v reset="$RESET" -v filter="$FILTER_REGEX" '
        {
            # Convert timestamps
            if ($0 ~ /^[0-9]{4}-[0-9]{2}-[0-9]{2}T/) {
                cmd = "date -d \"" $0 "\" +\"%Y-%m-%d %H:%M:%S\""
                cmd | getline local
                close(cmd)
                print blue local reset
                next
            }

            # Strict category filter using regex
            if ($0 !~ filter) {
                next
            }

            # Colorize levels
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

    echo
    echo "Stream closed — reconnecting..."
    sleep 1
done
