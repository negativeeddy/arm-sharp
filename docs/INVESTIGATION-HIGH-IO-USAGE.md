# Investigation: 157 GB I/O Spike in ArmRipper.WebUi (vigorous_borg)

**Branch:** `investigate/high-io-usage`  
**Date:** 2026-07-19  
**Status:** Root causes identified, fixes ready

---

## Executive Summary

ArmRipper.WebUi accumulated **157 GB total I/O** (125 GB reads + 31 GB writes) over ~5.5 hours while running in the devcontainer. This caused severe system degradation with swap pressure, high load average, and significant disk contention. **Root causes identified:**

1. **CliProcessRunner logs every subprocess output line at DEBUG level** — each line becomes a separate log entry with overhead
2. **Logging configuration sets CliProcessRunner to DEBUG** — appsettings.json explicitly enables verbose logging
3. **ffmpeg codec/format queries produce thousands of lines** — each logged individually, causing log file explosion

---

## Findings

### 1. CliProcessRunner: Excessive Logging

**File:** [src/ArmRipper.Core/Infrastructure/CliProcessRunner.cs](src/ArmRipper.Core/Infrastructure/CliProcessRunner.cs)

#### Current behavior (problematic):
- Lines 64-75: Logs **every line** of stdout and stderr separately at **DEBUG level**
- Line 128 (RunStreamingAsync): Logs each line at DEBUG with overhead
- Each log entry format: `[HH:mm:ss] {logLevel}: {category}: {message}`

```csharp
// Lines 64-75 - Post-process logging of ALL stdout/stderr lines
foreach (var line in stdOutList)
{
    if (!string.IsNullOrWhiteSpace(line))
        logger.LogDebug("{FileName}: {Line}", fileName, line);  // EACH LINE LOGGED
}
foreach (var line in stdErrList)
{
    if (!string.IsNullOrWhiteSpace(line))
        logger.LogDebug("STDERR {FileName}: {Line}", fileName, line);  // EACH LINE LOGGED
}

// Line 128 - Streaming also logs each line
logger.LogDebug("{Name}: {Line}", fileName, line);
```

#### Impact:
- When ffmpeg dumps formats/codecs: **~5,000+ lines** → **5,000+ log entries**
- **68 MB** arm.log with **667,501 lines** over 5.5 hours
- Each entry writes to disk (with WAL) + reads triggered by log rotation/queries

---

### 2. Logging Configuration: DEBUG Enabled for CliProcessRunner

**File:** [src/ArmRipper.WebUi/appsettings.json](src/ArmRipper.WebUi/appsettings.json)

```json
"Logging": {
  "LogLevel": {
    "Default": "Information",
    "Microsoft": "Warning",
    "CliProcessRunner": "Debug",           // ← PROBLEM: DEBUG level
    "ArmRipper.Core.Rip.DiscDb": "Debug",
    "ArmRipper.Core.Rip.TrackMapperService": "Debug",
    "ArmRipperService": "Debug",
    "IdentifyService": "Debug"
  }
}
```

This explicitly sets `CliProcessRunner` to DEBUG, causing all subprocess output to be logged verbosely.

---

### 3. Log File Growth

**Evidence:**
- **arm.log** reached **68 MB** in ~5.5 hours
- **667,501 lines** total
- Each line contains timestamp + category + message + potentially multi-line output
- Writing at DEBUG with every CLI call (ffmpeg, makemkv, filebot, etc.)

---

### 4. FileSystemWatcher Investigation

**Finding:** No problematic FileSystemWatcher found in code.
- The mention of 196 inotify watches in the problem statement likely refers to:
  - dotnet runtime's internal assembly scanning
  - Possible use of file monitoring in other system components
  - Not a code issue in ArmRipper.WebUi

---

## Root Cause Analysis

### The 125 GB Reads Mystery

While the **31 GB writes** are clearly from logging, the **125 GB reads** likely stem from:

1. **Log file I/O repeated reading:**
   - JobFileLoggerProvider writes to arm.log
   - SignalR's NotificationHub.StreamLog reads the file repeatedly (line 21 of NotificationHub.cs)
   - With 667K+ lines, each read operation + file size queries = significant I/O

2. **SQLite WAL (Write-Ahead Log) operations:**
   - arm.db (3.1 MB) + WAL file written continuously
   - Each job update, track write triggers WAL operations

3. **.NET runtime assembly scanning:**
   - Debug mode with hot-reload watchers active
   - Assembly metadata reads, JIT compilation

