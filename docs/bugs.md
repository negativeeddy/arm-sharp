# Known Bugs

## `DelRawFiles` setting is never checked before final cleanup

**Severity:** Low (temp dirs get cleaned up regardless, which is usually desired behavior)

**Location:** `src/ArmRipper.Core/Rip/ArmRipperService.cs` — `RipVisualMediaAsync()`, line ~325

**Description:**
`ArmSettings.DelRawFiles` (YAML key `DELRAWFILES`, default `true`) stores a user preference to control whether raw MakeMKV output directories are deleted after transcoding. However, the final cleanup call on line 325 is unconditional:

```csharp
DeleteRawFiles(new[] { transcodeInPath, transcodeOutPath, makeMkvOutPath }.OfType<string>().ToArray());
```

This runs regardless of the `DelRawFiles` value. The setting is stored in the database and displayed in the Job Detail config snapshot, but is never actually consulted before deletion.

**Impact:**
- If a user sets `DelRawFiles: false` (expecting raw files to be kept), they will still be deleted because the setting is ignored.
- The only way to keep raw files is to set `SkipTranscode: true`, which bypasses the HandBrake step entirely and reassigns `transcodeOutPath = transcodeInPath`, so the deletion at line 325 would try to delete the now-final output directory — though in practice the `MoveFilesPostAsync` step has already moved the content and the final dir is a different path.

**Also missing:** `DelRawFiles` is not exposed as a checkbox in the Web UI Settings form (`Views/Settings/Index.cshtml`). Saving from the UI would reset it to `false` (default for unchecked HTML bools) since no input element exists for it.
