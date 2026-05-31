#!/bin/sh
set -e

echo "=== Installing bash ==="
apt-get update -qq && apt-get install -y -qq bash

echo "=== Installing opencode ==="
curl -fsSL https://opencode.ai/install | bash

echo "=== Cloning original ARM Python reference ==="
if [ ! -d "/workspaces/automatic-ripping-machine" ]; then
    git clone https://github.com/automatic-ripping-machine/automatic-ripping-machine.git /workspaces/automatic-ripping-machine
fi

echo "=== Restoring .NET tools & building ==="
dotnet tool restore
dotnet build
