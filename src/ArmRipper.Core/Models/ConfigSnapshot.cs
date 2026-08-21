using ArmRipper.Core.Configuration;

namespace ArmRipper.Core.Models;

public class ConfigSnapshot
{
    public int Id { get; init; }
    public int JobId { get; set; }

    public bool SkipTranscode { get; set; }
    public bool MainFeature { get; set; }
    /// <summary>When true, pause after title scan for manual track selection.</summary>
    public bool ManualSelection { get; set; }
    public bool UseFfmpeg { get; set; }
    public bool ManualWait { get; set; }
    public int ManualWaitTime { get; set; } = 60;
    public bool AllowDuplicates { get; set; }
    public bool PreferWidescreen { get; set; }
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

    public int? GpuIndex { get; set; }

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

    // ── TheDiscDb Integration ──
    public bool DiscDbEnabled { get; set; } = true;
    public string? DiscDbApiBaseUrl { get; set; }
    public double DiscDbMinConfidence { get; set; } = 0.7;
    public bool DiscDbRequireConfirmation { get; set; } = false;

    public Job Job { get; set; } = null!;

    /// <summary>
    /// Creates a snapshot from the given settings, carrying forward any disc-specific
    /// behavioural overrides (<see cref="MainFeature"/>, <see cref="Prevent99"/>,
    /// <see cref="RipMethod"/>, <see cref="MkvArgs"/>, <see cref="AutoEject"/>) from a
    /// previous snapshot when one is supplied. Call sites that need a different value for
    /// a property (e.g. <see cref="AutoEject"/> = false for jobs with no physical disc)
    /// override it on the returned snapshot.
    /// </summary>
    public static ConfigSnapshot FromSettings(
        ArmSettings settings,
        int jobId,
        ConfigSnapshot? carryForward = null)
    {
        return new ConfigSnapshot
        {
            JobId = jobId,
            SkipTranscode     = settings.SkipTranscode,
            MainFeature       = carryForward?.MainFeature ?? settings.MainFeature,
            ManualSelection   = carryForward?.ManualSelection ?? settings.ManualSelection,
            UseFfmpeg         = settings.UseFfmpeg,
            ManualWait        = settings.ManualWait,
            ManualWaitTime    = settings.ManualWaitTime,
            AllowDuplicates   = settings.AllowDuplicates,
            Prevent99         = carryForward?.Prevent99 ?? settings.Prevent99,
            GetVideoTitle     = settings.GetVideoTitle,
            GetAudioTitle     = settings.GetAudioTitle,
            AutoEject         = carryForward?.AutoEject ?? settings.AutoEject,
            DelRawFiles       = settings.DelRawFiles,
            RawPath           = settings.RawPath,
            TranscodePath     = settings.TranscodePath,
            CompletedPath     = settings.CompletedPath,
            LogPath           = settings.LogPath,
            RipMethod         = carryForward?.RipMethod ?? settings.RipMethod,
            MkvArgs           = carryForward?.MkvArgs ?? settings.MkvArgs,
            MinLength         = settings.MinLength,
            MaxLength         = settings.MaxLength,
            GpuIndex          = settings.GpuIndex,
            HbPresetDvd       = settings.HbPresetDvd,
            HbPresetBd        = settings.HbPresetBd,
            HbArgsDvd         = settings.HbArgsDvd,
            HbArgsBd          = settings.HbArgsBd,
            DestExt           = settings.DestExt,
            FfmpegCli         = settings.FfmpegCli,
            FfmpegPreFileArgs = settings.FfmpegPreFileArgs,
            FfmpegPostFileArgs = settings.FfmpegPostFileArgs,
            ExtrasSub         = settings.ExtrasSub,
            InstallPath       = settings.InstallPath,
            DbFile            = settings.DbFile,
            NotifyRip         = settings.NotifyRip,
            NotifyTranscode   = settings.NotifyTranscode,
            PbKey             = settings.PbKey,
            IftttKey          = settings.IftttKey,
            PoUserKey         = settings.PoUserKey,
            BashScript        = settings.BashScript,
            JsonUrl           = settings.JsonUrl,
            Apprise           = settings.Apprise,
            OmdbApiKey        = settings.OmdbApiKey,
            TmdbApiKey        = settings.TmdbApiKey,
            ArmApiKey         = settings.ArmApiKey,
            MetadataProvider  = settings.MetadataProvider,
            WebServerPort     = settings.WebServerPort,
            WebServerIp       = settings.WebServerIp,
            UiBaseUrl         = settings.UiBaseUrl,
            EmbyRefresh       = settings.EmbyRefresh,
            EmbyServer        = settings.EmbyServer,
            EmbyPort          = settings.EmbyPort,
            EmbyApiKey        = settings.EmbyApiKey,
            MaxConcurrentTranscodes  = settings.MaxConcurrentTranscodes,
            MaxConcurrentMakemkvInfo = settings.MaxConcurrentMakemkvInfo,
            DiscDbEnabled             = settings.DiscDbEnabled,
            DiscDbApiBaseUrl          = settings.DiscDbApiBaseUrl,
            DiscDbMinConfidence       = settings.DiscDbMinConfidence,
            DiscDbRequireConfirmation = settings.DiscDbRequireConfirmation,
        };
    }
}
