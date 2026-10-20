#!/bin/bash
# Controlled Linux tests, never run on a NAS containing a real /vol98765.
set -euo pipefail
[ "$(id -u)" = 0 ] || { echo 'Run as root on a disposable Linux/WSL test host'; exit 1; }
[ ! -e /vol98765 ] || { echo '/vol98765 already exists; refusing to touch it'; exit 1; }
repo=$(cd "$(dirname "$0")/.." && pwd)
mkdir /vol98765
root=$(mktemp -d /vol98765/mimotts-XXXXXXXX)
cleanup() {
    mountpoint -q "$root/cross" && umount "$root/cross"
    mountpoint -q "$root/full" && umount "$root/full"
    rm -rf -- "$root"
    rmdir /vol98765
}
trap cleanup EXIT
mkdir "$root/cross" "$root/full"
chmod 755 "$root"
chown -R nobody:nogroup "$root"
mount -t tmpfs -o size=8m,uid=65534,gid=65534,mode=700 tmpfs "$root/cross"
mount -t tmpfs -o size=64k,uid=65534,gid=65534,mode=700 tmpfs "$root/full"
# Build before dropping privileges; runtime and compiled files must be readable.
dotnet build "$repo/tests/Studio.Tests.csproj" -c Release --nologo >/dev/null
runuser -u nobody -- env MIMO_EXPORT_TEST_ROOT="$root" MIMO_EXPORT_CROSS_ROOT="$root/cross" MIMO_EXPORT_FULL_ROOT="$root/full" \
    dotnet "$repo/tests/bin/Release/net10.0/Studio.Tests.dll"
