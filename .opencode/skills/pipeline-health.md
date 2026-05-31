# Pipeline Health Skill

Diagnose issues with the ARM pipeline — containers, logs, progress tracking, common failure modes.

## Checks

### 1. Container status

```bash
docker ps --filter name=arm --format "{{.Names}} {{.Status}} {{.State}}"
```

**Expected:** `arm Up <time> running`. If restarting or unhealthy, check logs.

### 2. Recent job status

```bash
sqlite3 /home/arm/db/arm.db "
SELECT job_id, title, year, status, start_date, stop_date
FROM job
ORDER BY job_id DESC LIMIT 10;
"
```

**Status values:** `active`, `success`, `fail`, `waiting`
- Multiple `fail` in a row → systemic issue (disc drive, config, permissions)

### 3. MakeMKV progress (if job is active)

Check the progress file:
```bash
ls -lt /home/arm/logs/progress/ | head -5
cat /home/arm/logs/progress/<job_id>.log
```

**If empty:** MakeMKV progress fix not applied (see below).

### 4. Verify progress bar fix is applied

```bash
grep -n "progress" /opt/arm/arm/ui/json_api.py | head -5
```

**Expected code (line ~115):**
```python
progress_log = os.path.join(cfg.arm_config['LOGPATH'], "progress", f"{job.job_id}.log")
if os.path.exists(progress_log):
    lines = read_log_line(progress_log)
else:
    lines = read_log_line(os.path.join(cfg.arm_config['LOGPATH'], job.logfile))
```

**If missing:** UI progress bar will show 0% during MakeMKV rip.

### 5. Disk space

```bash
df -h /home/arm/media
```

**Expected:** >20% free. Raw rips are 20-40 GB each. Two simultaneous jobs need ~80 GB free.

### 6. Common error patterns in logs

```bash
# Permission errors
grep -i "permission denied\|cannot open\|access denied" /home/arm/logs/*.log | tail -20

# MakeMKV errors
grep -i "failed\|error\|failed to open disc" /home/arm/logs/*.log | tail -20

# HandBrake errors
grep -i "error\|failed\|segmentation fault" /home/arm/logs/*.log | tail -20
```

### 7. Raw files lingering

If `DELRAWFILES: true` but raw files remain for completed jobs:
```bash
ls /home/arm/media/raw/*/
```

Check for orphaned raw directories — indicates the post-processing step failed.

### 8. Container resource usage

```bash
docker stats arm --no-stream --format "{{.Name}} CPU:{{.CPUPerc}} MEM:{{.MemPerc}} {{.MemUsage}}"
```

High memory or CPU when idle → possible runaway process.

## Common Failure Modes

| Symptom | Likely Cause | Check |
|---------|-------------|-------|
| Job stuck at "ripping" | MakeMKV waiting on disc read | `docker top arm`, check disc drive |
| Job stuck at "transcoding" | HandBrake crashed | Check HandBrake process, log tail |
| Progress bar stuck at 0% | Progress fix not applied | Check `json_api.py` code |
| Job fails immediately | Config error or missing device | Check `arm.yaml`, disc drive path |
| Output is partial/short | Main feature selected wrong track | Run main-feature skill |
| Raw files not cleaned up | Post-processing step failed | Check logs for permission/script errors |
