# ARM / HandBrake Encoding Reference (Tarantino Hardware)

This is the encoding test plan and results from the original Python ARM, used to inform the current ARM Sharp encoding defaults. The hardware is the same machine that ARM Sharp runs on.

## Hardware
- **CPU:** Intel i5-8600K (5 cores available)
- **GPU:** NVIDIA GeForce GTX 1060 6GB (Pascal, CC 6.1)
- **NVENC:** Available (H.264 + H.265)
- **NVDEC:** Compiled into HandBrake (`nvdec: is available`)
- **Driver:** 580.159.03, CUDA 13.0

---

## Two Configs — Switch Based on Disc Type

### Config A: 1080p Blu-rays & DVDs (HandBrake re-encode)

Use when ripping standard Blu-rays or DVDs. Re-encodes video to save space.

**arm.yaml settings:**

```yaml
USE_FFMPEG: false

HB_PRESET_DVD: "HQ 480p30"
HB_PRESET_BD:  "HQ 1080p30"

HB_ARGS_DVD: "-e nvenc_h264 --encoder-preset slower --quality 18 --enable-hw-decoding nvdec --encopts spatial-aq=1:aq-strength=10:bf=4:cabac=1:g=250:keyint-min=23 --all-audio --all-subtitles --subtitle-burned=none --aencoder ac3 --mixdown 5point1 --ab 640"

HB_ARGS_BD:  "-e nvenc_h264 --encoder-preset slower --quality 18 --enable-hw-decoding nvdec --encopts spatial-aq=1:aq-strength=10:bf=4:cabac=1:g=250:keyint-min=23 --all-audio --all-subtitles --subtitle-burned=none --aencoder ac3 --mixdown 5point1 --ab 640"
```

Performance:
- 1080p H.264: ~93 fps, ~33 min (nvenc_h264)
- 4K HDR if you wanted to re-encode: ~58 fps, ~53 min (nvenc_h265_10bit)

### Config B: 4K UHD (ffmpeg video passthrough + AC3 audio)

Use when ripping 4K UHD discs. Copies HEVC video losslessly, only re-encodes audio. HDR metadata guaranteed intact.

```yaml
USE_FFMPEG: true
FFMPEG_CLI: "ffmpeg"
FFMPEG_PRE_FILE_ARGS: ""
FFMPEG_POST_FILE_ARGS: "-fflags +genpts -c:v copy -c:a ac3 -b:a 640k -c:s copy -map 0"
```

Performance:
- ~5-10 min per 4K movie (just remux + audio encode)
- File size: ~ same as source (~50-60 GB)
- No generation loss on video
- HDR metadata 100% preserved

---

## Test Results

### The Prestige (4K HDR HEVC 10-bit, 3840x2160)

With NVDEC enabled (`--enable-hw-decoding nvdec`) — 30-second test clips:

| Encoder | Preset | CRF | Avg FPS | Est. Full Time | Bit Depth | HDR Playback? | Notes |
|---------|--------|-----|---------|---------------|-----------|--------------|-------|
| nvenc_h264 | fast | 24 | ~45 | ~1h08m | 8-bit `yuv420p` | **No — broken** | Metadata tags survive but 8-bit output, playback will be wrong |
| nvenc_h265 | fast | 24 | ~45 | ~1h07m | 8-bit `yuv420p` | **No — broken** | Same issue: HDR tags but 8-bit range |
| nvenc_h265_10bit | fast | 24 | ~65 | ~48 min | 10-bit `yuv420p10le` | **Yes** | Proper 10-bit + HDR, best option |
| x265_10bit | medium | 22 | ~7 | ~7h+ | 10-bit | Yes | SW only, impractical |

**HDR verified on nvenc_h265_10bit output:**
- `hevc (Main 10), yuv420p10le, bt2020nc/bt2020/smpte2084` — correct
- `MaxCLL=1121, MaxFALL=284`, Mastering Display Metadata — preserved

**Key finding:** nvenc_h264 and nvenc_h265 (8-bit) carry HDR metadata tags but encode 8-bit video — players will see HDR flags and expect 10-bit range, resulting in washed-out playback. Only `nvenc_h265_10bit` properly preserves HDR, or use ffmpeg passthrough (`-c:v copy`).

ffmpeg passthrough:

| Method | Time | Keeps HDR? | Notes |
|--------|------|-----------|-------|
| `-c:v copy -c:a ac3` | ~5 min | Yes | Zero loss, full quality |

### The Game (1080p H.264)

| Encoder | Preset | CRF | Avg FPS | Est. Full Time | Notes |
|---------|--------|-----|---------|---------------|-------|
| nvenc_h264 | slower | 18 | ~93 | ~33 min | Previous baseline |
| nvenc_h265 | slower | 18 | _ | _ | Needs test |
| x264 | medium | 18 | _ | _ | SW baseline |

---

## HandBrake Rebuild History
- Original: `nvdec: is not compiled into this build`
- Rebuilt: Cloned HandBrake 1.11.1, `./configure --enable-nvdec --disable-gtk`, binary at `/usr/local/bin/HandBrakeCLI`
- Original backup: `/usr/local/bin/HandBrakeCLI.orig`
