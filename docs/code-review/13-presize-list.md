# Pre-Size `List<string>` in `ReadAllLinesAsync`

**Priority:** 🟢 Low
**File:** `src/ArmRipper.Core/Infrastructure/CliProcessRunner.cs`
**Status:** ⬜ Todo

---

## Problem

`CliProcessRunner.ReadAllLinesAsync` creates a `List<string>` with default capacity:

```csharp
private static async Task<List<string>> ReadAllLinesAsync(
    StreamReader reader, CancellationToken ct)
{
    var lines = new List<string>();  // ← default capacity (4, grows to 8, 16, 32, ...)
    while (await reader.ReadLineAsync(ct) is { } line)
        lines.Add(line);
    return lines;
}
```

For MakeMKV info output (which can produce thousands of lines), this triggers multiple internal
array resizes and copies. Each resize doubles the capacity and copies all existing elements.

## Proposed Fix

Pre-size with a reasonable initial capacity:

```csharp
private static async Task<List<string>> ReadAllLinesAsync(
    StreamReader reader, CancellationToken ct)
{
    var lines = new List<string>(capacity: 256);
    while (await reader.ReadLineAsync(ct) is { } line)
        lines.Add(line);
    return lines;
}
```

256 is a sensible default — most process outputs are well under this, and for those that aren't,
it significantly reduces the number of resizes.

### Alternative (higher effort, better for extreme cases)

If MakeMKV output regularly exceeds 10,000 lines, consider using `ArrayPool<string>` or
returning the lines via `IAsyncEnumerable<string>` instead of buffering all in memory:

```csharp
public static async IAsyncEnumerable<string> ReadLinesAsync(
    StreamReader reader,
    [EnumeratorCancellation] CancellationToken ct)
{
    while (await reader.ReadLineAsync(ct) is { } line)
        yield return line;
}
```

But this changes the API surface of `CliProcessRunner` — only do it if profiling shows a real
memory pressure issue.
