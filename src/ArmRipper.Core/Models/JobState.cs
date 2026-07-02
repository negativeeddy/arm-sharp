namespace ArmRipper.Core.Models;

public enum JobState
{
    Success,
    Failure,
    Active,
    VideoRipping,
    VideoWaiting,
    VideoInfo,
    AudioRipping,
    TranscodeActive,
    TranscodeWaiting,
    ManualWaitStarted,
    /// <summary>Job was cancelled during app shutdown — safe to resume from completed stages.</summary>
    Stopping,
    Cancelled
}

public static class JobStateDbValues
{
    public const string Success = "success";
    public const string Failure = "fail";
    public const string Active = "active";
    public const string Ripping = "ripping";
    public const string Waiting = "waiting";
    public const string Info = "info";
    public const string Transcoding = "transcoding";
    public const string WaitingTranscode = "waiting_transcode";
    public const string Cancelled = "cancelled";
    public const string Stopping = "stopping";
}

public static class JobStateExtensions
{
    public static string ToDbString(this JobState state) => state switch
    {
        JobState.Success => JobStateDbValues.Success,
        JobState.Failure => JobStateDbValues.Failure,
        JobState.Active => JobStateDbValues.Active,
        JobState.VideoRipping => JobStateDbValues.Ripping,
        JobState.VideoWaiting => JobStateDbValues.Waiting,
        JobState.VideoInfo => JobStateDbValues.Info,
        JobState.AudioRipping => JobStateDbValues.Ripping,
        JobState.TranscodeActive => JobStateDbValues.Transcoding,
        JobState.TranscodeWaiting => JobStateDbValues.WaitingTranscode,
        JobState.ManualWaitStarted => JobStateDbValues.Waiting,
        JobState.Cancelled => JobStateDbValues.Cancelled,
        JobState.Stopping => JobStateDbValues.Stopping,
        _ => JobStateDbValues.Active
    };

    public static bool IsTerminal(this JobState state) =>
        state is JobState.Success or JobState.Failure or JobState.Cancelled;

    /// <summary>Returns true when the job is in a state where it can be resumed
    /// (was interrupted by shutdown or cancellation, has completed stages to pick up from).</summary>
    public static bool IsResumable(this JobState state) =>
        state is JobState.Stopping or JobState.Cancelled;

    /// <summary>Returns true when the job is in a state where the optical drive is
    /// actively being used for ripping (i.e. the drive is busy).</summary>
    public static bool IsRippingState(this JobState state) => state switch
    {
        JobState.Active or JobState.VideoRipping or JobState.VideoWaiting
            or JobState.VideoInfo or JobState.AudioRipping
            or JobState.ManualWaitStarted => true,
        _ => false
    };

    public static JobState FromDbString(string value) => value switch
    {
        JobStateDbValues.Success => JobState.Success,
        JobStateDbValues.Failure => JobState.Failure,
        JobStateDbValues.Active => JobState.Active,
        JobStateDbValues.Ripping => JobState.VideoRipping,
        JobStateDbValues.Waiting => JobState.VideoWaiting,
        JobStateDbValues.Info => JobState.VideoInfo,
        JobStateDbValues.Transcoding => JobState.TranscodeActive,
        JobStateDbValues.WaitingTranscode => JobState.TranscodeWaiting,
        JobStateDbValues.Cancelled => JobState.Cancelled,
        JobStateDbValues.Stopping => JobState.Stopping,
        _ => JobState.Active
    };
}
