#!/usr/bin/env bash
set -euo pipefail

DEVICES=("/dev/sr0" "/dev/sr1")
POLL_SECONDS=5
STATE_FILE="/home/arm/scripts/.disc-state.json"
LOCK_FILE="/home/arm/scripts/.rip.lock"
LOG_FILE="/home/arm/logs/watch-discs.log"

log() { echo "$(date '+%Y-%m-%d %H:%M:%S') $1" >> "$LOG_FILE"; }

get_size() {
    local sysfs="${1//\/dev\//\/sys\/class\/block/}/size"
    if [ -f "$sysfs" ]; then
        cat "$sysfs" 2>/dev/null || echo -1
    else
        echo -1
    fi
}

get_label() {
    blkid -o value -s LABEL "$1" 2>/dev/null | tr -d '\n' || true
}

load_state() {
    if [ -f "$STATE_FILE" ]; then
        cat "$STATE_FILE" 2>/dev/null || echo "{}"
    else
        echo "{}"
    fi
}

save_state() {
    echo "$1" > "$STATE_FILE"
}

get_rip_lock() {
    if [ -f "$LOCK_FILE" ]; then
        local pid
        pid=$(cat "$LOCK_FILE" 2>/dev/null || echo "")
        if [ -n "$pid" ] && kill -0 "$pid" 2>/dev/null; then
            return 1
        fi
        log "WARN: Stale lock file (PID $pid not running), removing"
        rm -f "$LOCK_FILE"
    fi
    echo "$$" > "$LOCK_FILE"
}

release_rip_lock() {
    rm -f "$LOCK_FILE"
}

cli_bin="${ARM_CLI_PATH:-}"
if [ -z "$cli_bin" ]; then
    for candidate in \
        "/workspaces/arm-sharp/src/ArmRipper.Cli/bin/Release/net10.0/ArmRipper.Cli" \
        "/usr/local/bin/ArmRipper.Cli" \
        "/usr/bin/ArmRipper.Cli"; do
        if [ -x "$candidate" ]; then
            cli_bin="$candidate"
            break
        fi
    done
fi
if [ -z "$cli_bin" ] || [ ! -x "$cli_bin" ]; then
    log "FATAL: ArmRipper.Cli not found (set ARM_CLI_PATH or install to /usr/local/bin)"
    exit 1
fi

start_rip() {
    local dev="$1"
    local label
    label=$(get_label "$dev")
    log "INFO: Starting rip for $dev (label=$label)"
    if "$cli_bin" --device "$dev"; then
        log "INFO: Rip for $dev completed successfully"
    else
        local rc=$?
        log "ERROR: Rip for $dev failed with exit code $rc"
    fi
}

log "INFO: watch-discs.sh started, polling every ${POLL_SECONDS}s, devices: ${DEVICES[*]}, cli: $cli_bin"

declare -A prev
local_state=$(load_state)
for key in $(echo "$local_state" | python3 -c "import sys,json; d=json.load(sys.stdin); [print(k) for k in d]" 2>/dev/null || true); do
    val=$(echo "$local_state" | python3 -c "import sys,json; print(json.load(sys.stdin).get('$key', 0))" 2>/dev/null || echo 0)
    prev["$key"]=$val
done
log "INFO: Loaded previous state: $local_state"

init=true

while true; do
    for dev in "${DEVICES[@]}"; do
        [ -e "$dev" ] || continue
        name=$(basename "$dev")
        size=$(get_size "$dev")
        [ "$size" = "-1" ] && continue

        prev_size=${prev[$name]:-0}

        if $init; then
            prev[$name]=$size
            if [ "$size" -gt 0 ]; then
                log "INFO: $name initially has media (size=$size, label=$(get_label "$dev")) -- waiting for change"
            fi
            continue
        fi

        if [ "$prev_size" -ne "$size" ]; then
            label=$(get_label "$dev")
            if [ "$size" -gt 0 ]; then
                log "INFO: $name disc inserted (size=$size, label=$label)"
                if get_rip_lock; then
                    start_rip "$dev"
                    release_rip_lock
                else
                    log "WARN: $name rip already in progress -- skipping"
                fi
            else
                log "INFO: $name disc removed"
            fi
            prev[$name]=$size
            state_json="{"
            first=true
            for k in "${!prev[@]}"; do
                $first || state_json+=", "
                state_json+="\"$k\": ${prev[$k]}"
                first=false
            done
            state_json+="}"
            save_state "$state_json"
        fi
    done
    init=false
    sleep "$POLL_SECONDS"
done
