#!/bin/bash
set -e

if [ "$1" = "cli" ]; then
    shift
    exec /app/cli/ArmRipper.Cli "$@"
elif [ "$1" = "webui" ]; then
    exec /app/webui/ArmRipper.WebUi
else
    echo "Usage: docker run <image> cli|webui [options]"
    exit 1
fi
