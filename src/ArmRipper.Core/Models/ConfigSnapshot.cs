using System.Collections;

namespace ArmRipper.Core.Models;

public class ConfigSnapshot
{
    public int Id { get; init; }
    public int JobId { get; set; }

    public bool SkipTranscode { get; set; }
    public bool MainFeature { get; set; }
    public bool UseFfmpeg { get; set; }
    public bool ManualWait { get; set; }
    public bool AllowDuplicates { get; set; }
    public bool Prevent99 { get; set; }
    public bool GetVideoTitle { get; set; }
    public string? GetAudioTitle { get; set; }
    public bool AutoEject { get; set; }
    public bool DelRawFiles { get; set; }

    public string? RawPath { get; set; }
    public string? TranscodePath { get; set; }
    public string? CompletedPath { get; set; }
    public string? LogPath { get; set; }
    public string? DbFile { get; set; }
    public string? InstallPath { get; set; }
    public string? ExtrasSub { get; set; }

    public string? RipMethod { get; set; }
    public string? MkvArgs { get; set; }
    public int? MinLength { get; set; }
    public int? MaxLength { get; set; }

    public string? HbPresetDvd { get; set; }
    public string? HbPresetBd { get; set; }
    public string? HbArgsDvd { get; set; }
    public string? HbArgsBd { get; set; }
    public string? DestExt { get; set; }

    public string? FfmpegCli { get; set; }
    public string? FfmpegPreFileArgs { get; set; }
    public string? FfmpegPostFileArgs { get; set; }

    public bool NotifyRip { get; set; }
    public bool NotifyTranscode { get; set; }
    public string? PbKey { get; set; }
    public string? IftttKey { get; set; }
    public string? PoUserKey { get; set; }
    public string? BashScript { get; set; }
    public string? JsonUrl { get; set; }
    public string? Apprise { get; set; }

    public string? OmdbApiKey { get; set; }
    public string? TmdbApiKey { get; set; }
    public string? ArmApiKey { get; set; }
    public string? MetadataProvider { get; set; }

    public string? WebServerIp { get; set; }
    public int? WebServerPort { get; set; }
    public string? UiBaseUrl { get; set; }

    public bool EmbyRefresh { get; set; }
    public string? EmbyServer { get; set; }
    public int? EmbyPort { get; set; }
    public string? EmbyApiKey { get; set; }

    public int? MaxConcurrentTranscodes { get; set; }
    public int? MaxConcurrentMakemkvInfo { get; set; }

    public Job Job { get; set; } = null!;
}
