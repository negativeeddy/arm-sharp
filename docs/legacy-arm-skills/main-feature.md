# Main Feature Detection Skill

Diagnose which title ARM selected as the main feature and whether it's correct.

## Background

ARM's default sort for choosing the main feature is:
```
Track.chapters.desc(), Track.filesize.desc(), Track.track_number.asc()
```

This picks extras with many fake chapter markers over the actual movie. The fix reorders to filesize-first:
```
Track.filesize.desc(), Track.chapters.desc(), Track.track_number.asc()
```

## Checks

### 1. Verify sort order in source code
- **File:** `/opt/arm/arm/ripper/makemkv.py` (inside container)
- **Line:** ~671
- **Expected:** `Track.filesize.desc()` first
- **If wrong:** ARM may select short extras with many chapter markers.

### 2. Check which title was selected
Check the job log for the selected track number and title.

```bash
# Find the job log
ls -lt /home/arm/logs/*.log | head -5

# Look for main feature selection
grep -i "mainfeature\|main feature\|selected\|title" /home/arm/logs/<job>.log
```

### 3. Compare track sizes and chapters
If the wrong title was selected, query the DB to compare:

```bash
sqlite3 /home/arm/db/arm.db "
SELECT track_number, filesize, chapters, length, filename
FROM track
WHERE job_id = <job_id>
ORDER BY filesize DESC;
"
```

The largest file with a reasonable number of chapters is typically the movie.

## Diagnosis Flow

1. If output file is much shorter than expected → check selection
2. If output has extra/commentary as main → filesize-first sort not applied
3. If output is correct → feature detection is healthy

## Fix

Edit `/opt/arm/arm/ripper/makemkv.py` line ~671:
```python
# Before:
Track.chapters.desc(), Track.filesize.desc(), Track.track_number.asc()

# After:
Track.filesize.desc(), Track.chapters.desc(), Track.track_number.asc()
```

Restart ARM to apply.

## Verify

```bash
grep "Main Feature" /home/arm/logs/<job>.log
# Should show the largest track, not the one with most chapters
```
