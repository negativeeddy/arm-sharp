# Deep-Review `ShutdownJobCancellationService`

**Priority:** 🟡 Medium
**File:** `src/ArmRipper.WebUi/Services/ShutdownJobCancellationService.cs` (verify path)
**Status:** ⬜ Todo

---

## Problem

This hosted service is registered in `Program.cs` and presumably cancels in-flight rip jobs on
SIGTERM/SIGINT. If it doesn't properly:

- Cancel the `CancellationTokenSource` for each active job
- Wait for jobs to reach a safe stopping point
- Save `CompletedStages` before the process exits
- Respect Docker's 10-second SIGTERM → SIGKILL grace period

...then "resumable" jobs won't actually resume — they'll restart from scratch, or worse, leave
the database in an inconsistent state.

## Investigation Tasks

1. Locate the file (may be under `Services/` or inline in `Program.cs`)
2. Read how it hooks into `IHostApplicationLifetime` or `AppDomain.ProcessExit`
3. Verify it calls `CancellationTokenSource.Cancel()` on all active jobs
4. Check whether it waits for jobs to finish saving state before returning
5. Check the timeout — Docker gives 10 seconds by default before SIGKILL
6. Verify `finally` blocks in `Conductor` and `BackgroundRipService` still run

## Deliverable

After deep review, either:
- Close with "shutdown handling is correct"
- Create new sub-documents for any shutdown-race bugs found
