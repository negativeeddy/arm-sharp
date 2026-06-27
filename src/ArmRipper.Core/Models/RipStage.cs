namespace ArmRipper.Core.Models;

public enum RipStage
{
    Setup,
    Identify,
    Rip,
    Transcode,
    Finalize,
    Done,
    CrcSubmitted
}

public static class RipStageExtensions
{
    public static string ToClientString(this RipStage stage) => stage switch
    {
        RipStage.Setup => "Setup",
        RipStage.Identify => "Identify",
        RipStage.Rip => "Rip",
        RipStage.Transcode => "Transcode",
        RipStage.Finalize => "Finalize",
        RipStage.Done => "Done",
        RipStage.CrcSubmitted => "CrcSubmitted",
        _ => "Setup"
    };
}
