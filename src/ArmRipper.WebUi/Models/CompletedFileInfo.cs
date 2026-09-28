using ArmRipper.Core.Configuration;

namespace ArmRipper.WebUi.Models;

public class CompletedFileInfo
{
    public string FilePath { get; set; } = "";
    public string FileName => Path.GetFileName(FilePath);
    public string RelativeDirectory { get; set; } = "";
    /// <summary>Which directory the file came from (see <see cref="FileSource"/>).</summary>
    public FileSource Source { get; set; } = FileSource.Completed;
    /// <summary>True if the file lives in an "extras" subfolder and can be promoted to a standalone movie.</summary>
    public bool IsExtra
    {
        get
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (string.IsNullOrEmpty(dir)) return false;
            var segments = dir.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return segments.Contains("extras", StringComparer.OrdinalIgnoreCase);
        }
    }
    /// <summary>The job ID that produced this file (if one can be determined).</summary>
    public int? JobId { get; set; }
    /// <summary>If the file is a raw file, the original job ID it was ripped from (kept for backward compat).</summary>
    public int? OriginalJobId { get; set; }
    public string? JobTitle { get; set; }
    public DateTime LastModified { get; set; }
    public string LastModifiedFormatted => LastModified.ToString("yyyy-MM-dd HH:mm");
    public long SizeBytes { get; set; }
    public string SizeFormatted => SizeBytes switch
    {
        >= 1_000_000_000 => $"{SizeBytes / 1_000_000_000.0:F2} GB",
        >= 1_000_000 => $"{SizeBytes / 1_000_000.0:F1} MB",
        _ => $"{SizeBytes / 1_000.0:F0} KB"
    };
    public double DurationSeconds { get; set; }
    public string DurationFormatted
    {
        get
        {
            var ts = TimeSpan.FromSeconds(DurationSeconds);
            return ts.Hours > 0
                ? $"{ts.Hours}h {ts.Minutes}m {ts.Seconds}s"
                : $"{ts.Minutes}m {ts.Seconds}s";
        }
    }
    public double BitrateKbps { get; set; }

    public VideoStreamInfo? Video { get; set; }
    public List<AudioStreamInfo> AudioStreams { get; set; } = [];
    public List<SubtitleStreamInfo> SubtitleStreams { get; set; } = [];
}

public class VideoStreamInfo
{
    public string CodecName { get; set; } = "";
    public string CodecLongName { get; set; } = "";
    public string Profile { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public string PixelFormat { get; set; } = "";
    public string FrameRate { get; set; } = "";
    public string ColorSpace { get; set; } = "";
    public string ColorTransfer { get; set; } = "";
    public bool IsHdr => ColorTransfer is "smpte2084" or "arib-std-b67";
    public int? BFrames { get; set; }
    /// <summary>True when the display aspect ratio is approximately 4:3 (fullscreen), which is unusual for modern movies.</summary>
    public bool IsFullScreen => Height > 0 && (double)Width / Height is > 1.30 and < 1.40;
    /// <summary>Display aspect ratio as a human-readable string like "16:9" or "4:3".</summary>
    public string? AspectRatioFormatted
    {
        get
        {
            if (Height <= 0) return null;
            var ratio = (double)Width / Height;
            return ratio switch
            {
                >= 2.33 and <= 2.40 => "21:9",
                >= 1.76 and <= 1.79 => "16:9",
                >= 1.49 and <= 1.51 => "3:2",
                >= 1.32 and <= 1.34 => "4:3",
                _ => $"{ratio:F2}:1"
            };
        }
    }
}

public class AudioStreamInfo
{
    public string CodecName { get; set; } = "";
    public string CodecLongName { get; set; } = "";
    public int Channels { get; set; }
    public string ChannelLayout { get; set; } = "";
    public int SampleRate { get; set; }
    public int Bitrate { get; set; }
    public string BitrateFormatted => Bitrate > 0 ? $"{Bitrate / 1000} kbps" : "VBR";
    public string Language { get; set; } = "";
    public string? Title { get; set; }
}

public class SubtitleStreamInfo
{
    public string CodecName { get; set; } = "";
    public string Language { get; set; } = "";
    public bool Forced { get; set; }
}
