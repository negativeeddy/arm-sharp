# Configuration & Setup — Add 4K UHD Disc Type with Separate Settings

Currently `ArmSettings` and `ConfigSnapshot` only have `HbArgsDvd` / `HbPresetDvd` and `HbArgsBd` / `HbPresetBd`. 4K UHD discs need different handling (ffmpeg passthrough to preserve HDR, no HandBrake re-encode).

Add `HbArgsUhd` / `HbPresetUhd` and `FfmpegPostFileArgsUhd` properties, a `DiscType.Uhd` enum value, and wire them through `HandBrakeService.GetHbSettings()` and the `Conductor` pipeline. For 4K UHD, the typical workflow is `USE_FFMPEG: true` with `-c:v copy -c:a ac3 -b:a 640k` to preserve HDR metadata losslessly.

Users currently have to swap `arm.yaml` manually when switching between 1080p and 4K discs — automatic disc-type detection would eliminate this.
