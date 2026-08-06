# Add Debug Logging to Empty `catch { }` Blocks

**Priority:** 🟡 Medium
**Files:**
- `src/ArmRipper.Core/Rip/MakeMkvService.cs` (2 empty catch blocks)
- `src/ArmRipper.WebUi/Controllers/JobsController.cs` (1 empty catch block)
- Any other files with `catch { }` or `catch { /* ... */ }`

**Status:** ⬜ Todo

---

## Problem

Several places catch exceptions and silently swallow them with no logging:

```csharp
// MakeMkvService.cs — FetchBetaKeyAsync
try
{
    var json = await httpClient.GetStringAsync(BetaKeyApi, ct);
    // ...
}
catch { }  // ← silently drops network errors, JSON parse failures, etc.

// Fallback: scrape the MakeMKV forum
var html = await httpClient.GetStringAsync(BetaKeyForum, ct);
// ...
catch { }  // ← silently drops HTTP errors for the forum scrape

// JobsController.cs — JobDetail
try
{
    var metadata = await omdb.LookupByImdbAsync(job.ImdbId, apiKey, plot: "full");
    ViewBag.Metadata = metadata;
}
catch { /* non-critical */ }  // ← comment says it, but no debug log
```

This makes diagnosing network issues, API changes, or configuration problems impossible without
attaching a debugger.

## Proposed Fix

Add `Debug`-level logging to every empty catch block:

```csharp
// MakeMkvService.cs
try
{
    var json = await httpClient.GetStringAsync(BetaKeyApi, ct);
    // ...
}
catch (Exception ex)
{
    logger.LogDebug(ex, "MakeMKV beta key fetch via Ayra API failed — will try forum scrape");
}
```

If the logger is not available in the context (e.g., a nested method without DI), add it as a
constructor parameter or use a static `ILogger` field with `NullLogger` fallback.

### Audit

Run this search to find all empty catch blocks:

```bash
grep -rn "catch\s*{" --include="*.cs" src/
grep -rn "catch\s*{\s*/\*" --include="*.cs" src/
```

Every hit should either:
1. Have a `logger.LogDebug(...)` call inside, or
2. Have a comment explaining why logging is impossible (and what the fallback behavior is)
