# Development

## Prerequisites

- .NET 10 SDK
- (Optional) Docker for container builds
- (Optional) Optical drive + disc for hardware testing

## Build

```bash
dotnet build
# 0 warnings, 0 errors across 4 projects
```

## Test

```bash
dotnet test
# 52+ tests, all passing
```

Tests use mocked `ICliProcessRunner` to avoid requiring real hardware.

## Run

### CLI

```bash
# Normal mode (requires a disc in the drive)
dotnet run --project src/ArmRipper.Cli -- --device /dev/sr0

# Test mode (rips first 2 minutes of first title)
dotnet run --project src/ArmRipper.Cli -- --device /dev/sr0 --test

# Help
dotnet run --project src/ArmRipper.Cli -- --help
```

### Web UI

```bash
dotnet run --project src/ArmRipper.WebUi
# Opens on http://localhost:8080
```

## Project Structure

```
src/
├── ArmRipper.Core/           # Class library — all business logic
│   ├── Configuration/        # ArmSettings, ArmYamlConfigLoader
│   ├── Infrastructure/       # CliProcessRunner, ArmDbContext (EF Core)
│   ├── Metadata/             # OmdbService, TmdbService
│   ├── Models/               # EF Core entities (Job, Track, User, etc.)
│   ├── Notifications/        # NotificationService, SignalR broadcaster
│   └── Rip/                  # Conductor, services, DvdCrc64
├── ArmRipper.Cli/            # Console app
└── ArmRipper.WebUi/          # ASP.NET Core MVC app
    ├── Controllers/          # 9 controllers
    ├── Views/                # Razor views
    ├── Hubs/                 # SignalR hub
    └── wwwroot/              # Static assets
tests/
└── ArmRipper.Core.Tests/     # xUnit tests
```

## Adding a New Feature

1. Add service interface in `ArmRipper.Core` (e.g., `IMyFeatureService`)
2. Implement service in the same project
3. Add model if persistent data is needed (EF Core entity in `Models/`)
4. Update `ArmDbContext` with new `DbSet<>` if needed
5. Register service in both CLI and WebUI `Program.cs`
6. Add controller + views in `ArmRipper.WebUi`
7. Write xUnit tests in `ArmRipper.Core.Tests`

## Testing Guidelines

- Use `Mock<ICliProcessRunner>` for services that shell out to external tools
- Avoid real hardware dependencies in unit tests
- Use `DbContextOptionsBuilder.UseSqlite("Data Source=:memory:")` for in-memory DB tests
- Follow existing test patterns in `ConductorTests.cs` and `NotificationServiceTests.cs`

## Code Style

- File-scoped namespaces (`namespace X.Y;`)
- Primary constructors for DI dependencies
- `sealed` on service classes that aren't designed for inheritance
- No comments unless explaining non-obvious behavior

## CI/CD

See `.github/workflows/ci.yml`:

- **On PR**: `dotnet build` + `dotnet test`
- **On push to main**: Build + test + Docker build + push to GHCR
- **On tag**: Same + semver Docker tag

## Devcontainer

A Tarantino devcontainer profile is at `.devcontainer/tarantino/`:

- Base: `automaticrippingmachine/arm-dependencies:1.7.3`
- .NET 10 SDK installed via Microsoft script
- `--privileged` mode for drive access
- GPU passthrough (`--gpus all`)
- Volume mounts for `/opt/arm` and `/etc/arm/config`
