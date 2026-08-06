# Fix sync-over-async in `StartImportJob`

**Priority:** 🔴 Critical
**File:** `src/ArmRipper.Core/Infrastructure/BackgroundRipService.cs`
**Status:** ⬜ Todo

---

## Problem

`BackgroundRipService.StartImportJob` uses `.GetAwaiter().GetResult()` on async calls inside a
sync method, which can deadlock under certain `SynchronizationContext` conditions:

```csharp
// Lines ~156-163 — DO NOT SHIP
var effectiveSettings = settingsService.GetEffectiveAsync(ct).GetAwaiter().GetResult();
// ...
var job = conductor.CreateImportJobAsync(...).GetAwaiter().GetResult();
```

`GetEffectiveAsync` and `CreateImportJobAsync` internally call `SaveChangesAsync` on the EF
`DbContext`, which can block indefinitely if the calling thread has a captured synchronization
context (e.g., within an ASP.NET request pipeline or certain `TaskScheduler` contexts).

## Proposed Fix

Make `StartImportJob` fully async, consistent with how `StartRip` fires a background `Task.Run`:

```csharp
public async Task<int> StartImportJobAsync(
    string rawFilePath, string title, string? year,
    VideoContentType? videoType, DiscType? discType,
    CancellationToken ct = default)
{
    using var scope = scopeFactory.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<ArmDbContext>();
    var settingsService = scope.ServiceProvider.GetRequiredService<ISettingsService>();
    var conductor = scope.ServiceProvider.GetRequiredService<IConductor>();

    var effectiveSettings = await settingsService.GetEffectiveAsync(ct);
    var job = await conductor.CreateImportJobAsync(
        rawFilePath, title, year, videoType, discType,
        effectiveSettings, ct);

    // Fire-and-forget background transcode
    var key = $"import-{rawFilePath.GetHashCode()}";
    var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    // ... rest of the fire-and-forget logic
    return job.Id;
}
```

Alternatively, if the sync signature must be preserved for a caller that cannot go async, use
`Task.Run(() => ...).GetAwaiter().GetResult()` to push the work onto a thread-pool thread without
a synchronization context.

## Affected Callers

Search for references to `StartImportJob` — any synchronous caller needs the same treatment.
