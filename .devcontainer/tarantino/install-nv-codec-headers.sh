#!/bin/bash
set -euo pipefail

version_file="/tmp/VERSION_NV_CODEC_HEADERS"
if [[ ! -f "$version_file" ]]; then
    echo "VERSION_NV_CODEC_HEADERS file not found at $version_file" >&2
    exit 1
fi

nvcodec_version="$(tr -d '[:space:]' < "$version_file")"
if [[ -z "$nvcodec_version" ]]; then
    echo "VERSION_NV_CODEC_HEADERS is empty" >&2
    exit 1
fi

workdir="$(mktemp -d)"
trap 'rm -rf "$workdir"' EXIT
cd "$workdir"

wget -O nv-codec-headers.tar.gz "https://github.com/FFmpeg/nv-codec-headers/archive/refs/tags/${nvcodec_version}.tar.gz"
tar -xzf nv-codec-headers.tar.gz
cd "nv-codec-headers-${nvcodec_version}"
make -j"$(nproc)"
make install PREFIX=/usr/local