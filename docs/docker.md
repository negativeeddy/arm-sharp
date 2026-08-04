# Docker Usage

## Building

### Local

```bash
docker build -t arm-sharp .
```

### With docker compose

```bash
docker compose build
# or build and start in one command:
docker compose up -d --build
```

Multi-stage build: `mcr.microsoft.com/dotnet/sdk:10.0` for compilation, `automaticrippingmachine/arm-dependencies:1.7.3` for runtime with the ASP.NET Core 10 runtime installed via the Microsoft install script.

---

## Configuration Strategy

On first boot, no settings are written to the DB. Settings are **DB-first**: the SQLite `ripper_settings` row stores only the overrides you make in the UI, and the **database always wins** over file config. `/etc/arm/config/arm.yaml` is not read at startup anymore — legacy ARM values are pulled into the DB once via the **"Import ARM settings"** button on the `/settings` page (it never overwrites existing DB values).

| State | Behaviour |
|-------|-----------|
| **Fresh install** | Empty `ripper_settings` row; file/code defaults apply |
| **Normal restart** | DB overrides win |
| **UI "Reset to defaults"** | Clears all DB overrides (back to defaults) |
| **UI "Import ARM settings"** | Pulls legacy `/etc/arm/config/arm.yaml` values into the DB once (skips keys already set) |

The startup logs will confirm which path was taken:

```
info: Program[0] Database path: /etc/arm/config/arm-sharp.db
info: Program[0] Config file: /etc/arm/config/arm.yaml (found)
info: Program[0] Database migrated successfully
info: Program[0] No existing DB settings — seeding from file config on first boot
```

---

## Quick Start (docker compose)

The recommended way to run arm-sharp is with `docker compose`. A ready-to-use `docker-compose.yml` is included in the project root.

```bash
# Default paths — adjust for your setup
mkdir -p /opt/arm/media /opt/arm/logs /etc/arm/config

# Copy your existing ARM config (if any)
cp /path/to/arm.yaml /etc/arm/config/

# Start
docker compose up -d

# Open http://localhost:8080
```

The compose file maps the same volume layout as the original ARM container, making it a drop-in replacement.

---

## Running (manual `docker run`)

### CLI Mode

```bash
# Basic usage
docker run --rm --privileged \
  -v /dev/sr0:/dev/sr0 \
  -v /opt/arm/media:/home/arm/media \
  -v /etc/arm/config:/etc/arm/config \
  arm-sharp cli --device /dev/sr0

# Test mode (fast, 2 min per track)
docker run --rm --privileged \
  -v /dev/sr0:/dev/sr0 \
  -v /etc/arm/config:/etc/arm/config \
  arm-sharp cli --device /dev/sr0 --test
```

### Web UI Mode

```bash
docker run -d --privileged \
  --name arm-sharp \
  -p 8080:8080 \
  -v /dev/sr0:/dev/sr0 \
  -v /opt/arm/media:/home/arm/media \
  -v /opt/arm/logs:/home/arm/logs \
  -v /etc/arm/config:/etc/arm/config \
  arm-sharp webui
```

---

## Volume Mappings

| Host path | Container path | Purpose |
|-----------|---------------|---------|
| `/dev/sr*` | `/dev/sr*` | Optical drives (requires `--privileged`) |
| `/opt/arm/media` | `/home/arm/media` | Raw rips, transcodes, completed files |
| `/opt/arm/logs` | `/home/arm/logs` | Log files |
| `/etc/arm/config` | `/etc/arm/config` | arm.yaml, arm-sharp.db, abcde.conf, apprise.yaml |

---

## Drive Access

The container needs `--privileged` to access `/dev/sr*` devices and perform mount/eject operations. Without privileged mode, mount and eject will fail.

For finer-grained access without full `--privileged`:

```bash
docker run \
  --device /dev/sr0:/dev/sr0 \
  --device /dev/sr1:/dev/sr1 \
  --cap-add SYS_ADMIN \
  --cap-add SYS_RAWIO \
  arm-sharp cli --device /dev/sr0
```

---

## GPU Passthrough

### NVIDIA (NVENC/NVDEC)

```bash
docker run --gpus all arm-sharp webui
```

With docker compose, uncomment the `deploy.resources.reservations` block in `docker-compose.yml`.

Requires `nvidia-container-toolkit` on the host:
```bash
# Ubuntu/Debian
sudo apt install nvidia-container-toolkit
sudo systemctl restart docker
```

Verify GPU is visible to HandBrakeCLI:
```bash
docker run --rm --gpus all arm-sharp cli --device /dev/sr0 --test
# Check logs for "NVENC" in hardware encoders
```

### Intel QuickSync (QSV)

```bash
docker run --device /dev/dri:/dev/dri arm-sharp webui
```

The `/dev/dri` device provides Intel GPU access for QSV encoding/decoding.

### AMD VAAPI

```bash
docker run --device /dev/dri:/dev/dri arm-sharp webui
```

---

## Entry Point

The image uses `docker-entrypoint.sh` as the entry point:

| Command | Behaviour |
|---------|-----------|
| `docker run <image> cli [options]` | Runs `ArmRipper.Cli` (one-shot rip) |
| `docker run <image> webui` | Runs `ArmRipper.WebUi` with built-in disc polling on port 8080 |
| `docker run <image>` (default) | Same as `webui` |

---

## Image Tags (CI)

Published to `ghcr.io/negativeeddy/arm-sharp` on every push to `main` and on semver tags. PRs build the Docker image for validation but do **not** push.

| Tag | Description |
|-----|-------------|
| `latest` | Latest build from `main` |
| `vX.Y.Z` | Semver release |
| `vX.Y` | Major.minor release |
| `main` | Latest commit on main branch |
| `sha-XXXXXX` | Short commit SHA |
| `pr-N` | PR validation builds (not pushed) |

---

## Ports

| Port | Service | Notes |
|------|---------|-------|
| 8080 | Web UI | ASP.NET Core, configurable via `Arm:WebServerPort` in config |

---

## Environment Variables

| Variable | Default | Description |
|----------|---------|-------------|
| `ARM_RESET_SETTINGS` | — | **Removed.** Settings are DB-first; use the "Import ARM settings" UI action to pull values from `arm.yaml` |
| `ARM_UID` | — | Host UID for `arm` user (file permission matching) |
| `ARM_GID` | — | Host GID for `arm` group (file permission matching) |
| `TZ` | `America/Chicago` | Timezone |
| `ConnectionStrings__ArmDb` | `Data Source=/etc/arm/config/arm-sharp.db` | SQLite database path |
