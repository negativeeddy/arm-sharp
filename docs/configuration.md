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
| `HbPresetDvd` | `HB_PRESET_DVD` | `HQ 480p30 Surround` | HandBrake preset for DVD |
| `HbPresetBd` | `HB_PRESET_BD` | `HQ 1080p30 Surround` | HandBrake preset for Blu-ray |
| `HbArgsDvd` | `HB_ARGS_DVD` | `-e nvenc_h264 --encoder-preset slower --quality 18 --enable-hw-decoding nvdec --encopts spatial-aq=1:aq-strength=10:bf=4:cabac=1:g=50:keyint-min=23 --all-audio --all-subtitles --subtitle-burned=none --aencoder copy:ac3,ac3 --ab 640` | Extra HandBrake args for DVD |
| `HbArgsBd` | `HB_ARGS_BD` | `-e nvenc_h265 --encoder-preset slower --quality 18 --enable-hw-decoding nvdec --encopts spatial-aq=1:aq-strength=10:g=50:keyint-min=23 --all-audio --all-subtitles --subtitle-burned=none --aencoder copy:ac3,ac3 --ab 640` | Extra HandBrake args for Blu-ray |
| `DestExt` | `DEST_EXT` | `mkv` | Output file extension |
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

### Disc Polling (ARM-Sharp)

| Property | YAML Key | Default | Description |
|----------|----------|---------|-------------|
| `DiscPollingEnabled` | `DISC_POLLING_ENABLED` | `true` | Enable in-process disc detection polling |
| `DiscPollIntervalSeconds` | `DISC_POLL_INTERVAL` | `5` | Polling interval in seconds |

### MakeMKV

| Property | YAML Key | Description |
|----------|----------|-------------|
| `MakeMkvPermaKey` | `MAKEMKV_PERMA_KEY` | MakeMKV permanent registration key |

## Encoding Rationale

The defaults are tuned for a system with a **NVIDIA GTX 1060 (Pascal, CUDA 6.1)** running **HandBrakeCLI 1.11.1**.

### Encoder selection by resolution

| Source | Encoder | Rationale |
|--------|---------|----------|
| DVD (480i/576i) | `nvenc_h264` | Pascal's HEVC encoder has no B-frame support. At DVD resolution, the codec efficiency gap between H.264 and HEVC is narrow — H.264 with `bf=4` achieves better compression than B-frame-less HEVC at these low resolutions. |
| Blu-ray (720p/1080p) | `nvenc_h265` | HEVC is 30–50% more efficient than H.264 at 1080p+, so even without B-frames it delivers superior compression. |

### Preset choice

`HQ 480p30 Surround` / `HQ 1080p30 Surround` — these are the renamed equivalents of the legacy `HQ 480p30` / `HQ 1080p30` presets in HandBrake 1.11.1. The "Surround" suffix indicates the preset includes an AC-3 5.1 audio track alongside AAC stereo. The `--all-audio` and `--aencoder copy:ac3,ac3` overrides take precedence, preserving the original channel layout.

### `--encoder-preset slower`

NVENC quality/speed preset. `slower` enables the encoder's highest-quality tuning for maximum compression efficiency on the GTX 1060. Supported values: `fastest`, `faster`, `fast`, `medium`, `slow`, `slower`, `slowest` (available for both `nvenc_h264` and `nvenc_h265`).

### `--quality 18`

RF (Rate Factor) scale for NVENC — lower is better quality. 18 delivers visually lossless or near-lossless output for both H.264 and H.265.

### `--encopts` breakdown

| Option | DVD | BD | Description |
|--------|:---:|:--:|-------------|
| `spatial-aq=1` | ✓ | ✓ | Adaptive quantization (spatial) — improves detail retention in complex scenes |
| `aq-strength=10` | ✓ | ✓ | AQ strength (0–15); 10 is a strong but not extreme setting |
| `bf=4` | ✓ | ✗ | Max consecutive B-frames — H.264 only. Pascal HEVC encoder does not support B-frames |
| `cabac=1` | ✓ | ✗ | CABAC entropy coding — H.264 only. HEVC always uses CABAC natively |
| `g=50` | ✓ | ✓ | GOP size (keyframe interval). 50 ≈ 2s at 23.976 fps |
| `keyint-min=23` | ✓ | ✓ | Minimum GOP size. Prevents keyframes from being placed too frequently |

### `--all-audio --all-subtitles`

Passthrough all available audio and subtitle tracks. `--subtitle-burned=none` prevents burning any subtitle into the video.

### `--aencoder copy:ac3,ac3 --ab 640`

- `copy:ac3` — passthrough any existing AC-3 track unchanged (preserves original 5.1/7.1 channel layout)
- `ac3` — encode fallback audio to AC-3 at 640 kbps
- AC-3 caps at 5.1 channels, preventing unintended format conversions

### `--enable-hw-decoding nvdec`

Requests NVDEC hardware decoding. **Note:** This HandBrake build does not include NVDEC support (`--enable-nvdec` was not compiled in), so this flag is silently ignored. Kept in the defaults for future compatibility.

---

> Settings can be overridden via `appsettings.json` (per-deployment) or `/etc/arm/config/arm.yaml` — see the [configuration hierarchy](#configuration).
