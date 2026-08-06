# Consider `WaitForExitAsync` for Process Management

**Priority:** 🟢 Low
**File:** `src/ArmRipper.Core/Infrastructure/CliProcessRunner.cs`
**Status:** ⬜ Todo

---

## Problem

`CliProcessRunner` uses `process.WaitForExit(timeoutMs)` (sync) combined with a
`CancellationToken.Register` callback to kill the process:

```csharp
using var _ = ct.Register(() =>
{
    try { process.Kill(entireProcessTree: true); } catch { }
    logger.LogWarning("Process cancelled ({Name})", fileName);
});

var stdout = ReadAllLinesAsync(process.StandardOutput, ct);
var stderr = ReadAllLinesAsync(process.StandardError, ct);

var exited = process.WaitForExit(timeoutMs) && !ct.IsCancellationRequested;

if (!exited)
{
    try { process.Kill(entireProcessTree: true); } catch { }
    // ...
}
```

This has a subtle race: if the process exits just as the cancellation fires, `process.Kill` may
throw `InvalidOperationException` ("No process is associated with this object"). The `try/catch`
mitigates it, but the pattern is fragile.

## Proposed Fix

.NET 9+ introduced `Process.WaitForExitAsync(CancellationToken)` which handles this cleanly:

```csharp
// Requires .NET 9+ (this project targets .NET 10 — safe to use)
try
{
    await process.WaitForExitAsync(ct);
}
catch (OperationCanceledException)
{
    try { process.Kill(entireProcessTree: true); } catch { }
    logger.LogWarning("Process cancelled ({Name})", fileName);
    return new CliResult(-1, string.Join("\n", await stdout),
        string.Join("\n", await stderr), true);
}
catch (InvalidOperationException)
{
    // Process already exited — that's fine
}

// Wait for async stdout/stderr readers to finish
await Task.WhenAll(stdout, stderr);
```

If timeout is still needed:

```csharp
using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMs));
using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

try
{
    await process.WaitForExitAsync(linkedCts.Token);
}
catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
{
    try { process.Kill(entireProcessTree: true); } catch { }
    logger.LogWarning("Process timed out after {Timeout}ms: {FileName}", timeoutMs, fileName);
    return new CliResult(-1, ...);
}
```

### Benefits

- No more `CancellationToken.Register` / manual cleanup
- `WaitForExitAsync` properly handles the process-exit-during-cancellation race internally
- Cleaner code with fewer try/catch blocks

### Verification

Check the TFM in `ArmRipper.Core.csproj`:
```xml
<TargetFramework>net10.0</TargetFramework>
```

Since this targets .NET 10, `WaitForExitAsync(CancellationToken)` is available.
