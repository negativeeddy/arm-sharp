#!/usr/bin/env bash
set -euo pipefail

POLL_SECONDS=${ARM_POLL_SECONDS:-5}
STATE_DIR="${ARM_STATE_DIR:-/opt/arm/scripts/.disc-state}"
LOCK_FILE="${ARM_RIP_LOCK:-/opt/arm/scripts/.rip.lock}"
LOG_FILE="${ARM_LOG_FILE:-/home/arm/logs/watch-discs.log}"
CLI_BIN="${ARM_CLI_PATH:-/app/cli/ArmRipper.Cli}"

log() { echo "$(date '+%Y-%m-%d %H:%M:%S') $1" >> "$LOG_FILE"; }

get_devices() {
    for dev in /dev/sr*; do
        [ -e "$dev" ] && echo "$dev"
    done
}

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

trap release_rip_lock EXIT

start_rip() {
    local dev="$1"
    local label
    label=$(get_label "$dev")
    log "INFO: Starting rip for $dev (label=$label)"
    if "$CLI_BIN" --device "$dev"; then
        log "INFO: Rip for $dev completed successfully"
    else
        local rc=$?
        log "ERROR: Rip for $dev failed with exit code $rc"
    fi
}

if [ ! -x "$CLI_BIN" ]; then
    log "FATAL: ArmRipper.Cli not found at $CLI_BIN (set ARM_CLI_PATH)"
    exit 1
fi

mkdir -p "$STATE_DIR" "$(dirname "$LOG_FILE")"

devices=( $(get_devices) )
log "INFO: watch-discs.sh started, polling every ${POLL_SECONDS}s, devices: ${devices[*]}, cli: $CLI_BIN"

# Load previous sizes from individual state files
declare -A prev
for dev in $(get_devices); do
    name=$(basename "$dev")
    sf="$STATE_DIR/$name"
    [ -f "$sf" ] && prev[$name]=$(cat "$sf") || prev[$name]=0
done
log "INFO: Loaded previous state: $(for k in "${!prev[@]}"; do echo "$k=${prev[$k]}"; done | tr '\n' ' ')"

init=true

while true; do
    for dev in $(get_devices); do
        name=$(basename "$dev")
        size=$(get_size "$dev")
        [ "$size" = "-1" ] && continue

        prev_size=${prev[$name]:-0}

        if $init; then
            prev[$name]=$size
            echo "$size" > "$STATE_DIR/$name"
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
            echo "$size" > "$STATE_DIR/$name"
        fi
    done
    init=false
    sleep "$POLL_SECONDS"
done
