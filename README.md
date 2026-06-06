# ARM Sharp

C# .NET 10 port of [automatic-ripping-machine](https://github.com/automatic-ripping-machine/automatic-ripping-machine) — a drop-in Docker replacement for optical disc ripping with feature parity.

## Quick Start

```bash
# Run the CLI (disc identification + rip + transcode)
docker run --rm --privileged \
  -v /dev/sr0:/dev/sr0 \
  -v /opt/arm:/home/arm/media \
  ghcr.io/negativeeddy/arm-sharp:latest cli --device /dev/sr0

# Run the Web UI
docker run -d --privileged \
  -p 8080:8080 \
  -v /dev/sr0:/dev/sr0 \
  -v /opt/arm:/home/arm/media \
  ghcr.io/negativeeddy/arm-sharp:latest webui
```

## Features

- **DVD rip**: Identify → CRC64 → MakeMKV → HandBrake transcode → move → notify
- **Blu-ray rip**: Identify → MakeMKV → HandBrake transcode → move → notify
- **Audio CD rip**: MusicBrainz lookup → abcde rip
- **Data disc**: dd-based raw rip
- **Web UI**: ASP.NET Core MVC with real-time SignalR notifications, job history, log viewer, settings

## Architecture

| Project | Description |
|---------|-------------|
| `ArmRipper.Core` | Business logic: identification, ripping, transcoding, metadata, notifications |
| `ArmRipper.Cli` | Console entry point for headless operation |
| `ArmRipper.WebUi` | ASP.NET Core MVC + Razor Pages web interface |
| `ArmRipper.Core.Tests` | xUnit tests (52+ tests) |

## Configuration

Configuration is read from `/etc/arm/config/arm.yaml` (native YAML loader maps ARM's `UPPER_CASE` keys to `Arm:CamelCase` config keys) with fallback to `appsettings.json`.

Key paths (configurable via arm.yaml):
- Raw rips: `/home/arm/media/raw`
- Transcoding: `/home/arm/media/transcode`
- Completed: `/home/arm/media/completed`
- Logs: `/home/arm/logs`
- Database: `/etc/arm/config/arm.db`

## Development

```bash
dotnet build      # 0 warnings, 0 errors
dotnet test       # 52/52 passing
dotnet run --project src/ArmRipper.Cli -- --device /dev/sr0 --test
dotnet run --project src/ArmRipper.WebUi
```

## Docker Build

```bash
docker build -t arm-sharp .
docker run --rm arm-sharp cli --help
docker run --rm arm-sharp webui
```

## GPU Passthrough

```bash
# NVIDIA
docker run --gpus all ...

# Intel QSV
docker run --device /dev/dri:/dev/dri ...

# AMD VAAPI
docker run --device /dev/dri:/dev/dri ...
```

## License

MIT
