# Encode Quality Skill

Verify output file meets expected encoding specifications.

## Checks

### 1. Video codec

```bash
ffprobe -v error -select_streams v:0 -show_entries stream=codec_name,profile,width,height,pix_fmt,avg_frame_rate \
  -of default=noprint_wrappers_1 <file.mkv>
```

**Expected for Blu-rays:** `hevc`, `Main`, 1920x*, `yuv420p`, `24000/1001` (23.976 fps)
**Expected for DVDs:** `h264`, `Main`, 720x*, `yuv420p`

### 2. Keyframe interval

Check that I-frames occur every ~48-50 frames (matching `g=50`):

```bash
ffprobe -v error -select_streams v:0 -show_entries frame=pict_type -of csv=p=0 \
  -read_intervals "0%+#500" <file.mkv> 2>/dev/null | \
  tr -d " " | tr "," "\n" | grep -n "I" | head -10
```

**Expected:** I-frame at positions 1, 51, 101, 151... (gap of 50 frames ≈ 2.09s at 24fps)
**If gap is ~250 frames:** Encoder used default keyframe interval instead of `g=50`

### 3. File size vs duration — compression ratio

```bash
ffprobe -v error -show_entries format=duration,bit_rate,size -of csv=p=0 <file.mkv>
```

**Expected bitrate range:** 8-12 Mbps for 1080p HEVC NVENC at quality 22.
**Expected compression:** 4-6x vs raw MakeMKV (typical raw is 30-50 Mbps).

| Duration | Expected Size Range |
|----------|-------------------|
| 90 min | 5-8 GB |
| 120 min | 7-11 GB |
| 150 min | 9-14 GB |

### 4. Verify complete rip (duration match)

```bash
# Get output duration
ffprobe -v error -show_entries format=duration -of csv=p=0 <file.mkv>

# Compare against expected runtime (IMDb, or DB track)
sqlite3 /home/arm/db/arm.db "
SELECT length, filename FROM track WHERE job_id = <job_id> ORDER BY ABS(length - <duration>) LIMIT 1;
"
```

**Expected:** Within 2% of expected duration. More than 5% short suggests partial rip.

### 5. Subtitle tracks

```bash
ffprobe -v error -select_streams s -show_entries stream=index,codec_name,codec_tag_string \
  -of default=noprint_wrappers_1 <file.mkv>
```

**Expected:** All PGS subtitles from source preserved.
**Watch for:** Missing forced subtitles if source has them.

## Full Audit Command

```bash
ffprobe -v error \
  -show_entries format=duration,bit_rate,size \
  -show_entries stream=index,codec_type,codec_name,profile,width,height,pix_fmt,channels,channel_layout,bit_rate,avg_frame_rate \
  -of default=noprint_wrappers=1 <file.mkv>
```
