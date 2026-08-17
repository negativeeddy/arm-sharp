using System.ComponentModel.DataAnnotations;

namespace ArmRipper.Core.Models;

/// <summary>
/// Pipeline stages for a rip job. Each value has a stable <see cref="DisplayAttribute"/>
/// Name that is used as the serialized key in <see cref="Job.CompletedStages"/>.
/// 
/// IMPORTANT: Never rename the <c>Display(Name = "...")</c> value — only rename
/// the C# member. Old jobs store the Display name; renaming it would break resume
/// for jobs that completed the stage under the old name.
/// </summary>
public enum RipStage
{
    [Display(Name = "Setup")]
    Setup,

    [Display(Name = "Identify")]
    Identify,

    [Display(Name = "Rip")]
    Rip,

    [Display(Name = "Transcode")]
    Transcode,

    [Display(Name = "Finalize")]
    Finalize,

    [Display(Name = "Done")]
    Done,

    [Display(Name = "CrcSubmitted")]
    CrcSubmitted
}

public static class RipStageExtensions
{
    /// <summary>Returns the stable serialized key for the stage (from <see cref="DisplayAttribute.Name"/>).</summary>
    public static string ToStageKey(this RipStage stage)
    {
        var field = typeof(RipStage).GetField(stage.ToString())
            ?? throw new ArgumentOutOfRangeException(nameof(stage));
        var attr = Attribute.GetCustomAttribute(field, typeof(DisplayAttribute)) as DisplayAttribute;
        return attr?.Name ?? stage.ToString();
    }

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
