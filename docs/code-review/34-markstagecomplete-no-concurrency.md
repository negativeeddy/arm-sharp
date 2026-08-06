# No concurrency protection on `MarkStageComplete` writes

**Priority:** 🟡 Medium
**File(s):** `src/ArmRipper.Core/Models/Job.cs`
**Status:** ⬜ Todo

---

## Problem

`MarkStageComplete` performs a read-modify-write cycle with no concurrency control:

```csharp
// Job.cs lines 147-159
public void MarkStageComplete(RipStage stage)
{
    var name = stage.ToString();
    var stages = string.IsNullOrEmpty(CompletedStages)
        ? Array.Empty<string>()
        : CompletedStages.Split('|', StringSplitOptions.RemoveEmptyEntries);  // READ

    if (stages.Any(s => s.Equals(name, StringComparison.OrdinalIgnoreCase)))
        return;

    CompletedStages = string.IsNullOrEmpty(CompletedStages)
        ? name
        : $"{CompletedStages}|{name}";  // WRITE
}
```

If two processes both call `SaveChangesAsync` after calling `MarkStageComplete` on the
same job, one write will silently overwrite the other.

In practice, this is unlikely during the pipeline (only one `ProcessJobAsync` runs per
job).  But `DatabaseSubmitService` and `IdentifyService` both call
`MarkStageComplete(RipStage.CrcSubmitted)` — the former runs post-pipeline and the
latter runs during identification.  If they happen to overlap (e.g., a manual submit
during identification), the write from one could be lost.

## Proposed Fix

Use EF Core's optimistic concurrency with a row version or concurrency token:

```csharp
// In Job.cs, add a concurrency token
public Guid ConcurrencyToken { get; set; } = Guid.NewGuid();

// In ArmDbContext.OnModelCreating:
entity.Property(e => e.ConcurrencyToken).IsConcurrencyToken();
```

Then `SaveChangesAsync` will throw `DbUpdateConcurrencyException` if two writes
collide.  The caller should catch it, reload, and retry:

```csharp
catch (DbUpdateConcurrencyException)
{
    await db.Entry(job).ReloadAsync(ct);
    job.MarkStageComplete(stage);
    await db.SaveChangesAsync(ct);
}
```

## Benefits

- No silent data loss from concurrent stage writes
- `MarkStageComplete` idempotency + concurrency token = correct under any concurrency model
- Minimal overhead (one extra GUID column)
