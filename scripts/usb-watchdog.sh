#!/usr/bin/env bash
# ──────────────────────────────────────────────────────────
# USB Watchdog — Rapid device node recreation for optical drives
# Designed for Docker containers where /dev is tmpfs and udev doesn't run.
#
# Polls /sys/class/block/sr*/dev every 1 second. When a USB optical drive
# reconnects after a dropout, the kernel creates new device nodes in devtmpfs
# (host) but the container's tmpfs /dev doesn't see them. This script detects
# the new major:minor numbers and recreates /dev/sr* and /dev/sg* nodes so
# MakeMKV and other tools can access the drive again.
#
# Usage:
#   ./scripts/usb-watchdog.sh [log_file_path]
#
# Recommended: run in a tmux/screen session or as a background service.
# ──────────────────────────────────────────────────────────
set -euo pipefail

LOG_FILE="${1:-/home/arm/logs/usb-watchdog.log}"
POLL_SECONDS="${USB_WATCHDOG_POLL:-1}"
STATE_DIR="${USB_WATCHDOG_STATE:-/tmp/usb-watchdog-state}"
mkdir -p "$STATE_DIR" "$(dirname "$LOG_FILE")"

log() { echo "$(date '+%Y-%m-%d %H:%M:%S') $1" >> "$LOG_FILE"; }

# Track last-known major:minor for each device to detect changes
declare -A prev_major_minor

