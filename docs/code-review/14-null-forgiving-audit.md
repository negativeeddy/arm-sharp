# Audit & Fix Null-Forgiving Operators (`!`)

**Priority:** 🔴 Critical
**Files:** Across the entire codebase
**Status:** ✅ Done

---

## Problem

Null-forgiving operators (`!`) on potentially-null values were observed in multiple places:

```csharp
// IdentifyService.cs
if (!CheckMediaPresent(job.DevPath!))  // DevPath is string?

// ArmRipperService.cs
await StartTranscodeAsync(job, logFile, transcodeInPath!, ...);  // transcodeInPath may be null

// Conductor.cs
var rawDir = Path.GetDirectoryName(rawFilePath)!;  // GetDirectoryName returns string?
```

Each `!` suppresses a compiler warning but doesn't make the value non-null at runtime. If the
guarding condition changes or is removed in a future refactor, these become latent
`NullReferenceException` bombs.

## Proposed Fix

### Step 1: Audit

```bash
grep -rn '!\b' --include="*.cs" src/ \
  | grep -v 'null!' \
  | grep -v 'string.Empty!' \
  | grep -v '!.IsNullOr'
```

### Step 2: Categorize each usage

For each `!` found:

| Category | Action |
|----------|--------|
| **Guarded by prior check** | Add a code comment explaining the guard, or use pattern matching |
| **Should never be null** | Replace the nullable type with a non-nullable type if possible |
| **Could actually be null** | Add a proper null check + throw/return/log |
| **Constructor-initialized** | Use `required` keyword or validate in constructor |

### Step 3: Apply fixes

Example — `DevPath!` in IdentifyService:

```csharp
// Before
if (!CheckMediaPresent(job.DevPath!))

// After
var devPath = job.DevPath ?? throw new InvalidOperationException(
    $"Job {job.Id} has no DevPath — cannot check media presence");
if (!CheckMediaPresent(devPath))
```

Example — `GetDirectoryName` in Conductor:

```csharp
// Before
var rawDir = Path.GetDirectoryName(rawFilePath)!;

// After
var rawDir = Path.GetDirectoryName(rawFilePath)
    ?? rawFilePath;  // fall back to rawFilePath if it's already a directory
```

## Target

Reduce the count of `!` operators in the codebase to zero (or near zero), with any remaining
ones accompanied by an explanatory comment.
