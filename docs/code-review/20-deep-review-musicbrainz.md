# Deep-Review `MusicBrainzService` — Audio CD Path

**Priority:** 🟡 Medium
**File:** `src/ArmRipper.Core/Rip/MusicBrainzService.cs`
**Status:** ⬜ Todo

---

## Problem

The audio CD ripping path (MusicBrainz lookup → abcde/flac rip) is an entirely separate code
path from video ripping. It was not reviewed at all.

Potential risks:
- Different error-handling patterns than the video path
- Audio-specific external tool invocation (abcde, cdparanoia, flac)
- MusicBrainz API rate limiting
- Multi-disc album handling
- Unicode artist/album names in file paths

## Investigation Tasks

1. Read `MusicBrainzService` fully
2. Verify it uses `ICliProcessRunner` (not direct `Process.Start`)
3. Check for hardcoded audio tool binary names
4. Verify cancellation token propagation
5. Check that the `Conductor` properly routes audio discs to this service
6. Verify how multi-disc sets are handled (disc number in metadata)

## Deliverable

After deep review, either:
- Close with "audio path is correct and consistent with video path"
- Create new sub-documents for any audio-specific bugs found
