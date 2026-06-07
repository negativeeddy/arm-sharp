#!/bin/bash
set -e

echo "=== Installing opencode (if missing) ==="
if ! command -v opencode >/dev/null 2>&1; then
    echo "opencode not found — installing..."
    curl -fsSL https://opencode.ai/install | bash
else
    echo "opencode already installed — skipping."
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

