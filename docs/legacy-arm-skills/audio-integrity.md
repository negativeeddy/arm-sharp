# Audio Integrity Skill

Verify audio tracks are correct — right languages, right channel count, no duplicates.

## Background

Two common audio issues:
1. **Non-empty preset adds redundant tracks** — A preset like "HQ 1080p30 Surround" adds AAC stereo + AC-3 surround alongside `--first-audio` selection, causing duplicate audio.
2. **Missing `--mixdown 5point1`** — Without a preset, HandBrake's AC3 encoder defaults to stereo mixdown even for 5.1/7.1 sources.

## Checks

### 1. List audio streams in output

```bash
ffprobe -v error -select_streams a \
  -show_entries stream=index,codec_name,channels,channel_layout,sample_rate,bit_rate,language \
  -of default=noprint_wrappers=1 <output.mkv>
```

### 2. Expected audio profile

| Source | Expected Output |
|--------|----------------|
| 7.1 TrueHD | AC3 6ch 5.1(side) — downmixed from 7.1 via `--mixdown 5point1` |
| 5.1 DTS/AC3 | AC3 6ch 5.1(side) |
| 2.0 Stereo | AC3 6ch 5.1(side) — upmixed with empty center/surrounds |
| 1.0 Mono | AC3 6ch 5.1(side) — upmixed with empty channels |

Note: Upmixed stereo/mono to 5.1 sounds identical — AC3 has no space penalty for empty channels.

### 3. Detect duplicate audio tracks

```bash
ffprobe -v error -select_streams a -show_entries stream=index,codec_name,channels,channel_layout \
  -of default=noprint_wrappers_1 <output.mkv>
```

If you see more than 1 audio stream with `--first-audio` in config, a non-empty preset is adding extra tracks.

### 4. Check source audio against output

```bash
# Source audio
ffprobe -v error -select_streams a -show_entries stream=codec_name,channels,channel_layout \
  -of default=noprint_wrappers_1 <raw.mkv>

# Output audio
ffprobe -v error -select_streams a -show_entries stream=codec_name,channels,channel_layout \
  -of default=noprint_wrappers_1 <output.mkv>
```

Compare channels: 7.1→6ch is expected (downmix). 6ch→2ch means mixdown is missing.

### 5. Verify `--mixdown 5point1` is in the encode command

```bash
grep "mixdown" /home/arm/logs/<job>.log
```

## Common Issues

| Symptom | Root Cause | Fix |
|---------|-----------|-----|
| Stereo output from 5.1/7.1 source | Missing `--mixdown 5point1` in `HB_ARGS_BD` | Add `--mixdown 5point1` to `HB_ARGS_BD` |
| 2+ audio tracks when expecting 1 | Non-empty `HB_PRESET_BD` adding tracks | Set `HB_PRESET_BD: ""` |
| Mono source at 640 kbps | Pre-fix encode before mixdown config | Re-encode or accept (wasteful but harmless) |

## Re-encode Command

If audio is wrong, re-encode the raw with correct settings:

```bash
HandBrakeCLI \
  -i <raw.mkv> \
  -o <output.mkv> \
  -e nvenc_h265 --encoder-preset slow --quality 22 --enable-hw-decoding nvdec \
  --encopts spatial-aq=1:aq-strength=10:g=50:keyint-min=23 \
  --audio-lang-list eng --first-audio \
  --aencoder ac3 --ab 640 --mixdown 5point1 \
  --all-subtitles --subtitle-burned=none
```
