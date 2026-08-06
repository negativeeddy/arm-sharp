# Deep-Review `OvidSubmitService` & `OvidApiClient`

**Priority:** 🟢 Low
**Files:**
- `src/ArmRipper.Core/Rip/OvidSubmitService.cs`
- `src/ArmMedia.OvidProvider/OvidApiClient.cs`

**Status:** ⬜ Todo

---

## Problem

The OVID fingerprint submission pipeline involves OAuth token management, fingerprint
registration, and disc metadata submission. Token expiry/renewal bugs could silently fail
all submissions.

Not reviewed at all during the initial pass.

## Investigation Tasks

1. Read `OvidSubmitService` and `OvidApiClient`
2. Verify OAuth token lifecycle (refresh before expiry, not just on 401)
3. Check error handling for API rate limits, auth failures, network errors
4. Verify that submission failures are logged and don't block the rip pipeline
5. Check whether `OvidSubmitted` flag is correctly set and persisted
6. Verify `SubmitPendingAsync` batch logic (inherited from `SubmitServiceBase`)

## Deliverable

After deep review, either:
- Close with "OVID integration is correct and fault-tolerant"
- Create new sub-documents for any OVID-specific bugs found
