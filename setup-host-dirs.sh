#!/bin/bash
set -e
BASE=/mnt/docker/arm-sharp
echo "Creating host directories for Tarantino devcontainer..."
for dir in home scripts music logs media config; do
    sudo mkdir -p "$BASE/$dir"
    echo "  $BASE/$dir"
done
echo "Done."
echo ""
echo "After rebuilding the devcontainer, create a default config:"
echo "  sudo cp /opt/arm/arm.yaml $BASE/config/arm.yaml"
echo "(or the app will use appsettings.json defaults)"
