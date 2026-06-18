#!/bin/bash
set -euo pipefail

version_file="/tmp/VERSION_HANDBRAKE"
if [[ ! -f "$version_file" ]]; then
    echo "VERSION_HANDBRAKE file not found at $version_file" >&2
    exit 1
fi

handbrake_version="$(tr -d '[:space:]' < "$version_file")"
if [[ -z "$handbrake_version" ]]; then
    echo "VERSION_HANDBRAKE is empty" >&2
    exit 1
fi

if [[ $(dpkg --print-architecture) =~ arm.* ]]; then
    echo "Running on arm architecture; skipping HandBrake source build" >&2
    exit 0
fi

workdir="$(mktemp -d)"
gnupg_home="$(mktemp -d)"
trap 'rm -rf "$workdir" "$gnupg_home"' EXIT
cd "$workdir"

wget -O handbrake.tar.bz2.sig "https://github.com/HandBrake/HandBrake/releases/download/${handbrake_version}/HandBrake-${handbrake_version}-source.tar.bz2.sig"
wget -O handbrake.tar.bz2 "https://github.com/HandBrake/HandBrake/releases/download/${handbrake_version}/HandBrake-${handbrake_version}-source.tar.bz2"

export GNUPGHOME="$gnupg_home"
gpg --batch --keyserver keyserver.ubuntu.com --recv-keys '1629C061B3DDE7EB4AE34B81021DB8B44E4A8645'
gpg --batch --verify handbrake.tar.bz2.sig handbrake.tar.bz2

mkdir -p handbrake-src
tar -xjf handbrake.tar.bz2 -C handbrake-src --strip-components=1

cd handbrake-src
nproc_count="$(nproc)"
./configure --disable-gtk --enable-qsv --enable-vce --enable-nvdec --launch-jobs="$nproc_count" --launch
make -C build -j "$nproc_count"
make -C build install
cp /usr/local/bin/HandBrakeCLI /usr/bin/HandBrakeCLI