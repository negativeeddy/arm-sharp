namespace ArmRipper.Core.Configuration;

public class ArmSettings
{
    public const string SectionName = "Arm";

    public string? RawPath { get; set; } = "/home/arm/media/raw";
    public string? TranscodePath { get; set; } = "/home/arm/media/transcode";
    public string? CompletedPath { get; set; } = "/home/arm/media";
    public string? LogPath { get; set; } = "/home/arm/logs";
    public string? DbFile { get; set; } = "/etc/arm/config/arm-sharp.db";
    public string? InstallPath { get; set; } = "/opt/arm";

    public bool SkipTranscode { get; set; }
    public bool MainFeature { get; set; } = true;
    public bool UseFfmpeg { get; set; }
    public bool ManualWait { get; set; } = true;
    public int ManualWaitTime { get; set; } = 60;
    public bool AllowDuplicates { get; set; } = true;
    public bool Prevent99 { get; set; } = true;
    public bool GetVideoTitle { get; set; } = true;
    public string? GetAudioTitle { get; set; } = "musicbrainz";
    public bool AutoEject { get; set; } = true;
    public bool DelRawFiles { get; set; } = false;

    public string? RipMethod { get; set; } = "mkv";
    public string? MkvArgs { get; set; } = "";
    public int MinLength { get; set; } = 600;
    public int MaxLength { get; set; } = 99999;

    public string? HbPresetDvd { get; set; } = "";
    public string? HbPresetBd { get; set; } = "";
    public string? HbArgsDvd { get; set; } = "-e nvenc_h264 --encoder-preset slower --quality 18 --enable-hw-decoding nvdec --encopts spatial-aq=1:aq-strength=10:bf=4:cabac=1:g=50:keyint-min=23 --all-audio --all-subtitles --subtitle-burned=none --aencoder aac --audio-fallback aac --mixdown none";
    public string? HbArgsBd { get; set; } = "-e nvenc_h265 --encoder-preset slower --quality 18 --enable-hw-decoding nvdec --encopts spatial-aq=1:aq-strength=10:g=50:keyint-min=23 --all-audio --all-subtitles --subtitle-burned=none --aencoder aac --audio-fallback aac --mixdown none";
    public string? DestExt { get; set; } = "mkv";

    public string? FfmpegCli { get; set; } = "ffmpeg";
    public string? FfmpegPreFileArgs { get; set; }
    public string? FfmpegPostFileArgs { get; set; } = "-fflags +genpts -c:v copy -c:a aac -b:a 640k -c:s copy -map 0";

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

    public int MaxConcurrentRips { get; set; } = 1;
    public int MaxConcurrentTranscodes { get; set; }
    public int MaxConcurrentMakemkvInfo { get; set; }

    public string? MakeMkvPermaKey { get; set; }
    public bool TestMode { get; set; }
    public bool DisableLogin { get; set; } = true;

    // Backward-compatible naming aliases.
    // Marked [JsonIgnore] so they are never persisted to the DB — only the
    // canonical property names (Prevent99, GetAudioTitle, DelRawFiles) are stored.
    [System.Text.Json.Serialization.JsonIgnore]
    public bool PreventTrack99
    {
        get => Prevent99;
        set => Prevent99 = value;
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public string? AudioMetadataProvider
    {
        get => GetAudioTitle;
        set => GetAudioTitle = value;
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool DeleteRawFiles
    {
        get => DelRawFiles;
        set => DelRawFiles = value;
    }
}
