# Completion Report Skill

Full end-to-end rip verification — run this after a job completes to validate the entire pipeline.

## One-Shot Audit

```bash
# Set this to the job output file
FILE="/path/to/output.mkv"

echo "=== FILE INFO ==="
ls -lh "$FILE"

echo "=== DURATION & BITRATE ==="
ffprobe -v error -show_entries format=duration,bit_rate,size -of csv=p=0 "$FILE"

echo "=== VIDEO ==="
ffprobe -v error -select_streams v:0 \
  -show_entries stream=codec_name,profile,width,height,pix_fmt,avg_frame_rate \
  -of default=noprint_wrappers=1 "$FILE"

echo "=== AUDIO ==="
ffprobe -v error -select_streams a \
  -show_entries stream=index,codec_name,channels,channel_layout,sample_rate,bit_rate \
  -of default=noprint_wrappers=1 "$FILE"

echo "=== SUBTITLES ==="
ffprobe -v error -select_streams s -show_entries stream=index,codec_name \
  -of default=noprint_wrappers=1 "$FILE"

echo "=== KEYFRAMES ==="
ffprobe -v error -select_streams v:0 -show_entries frame=pict_type -of csv=p=0 \
  -read_intervals "0%+#500" "$FILE" 2>/dev/null | \
  tr -d " " | tr "," "\n" | grep -n "I" | head -10

echo "=== COMPLETENESS CHECK ==="
# If we have the job_id, check against DB
sqlite3 /home/arm/db/arm.db "
SELECT j.job_id, j.title, j.year, t.filesize, t.length,
       ROUND(CAST(t.filesize AS REAL) / CAST(t.length AS REAL), 0) as raw_bitrate
FROM job j
JOIN track t ON t.job_id = j.job_id
WHERE j.status = 'success'
ORDER BY j.job_id DESC LIMIT 5;
"
```

## Checklist

| Check | Pass/Fail | Notes |
|-------|-----------|-------|
| Video codec HEVC/h264 | | Should match source type |
| Resolution correct | | 1920x* for BD, 720x* for DVD |
| Frame rate 23.976 | | Unless interlaced source |
| Audio 5.1(side) 6ch | | Mixed down from any source |
| Subtitle tracks present | | All PGS kept |
| Keyframes every ~2s | | 50-frame gap at 24fps |
| Duration matches source | | Within 2% of expected |
| Bitrate 8-12 Mbps | | For 1080p HEVC |
| No duplicate audio | | Check only 1 audio stream |
| File size reasonable | | 5-14 GB depending on length |

## Duration Comparison

```python
#!/usr/bin/env python3
"""Verify output duration matches expected"""
import sqlite3, subprocess, json, sys, os

def verify(job_id):
    conn = sqlite3.connect("/home/arm/db/arm.db")
    c = conn.cursor()
    c.execute("""
        SELECT j.title, j.year, j.path, t.length, t.filesize
        FROM job j
        JOIN track t ON t.job_id = j.job_id
        WHERE j.job_id = ?
        ORDER BY t.filesize DESC LIMIT 1
    """, (job_id,))
    row = c.fetchone()
    if not row:
        print("No job found")
        return

    title, year, path, track_len, track_size = row
    mkv = [os.path.join(path, f) for f in os.listdir(path) if f.endswith(('.mkv','.mp4'))]
    if not mkv:
        print(f"No output file found in {path}")
        return

    result = subprocess.run(
        ["ffprobe", "-v", "error", "-show_entries", "format=duration,size", "-of", "json", mkv[0]],
        capture_output=True, text=True, timeout=30
    )
    data = json.loads(result.stdout)
    actual = float(data['format']['duration'])

    diff_pct = abs(actual - track_len) / track_len * 100
    status = "OK" if diff_pct < 2 else "SHORT"
    print(f"{title} ({year}): {track_len//60}m -> {int(actual)//60}m ({status}, {diff_pct:.1f}%)")
    conn.close()

if __name__ == "__main__":
    verify(sys.argv[1])
```
