# Fix `JobLogger` Thread-Safety

**Priority:** 🔴 Critical
**File:** `src/ArmRipper.Core/Infrastructure/JobLogger.cs`
**Status:** ⬜ Todo

---

## Problem

`JobLogger` wraps a `StreamWriter` which is **not thread-safe**. Multiple concurrent `Log<TState>`
calls from different threads (common when multiple background services log to the same job) will
interleave or corrupt output:

```csharp
// Line 16
_fileWriter = new StreamWriter(_logPath, append: true) { AutoFlush = true };

public void Log<TState>(...) // line 25 — no synchronization
{
    var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{logLevel}] {formatter(state, exception)}";
    _fileWriter.WriteLine(line); // ← race: interleaved writes
    _inner.Log(logLevel, eventId, state, exception, formatter);
}
```

Symptoms: garbled log lines, partial writes, `ObjectDisposedException` if one thread disposes
while another writes.

## Proposed Fix

Wrap the file write with a lock:

```csharp
private readonly object _writeLock = new();

public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
    Exception? exception, Func<TState, Exception?, string> formatter)
{
    var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{logLevel}] {formatter(state, exception)}";
    lock (_writeLock)
        _fileWriter.WriteLine(line);
    _inner.Log(logLevel, eventId, state, exception, formatter);
}
```

For the `Dispose`/`DisposeAsync` methods, acquire the same lock before flushing/closing.

### Alternative (higher throughput)

Use a `Channel<string>` with a dedicated write loop:

```csharp
private readonly Channel<string> _channel = Channel.CreateUnbounded<string>();

public JobLogger(...)
{
    _ = Task.Run(WriteLoopAsync);
}

private async Task WriteLoopAsync()
{
    await foreach (var line in _channel.Reader.ReadAllAsync())
        await _fileWriter.WriteLineAsync(line);
}

public void Log<TState>(...) =>
    _channel.Writer.TryWrite(line);
```

The lock is simpler and sufficient for this use case (job logs are low-volume).
