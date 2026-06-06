# Docker Usage

## Building

```bash
docker build -t arm-sharp .
```

Multi-stage build: SDK image for compilation, `automaticrippingmachine/arm-dependencies:1.7.3` for runtime with .NET 10 installed via Microsoft install script.

## Running

### CLI Mode

```bash
# Basic usage
docker run --rm --privileged \
  -v /dev/sr0:/dev/sr0 \
  -v /opt/arm:/home/arm/media \
  -v /etc/arm/config:/etc/arm/config \
  arm-sharp cli --device /dev/sr0

# Test mode (fast, 2 min per track)
docker run --rm --privileged \
  -v /dev/sr0:/dev/sr0 \
  arm-sharp cli --device /dev/sr0 --test
```

### Web UI Mode

```bash
docker run -d --privileged \
  --name arm-sharp \
  -p 8080:8080 \
  -v /dev/sr0:/dev/sr0 \
  -v /opt/arm:/home/arm/media \
  -v /etc/arm/config:/etc/arm/config \
  arm-sharp webui
```

## Volume Mappings

| Host path | Container path | Purpose |
|-----------|---------------|---------|
| `/dev/sr*` | `/dev/sr*` | Optical drives (requires `--privileged`) |
| `/opt/arm` | `/home/arm/media` | Raw rips, transcodes, completed files |
| `/opt/arm/logs` | `/home/arm/logs` | Log files |
| `/etc/arm/config` | `/etc/arm/config` | arm.yaml, arm.db, abcde.conf, apprise.yaml |

## Drive Access

The container needs `--privileged` to access `/dev/sr*` devices and perform mount/eject operations. Without privileged mode, mount and eject will fail.

For finer-grained access, you can use `--device`:

```bash
docker run \
  --device /dev/sr0:/dev/sr0 \
  --device /dev/sr1:/dev/sr1 \
  --cap-add SYS_ADMIN \
  --cap-add SYS_RAWIO \
  arm-sharp cli --device /dev/sr0
```

## GPU Passthrough

### NVIDIA (NVENC/NVDEC)

```bash
docker run --gpus all arm-sharp webui
```

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

## Entry Point

The image uses `docker-entrypoint.sh` as the entry point:

- `docker run <image> cli [options]` — runs `ArmRipper.Cli`
- `docker run <image> webui` — runs `ArmRipper.WebUi` listening on port 8080
- `docker run <image>` — prints usage message

## Image Tags (CI)

Published to `ghcr.io/negativeeddy/arm-sharp` with tags:

| Tag | Description |
|-----|-------------|
| `latest` | Latest build from `main` |
| `vX.Y.Z` | Semver release |
| `sha-XXXXXX` | Short commit SHA |

## Ports

| Port | Service | Notes |
|------|---------|-------|
| 8080 | Web UI | ASP.NET Core, configurable via `WEBSERVER_PORT` |
