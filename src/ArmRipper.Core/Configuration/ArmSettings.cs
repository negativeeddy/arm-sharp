namespace ArmRipper.Core.Configuration;

public class ArmSettings
{
    public const string SectionName = "Arm";

    public string? RawPath { get; set; } = "/home/arm/media/raw";
    public string? TranscodePath { get; set; } = "/home/arm/media/transcode";
    public string? CompletedPath { get; set; } = "/home/arm/media";
    public string? LogPath { get; set; } = "/home/arm/logs";
    public string? DbFile { get; set; } = "/etc/arm/config/arm.db";
    public string? InstallPath { get; set; } = "/opt/arm";

    public bool SkipTranscode { get; set; }
    public bool MainFeature { get; set; }
    public bool UseFfmpeg { get; set; }
    public bool ManualWait { get; set; }
    public bool AllowDuplicates { get; set; }
    public bool Prevent99 { get; set; }
    public bool GetVideoTitle { get; set; } = true;
    public bool GetAudioTitle { get; set; } = true;
    public bool AutoEject { get; set; } = true;
    public bool DelRawFiles { get; set; } = true;

    public string? RipMethod { get; set; } = "mkv";
    public string? MkvArgs { get; set; } = "";
    public int MinLength { get; set; }
    public int MaxLength { get; set; } = 99999;

    public string? HbPresetDvd { get; set; } = "Very Fast 1080p30";
    public string? HbPresetBd { get; set; } = "Very Fast 1080p30";
    public string? DestExt { get; set; } = "mp4";

    public bool NotifyRip { get; set; } = true;
    public bool NotifyTranscode { get; set; } = true;

    public string? MetadataProvider { get; set; } = "omdb";
    public string? OmdbApiKey { get; set; }
    public string? TmdbApiKey { get; set; }

    public string? WebServerIp { get; set; } = "0.0.0.0";
    public int WebServerPort { get; set; } = 8080;

    public int MaxConcurrentTranscodes { get; set; } = 2;
    public int MaxConcurrentMakemkvInfo { get; set; } = 1;
}