# ── Device node creation ──────────────────────────────────
create_or_fix_device_node() {
    local sysblock="$1"
    local devname
    devname=$(basename "$sysblock")
    local devpath="/dev/$devname"
    local nums_file="$sysblock/dev"

    [ -f "$nums_file" ] || return 1

    local nums
    nums=$(cat "$nums_file")
    local major="${nums%%:*}"
    local minor="${nums##*:}"

    # Check if node needs creating or updating
    local need_create=0
    if [ ! -e "$devpath" ]; then
        need_create=1
    else
        # stat format: major:minor in hex, e.g. "b:30" for 11:0
        local current
        current=$(stat -c '%t:%T' "$devpath" 2>/dev/null || echo "")
        local expected_hex
        expected_hex=$(printf "%x:%x" "$major" "$minor")
        if [ "$current" != "$expected_hex" ]; then
            need_create=1
        fi
    fi

    if [ "$need_create" = "1" ]; then
        rm -f "$devpath"
        if mknod "$devpath" b "$major" "$minor" 2>/dev/null; then
            chgrp cdrom "$devpath" 2>/dev/null || true
            chmod 660 "$devpath" 2>/dev/null || true
            log "RECREATED $devpath ($major:$minor)"
        else
            log "FAILED mknod $devpath ($major:$minor)"
        fi
    fi

    # Also create the SCSI generic device (sg*)
    local sgdir="$sysblock/device/scsi_generic"
    if [ -d "$sgdir" ]; then
        for sgentry in "$sgdir"/*/; do
            [ -d "$sgentry" ] || continue
            local sgname
            sgname=$(basename "$sgentry")
            local sgdev="/dev/$sgname"
            local sg_nums_file="$sgentry/dev"

            [ -f "$sg_nums_file" ] || continue

            local sg_nums
            sg_nums=$(cat "$sg_nums_file")
            local sg_major="${sg_nums%%:*}"
            local sg_minor="${sg_nums##*:}"

            local need_sg_create=0
            if [ ! -e "$sgdev" ]; then
                need_sg_create=1
            else
                local sg_current
                sg_current=$(stat -c '%t:%T' "$sgdev" 2>/dev/null || echo "")
                local sg_expected_hex
                sg_expected_hex=$(printf "%x:%x" "$sg_major" "$sg_minor")
                if [ "$sg_current" != "$sg_expected_hex" ]; then
                    need_sg_create=1
                fi
            fi

            if [ "$need_sg_create" = "1" ]; then
                rm -f "$sgdev"
                if mknod "$sgdev" c "$sg_major" "$sg_minor" 2>/dev/null; then
                    chgrp cdrom "$sgdev" 2>/dev/null || true
                    chmod 660 "$sgdev" 2>/dev/null || true
                    log "RECREATED $sgdev ($sg_major:$sg_minor)"
                else
                    log "FAILED mknod $sgdev ($sg_major:$sg_minor)"
                fi
            fi
        done
    fi

    return 0
}

# ── dmesg monitor (runs in background) ────────────────────
# Tails dmesg for USB connect/disconnect events and logs them
tail_dmesg() {
    local known_disconnects="$STATE_DIR/disconnects"
    : > "$known_disconnects"

    # Use dmesg --follow if available, otherwise poll dmesg
    if dmesg --help 2>/dev/null | grep -q -- '--follow'; then
        dmesg --follow 2>/dev/null | while IFS= read -r line; do
            if echo "$line" | grep -qiE "USB disconnect|device reset|Power-on or device reset|BU40N|sr[0-9]"; then
                log "DMESG: $line"
            fi
        done &
    else
        # Fallback: poll dmesg for new events
        local last_dmesg="$STATE_DIR/dmesg_last"
        dmesg > "$last_dmesg" 2>/dev/null || true
        while true; do
            sleep 3
            local new_dmesg
            new_dmesg=$(mktemp)
            dmesg > "$new_dmesg" 2>/dev/null || true
            diff --unchanged-line-format= --old-line-format= --new-line-format='%L' \
                "$last_dmesg" "$new_dmesg" 2>/dev/null | while IFS= read -r line; do
                if echo "$line" | grep -qiE "USB disconnect|device reset|Power-on or device reset|BU40N|sr[0-9]"; then
                    log "DMESG: $line"
                fi
            done || true
            mv "$new_dmesg" "$last_dmesg"
        done &
    fi
}

# ── Main loop ─────────────────────────────────────────────
log "═══════════════════════════════════════════════════════"
log "USB Watchdog started"
log "  Poll interval: ${POLL_SECONDS}s"
log "  Log file: $LOG_FILE"
log "  State dir: $STATE_DIR"
log "═══════════════════════════════════════════════════════"

# Start background dmesg monitor
tail_dmesg
DMESG_PID=$!
trap 'kill "$DMESG_PID" 2>/dev/null; echo "$(date "+%Y-%m-%d %H:%M:%S") USB Watchdog shutting down" >> "$LOG_FILE"' EXIT

# Initialize state
for sysblock in /sys/class/block/sr*; do
    [ -d "$sysblock" ] || continue
    devname=$(basename "$sysblock")
    nums_file="$sysblock/dev"
    if [ -f "$nums_file" ]; then
        nums=$(cat "$nums_file")
        prev_major_minor["$devname"]="$nums"
    fi
done

log "Initial devices: ${#prev_major_minor[@]} found"
for dev in "${!prev_major_minor[@]}"; do
    log "  $dev = ${prev_major_minor[$dev]}"
done

# Main polling loop
while true; do
    for sysblock in /sys/class/block/sr*; do
        [ -d "$sysblock" ] || continue

        devname=$(basename "$sysblock")
        nums_file="$sysblock/dev"

        if [ ! -f "$nums_file" ]; then
            # Device disappeared from sysfs
            if [ -n "${prev_major_minor[$devname]:-}" ]; then
                log "DEVICE LOST $devname (was ${prev_major_minor[$devname]})"
                unset "prev_major_minor[$devname]"
            fi
            continue
        fi

        nums=$(cat "$nums_file")
        prev_nums="${prev_major_minor[$devname]:-}"

        if [ "$nums" != "$prev_nums" ]; then
            # Device changed or is new — recreate node(s)
            log "DEVICE CHANGED $devname: ${prev_nums:-new} → $nums"
            create_or_fix_device_node "$sysblock"
            prev_major_minor["$devname"]="$nums"
        else
            # Same numbers — still ensure node exists (handles container restarts)
            devpath="/dev/$devname"
            if [ ! -e "$devpath" ]; then
                log "DEVICE NODE MISSING $devpath → recreating"
                create_or_fix_device_node "$sysblock"
            fi
        fi
    done

    sleep "$POLL_SECONDS"
done
