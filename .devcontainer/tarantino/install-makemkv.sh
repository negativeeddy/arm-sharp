#!/bin/bash
set -euo pipefail

version_file="/tmp/VERSION_MAKEMKV"
if [[ ! -f "$version_file" ]]; then
    echo "VERSION_MAKEMKV file not found at $version_file" >&2
    exit 1
fi

makemkv_version="$(tr -d '[:space:]' < "$version_file")"
if [[ -z "$makemkv_version" ]]; then
    echo "VERSION_MAKEMKV is empty" >&2
    exit 1
fi

workdir="$(mktemp -d)"
trap 'rm -rf "$workdir"' EXIT
cd "$workdir"

wget -O sha256sums.txt.sig "https://www.makemkv.com/download/makemkv-sha-${makemkv_version}.txt"

export GNUPGHOME="$(mktemp -d)"
trap 'rm -rf "$GNUPGHOME" "$workdir"' EXIT
gpg --batch --keyserver keyserver.ubuntu.com --recv-keys 2ECF23305F1FC0B32001673394E3083A18042697
gpg --batch --decrypt --output sha256sums.txt sha256sums.txt.sig
gpgconf --kill all
rm -rf "$GNUPGHOME" sha256sums.txt.sig

for archive in makemkv-oss makemkv-bin; do
    wget -O "$archive.tgz" "https://www.makemkv.com/download/${archive}-${makemkv_version}.tar.gz"
    checksum="$(grep "  ${archive}-${makemkv_version}[.]tar[.]gz$" sha256sums.txt | awk '{print $1}')"
    if [[ -z "$checksum" ]]; then
        echo "Checksum not found for $archive $makemkv_version" >&2
        exit 1
    fi

    echo "$checksum *$archive.tgz" | sha256sum -c -
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