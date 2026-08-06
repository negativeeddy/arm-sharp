# Standardize Logger Usage (`ILogger<T>` vs `ILoggerFactory`)

**Priority:** 🟢 Low
**Files:** Multiple across `ArmRipper.Core` and `ArmRipper.WebUi`
**Status:** ⬜ Todo

---

## Problem

The codebase is inconsistent about how loggers are created:

| Service | Style |
|---------|-------|
| `Conductor` | `ILoggerFactory.CreateLogger("Conductor")` |
| `MakeMkvService` | `ILoggerFactory.CreateLogger("MakeMkvService")` |
| `HandBrakeService` | `ILoggerFactory.CreateLogger("HandBrakeService")` |
| `FfmpegService` | `ILoggerFactory.CreateLogger("FfmpegService")` |
| `IdentifyService` | `ILoggerFactory.CreateLogger("IdentifyService")` |
| `ArmRipperService` | `ILoggerFactory.CreateLogger("ArmRipperService")` |
| `TrackMapperService` | `ILogger<TrackMapperService>` |
| `DiscDbMappingService` | `ILogger<DiscDbMappingService>` |
| `EpisodeIdentificationOrchestrator` | `ILogger<EpisodeIdentificationOrchestrator>` |

The `ILoggerFactory.CreateLogger("CategoryName")` approach:
- Duplicates the class name as a string (typo risk)
- Doesn't auto-update if the class is renamed
- Loses the compile-time type safety of `ILogger<T>`

The `ILogger<T>` approach:
- Automatically uses the full type name as the category
- Survives renames via IDE refactoring
- Is the .NET convention

## Proposed Fix

Switch all services to `ILogger<T>`:

```csharp
// Before
public sealed class Conductor(
    ILoggerFactory loggerFactory, ...) : IConductor
{
    private readonly ILogger logger = loggerFactory.CreateLogger("Conductor");

// After
public sealed class Conductor(
    ILogger<Conductor> logger, ...) : IConductor
{
    // logger is already available via primary constructor
```

### Migration steps

1. Replace `ILoggerFactory` with `ILogger<T>` in each constructor
2. Remove the `private readonly ILogger logger = loggerFactory.CreateLogger("...")` field
3. Use the primary constructor parameter directly
4. If the class doesn't use primary constructors, keep `ILogger<T>` but assign normally

### Edge case: `BackgroundRipService`

This service creates its own `ILoggerFactory.CreateLogger("BackgroundRipService")` — same fix.

### Verification

```bash
grep -rn "loggerFactory.CreateLogger" --include="*.cs" src/
```

All remaining usages should be in `Program.cs` or bootstrapping code where `ILogger<T>` isn't
available during DI setup.