4. **Swap I/O:**
   - With 16 GB system, 2.3 GB swap used, memory pressure forces reads/writes to swap

---

## Proposed Fixes

### Fix 1: Change CliProcessRunner Logging to TRACE (Code Change)

**File:** [src/ArmRipper.Core/Infrastructure/CliProcessRunner.cs](src/ArmRipper.Core/Infrastructure/CliProcessRunner.cs)

**Action:** Change DEBUG logs to TRACE for subprocess output lines (lines 67, 73, 128, 141).

**Rationale:**
- TRACE level is typically disabled in production
- Allows debug builds to enable if needed: `"CliProcessRunner": "Trace"`
- By default (INFO or higher), subprocess output is NOT logged line-by-line
- Preserves error-level logging for timeouts, failures

**Code locations to change:**
- Line 67: `logger.LogDebug("{FileName}: {Line}", fileName, line);` → `LogTrace`
- Line 73: `logger.LogDebug("STDERR {FileName}: {Line}", fileName, line);` → `LogTrace`
- Line 128: `logger.LogDebug("{Name}: {Line}", fileName, line);` → `LogTrace`
- Line 141: `logger.LogDebug("STDERR {FileName}: {Line}", fileName, errLine);` → `LogTrace`

**Impact:** Single run will reduce logs from 667K lines to ~0 (TRACE disabled).

---

### Fix 2: Update appsettings.json to Remove CliProcessRunner DEBUG

**File:** [src/ArmRipper.WebUi/appsettings.json](src/ArmRipper.WebUi/appsettings.json)

**Action:** Remove or comment out `"CliProcessRunner": "Debug"` line.

```json
"Logging": {
  "LogLevel": {
    "Default": "Information",
    "Microsoft": "Warning",
    // "CliProcessRunner": "Debug",  // ← REMOVED (was causing line logging)
    "ArmRipper.Core.Rip.DiscDb": "Debug",
    "ArmRipper.Core.Rip.TrackMapperService": "Debug",
    "ArmRipperService": "Debug",
    "IdentifyService": "Debug"
  }
}
```

**Rationale:**
- Default (Information) level will only show command start/end, not each line
- Still captures errors and important diagnostics
- Reduces noise significantly

---

### Fix 3: Optional - Summary Logging Instead of Line-by-Line

**File:** [src/ArmRipper.Core/Infrastructure/CliProcessRunner.cs](src/ArmRipper.Core/Infrastructure/CliProcessRunner.cs)

**Action (if line-by-line logging is desired for debugging):** Log a summary instead of individual lines.

**Example:**
```csharp
// Lines 64-75: Instead of logging each line, just log a summary
if (stdOutList.Count > 0)
{
    logger.LogDebug(
        "Process {FileName} produced {LineCount} lines of output",
        fileName, stdOutList.Count);
}
if (stdErrList.Count > 0)
{
    logger.LogDebug(
        "Process {FileName} produced {LineCount} lines of stderr",
        fileName, stdErrList.Count);
}
```

**Impact:** Reduces 5,000 log entries to 1-2 summary entries per command.

---

## Testing the Fixes

1. **Before:** Run a job with ffmpeg codec list query
   - Monitor arm.log size growth
   - Check line count: `wc -l /home/arm/logs/arm.log`
   - System I/O metrics

2. **After:** Apply fixes and run same job
   - arm.log should show no subprocess output lines
   - Line count should be ~50-100x lower
   - System I/O should drop significantly

---

## Implementation Status

- [x] Root cause analysis complete
- [x] Fixes designed
- [ ] Code changes implemented
- [ ] Testing performed
- [ ] PR created for review

---

## Recommendations

### Immediate:
1. Apply Fix 1 (change DEBUG → TRACE in CliProcessRunner)
2. Apply Fix 2 (remove CliProcessRunner DEBUG from appsettings.json)

### Short-term:
1. Add conditional logging: only log verbose output if DEBUG is enabled AND a flag is set
2. Consider using IAsyncEnumerable chunking for streaming output

### Long-term:
1. Implement structured logging with log sinks (e.g., Application Insights, ELK)
2. Add metrics collection for subprocess execution times/exit codes
3. Consider splitting CLI logging into separate diagnostic stream

---

## References

- [CliProcessRunner.cs](src/ArmRipper.Core/Infrastructure/CliProcessRunner.cs)
- [appsettings.json](src/ArmRipper.WebUi/appsettings.json)
- Original investigation notes in `/memories/repo/logging-notes.md`
