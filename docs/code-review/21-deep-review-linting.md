# Deep-Review `ArmMedia.Linting.DefaultLintingEngine`

**Priority:** 🟢 Low
**Files:** `src/ArmMedia.Linting/DefaultLintingEngine.cs` and related models
**Status:** ⬜ Todo

---

## Problem

The linting module validates naming conventions for ripped files. Naming bugs are a top user
complaint in the original ARM — a linting engine that rejects valid names or accepts invalid
ones directly impacts the user's media library organization.

Not reviewed at all during the initial pass.

## Investigation Tasks

1. Read `DefaultLintingEngine` and understand the linting rules
2. Check for configurable naming templates (e.g., `{Title} ({Year})`)
3. Verify edge cases: multi-episode files, special edition naming, Unicode titles
4. Check whether linting failures block the pipeline or just warn
5. Verify how linting interacts with the episode identification pipeline
6. Look for hardcoded assumptions about file naming conventions

## Deliverable

After deep review, either:
- Close with "linting engine is correct and configurable"
- Create new sub-documents for any linting bugs or missing rules
