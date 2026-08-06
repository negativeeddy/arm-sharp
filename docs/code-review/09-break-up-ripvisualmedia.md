# Break Up `RipVisualMediaAsync` into Sub-Phases

**Priority:** 🟡 Medium
**File:** `src/ArmRipper.Core/Rip/ArmRipperService.cs`
**Status:** ⬜ Todo

---

## Problem

`ArmRipperService.RipVisualMediaAsync` is ~300 lines long and does everything:

1. Path computation
2. Duplicate folder detection
3. MakeMKV rip
4. Eject disc
5. DB reload (user changes)
6. Test-mode MKV trimming
7. TV episode identification
8. Another DB reload
9. Transcode (HandBrake or ffmpeg)
10. Manual title fix-up
11. File moves
12. Poster relocation
13. Emby refresh
14. Permissions
15. Raw file cleanup
16. Notifications
17. Stage transition

The method is hard to test, hard to reason about, and any change to one phase risks breaking
another.

## Proposed Fix

Extract each numbered phase into a private method. Example structure:

```csharp
public async Task<string> RipVisualMediaAsync(Job job, string logFile, ...)
{
    var (transcodeOutPath, finalDirectory, jobTitle) = ComputePaths(job);
    transcodeOutPath = ApplyDupeSuffix(hasDupes, transcodeOutPath, job);
    finalDirectory = ApplyDupeSuffix(hasDupes, finalDirectory, job);

    var transcodeInPath = await RipWithMakeMkvIfNeededAsync(job, jobTitle, ...);
    await EjectDiscIfConfiguredAsync(job, ct);
    await ReloadJobFromDbAsync(job, ct);
    await TrimForTestModeIfEnabledAsync(transcodeInPath, ct);
    await IdentifyEpisodesIfSeriesAsync(job, makeMkvOutPath, ct);

    var (transcodeSucceeded, finalOutput) = await TranscodeIfNeededAsync(job, ...);
    await FinalizeAsync(job, transcodeOutPath, finalDirectory, transcodeSucceeded, ct);

    return finalDirectory; // or whatever the return value represents
}
```

Each private method should:
- Accept only the parameters it needs (not the full `Job` unless necessary)
- Handle its own `SaveChangesAsync` / broadcast
- Have a clear single responsibility
- Be independently testable (consider making them `internal` + `InternalsVisibleTo` for testing)

### Benefits

- Each phase can be unit-tested in isolation
- The orchestrator method becomes a readable sequence
- Phase ordering is explicit and hard to accidentally break
- Logging per-phase becomes natural
