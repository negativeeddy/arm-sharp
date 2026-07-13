# 4K UHD Blu-ray Encoding Plan

## Overview

ARM currently handles 1080p Blu-ray and DVD encoding with different presets. This plan defines 4K UHD Blu-ray support, including HDR handling and client compatibility.

## Hardware Context

- **GPU:** GTX 1060 6GB (Pascal, CC 6.1)
- **NVENC:** HEVC 8-bit only, no 10-bit, no B-frames
- **NVDEC:** Hardware decode for all formats including 4K HEVC 10-bit
- **Constraint:** Cannot encode 10-bit HDR natively with NVENC

## Encoding Strategies

### Strategy A: Passthrough (Recommended)

**When to use:** Default for 4K UHD discs

```yaml
# arm.yaml settings for 4K
USE_FFMPEG: true
FFMPEG_POST_FILE_ARGS: "-fflags +genpts -c:v copy -c:a ac3 -b:a 640k -c:s copy -map 0"
```

**Characteristics:**
- Video: Lossless copy (zero generation loss)
- Audio: AC3 5.1 640kbps (ffmpeg preserves AC3 as specified). For lossless passthrough, use `-c:a copy` instead
- HDR: 100% preserved (HDR10, HDR10+, Dolby Vision)
- File size: ~25-70 GB (matches source)
- Time: ~5-10 minutes per disc
- Quality: Identical to source

**Pros:**
- Maximum quality
- HDR metadata intact
- Fast processing
- No GPU encoding required

**Cons:**
- Large files (25-70 GB per movie)
- Storage intensive

### Strategy B: SDR Tonemap

**When to use:** Storage-constrained, non-HDR displays

```yaml
# arm.yaml settings for 4K SDR
USE_FFMPEG: false
HB_PRESET_4K: "HQ 2160p30"
HB_ARGS_4K: "-e nvenc_h265 --encoder-preset slow --quality 22 --enable-hw-decoding nvdec --encopts spatial-aq=1:aq-strength=8:g=50:keyint-min=23 --all-audio --all-subtitles --subtitle-burned=none --aencoder ac3 --ab 640"
```

**Characteristics:**
- Video: Re-encode to 8-bit HEVC SDR
- Audio: AAC (HandBrake overrides `--aencoder ac3` to AAC at runtime)
- HDR: Lost (converted to SDR)
- File size: ~15-45 GB
- Time: ~45-70 minutes per disc
- Quality: Good, but loses HDR benefit

**Pros:**
- Smaller files than passthrough
- Compatible with all devices
- No server-side transcoding needed

**Cons:**
- Permanent HDR loss
- Slower processing
- Quality loss from re-encode

### Strategy C: Skip Transcode

**When to use:** Maximum quality, ample storage

```yaml
# arm.yaml settings
SKIP_TRANSCODE: true
DELRAWFILES: false
```

**Characteristics:**
- Keep raw MakeMKV output (MKV with HEVC 10-bit + lossless audio)
- File size: ~40-80 GB per disc
- Time: Only rip time (~10-20 minutes)
- Quality: Lossless

**Pros:**
- Zero quality loss
- Fastest processing
- All metadata preserved

**Cons:**
- Largest files
- May need transcoding for some clients

## Client Compatibility Matrix

| Client | Passthrough | SDR Tonemap | Skip Transcode |
|--------|-------------|-------------|----------------|
| **Xbox One X (Jellyfin app)** | ✅ Direct play | ✅ Direct play | ✅ Direct play |
| **Chrome/Edge (HDR display)** | ✅ Direct play | ✅ Direct play | ✅ Direct play |
| **Chrome/Edge (SDR display)** | ❌ Transcode | ✅ Direct play | ❌ Transcode |
| **Firefox** | ❌ Transcode | ✅ Direct play | ❌ Transcode |
| **iOS Safari** | ❌ Transcode | ✅ Direct play | ❌ Transcode |
| **Android (ExoPlayer)** | ✅ Direct play | ✅ Direct play | ✅ Direct play |
| **Smart TVs** | ⚠️ Varies | ✅ Direct play | ⚠️ Varies |

**Key insights:**
- Xbox One X with Jellyfin app: Full 4K HDR/HEVC support (since v0.9.3)
- Web players: Inconsistent HDR support, often force transcoding
- Server-side tonemapping: Handles non-HDR clients automatically

## Size Expectations

| Duration | Passthrough (4K) | SDR Tonemap (4K) | 1080p Encode |
|----------|------------------|------------------|--------------|
| 90 min | 25-40 GB | 15-25 GB | 5-8 GB |
| 120 min | 35-55 GB | 20-35 GB | 7-11 GB |
| 150 min | 45-70 GB | 25-45 GB | 9-14 GB |

## Detection Logic

ARM should auto-detect disc type and apply appropriate strategy:

```csharp
// In Conductor.cs or ArmRipperService.cs
if (disc.Is4KUHD)
{
    if (settings.SkipTranscode4K)
        return EncodingStrategy.SkipTranscode;
    else if (settings.SdrTonemap4K)
        return EncodingStrategy.SdrTonemap;
    else
        return EncodingStrategy.Passthrough; // default
}
else
{
    // Existing 1080p/DVD logic
}
```

## Recommended Configuration

For your setup (Xbox One X primary, web player secondary):

```yaml
# Default: Passthrough for 4K (preserves HDR for Xbox)
# SDR tonemap only if explicitly requested
SKIP_TRANSCODE_4K: false
SDR_TONEMAP_4K: false

# For storage-constrained users:
# SKIP_TRANSCODE_4K: false
# SDR_TONEMAP_4K: true
```

## Testing Checklist

- [ ] 4K HDR10 disc → passthrough → Xbox direct play
- [ ] 4K HDR10 disc → passthrough → web player transcoding
- [ ] 4K Dolby Vision disc → passthrough → Xbox direct play
- [ ] 4K disc → SDR tonemap → web player direct play
- [ ] Duration match: output within 2% of source
- [ ] HDR metadata preserved (MaxCLL/MaxFALL)
- [ ] Audio tracks preserved (TrueHD/Atmos passthrough or AC3)
- [ ] Subtitle tracks preserved (PGS)
- [ ] File size within expected range

## Implementation Steps

1. **Detect 4K discs** — Check resolution from MakeMKV info (3840x* = 4K)
2. **Add arm.yaml settings** — `HB_PRESET_4K`, `HB_ARGS_4K`, `SKIP_TRANSCODE_4K`, `SDR_TONEMAP_4K`
3. **Update encoding logic** — Branch based on disc type
4. **Update UI** — Show 4K-specific options
5. **Update documentation** — Add 4K section to config docs
6. **Test with real discs** — Verify all strategies work

## References

- [Jellyfin HDR Support](https://jellyfin.org/docs/general/clients/codec-support/)
- [Jellyfin Xbox App](https://github.com/jellyfin/jellyfin-xbox)
- [NVENC HEVC Capabilities](https://developer.nvidia.com/video-codec-sdk)
- [Original ARM encoding_test_plan.md](../plans/ENCODING_TEST_PLAN.md)
