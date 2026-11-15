# Configuration

> **DB-first (2026-08-02):** The database is the source of truth for runtime
> settings. `/etc/arm/config/arm.yaml` is **no longer loaded at startup** — it is
> only read by the explicit **"Import ARM settings"** action in the Settings UI
> (one-time migration of legacy ARM values into the DB). Files never override the DB.

Runtime settings resolve as:

1. `ripper_settings` (DB) — user overrides, highest priority
2. `appsettings.json` — built into the WebUI project (file defaults)
3. C# class defaults (`ArmSettings`, provider options)

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
| `MinLength` | `MINLENGTH` | `300` | Minimum title length (seconds) |
| `MaxLength` | `MAXLENGTH` | `99999` | Maximum title length (seconds; `99999` = no limit). Tracks over this are skipped, including series "play all" concatenations. |
| `MainFeature` | `MAINFEATURE` | `false` | Rip only main feature |
| `SkipTranscode` | `SKIP_TRANSCODE` | `false` | Skip transcoding step |
| `UseFfmpeg` | `USE_FFMPEG` | `false` | Use ffmpeg instead of HandBrake |
| `DelRawFiles` | `DELRAWFILES` | `true` | Delete raw rips after transcode |
| `Prevent99` | `PREVENT_99` | `false` | Skip title 99 (common false positive) |
| `AllowDuplicates` | `ALLOW_DUPLICATES` | `false` | Allow ripping already-ripped discs |
| `ManualWait` | `MANUAL_WAIT` | `false` | Wait for manual intervention |
| `ManualWaitTime` | `MANUAL_WAIT_TIME` | `60` | Manual title-identification wait timeout in seconds (`0` = wait indefinitely) |
| `ManualSelectionWaitTime` | `MANUAL_SELECTION_WAIT_TIME` | `60` | Manual track-selection wait timeout in seconds (`0` = wait indefinitely) |
| `AutoEject` | `AUTO_EJECT` | `true` | Eject disc after completion |
| `GetVideoTitle` | `GET_VIDEO_TITLE` | `true` | Look up video metadata |
| `GetAudioTitle` | `GET_AUDIO_TITLE` | `true` | Look up audio CD metadata |
| `TestMode` | — | `false` | Rip first 2 min of first title (CLI `--test`) |

### Transcode Settings

| Property | YAML Key | Default | Description |
|----------|----------|---------|-------------|
| `HbPresetDvd` | `HB_PRESET_DVD` | `HQ 480p30 Surround` | HandBrake preset for DVD |
| `HbPresetBd` | `HB_PRESET_BD` | `HQ 1080p30 Surround` | HandBrake preset for Blu-ray |
| `HbArgsDvd` | `HB_ARGS_DVD` | `-e nvenc_h264 --encoder-preset slower --quality 18 --enable-hw-decoding nvdec --encopts spatial-aq=1:aq-strength=10:bf=4:cabac=1:g=50:keyint-min=23 --comb-detect --decomb --all-audio --all-subtitles --subtitle-burned=none --aencoder aac --audio-fallback aac --mixdown none` | Extra HandBrake args for DVD |
| `HbArgsBd` | `HB_ARGS_BD` | `-e nvenc_h265 --encoder-preset slower --quality 22 --enable-hw-decoding nvdec --encopts spatial-aq=1:aq-strength=10:g=50:keyint-min=23 --all-audio --all-subtitles --subtitle-burned=none --aencoder copy --audio-copy-mask dtshd,dts,truehd,eac3,aac --audio-fallback av_aac --mixdown none --automatic-naming-behaviour off` | Extra HandBrake args for Blu-ray |
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
| `MaxConcurrentTranscodes` | `MAX_CONCURRENT_TRANSCODES` | `1` | Max parallel transcodes (default 1 = serialized) |
| `MaxConcurrentMakemkvInfo` | `MAX_CONCURRENT_MAKEMKVINFO` | `1` | Max parallel MakeMKV info scans |

> **Note:** The old `MaxConcurrentRips` setting has been removed. Rips are now gated per-drive — only one rip per optical drive at a time, but rips on different drives run in parallel. The `MaxConcurrentTranscodes` default changed from 2 to 1 because parallel transcodes rarely improve throughput and serializing makes debugging easier.

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

`HQ 480p30 Surround` / `HQ 1080p30 Surround` — these are the renamed equivalents of the legacy `HQ 480p30` / `HQ 1080p30` presets in HandBrake 1.11.1. The "Surround" suffix indicates the preset includes an AC-3 5.1 audio track alongside AAC stereo. The `--all-audio` and Auto Passthru overrides take precedence. For Blu-ray, lossless and object formats are copied bit-for-bit while AC-3 and LPCM are transcoded to AAC.

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

### `--aencoder copy --audio-copy-mask ... --audio-fallback av_aac` (Blu-ray)

- `copy` — Auto Passthru: copy each track bit-for-bit when its codec is in the mask and the output container supports it
- `--audio-copy-mask dtshd,dts,truehd,eac3,aac` — permit passthrough of DTS-HD MA/DTS:X, DTS, TrueHD/Atmos, E-AC3/Atmos, and AAC
- `--audio-fallback av_aac` — transcode everything else (AC-3, LPCM, unsupported codecs) to AAC
- `--mixdown none` — keep the source channel layout on fallback encodes
- `--automatic-naming-behaviour off` — prevent HandBrake from auto-naming every track "Stereo" (which made Jellyfin report all tracks as 2-channel); see metadata caveats below

Dolby Atmos (TrueHD/E-AC3) and DTS:X metadata are carried inside the copied bitstream. Keep `DestExt` as `mkv`; MP4 strips Atmos metadata and may reject TrueHD/DTS-HD.

#### Audio metadata caveats (HandBrake 1.11.x)

- **Language tags:** HandBrake only writes language codes it recognizes. Codes outside its table are treated as `Unknown` and omitted. For example, `grc` (Greek, Ancient) is dropped, while `eng`, `deu`, `fra`, `spa`, and `ell`/`gre` are preserved. If the tag matters, set it after transcode, e.g. `mkvpropedit "output.mkv" --edit track:a1 --set language=grc`.
- **Track names:** HandBrake treats `Stereo`, `Mono`, and `Surround` as reserved auto-generated names. With `--automatic-naming-behaviour off`, those names are dropped from passthrough tracks; more specific names such as `Surround 5.1` survive. `--keep-aname` does not restore reserved names in 1.11.2. Use a post-pass or explicit `--aname` if titles matter.
- **Lossless + core pairs:** MakeMKV may expose DTS-HD MA and its DTS core as separate tracks. Both are copied because `dts` is in the copy mask. A TrueHD track's AC-3 companion is not copied (AC-3 is not in the mask) and becomes AAC if selected. This is expected.
- **Avoid `copy:<type>`:** Explicit per-track encoders are positional and silently drop non-matching tracks; `--audio-fallback` only applies to `copy`. Keep `--aencoder copy` with the copy mask.

DVD defaults use `--aencoder aac --audio-fallback aac --mixdown none`, transcoding all tracks to AAC.

### `--enable-hw-decoding nvdec`

Requests NVDEC hardware decoding. **Note:** This HandBrake build does not include NVDEC support (`--enable-nvdec` was not compiled in), so this flag is silently ignored. Kept in the defaults for future compatibility.

---

> Settings can be overridden via `appsettings.json` (per-deployment) or `/etc/arm/config/arm.yaml` — see the [configuration hierarchy](#configuration).
