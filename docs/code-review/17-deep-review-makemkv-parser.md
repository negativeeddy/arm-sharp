# Deep-Review `MakeMkvOutputParser` and `MakeMkvModels`

**Priority:** 🟡 Medium
**Files:**
- `src/ArmRipper.Core/Rip/MakeMkvOutputParser.cs`
- `src/ArmRipper.Core/Rip/MakeMkvModels.cs`
- `src/ArmRipper.Core/Rip/MakeMkvService.cs`

**Status:** ⬜ Todo

---

## Problem

MakeMKV's `--robot` output is line-oriented and fragile. The parser must handle:

- **Truncated output:** If MakeMKV is SIGKILL'd mid-rip, partial lines may be emitted
- **Unicode titles:** Non-ASCII movie names in CJK, Cyrillic, etc.
- **Format drift:** MakeMKV changes output format across versions
- **Multi-angle / multi-edition discs:** Special TINFO/SINFO sequences for alternate cuts
- **Empty / zero-duration tracks:** Junk titles that should be filtered out

The parser and models were not reviewed at all. The original Python ARM has a long history of
parser-related bugs (issue #145, #203, #367 in the ARM repo).

## Investigation Tasks

1. Read `MakeMkvOutputParser.ParseLine` — understand the parsing strategy
2. Read `MakeMkvModels` — check enum definitions match known MakeMKV output codes
3. Check error handling: what happens on unparseable lines (skip? throw? log?)
4. Verify `GetTrackInfoAsync` filters tracks by `MinLength` correctly
5. Check for hardcoded assumptions about output ordering (TINFO before SINFO, etc.)
6. Look for any `Substring` or `Split` calls that assume fixed-width fields

## Deliverable

After deep review, either:
- Close with "parser is robust and well-guarded"
- Create new sub-documents for any parser bugs or fragility issues found
