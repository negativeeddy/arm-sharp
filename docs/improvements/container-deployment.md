# Container / Deployment

## Docker Image Size
Docker image is ~2GB with full .NET SDK. Switch to self-contained publish with runtime-only image to reduce size.

## Multi-Arch Builds
GitHub Actions CI has QEMU set up but only builds `linux/amd64`. Add `linux/arm64` multi-arch build once ARM64 runners or cross-compilation are available.

## HandBrake nvdec Support
Current `arm-dependencies:1.7.3` base image compiles HandBrake without `--enable-nvdec`. The devcontainer has a custom rebuild with nvdec working, but the production Dockerfile still uses the base image's build (no hw-decoding). Need to either fork and rebuild `arm-dependencies`, or add a multi-stage HandBrake build step to the production Dockerfile.

## BuildKit Migration
Docker buildx warning — migrate from legacy builder to BuildKit.
