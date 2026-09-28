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
    /// <summary>Sample/pixel aspect ratio as reported by ffprobe, e.g. "64:45". Null for square pixels or when unknown.</summary>
    public string? SampleAspectRatio { get; set; }
    /// <summary>Display aspect ratio as reported by ffprobe, e.g. "1024:552" or "185:100". Null when unknown.</summary>
    public string? DisplayAspectRatio { get; set; }
    /// <summary>Numerical pixel aspect ratio (1.0 = square pixels).</summary>
    public double? PixelAspectRatio { get; set; }
    /// <summary>Width the video must be scaled to when rendered, accounting for non-square (anamorphic) pixels.</summary>
    public int DisplayWidth { get; set; }
    /// <summary>Height the video is rendered at (anamorphy only affects horizontal scaling).</summary>
    public int DisplayHeight { get; set; }
    /// <summary>Display aspect ratio (rendered width / height), preferred over the coded ratio when pixels are non-square.</summary>
    public double? DisplayAspect { get; set; }
    /// <summary>True when the video is stored with non-square (anamorphic) pixels, e.g. DVD 16:9 or 4:3 letterboxed content.</summary>
    public bool IsAnamorphic
    {
        get
        {
            if (PixelAspectRatio is not { } par) return false;
            if (par <= 0) return false;
            if (DisplayWidth <= 0 || DisplayWidth == Width) return false;
            return Math.Abs(par - 1.0) > 0.02;
        }
    }
    /// <summary>Aspect ratio used for classification: display ratio when known, otherwise the coded ratio.</summary>
    public double Aspect => DisplayAspect is > 0 ? DisplayAspect.Value : Height > 0 ? (double)Width / Height : 0;
    /// <summary>True when the display aspect ratio is approximately 4:3 (fullscreen), which is unusual for modern movies.</summary>
    public bool IsFullScreen => Aspect is > 1.30 and < 1.40;
    /// <summary>Display aspect ratio as a human-readable string like "16:9" or "4:3".</summary>
    public string? AspectRatioFormatted
    {
        get
        {
            if (Aspect <= 0) return null;
            return Aspect switch
            {
                >= 2.33 and <= 2.40 => "21:9",
                >= 1.76 and <= 1.79 => "16:9",
                >= 1.49 and <= 1.51 => "3:2",
                >= 1.32 and <= 1.34 => "4:3",
                _ => $"{Aspect:F2}:1"
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
