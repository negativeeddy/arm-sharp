# Fix Hardcoded `ffmpeg` Binary Name in Test-Mode Path

**Priority:** 🟡 Medium
**File:** `src/ArmRipper.Core/Rip/ArmRipperService.cs`
**Status:** ⬜ Todo

---

## Problem

In `RipVisualMediaAsync`, the test-mode trim path hardcodes `"ffmpeg"`:

```csharp
// Line ~100 in RipVisualMediaAsync
var trimResult = await runner.RunAsync("ffmpeg",
    $"-t 30 -i \"{file}\" -c copy -y \"{tmp}\"", timeoutMs: 60_000, ct: ct);
```

But `FfmpegCli` is a configurable setting (`settings.Value.FfmpegCli`) respected everywhere else
in the codebase. If a user has a custom ffmpeg path or wrapper script, the test-mode trim will
fail silently.

## Proposed Fix

```csharp
var ffmpegCli = settings.Value.FfmpegCli;
if (string.IsNullOrWhiteSpace(ffmpegCli))
    ffmpegCli = "ffmpeg";

var trimResult = await runner.RunAsync(ffmpegCli,
    $"-t 30 -i \"{file}\" -c copy -y \"{tmp}\"", timeoutMs: 60_000, ct: ct);
```

## Audit

Search for other hardcoded binary names throughout the codebase:

```bash
grep -rn '"ffmpeg"' --include="*.cs" src/
grep -rn '"makemkvcon"' --include="*.cs" src/
grep -rn '"HandBrakeCLI"' --include="*.cs" src/
```

For `makemkvcon` and `HandBrakeCLI`, validate whether they have configurable overrides. If not,
consider adding them (or document why they're intentionally fixed).
