# Hardware Acceleration Skill

Verify NVENC/NVDEC hardware encoding and decoding are active.

## Background

GTX 1060 (Pascal GP106, CC 6.1):
- **HEVC NVENC:** 8-bit only, **no B-frames** (`bf=0` required)
- **h264 NVENC:** Supports B-frames (`bf=4`)
- **NVDEC:** Supports hardware decoding for both h264 and HEVC

## Checks

### 1. Verify HandBrake is using NVENC

Check the encode log for NVENC initialization:

```bash
grep -i "nvenc\|nvdec\|cuvid\|cuda" /home/arm/logs/<job>.log
```

Look for:
- `encavcodecInit: H.265 (Nvidia NVENC)` — HEVC path active
- `encavcodec: encoding at rc=vbr, 22.00` — quality level confirmed
- `enable-hw-decoding nvdec` — hardware decoder active

**Watch for:** If you see `encavcodecInit: H.265 (Nvidia NVENC)` but also software encoder messages, it may be falling back.

### 2. Check for software fallback

```bash
grep -i "x265\|libx265\|software\|cpu" /home/arm/logs/<job>.log
```

If HandBrake falls back to software encoder (`x265`), the encode will be:
- Much slower (single-digit fps vs 150+ fps)
- Correct but slower

Common causes of fallback:
- `--enable-hw-decoding` without matching encoder type
- NVENC driver not loaded
- Encoder in use by another process

### 3. Verify driver and encoder availability

```bash
# Check NVIDIA driver
nvidia-smi --query-gpu=name,driver_version,compute_cap --format=csv,noheader

# Check encoder sessions
nvidia-smi -q -d ENCODER | grep -A2 "Encoder"

# Check available encoder count
nvidia-smi -q -d ENCODER | grep "Session Count"
```

### 4. Verify no B-frames in HEVC output

HEVC NVENC on Pascal cannot use B-frames. Check the frame types:

```bash
ffprobe -v error -select_streams v:0 -show_entries frame=pict_type \
  -of csv=p=0 -read_intervals "0%+#200" <output.mkv> 2>/dev/null | \
  tr -d " " | tr "," "\n" | sort | uniq -c
```

**Expected:** Only `I` and `P` frames. If `B` appears, the encoder supports B-frames (Turing+) or the wrong encoder was used.

### 5. Compare encode speed

HW encode should sustain 140-200 fps for 1080p content.
```bash
grep -oP "avg [\d.]+ fps" /home/arm/logs/<job>.log | tail -1
```

**Expected:** ~150+ fps average. If <50 fps, likely software fallback.

### 6. NVDEC hardware decode is active

```bash
grep "hw-decoding" /home/arm/logs/<job>.log
```

The flag `--enable-hw-decoding nvdec` offloads decoding to the GPU's hardware decoder, reducing CPU usage.

## Encoder Cap Matrix (GTX 1060 Pascal)

| Encoder | Codec | B-frames | Max Sessions | Notes |
|---------|-------|----------|-------------|-------|
| NVENC | HEVC | No (`bf=0`) | 2 | 8-bit only |
| NVENC | h264 | Yes (`bf=4`) | 2 | Full support |
| NVDEC | HEVC | N/A | Unlimited | Hardware decode |
| NVDEC | h264 | N/A | Unlimited | Hardware decode |

## Check Commands

```bash
# Quick hardware check
docker compose exec -T arm nvidia-smi --query-gpu=name,driver_version --format=csv,noheader

# Check what HandBrake is using (if running)
docker top arm | grep HandBrake

# Verify NVENC in log
grep "NVENC\|NVDEC\|encavcodec" /home/arm/logs/*.log | tail -5
```
