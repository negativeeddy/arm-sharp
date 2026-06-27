namespace ArmRipper.Core.Configuration;

/// <summary>
/// Identifies which stage/media directory a completed file originated from.
/// Used by <see cref="CompletedFileInfo.Source"/> and path-resolution logic.
/// </summary>
public enum FileSource
{
    /// <summary>File lives under the raw (MakeMKV) directory.</summary>
    Raw,
    /// <summary>File lives under the transcode (HandBrake/FFmpeg) working directory.</summary>
    Transcode,
    /// <summary>File lives under the final completed output directory, ready for playback.</summary>
    Completed
}
