#!/bin/bash
# Install MakeMKV from locally downloaded archives instead of fetching from the website.
#
# Usage:
#   ./install-makemkv-from-files.sh [directory]
#
# If [directory] is omitted the script looks in the same directory as itself.
# It expects to find:
#   makemkv-oss-<version>.tar.gz
#   makemkv-bin-<version>.tar.gz
set -euo pipefail

src_dir="${1:-"$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"}"

if [[ ! -d "$src_dir" ]]; then
    echo "Source directory not found: $src_dir" >&2
    exit 1
fi

# Detect version from filenames
oss_archive="$(find "$src_dir" -maxdepth 1 -name 'makemkv-oss-*.tar.gz' | sort -V | tail -1)"
bin_archive="$(find "$src_dir" -maxdepth 1 -name 'makemkv-bin-*.tar.gz' | sort -V | tail -1)"

if [[ -z "$oss_archive" ]]; then
    echo "makemkv-oss-*.tar.gz not found in $src_dir" >&2
    exit 1
fi
if [[ -z "$bin_archive" ]]; then
    echo "makemkv-bin-*.tar.gz not found in $src_dir" >&2
    exit 1
fi

# Extract version from the oss archive filename
makemkv_version="$(basename "$oss_archive" | sed 's/makemkv-oss-\(.*\)\.tar\.gz/\1/')"
echo "Installing MakeMKV $makemkv_version from local files"
echo "  oss: $oss_archive"
echo "  bin: $bin_archive"

workdir="$(mktemp -d)"
trap 'rm -rf "$workdir"' EXIT
cd "$workdir"

declare -A archive_paths=(
    [makemkv-oss]="$oss_archive"
    [makemkv-bin]="$bin_archive"
)

for archive in makemkv-oss makemkv-bin; do
    src="${archive_paths[$archive]}"
    cp "$src" "$archive.tgz"

    mkdir "$archive"
    tar -xzf "$archive.tgz" -C "$archive" --strip-components=1
    rm -f "$archive.tgz"

    pushd "$archive" >/dev/null
    if [[ -f configure ]]; then
        ./configure --prefix=/usr/local
    else
        mkdir -p tmp
        touch tmp/eula_accepted
    fi
    make -j"$(nproc)" PREFIX=/usr/local
    make install PREFIX=/usr/local
    popd >/dev/null
done

echo "MakeMKV $makemkv_version installed successfully"
