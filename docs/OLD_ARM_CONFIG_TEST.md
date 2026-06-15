This is the test plan I used on the old ARM software before the port to C#. It has my preferences for encoding and the hardware that the tarantino dev container and tarantino ARM instance run on.

# ARM / HandBrake Encoding Test Plan

## Hardware

- **CPU:** Intel i5-8600K (5 cores available)
- **GPU:** NVIDIA GeForce GTX 1060 6GB (Pascal, CC 6.1)
- **NVENC:** Available (H.264 + H.265)
- **NVDEC:** Now compiled into HandBrake (`nvdec: is available`)
- **Driver:** 580.159.03, CUDA 13.0

## Source Files (raw MakeMKV rips)

| Movie | Resolution | Codec | HDR | Bitrate | Size |
|-------|-----------|-------|-----|---------|------|
| The Game (1997) | 1920x1080 | H.264 8-bit | No | ~38 Mbps | ~26 GB |
| The Prestige (4K) | 3840x2160 | HEVC 10-bit | HDR10 | ~61 Mbps | ~56 GB |

---

## Two Configs — Switch Based on Disc Type

You need to change arm.yaml lines when switching between 1080p Blu-rays and 4K UHD discs.

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

**arm.yaml settings:**

```yaml
USE_FFMPEG: true

# No HandBrake presets needed when USE_FFMPEG=true

FFMPEG_CLI: "ffmpeg"
FFMPEG_PRE_FILE_ARGS: ""
FFMPEG_POST_FILE_ARGS: "-fflags +genpts -c:v copy -c:a ac3 -b:a 640k -c:s copy -map 0"

# These settings are ignored when USE_FFMPEG=true but keep them commented
# for reference when switching back:
# HB_ARGS_DVD: "..."
# HB_ARGS_BD:  "..."
```

Performance:
- ~5-10 min per 4K movie (just remux + audio encode)
- File size: ~ same as source (~50-60 GB)
- No generation loss on video
- HDR metadata 100% preserved

---

## Audio Options (applied in both configs)

| Wrong | Correct |
|-------|---------|
| `--audio-encoder ac3` | `-E ac3` or `--aencoder ac3` |
| `--audio-bitrate 640` | `-B 640` or `--ab 640` |
| `--mixdown 5point1` | `--mixdown 5point1` (correct) |

Output will be AC3 5.1 640kbps on all audio tracks.

---

## How to Run a Test Encode

### HandBrake (60s clip)

```bash
docker exec arm HandBrakeCLI \
  -i '/home/arm/media/raw/<MOVIE>/<FILE>.mkv' \
  -o /tmp/test_<label>.mkv \
  -e <encoder> \
  --encoder-preset <preset> \
  --quality <crf> \
  --start-at duration=60 \
  --stop-at duration=120 \
  --enable-hw-decoding nvdec \
  --all-audio \
  -E ac3 -B 640 --mixdown 5point1
```

### ffmpeg passthrough (full remux)

```bash
docker exec arm ffmpeg \
  -i '/home/arm/media/raw/<MOVIE>/<FILE>.mkv' \
  -map 0 -c:v copy \
  -c:a ac3 -b:a 640k \
  '/tmp/test_<label>.mkv'
```

---

## Test Results

### The Prestige (4K HDR HEVC 10-bit, 3840x2160)

CPU decode baseline (no NVDEC): — no longer relevant, NVDEC is now compiled in

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
- Auto-crop removes letterbox bars (e.g. 3840x1596 output)

**⚠️ nvenc_h264 and nvenc_h265 (8-bit) carry HDR metadata tags but encode 8-bit video.**
Players will see HDR flags and expect 10-bit range, but get 8-bit data. This will look washed out or wrong on HDR displays. Only `nvenc_h265_10bit` properly preserves actual HDR.

ffmpeg passthrough:

| Method | Time | Keeps HDR? | Notes |
|--------|------|-----------|-------|
| `-c:v copy -c:a ac3` | ~5 min | Yes | Zero loss, full quality |

### The Game (1080p H.264) — not yet re-tested after NVDEC rebuild

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

## Other Config Notes

- **DELRAWFILES: false** — raw files preserved for testing
- **NVDEC must be explicitly enabled** with `--enable-hw-decoding nvdec` in CLI args
- **`ffmpeg -hwaccel cuda`** also works for GPU decode but `-c:v copy` is simpler for passthrough
