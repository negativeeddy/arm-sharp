#!/bin/bash
set -euo pipefail

# Adjust arm user UID/GID to match host-mounted volume permissions
if [ -n "${ARM_UID:-}" ] && [ -n "${ARM_GID:-}" ]; then
    armhome="/home/arm"
    if getent passwd arm >/dev/null 2>&1; then
        # usermod needs the target GID to exist
        if ! getent group "$ARM_GID" >/dev/null 2>&1; then
            groupadd -f -g "$ARM_GID" armgroup
            usermod -g "$ARM_GID" arm
        fi
        usermod -u "$ARM_UID" arm
    else
        if ! getent group "$ARM_GID" >/dev/null 2>&1; then
            groupadd -f -g "$ARM_GID" arm
        fi
        useradd -u "$ARM_UID" -g "$ARM_GID" -d "$armhome" -s /bin/bash arm
    fi
    chown -R "$ARM_UID:$ARM_GID" "$armhome" 2>/dev/null || true
fi

case "${1:-}" in
    cli)
        shift
        exec /app/cli/ArmRipper.Cli "$@"
        ;;
    webui)
        exec /app/webui/ArmRipper.WebUi
        ;;
    dev)
        # Debug mode: restore, build, and run with dotnet run so
        # vsdbg can attach.  Workspace is mounted at /src.
        echo "==> Restoring packages..."
        dotnet restore /src/ArmRipper.slnx
        echo "==> Building (Debug)..."
        dotnet build /src/ArmRipper.slnx -c Debug --no-restore
        echo "==> Starting Web UI (dotnet run)..."
        exec dotnet run --project /src/src/ArmRipper.WebUi --no-launch-profile --no-build
        ;;
    *)
        exec /app/webui/ArmRipper.WebUi
        ;;
esac
