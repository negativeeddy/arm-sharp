# Configuration

Configuration is read from two sources, merged in order (later wins):

1. `appsettings.json` — built into the WebUI project
2. `/etc/arm/config/arm.yaml` — native YAML overlay (50+ key mappings)

## ArmSettings (bound from `Arm:` section)

### Paths

| Property | YAML Key | Default | Description |
|----------|----------|---------|-------------|
| `RawPath` | `RAW_PATH` | `/home/arm/media/raw` | Raw MakeMKV/abcde output |
| `TranscodePath` | `TRANSCODE_PATH` | `/home/arm/media/transcode` | Transcoding work directory |
| `CompletedPath` | `COMPLETED_PATH` | `/home/arm/media` | Final output directory |
| `LogPath` | `LOGPATH` | `/home/arm/logs` | Log file directory |
| `DbFile` | `DBFILE` | `/etc/arm/config/arm.db` | SQLite database path |
| `InstallPath` | `INSTALLPATH` | `/opt/arm` | ARM installation root |
| `ExtrasSub` | `EXTRAS_SUB` | — | Subdirectory for extras |

### Rip Settings

| Property | YAML Key | Default | Description |
|----------|----------|---------|-------------|
| `RipMethod` | `RIPMETHOD` | `mkv` | Rip method (`mkv`, `backup`) |
| `MkvArgs` | `MKV_ARGS` | `""` | Extra MakeMKV arguments |
| `MinLength` | `MINLENGTH` | `0` | Minimum title length (minutes) |
| `MaxLength` | `MAXLENGTH` | `99999` | Maximum title length (minutes) |
| `MainFeature` | `MAINFEATURE` | `false` | Rip only main feature |
| `SkipTranscode` | `SKIP_TRANSCODE` | `false` | Skip transcoding step |
| `UseFfmpeg` | `USE_FFMPEG` | `false` | Use ffmpeg instead of HandBrake |
| `DelRawFiles` | `DELRAWFILES` | `true` | Delete raw rips after transcode |
| `Prevent99` | `PREVENT_99` | `false` | Skip title 99 (common false positive) |
| `AllowDuplicates` | `ALLOW_DUPLICATES` | `false` | Allow ripping already-ripped discs |
| `ManualWait` | `MANUAL_WAIT` | `false` | Wait for manual intervention |
| `AutoEject` | `AUTO_EJECT` | `true` | Eject disc after completion |
| `GetVideoTitle` | `GET_VIDEO_TITLE` | `true` | Look up video metadata |
| `GetAudioTitle` | `GET_AUDIO_TITLE` | `true` | Look up audio CD metadata |
| `TestMode` | — | `false` | Rip first 2 min of first title (CLI `--test`) |

### Transcode Settings

| Property | YAML Key | Default | Description |
|----------|----------|---------|-------------|
| `HbPresetDvd` | `HB_PRESET_DVD` | `Very Fast 1080p30` | HandBrake preset for DVD |
| `HbPresetBd` | `HB_PRESET_BD` | `Very Fast 1080p30` | HandBrake preset for Blu-ray |
| `HbArgsDvd` | `HB_ARGS_DVD` | — | Extra HandBrake args for DVD |
| `HbArgsBd` | `HB_ARGS_BD` | — | Extra HandBrake args for Blu-ray |
| `DestExt` | `DEST_EXT` | `mp4` | Output file extension |
| `FfmpegCli` | `FFMPEG_CLI` | `ffmpeg` | ffmpeg binary path |
| `FfmpegPreFileArgs` | `FFMPEG_PRE_FILE_ARGS` | — | ffmpeg args before input file |
| `FfmpegPostFileArgs` | `FFMPEG_POST_FILE_ARGS` | — | ffmpeg args after input file |

### Notification Channels

| Property | YAML Key | Description |
|----------|----------|-------------|
| `NotifyRip` | `NOTIFY_RIP` | Notify on rip completion |
| `NotifyTranscode` | `NOTIFY_TRANSCODE` | Notify on transcode completion |
| `PbKey` | `PB_KEY` | Pushbullet API key |
| `IftttKey` | `IFTTT_KEY` | IFTTT webhook key |
| `PoUserKey` | `PO_USER_KEY` | Pushover user key |
| `BashScript` | `BASH_SCRIPT` | Custom script path |
| `JsonUrl` | `JSON_URL` | JSON webhook URL |
| `Apprise` | `APPRISE` | Apprise notification URLs |

### Metadata Providers

| Property | YAML Key | Default | Description |
|----------|----------|---------|-------------|
| `MetadataProvider` | `METADATA_PROVIDER` | `omdb` | Provider (`omdb`, `tmdb`) |
| `OmdbApiKey` | `OMDB_API_KEY` | — | OMDB API key |
| `TmdbApiKey` | `TMDB_API_KEY` | — | TMDB API key |
| `ArmApiKey` | `ARM_API_KEY` | — | ARM central API key |

### Web Server

| Property | YAML Key | Default | Description |
|----------|----------|---------|-------------|
| `WebServerIp` | `WEBSERVER_IP` | `0.0.0.0` | Bind address |
| `WebServerPort` | `WEBSERVER_PORT` | `8080` | Bind port |
| `UiBaseUrl` | `UI_BASE_URL` | — | Public URL for notifications |

### Emby

| Property | YAML Key | Description |
|----------|----------|-------------|
| `EmbyRefresh` | `EMBY_REFRESH` | Refresh Emby library after rip |
| `EmbyServer` | `EMBY_SERVER` | Emby server hostname |
| `EmbyPort` | `EMBY_PORT` | Emby server port |
| `EmbyApiKey` | `EMBY_API_KEY` | Emby API key |

### Concurrency

| Property | YAML Key | Default | Description |
|----------|----------|---------|-------------|
| `MaxConcurrentTranscodes` | `MAX_CONCURRENT_TRANSCODES` | `2` | Max parallel transcodes |
| `MaxConcurrentMakemkvInfo` | `MAX_CONCURRENT_MAKEMKVINFO` | `1` | Max parallel MakeMKV info scans |

### MakeMKV

| Property | YAML Key | Description |
|----------|----------|-------------|
| `MakeMkvPermaKey` | `MAKEMKV_PERMA_KEY` | MakeMKV permanent registration key |

## Example arm.yaml

```yaml
RAW_PATH: /home/arm/media/raw
TRANSCODE_PATH: /home/arm/media/transcode
COMPLETED_PATH: /home/arm/media/completed
LOGPATH: /home/arm/logs
DBFILE: /etc/arm/config/arm.db
RIPMETHOD: mkv
MAINFEATURE: false
SKIP_TRANSCODE: false
HB_PRESET_DVD: Very Fast 1080p30
HB_PRESET_BD: Very Fast 1080p30
DEST_EXT: mp4
OMDB_API_KEY: your_key_here
WEBSERVER_PORT: 8080
```
