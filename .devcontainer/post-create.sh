#!/bin/bash
set -e

echo "=== Ensuring latest MakeMKV is installed ==="
latest_makemkv_version="$({ curl -fsSL https://www.makemkv.com/download/ || true; } \
    | grep -oE 'makemkv-bin-[0-9]+\.[0-9]+\.[0-9]+\.tar\.gz' \
    | sed -E 's/.*-([0-9]+\.[0-9]+\.[0-9]+)\.tar\.gz/\1/' \
    | sort -Vu \
    | tail -n1)"

if [[ -z "$latest_makemkv_version" ]]; then
    echo "Unable to determine latest MakeMKV version from makemkv.com; skipping update."
else
    installed_makemkv_version=""
    if command -v makemkvcon >/dev/null 2>&1; then
        installed_makemkv_version="$(makemkvcon --version 2>/dev/null | grep -oE '[0-9]+\.[0-9]+\.[0-9]+' | head -n1 || true)"
    fi

    if [[ "$installed_makemkv_version" == "$latest_makemkv_version" ]]; then
        echo "MakeMKV $installed_makemkv_version already installed — skipping."
    else
        echo "Installing MakeMKV $latest_makemkv_version (current: ${installed_makemkv_version:-none})..."
        echo "$latest_makemkv_version" > /tmp/VERSION_MAKEMKV
        /workspaces/arm-sharp/.devcontainer/tarantino/install-makemkv.sh
        rm -f /tmp/VERSION_MAKEMKV
    fi
fi

# ─────────────────────────────────────────────────────────────────
# MakeMKV SDF download workaround for older BD-ROM drives
# ─────────────────────────────────────────────────────────────────
# When the local sdf.bin (bundled with makemkv-bin) lacks an entry for
# an older drive, makemkvcon tries to auto-download an updated SDF from
# makemkv.com.  If the site is unreachable, it hangs at 99% CPU
# indefinitely.  The sdf_Stop setting tells MakeMKV to skip the network
# lookup for a specific drive and fall back to direct disc access.
#
# Drive IDs can be obtained from:
#   makemkvcon --debug --robot --messages=-stdout info dev:/dev/sr0
#   (look for "No SDF …" in /root/MakeMKV_log.txt)
#
# Add each unrecognised drive as:
#   sdf_Stop = "<VENDOR_MODEL_FIRMWARE_MFG_DATE_SERIAL>"
#
# When makemkv.com is reachable, remove these lines to let MakeMKV
# download an updated sdf.bin that may natively support the drive.
# ─────────────────────────────────────────────────────────────────
makemkv_settings_dir="$HOME/.MakeMKV"
makemkv_settings_file="$makemkv_settings_dir/settings.conf"

# Known older drives that need direct disc access
declare -A sdf_stop_drives=(
    # LG GBC-H20N — Blu-ray / HD-DVD combo (Dell/Acer OEM)
    ["HL-DT-ST_DVDRWBD_GBC-H20N_B101_20070911123456_K187A8F5120"]=1
)

if command -v makemkvcon >/dev/null 2>&1; then
    mkdir -p "$makemkv_settings_dir"
    # settings.conf may not exist yet if makemkvcon has never run;
    # create it if missing.
    if [[ ! -f "$makemkv_settings_file" ]]; then
        touch "$makemkv_settings_file"
    fi

    for drive_id in "${!sdf_stop_drives[@]}"; do
        line="sdf_Stop = \"$drive_id\""
        if grep -qF "$line" "$makemkv_settings_file" 2>/dev/null; then
            echo "SDF stop already configured for drive: $drive_id"
        else
            echo "$line" >> "$makemkv_settings_file"
            echo "Added SDF stop for drive: $drive_id"
        fi
    done
fi

echo "=== Installing opencode (if missing) ==="
if ! command -v opencode >/dev/null 2>&1; then
    echo "opencode not found — installing..."
    curl -fsSL https://opencode.ai/install | bash
else
    echo "opencode already installed — skipping."
fi

apt update
if ! command -v sqlite3 >/dev/null 2>&1; then
    apt install -y sqlite3
else
    echo "sqlite3 already installed — skipping."
fi

# echo "=== Fixing workspace permissions ==="
# chown vscode:vscode /workspaces

echo "=== Creating ARM default media directories ==="
# Tests (e.g. DatabaseImport_ReturnsJson) and app code expect the default
# ArmPaths directories to exist. Create them if missing.
for dir in \
    "/home/arm/media" \
    "/home/arm/media/completed" \
    "/home/arm/media/raw" \
    "/home/arm/media/transcode" \
    "/home/arm/logs"; do
    if [ ! -d "$dir" ]; then
        sudo mkdir -p "$dir"
        echo "Created $dir"
    else
        echo "Already exists: $dir"
    fi
done

echo "=== Cloning original ARM Python reference ==="
if [ ! -d "/workspaces/automatic-ripping-machine" ]; then
    git clone https://github.com/automatic-ripping-machine/automatic-ripping-machine.git /workspaces/automatic-ripping-machine
fi

echo "=== Restoring .NET tools & building ==="
dotnet tool restore
dotnet restore
dotnet build

