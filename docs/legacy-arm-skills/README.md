# Legacy ARM Skills (Reference Only)

These 7 files were originally opencode skills written for the **original Python ARM** system. They are preserved here as reference material — the encoding philosophy, ffprobe verification commands, and quality checks are still useful context for ARM-Sharp's output verification.

**⚠️ Not active skills.** They are NOT loaded by opencode or VS Code:

- Wrong format — opencode only discovers `.opencode/skills/<name>/SKILL.md` folders with YAML frontmatter, not flat `.md` files
- Stale paths — they reference the old Python ARM layout (`/opt/arm/arm/ripper/makemkv.py`, `arm.yaml` settings like `HB_PRESET_BD`/`HB_ARGS_BD`, raw `sqlite3 /home/arm/db/arm.db` queries). ARM-Sharp replaced all of this with EF Core + SQLite, DB-first settings, and `HandBrakeService`/`ArmRipperService`.

## Files

| File | Topic |
|------|-------|
| `audio-integrity.md` | Audio track verification (languages, channels, duplicates, mixdown) |
| `completion-report.md` | End-to-end rip verification after a job completes |
| `config-audit.md` | arm.yaml settings audit (presets, HB_ARGS, FFMPEG, permissions) |
| `encode-quality.md` | Output encoding spec verification (codec, keyframes, bitrate, size) |
| `hardware-accel.md` | NVENC/NVDEC hardware encode/decode verification |
| `main-feature.md` | Main-feature title selection diagnosis |
| `pipeline-health.md` | Container, job status, progress, disk space checks |

## If you still run the old Python ARM

Copy the relevant files to `~/.config/opencode/skills/<name>/SKILL.md` (global) or `.opencode/skills/<name>/SKILL.md` (project) on that machine, adding YAML frontmatter:

```yaml
---
name: <skill-name>
description: <what it does>
---
```