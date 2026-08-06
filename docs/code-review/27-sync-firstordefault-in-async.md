# Synchronous `FirstOrDefault` blocks async pipeline

**Priority:** 🟡 Medium
**File(s):** `src/ArmRipper.Core/Rip/Conductor.cs`
**Status:** ⬜ Todo

---

## Problem

`ProcessJobAsync` line 652 uses a synchronous EF Core query inside an async method:

```csharp
var cfg = job.Config ?? db.ConfigSnapshots.FirstOrDefault(c => c.JobId == job.Id);
```

`FirstOrDefault` is a blocking I/O call.  In an ASP.NET Core context this wastes a
thread-pool thread.  If the DB is under load, it can cause thread-pool starvation.

## Proposed Fix

Use the async equivalent:

```csharp
var cfg = job.Config ?? await db.ConfigSnapshots
    .FirstOrDefaultAsync(c => c.JobId == job.Id, ct);
```

## Benefits

- Non-blocking I/O throughout the pipeline
- No thread-pool starvation risk under DB load
- Follows EF Core best practices
