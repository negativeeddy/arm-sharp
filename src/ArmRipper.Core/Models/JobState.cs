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
    ManualWaitStarted
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
        _ => "active"
    };

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
        _ => JobState.Active
    };
}
