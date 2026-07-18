using YamlDotNet.RepresentationModel;

namespace ArmRipper.Core.Configuration;

public static class ArmYamlConfigLoader
{
    private static readonly Dictionary<string, string> KeyMap = new()
    {
        ["ARM_API_KEY"] = "Arm:ArmApiKey",
        ["RAW_PATH"] = "Arm:RawPath",
        ["TRANSCODE_PATH"] = "Arm:TranscodePath",
        ["COMPLETED_PATH"] = "Arm:CompletedPath",
        ["LOGPATH"] = "Arm:LogPath",
        ["INSTALLPATH"] = "Arm:InstallPath",
        ["DBFILE"] = "Arm:DbFile",
        ["EXTRAS_SUB"] = "Arm:ExtrasSub",
        ["RIPMETHOD"] = "Arm:RipMethod",
        ["MKV_ARGS"] = "Arm:MkvArgs",
        ["MINLENGTH"] = "Arm:MinLength",
        ["MAXLENGTH"] = "Arm:MaxLength",
        ["MAINFEATURE"] = "Arm:MainFeature",
        ["SET_MEDIA_OWNER"] = "Arm:SetMediaOwner",
        ["SET_MEDIA_PERMISSIONS"] = "Arm:SetMediaPermissions",
        ["SKIP_TRANSCODE"] = "Arm:SkipTranscode",
        ["USE_FFMPEG"] = "Arm:UseFfmpeg",
        ["GET_VIDEO_TITLE"] = "Arm:GetVideoTitle",
        ["GET_AUDIO_TITLE"] = "Arm:GetAudioTitle",
        ["AUTO_EJECT"] = "Arm:AutoEject",
        ["CHMOD_VALUE"] = "Arm:ChmodValue",
        ["CHOWN_GROUP"] = "Arm:ChownGroup",
        ["CHOWN_USER"] = "Arm:ChownUser",
        ["DELRAWFILES"] = "Arm:DelRawFiles",
        ["PREVENT_99"] = "Arm:Prevent99",
        ["ALLOW_DUPLICATES"] = "Arm:AllowDuplicates",
        ["HB_PRESET_DVD"] = "Arm:HbPresetDvd",
        ["HB_PRESET_BD"] = "Arm:HbPresetBd",
        ["HB_ARGS_DVD"] = "Arm:HbArgsDvd",
        ["HB_ARGS_BD"] = "Arm:HbArgsBd",
        ["DEST_EXT"] = "Arm:DestExt",
        ["FFMPEG_CLI"] = "Arm:FfmpegCli",
        ["FFMPEG_PRE_FILE_ARGS"] = "Arm:FfmpegPreFileArgs",
        ["FFMPEG_POST_FILE_ARGS"] = "Arm:FfmpegPostFileArgs",
        ["MANUAL_WAIT"] = "Arm:ManualWait",
        ["MANUAL_WAIT_TIME"] = "Arm:ManualWaitTime",
        ["NOTIFY_RIP"] = "Arm:NotifyRip",
        ["NOTIFY_TRANSCODE"] = "Arm:NotifyTranscode",
        ["PB_KEY"] = "Arm:PbKey",
        ["IFTTT_KEY"] = "Arm:IftttKey",
        ["PO_USER_KEY"] = "Arm:PoUserKey",
        ["BASH_SCRIPT"] = "Arm:BashScript",
        ["JSON_URL"] = "Arm:JsonUrl",
        ["APPRISE"] = "Arm:Apprise",
        ["OMDB_API_KEY"] = "Arm:OmdbApiKey",
        ["TMDB_API_KEY"] = "Arm:TmdbApiKey",
        ["METADATA_PROVIDER"] = "Arm:MetadataProvider",
        ["WEBSERVER_IP"] = "Arm:WebServerIp",
        ["WEBSERVER_PORT"] = "Arm:WebServerPort",
        ["UI_BASE_URL"] = "Arm:UiBaseUrl",
        ["EMBY_REFRESH"] = "Arm:EmbyRefresh",
        ["EMBY_SERVER"] = "Arm:EmbyServer",
        ["EMBY_PORT"] = "Arm:EmbyPort",
        ["EMBY_API_KEY"] = "Arm:EmbyApiKey",
        ["MAX_CONCURRENT_TRANSCODES"] = "Arm:MaxConcurrentTranscodes",
        ["MAX_CONCURRENT_MAKEMKVINFO"] = "Arm:MaxConcurrentMakemkvInfo",
        ["MAKEMKV_PERMA_KEY"] = "Arm:MakeMkvPermaKey",
        ["DISABLE_LOGIN"] = "Arm:DisableLogin",

        // ── Disc polling (ARM-Sharp specific) ──
        ["DISC_POLLING_ENABLED"] = "Arm:DiscPollingEnabled",
        ["DISC_POLL_INTERVAL"] = "Arm:DiscPollIntervalSeconds",
        // ── MakeMKV I/O watchdog (ARM-Sharp specific) ──
        ["MAKEMKV_MAX_READ_BYTES"] = "Arm:MakemkvMaxReadBytes",
        ["MAKEMKV_IO_WATCHDOG_INTERVAL"] = "Arm:MakemkvIoWatchdogIntervalSeconds",
    };

    public static Dictionary<string, string?> LoadYamlValues(string yamlPath)
    {
        var result = new Dictionary<string, string?>();
        if (!File.Exists(yamlPath))
            return result;

        try
        {
            var yamlText = File.ReadAllText(yamlPath);
            if (string.IsNullOrWhiteSpace(yamlText))
                return result;

            var yaml = new YamlStream();
            using var reader = new StringReader(yamlText);
            yaml.Load(reader);

            if (yaml.Documents.Count == 0)
                return result;

            if (yaml.Documents[0].RootNode is not YamlMappingNode root)
                return result;

            foreach (var entry in root.Children)
            {
                var key = ((YamlScalarNode)entry.Key).Value ?? "";
                var value = ((YamlScalarNode)entry.Value).Value ?? "";

                if (!KeyMap.TryGetValue(key, out var configKey))
                    continue;

                result[configKey] = value;
            }
        }
        catch (Exception)
        {
            // YAML parse failure — return empty; fall back to appsettings.json defaults
        }

        return result;
    }
}
