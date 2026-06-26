namespace ArmRipper.WebUi.Models;

public class CompletedFileInfo
{
    public string FilePath { get; set; } = "";
    public string FileName => Path.GetFileName(FilePath);
    public string RelativeDirectory { get; set; } = "";
    /// <summary>Which directory the file came from: "Output", "Raw", or "Transcode".</summary>
    public string Source { get; set; } = "Output";
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
