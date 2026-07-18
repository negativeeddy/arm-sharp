namespace ArmRipper.Core.Configuration;

public class ArmSettings
{
    public const string SectionName = "Arm";

    public string? RawPath { get; set; } = ArmPaths.DefaultRawPath;
    public string? TranscodePath { get; set; } = ArmPaths.DefaultTranscodePath;
    public string? CompletedPath { get; set; } = ArmPaths.DefaultCompletedPath;
    public string? LogPath { get; set; } = ArmPaths.DefaultLogPath;
    public string? DbFile { get; set; } = ArmPaths.DefaultDbFile;
    public string? InstallPath { get; set; } = ArmPaths.DefaultInstallPath;

    public bool SkipTranscode { get; set; }
    public bool MainFeature { get; set; } = true;
    public bool UseFfmpeg { get; set; }
    public bool ManualWait { get; set; } = true;
    public int ManualWaitTime { get; set; } = 60;
    public bool AllowDuplicates { get; set; } = true;
    public bool PreferWidescreen { get; set; } = true;
    public bool Prevent99 { get; set; } = true;
    public bool GetVideoTitle { get; set; } = true;
    public string? GetAudioTitle { get; set; } = "musicbrainz";
    public bool AutoEject { get; set; } = true;
    public bool DelRawFiles { get; set; } = false;

    public bool SetMediaPermissions { get; set; } = true;
    public string? ChmodValue { get; set; } = "777";
    public bool SetMediaOwner { get; set; } = false;
    public string? ChownUser { get; set; } = "arm";
    public string? ChownGroup { get; set; } = "arm";

    public string? RipMethod { get; set; } = "mkv";
    public string? MkvArgs { get; set; } = "";
    public int MinLength { get; set; } = 300;
    public int MaxLength { get; set; } = 99999;

    public string? HbPresetDvd { get; set; } = "";
    public string? HbPresetBd { get; set; } = "";
    public string? HbArgsDvd { get; set; } = "-e nvenc_h264 --encoder-preset slower --quality 18 --enable-hw-decoding nvdec --encopts spatial-aq=1:aq-strength=10:bf=4:cabac=1:g=50:keyint-min=23 --comb-detect --decomb --all-audio --all-subtitles --subtitle-burned=none --aencoder aac --audio-fallback aac --mixdown none";
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
    public string? TvdbApiKey { get; set; }

    /// <summary>
    /// OAuth bearer token for authenticated OVID API operations
    /// (fingerprint registration and disc submission).
    /// Obtainable via OAuth at https://api.oviddb.org.
    /// </summary>
    public string? OvidApiToken { get; set; }

    public string? WebServerIp { get; set; } = "0.0.0.0";
    public int WebServerPort { get; set; } = 8080;
    public string? UiBaseUrl { get; set; }

    public bool EmbyRefresh { get; set; }
    public string? EmbyServer { get; set; }
    public int? EmbyPort { get; set; }
    public string? EmbyApiKey { get; set; }

    /// <summary>
    /// Maximum number of simultaneous transcode processes.
    /// Default: 1 (serialized) — parallel transcodes rarely gain time and
    /// serializing makes logs easier to debug.
    /// </summary>
    public int MaxConcurrentTranscodes { get; set; } = 1;
    public int MaxConcurrentMakemkvInfo { get; set; }

    public string? MakeMkvPermaKey { get; set; }
    public bool TestMode { get; set; }
    public bool DisableLogin { get; set; } = true;

    // ── Disc auto-polling ──
    /// <summary>Whether to poll optical drives for disc insertion. Default: true.</summary>
    public bool DiscPollingEnabled { get; set; } = true;
    /// <summary>Poll interval in seconds. Default: 5.</summary>
    public int DiscPollIntervalSeconds { get; set; } = 5;
    /// <summary>
    /// Cooldown in seconds after a rip completes before the same device is eligible
    /// for a new auto-rip. Prevents the event-driven monitor from re-triggering a rip
    /// while the disc is still physically ejecting. Default: 15.
    /// </summary>
    public int EjectCooldownSeconds { get; set; } = 15;

    // ── TheDiscDb Integration ──
    public bool DiscDbEnabled { get; set; } = true;
    // public string? DiscDbApiBaseUrl { get; set; } = "https://api.thediscdb.com/graphql";
    public string? DiscDbApiBaseUrl { get; set; } = "https://thediscdb.com/graphql";

    // ── MakeMKV I/O Watchdog ──
    /// <summary>
    /// Maximum bytes a <c>makemkvcon</c> process can read from disc before the I/O watchdog
    /// cancels the job. This prevents runaway read retry storms on scratched/damaged discs.
    /// Set to 0 to disable the watchdog entirely.
    /// Default: 150 GiB (approximately 3× a BDXL disc, well above any legitimate single rip).
    /// </summary>
    public long MakemkvMaxReadBytes { get; set; } = 150L * 1024 * 1024 * 1024;

    /// <summary>
    /// Polling interval in seconds for the MakeMKV I/O watchdog. Default: 30.
    /// Minimum effective value is 10 seconds.
    /// </summary>
    public int MakemkvIoWatchdogIntervalSeconds { get; set; } = 30;

    // ── OVID Integration ──
    /// <summary>Whether to submit OVID fingerprints to the community OVID database.</summary>
    public bool OvidSubmitEnabled { get; set; } = true;
    public double DiscDbMinConfidence { get; set; } = 0.7;
    public bool DiscDbRequireConfirmation { get; set; } = false;

    /// <summary>
    /// Gets or sets a value indicating whether FileBot should use the
    /// <c>-non-strict</c> flag for fuzzy matching. Default is <c>true</c>
    /// (non-strict/fuzzy matching enabled).
    /// </summary>
    public bool FileBotNonStrict { get; set; } = true;

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
