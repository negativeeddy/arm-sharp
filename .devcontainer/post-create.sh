#!/bin/sh
set -e

echo "=== Installing opencode ==="
curl -fsSL https://opencode.ai/install | sh

echo "=== Cloning original ARM Python reference ==="
if [ ! -d "$HOME/automatic-ripping-machine" ]; then
    git clone https://github.com/automatic-ripping-machine/automatic-ripping-machine.git "$HOME/automatic-ripping-machine"
fi

echo "=== Restoring .NET tools & building ==="
dotnet tool restore
dotnet build
