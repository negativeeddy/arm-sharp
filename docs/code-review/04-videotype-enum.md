# Convert `VideoType` from String to Enum

**Priority:** 🟡 Medium
**Files:**
- `src/ArmRipper.Core/Models/Job.cs`
- `src/ArmRipper.Core/Models/VideoContentType.cs`
- All call sites comparing `VideoType` to `"series"`, `"tv"`, `"movie"`

**Status:** ✅ Done

---

## Problem

`Job.VideoType` is a `string?` compared to magic strings throughout the codebase:

```csharp
// In ArmRipperService.cs, IdentifyService.cs, Conductor.cs, and many others:
if (job.VideoType == "series" || job.VideoType == "tv") { ... }
if (job.VideoType == "movie") { ... }
```

This invites typos (`"serires"`), makes refactoring dangerous, and prevents compiler-checked
exhaustiveness. The `VideoContentType` enum already exists but is unused for `Job.VideoType`.

## Proposed Fix

### Step 1: Change the property type

```csharp
// In Job.cs
public VideoContentType VideoType { get; set; } = VideoContentType.Unknown;
public VideoContentType? VideoTypeAuto { get; set; }
public VideoContentType? VideoTypeManual { get; set; }
```

### Step 2: Update EF configuration

```csharp
// In ArmDbContext.cs
entity.Property(e => e.VideoType).HasConversion<string>().HasMaxLength(20);
```

### Step 3: Replace all magic-string comparisons

```csharp
// Before
if (job.VideoType == "series" || job.VideoType == "tv")

// After
if (job.VideoType is VideoContentType.Series or VideoContentType.Tv)
```

### Step 4: Audit all usages

Search for `"series"`, `"tv"`, `"movie"` in string context and replace with enum references.
The `VideoContentType` enum already has `Movie`, `Series`, `Tv`, `Unknown` — ensure all needed
values exist before the migration.
