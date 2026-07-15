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

echo "=== Cloning original ARM Python reference ==="
if [ ! -d "/workspaces/automatic-ripping-machine" ]; then
    git clone https://github.com/automatic-ripping-machine/automatic-ripping-machine.git /workspaces/automatic-ripping-machine
fi

echo "=== Restoring .NET tools & building ==="
dotnet tool restore
dotnet restore
dotnet build

