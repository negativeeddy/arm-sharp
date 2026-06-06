using System.Text.RegularExpressions;

namespace ArmRipper.Core.Configuration;

public static partial class ArmYamlConfigLoader
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
        ["SKIP_TRANSCODE"] = "Arm:SkipTranscode",
        ["USE_FFMPEG"] = "Arm:UseFfmpeg",
        ["GET_VIDEO_TITLE"] = "Arm:GetVideoTitle",
        ["GET_AUDIO_TITLE"] = "Arm:GetAudioTitle",
        ["AUTO_EJECT"] = "Arm:AutoEject",
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
    };

    private static readonly Regex YamlLineRegex = YamlLineRegexFactory();

    public static Dictionary<string, string?> LoadYamlValues(string yamlPath)
    {
        var result = new Dictionary<string, string?>();
        if (!File.Exists(yamlPath))
            return result;

        foreach (var line in File.ReadLines(yamlPath))
        {
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                continue;

            var match = YamlLineRegex.Match(trimmed);
            if (!match.Success)
                continue;

            var key = match.Groups[1].Value;
            var value = match.Groups[2].Value;

            if (!KeyMap.TryGetValue(key, out var configKey))
                continue;

            result[configKey] = value;
        }

        return result;
    }

    [GeneratedRegex(@"^([A-Z][A-Z_0-9]+)\s*:\s*(.*)")]
    private static partial Regex YamlLineRegexFactory();
}
