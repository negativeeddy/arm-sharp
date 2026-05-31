# Config Audit Skill

Verify arm.yaml settings match intended encoding philosophy.

## Checks

### 1. PRESET_BD must be empty
- **Setting:** `HB_PRESET_BD`
- **Expected:** `""` (empty)
- **Why:** Non-empty presets add redundant audio tracks (e.g., AAC stereo + AC-3 surround alongside `--first-audio` selection). All video overrides are in `HB_ARGS_BD`.
- **If wrong:** Output will have duplicate/extra audio tracks.

### 2. HB_ARGS_BD contains required flags
- **Setting:** `HB_ARGS_BD`
- **Expected contains:**
  - `-e nvenc_h265` — HEVC NVENC encode
  - `--encoder-preset slow`
  - `--quality 22`
  - `--enable-hw-decoding nvdec`
  - `--encopts spatial-aq=1:aq-strength=10:g=50:keyint-min=23`
  - `--audio-lang-list eng` — English tracks only
  - `--first-audio` — Keep first track (for foreign films)
  - `--aencoder ac3 --ab 640`
  - `--mixdown 5point1` — Required! Without it AC3 defaults to stereo
  - `--all-subtitles`
  - `--subtitle-burned=none`

### 3. HB_ARGS_DVD contains required flags
- **Setting:** `HB_ARGS_DVD`
- **Expected contains:**
  - `-e nvenc_h264` — h264 (not HEVC, GTX 1060 HEVC has no B-frames)
  - `--encopts ...bf=4:cabac=1:g=50:keyint-min=23` — B-frames supported for h264
  - `--all-audio` — Keep all audio for DVDs
  - `--all-subtitles`
  - `--aencoder ac3 --ab 640`

### 4. Disable FFMPEG
- **Setting:** `USE_FFMPEG`
- **Expected:** `false`
- **Why:** FFMPEG produces lossless ~31+ GB files.

### 5. Raw file cleanup
- **Setting:** `DELRAWFILES`
- **Expected:** `true`
- **Why:** Raw MakeMKV rips are ~20-40 GB each. Delete after successful transcode.

### 6. Allow duplicates
- **Setting:** `ALLOW_DUPLICATES`
- **Expected:** `true`
- **Why:** Prevents ARM from skipping re-rips of same disc.

### 7. Main feature mode
- **Setting:** `MAINFEATURE`
- **Expected:** `true`
- **Why:** With filesize-first sort fix, auto-selection is reliable.

### 8. Permissions
- **Setting:** `SET_MEDIA_PERMISSIONS`, `CHMOD_VALUE: 777`, `SET_MEDIA_OWNER: true`, `CHOWN_USER: "arm"`, `CHOWN_GROUP: "arm"`
- **Why:** Ensures host can access files without sudo.

## Check Commands

```bash
# Dump all relevant settings
grep -E "^(HB_PRESET_BD|HB_ARGS_BD|HB_ARGS_DVD|USE_FFMPEG|DELRAWFILES|ALLOW_DUPLICATES|MAINFEATURE|DISABLE_LOGIN)" /etc/arm/config/arm.yaml
```
