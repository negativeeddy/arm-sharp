# Consider `Channel<T>` for `DiscPollingService` Events

**Priority:** 🟢 Low
**File:** `src/ArmRipper.Core/Infrastructure/DiscPollingService.cs`
**Status:** ⬜ Todo

---

## Problem

`DiscPollingService` uses a complex state machine with:
- `SemaphoreSlim` for setting-change signaling
- `ConcurrentDictionary` for inflight check deduplication
- `CancellationTokenSource` for pump lifecycle
- Manual task management (`_pumpTask`, `_pumpCts`)

While functionally correct, the interplay between these primitives is hard to reason about and
hard to test.

## Proposed Fix

Replace the manual coordination with a `Channel<DiscEvent>` that serializes all disc events:

```csharp
public sealed class DiscPollingService : BackgroundService, IDiscPollingNotifier
{
    private readonly Channel<DiscEvent> _eventChannel =
        Channel.CreateBounded<DiscEvent>(new BoundedChannelOptions(32)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });

    // UeventMonitor writes events into the channel
    private async Task PumpUeventsAsync(CancellationToken ct)
    {
        await foreach (var msg in _monitor!.ListenAsync(ct))
        {
            var evt = new DiscEvent(msg.DevPath, msg.Action);
            await _eventChannel.Writer.WriteAsync(evt, ct);
        }
    }

    // Single consumer processes events sequentially
    private async Task ProcessEventsAsync(CancellationToken ct)
    {
        await foreach (var evt in _eventChannel.Reader.ReadAllAsync(ct))
        {
            await HandleDiscEventAsync(evt, ct);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_monitor?.TryStart() == true)
        {
            var pumpTask = PumpUeventsAsync(stoppingToken);
            var processTask = ProcessEventsAsync(stoppingToken);
            await Task.WhenAll(pumpTask, processTask);
        }
    }
}

internal record DiscEvent(string DevPath, string Action);
```

### Benefits

- Single-threaded event processing eliminates the need for `ConcurrentDictionary`
- `Channel` provides backpressure (bounded capacity)
- The consumer loop is trivial to test (just write to the channel and observe)
- Setting-change signals can also be written as events into the same channel

### When to do this

This is low-priority because the current implementation works. Do it when:
- Adding new features to disc detection (the complexity payoff grows)
- Debugging a race condition in disc detection
- Improving test coverage of `DiscPollingService`
