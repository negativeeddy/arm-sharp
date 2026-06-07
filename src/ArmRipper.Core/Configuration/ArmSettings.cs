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
    public string? GetAudioTitle { get; set; } = "musicbrainz";
    public bool AutoEject { get; set; } = true;
    public bool DelRawFiles { get; set; } = true;

    public string? RipMethod { get; set; } = "mkv";
    public string? MkvArgs { get; set; } = "";
    public int MinLength { get; set; } = 600;
    public int MaxLength { get; set; } = 99999;

    public string? HbPresetDvd { get; set; } = "Very Fast 1080p30";
    public string? HbPresetBd { get; set; } = "Very Fast 1080p30";
    public string? HbArgsDvd { get; set; }
    public string? HbArgsBd { get; set; }
    public string? DestExt { get; set; } = "mp4";

    public string? FfmpegCli { get; set; } = "ffmpeg";
    public string? FfmpegPreFileArgs { get; set; }
    public string? FfmpegPostFileArgs { get; set; }

    public string? ExtrasSub { get; set; }

    public bool NotifyRip { get; set; } = true;
    public bool NotifyTranscode { get; set; } = true;
    public string? PbKey { get; set; }
    public string? IftttKey { get; set; }
    public string? PoUserKey { get; set; }
    public string? BashScript { get; set; }
    public string? JsonUrl { get; set; }
    public string? Apprise { get; set; }

    public string? ArmApiKey { get; set; }
    public string? MetadataProvider { get; set; } = "omdb";
    public string? OmdbApiKey { get; set; }
    public string? TmdbApiKey { get; set; }

    public string? WebServerIp { get; set; } = "0.0.0.0";
    public int WebServerPort { get; set; } = 8080;
    public string? UiBaseUrl { get; set; }

    public bool EmbyRefresh { get; set; }
    public string? EmbyServer { get; set; }
    public int? EmbyPort { get; set; }
    public string? EmbyApiKey { get; set; }

    public int MaxConcurrentTranscodes { get; set; } = 2;
    public int MaxConcurrentMakemkvInfo { get; set; } = 1;

    public string? MakeMkvPermaKey { get; set; }
    public bool TestMode { get; set; }
}
