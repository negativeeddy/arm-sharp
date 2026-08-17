namespace ArmRipper.Core.Rip;

/// <summary>
/// Tracks the currently-active MakeMKV rip for each job so the UI can request a
/// mid-rip redirect: the running rip is cancelled and the pipeline re-rips the
/// newly-selected track. Registered as a singleton so the scoped rip pipeline
/// and the (separately scoped) web API controller share one instance.
/// </summary>
public interface IRipRedirectService
{
    /// <summary>
    /// Registers the active rip for the job and returns a cancellation source
    /// whose token is cancelled when a redirect is requested. The source is
    /// linked to the pipeline token, so cancelling it does not cancel the
    /// pipeline itself. Cancels immediately if a redirect is already pending for
    /// the job.
    /// </summary>
    CancellationTokenSource BeginRip(int jobId, CancellationToken pipelineCt);

    /// <summary>
    /// Requests a redirect for the job. Marks a redirect as pending and cancels
    /// the active rip (if any). Returns true only when a rip was actively in
    /// progress and was cancelled — returns false when MakeMKV has already exited.
    /// </summary>
    bool RequestRedirect(int jobId);

    /// <summary>True if a redirect has been requested and not yet acknowledged.</summary>
    bool WasRedirectRequested(int jobId);

    /// <summary>Clears the pending-redirect flag once the pipeline has handled it.</summary>
    void AcknowledgeRedirect(int jobId);

    /// <summary>Stops tracking the job's rip and releases its cancellation source.</summary>
    void EndRip(int jobId);
}
