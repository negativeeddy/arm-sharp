# Extract `ConfigSnapshot` Factory from `Conductor`

**Priority:** 🟡 Medium
**Files:**
- `src/ArmRipper.Core/Rip/Conductor.cs` (primary)
- `src/ArmRipper.Core/Models/ConfigSnapshot.cs` (add factory)

**Status:** ✅ Done

---

## Problem

`Conductor.RunForkedTranscodeAsync` manually maps **40+ properties** from `ArmSettings` to
`ConfigSnapshot`, one assignment per line (~60 lines of boilerplate):

```csharp
config.SkipTranscode      = armSettings.SkipTranscode;
config.UseFfmpeg           = armSettings.UseFfmpeg;
config.ManualWait          = armSettings.ManualWait;
// ... 37 more identical assignments
```

This is brittle: adding a new `ArmSettings` property requires updating this method too. It's
also duplicated (or partially duplicated) in other places that create snapshots.

## Proposed Fix

Add a static factory method to `ConfigSnapshot`:

```csharp
// In ConfigSnapshot.cs
public static ConfigSnapshot FromSettings(
    ArmSettings settings,
    int jobId,
    ConfigSnapshot? carryForward = null)
{
    return new ConfigSnapshot
    {
        JobId = jobId,
        SkipTranscode      = settings.SkipTranscode,
        UseFfmpeg           = settings.UseFfmpeg,
        ManualWait          = settings.ManualWait,
        ManualWaitTime      = settings.ManualWaitTime,
        AllowDuplicates     = settings.AllowDuplicates,
        GetVideoTitle       = settings.GetVideoTitle,
        GetAudioTitle       = settings.GetAudioTitle,
        DelRawFiles         = settings.DelRawFiles,
        RawPath             = settings.RawPath,
        TranscodePath       = settings.TranscodePath,
        CompletedPath       = settings.CompletedPath,
        LogPath             = settings.LogPath,
        MinLength           = settings.MinLength,
        MaxLength           = settings.MaxLength,
        HbPresetDvd         = settings.HbPresetDvd,
        HbPresetBd          = settings.HbPresetBd,
        HbArgsDvd           = settings.HbArgsDvd,
        HbArgsBd            = settings.HbArgsBd,
        GpuIndex            = settings.GpuIndex,
        DestExt             = settings.DestExt,
        FfmpegCli           = settings.FfmpegCli,
        FfmpegPreFileArgs   = settings.FfmpegPreFileArgs,
        FfmpegPostFileArgs  = settings.FfmpegPostFileArgs,
        ExtrasSub           = settings.ExtrasSub,
        InstallPath         = settings.InstallPath,
        DbFile              = settings.DbFile,
        NotifyRip           = settings.NotifyRip,
        NotifyTranscode     = settings.NotifyTranscode,
        PbKey               = settings.PbKey,
        IftttKey            = settings.IftttKey,
        PoUserKey           = settings.PoUserKey,
        BashScript          = settings.BashScript,
        JsonUrl             = settings.JsonUrl,
        Apprise             = settings.Apprise,
        OmdbApiKey          = settings.OmdbApiKey,
        TmdbApiKey          = settings.TmdbApiKey,
        ArmApiKey           = settings.ArmApiKey,
        MetadataProvider    = settings.MetadataProvider,
        WebServerPort       = settings.WebServerPort,
        WebServerIp         = settings.WebServerIp,
        UiBaseUrl           = settings.UiBaseUrl,
        EmbyRefresh         = settings.EmbyRefresh,
        EmbyServer          = settings.EmbyServer,
        EmbyPort            = settings.EmbyPort,
        EmbyApiKey          = settings.EmbyApiKey,
        MaxConcurrentTranscodes   = settings.MaxConcurrentTranscodes,
        MaxConcurrentMakemkvInfo  = settings.MaxConcurrentMakemkvInfo,
        DiscDbEnabled              = settings.DiscDbEnabled,
        DiscDbApiBaseUrl           = settings.DiscDbApiBaseUrl,
        DiscDbMinConfidence        = settings.DiscDbMinConfidence,
        DiscDbRequireConfirmation  = settings.DiscDbRequireConfirmation,
        // Carry forward disc-specific overrides from a previous snapshot
        MainFeature = carryForward?.MainFeature ?? settings.MainFeature,
        Prevent99   = carryForward?.Prevent99 ?? settings.Prevent99,
        RipMethod   = carryForward?.RipMethod ?? settings.RipMethod,
        MkvArgs     = carryForward?.MkvArgs ?? settings.MkvArgs,
        AutoEject   = carryForward?.AutoEject ?? settings.AutoEject,
    };
}
```

Then in `Conductor.RunForkedTranscodeAsync`:

```csharp
var config = ConfigSnapshot.FromSettings(armSettings, job.Id, sourceConfig);
// Disc-specific overrides already handled by carryForward parameter
db.ConfigSnapshots.Add(config);
```

Also audit other call sites that manually construct `ConfigSnapshot` and replace with the
factory where appropriate.

### Future improvement

Consider a source-generated mapper (e.g., Mapperly or a simple incremental generator) if
the property list grows beyond ~50 or if mapping happens in more than 2 places.
