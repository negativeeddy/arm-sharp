using System.Collections.Concurrent;

namespace ArmRipper.Core.Rip;

/// <summary>
/// In-memory registry of active rip cancellation sources, keyed by job ID.
/// Singleton: the WebUi API controller (which persists the redirect request) and
/// the scoped rip pipeline (which observes it) share the same instance.
/// </summary>
public sealed class RipRedirectService : IRipRedirectService
{
    private readonly ConcurrentDictionary<int, CancellationTokenSource> _ripCts = new();
    private readonly ConcurrentDictionary<int, byte> _redirectPending = new();
    private readonly ConcurrentDictionary<int, byte> _activeRips = new();

    public CancellationTokenSource BeginRip(int jobId, CancellationToken pipelineCt)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(pipelineCt);

        // Mark the rip as active before registering the CTS. This ordering
        // ensures RequestRedirect never sees a stale CTS entry without the
        // active flag — it will always return false for an already-finished rip.
        _activeRips[jobId] = 0;

        // Register first, then check for a pending redirect. RequestRedirect
        // cancels whichever CTS is currently registered, so storing the source
        // before the check closes the race where a redirect landing between the
        // check and the store would be lost (the new rip would never see it).
        _ripCts[jobId] = cts;
        if (_redirectPending.ContainsKey(jobId))
            cts.Cancel();
        return cts;
    }

    public bool RequestRedirect(int jobId)
    {
        _redirectPending[jobId] = 0;

        // Only return true when a rip is actively in progress. If MakeMKV has
        // already exited the CTS may still linger in _ripCts (EndRip has not
        // run yet), but nobody will observe the cancellation — returning true
        // would mislead the UI into saying "rip will restart" when it won't.
        if (!_activeRips.ContainsKey(jobId))
            return false;

        if (!_ripCts.TryGetValue(jobId, out var cts))
            return false;
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        return true;
    }

    public bool WasRedirectRequested(int jobId) => _redirectPending.ContainsKey(jobId);

    public void AcknowledgeRedirect(int jobId) => _redirectPending.TryRemove(jobId, out _);

    public void EndRip(int jobId)
    {
        _activeRips.TryRemove(jobId, out _);
        _redirectPending.TryRemove(jobId, out _);
        if (_ripCts.TryRemove(jobId, out var cts))
            cts.Dispose();
    }
}
