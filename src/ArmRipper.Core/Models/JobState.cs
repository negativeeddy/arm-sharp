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

public static class JobStateExtensions
{
    public static string ToDbString(this JobState state) => state switch
    {
        JobState.Success => "success",
        JobState.Failure => "fail",
        JobState.Active => "active",
        JobState.VideoRipping => "ripping",
        JobState.VideoWaiting => "waiting",
        JobState.VideoInfo => "info",
        JobState.AudioRipping => "ripping",
        JobState.TranscodeActive => "transcoding",
        JobState.TranscodeWaiting => "waiting_transcode",
        JobState.ManualWaitStarted => "waiting",
        JobState.Cancelled => "cancelled",
        _ => "active"
    };

    public static bool IsTerminal(this JobState state) =>
        state is JobState.Success or JobState.Failure or JobState.Cancelled;

    public static JobState FromDbString(string value) => value switch
    {
        "success" => JobState.Success,
        "fail" => JobState.Failure,
        "active" => JobState.Active,
        "ripping" => JobState.VideoRipping,
        "waiting" => JobState.VideoWaiting,
        "info" => JobState.VideoInfo,
        "transcoding" => JobState.TranscodeActive,
        "waiting_transcode" => JobState.TranscodeWaiting,
        "cancelled" => JobState.Cancelled,
        _ => JobState.Active
    };
}
