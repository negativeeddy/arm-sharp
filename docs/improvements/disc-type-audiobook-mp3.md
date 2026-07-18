# Disc Type Detection — MP3 Audiobook / Audio CD-ROM Support

## Context

Discovered while investigating job 607: an "Angels & Demons" audiobook disc mounted as an iso9660 filesystem containing `.mp3` files at the root — no `VIDEO_TS`, `BDMV`, or `CDA` directories present.

## The Problem

`GetDiscType()` in `IdentifyService.cs` only recognizes three disc structures:

| Check | Pattern | Detects |
|-------|---------|---------|
| `Directory.Exists("VIDEO_TS")` | DVD | ✅ |
| `Directory.Exists("BDMV")` | Blu-ray | ✅ |
| `FindOnDisc("CDA")` | Audio CD | ✅ |
| *(everything else)* | `DiscType.Unknown` | ❌ Fails |

An MP3 audiobook disc has none of these structures, so the Conductor hits the `default` case and the job fails with:

> `Couldn't identify the disc type. Exiting without any action.`

## What Should Happen

The system already has a `DiscType.Data` case in the Conductor that calls `RipDataAsync`, and an existing `DiscType.Music` path using MusicBrainZ + `RipMusicAsync`. We should add MP3/audio file detection to `GetDiscType()`:

- Scan the root of the mounted disc for common audio file extensions (`.mp3`, `.flac`, `.ogg`, `.wav`, `.m4a`, `.aac`)
- If audio files are found, classify as `DiscType.Music` so the existing music pipeline can handle it
- Alternatively, classify as `DiscType.Data` as a fallback for discs with files but no known video/audio directory structure

## Open Questions

- Should `RipMusicAsync` / the music pipeline handle MP3 files, or should this be a generic file copy (data disc) operation?
- Would MusicBrainZ identification work for an audiobook with no CDDA tracks?
- Should we add a new `DiscType.Audiobook` for dedicated handling?
