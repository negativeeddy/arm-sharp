# MakeMKV — SDF Download Hang (Older BD-ROM Drives)

## Problem

`makemkvcon info` hangs at 99% CPU immediately after startup:

```
MSG:1005,0,1,"MakeMKV v1.18.3 linux(x64-release) started","%1 started","MakeMKV …"
[no further output — process spins forever]
```

The debug log (`/root/MakeMKV_log.txt`) shows:

```
No  SDF v0a6: HL-DT-ST_DVDRWBD_GBC-H20N_B101_20070911123456_K187A8F5120
SDF auto v0a6: HL-DT-ST_DVDRWBD_GBC-H20N_B101_20070911123456_K187A8F5120
```

## Root Cause

1. The local `_private_data.tar` (bundled with `makemkv-bin`, stored in
   `~/.MakeMKV/`) contains an `sdf.bin` that lacks an entry for certain
   older Blu-ray drives (e.g. LG GBC-H20N / GGC-H20N).
2. When MakeMKV encounters an unknown drive, it attempts to auto-download
   an updated `sdf.bin` from `https://www.makemkv.com/svq/sdf.bin`.
3. If `makemkv.com` is unreachable (DNS down, Cloudflare SSL error, etc.),
   the download hangs indefinitely at 99% CPU.

With `--noscan` the drive *is* detected (direct SCSI access), but without
SDF data MakeMKV cannot read the disc itself.

## Fix

Add an `sdf_Stop` line to `~/.MakeMKV/settings.conf` that tells MakeMKV to
skip the SDF download for a specific drive and fall back to **direct disc
access mode** (which works fine for most discs):

```
sdf_Stop = "<DRIVE_ID>"
```

The drive ID is shown in the debug log. Format:

```
<VENDOR>_<MODEL>_<FIRMWARE>_<MFG_DATE>_<SERIAL>
```

### Example — LG GBC-H20N

```
sdf_Stop = "HL-DT-ST_DVDRWBD_GBC-H20N_B101_20070911123456_K187A8F5120"
```

### Verifying the fix

```bash
makemkvcon --robot --messages=-stdout info dev:/dev/sr0 --minlength=0
```

Successful output includes the disc label on the `DRV:` line (drive status
changes from `0` to `2`) and `"Using direct disc access mode"`.

## Diagnosis for New Drives

If a *different* drive hangs in the future:

```bash
# 1. Run with debug logging (timeout prevents permanent hang)
timeout 15 makemkvcon --debug --robot --messages=-stdout info dev:/dev/sr0 --minlength=0

# 2. Extract the drive ID from the log
grep "No SDF" /root/MakeMKV_log.txt

# 3. Add to settings.conf
echo 'sdf_Stop = "<DRIVE_ID_FROM_LOG>"' >> ~/.MakeMKV/settings.conf
```

## Automation

The devcontainer `post-create.sh` script automatically adds known
`sdf_Stop` entries during container startup. Add new drives to the
`declare -A sdf_stop_drives` associative array in
`.devcontainer/post-create.sh`.

## When to Remove

Once `makemkv.com` is reachable again, consider removing the `sdf_Stop`
line(s) and letting MakeMKV download an updated `sdf.bin` that may
natively support the drive. Direct disc access is a fallback — native
SDF support is preferred for full AACS handling.

## References

- [MakeMKV forum thread: "not reading bluray (but on windows yes)"](https://forum.makemkv.com/forum/viewtopic.php?t=42148)
  — Same class of drives (LG GBC/GGC-H20N) with the same fix.
