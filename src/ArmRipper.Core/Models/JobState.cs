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
        _ => JobStateDbValues.Active
    };

    public static bool IsTerminal(this JobState state) =>
        state is JobState.Success or JobState.Failure or JobState.Cancelled;

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
        _ => JobState.Active
    };
}
