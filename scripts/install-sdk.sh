#!/usr/bin/env bash
# Install the pinned official .NET SDK into a caller-selected private directory.
set -euo pipefail
sdk_dir="${1:?Usage: install-sdk.sh ABSOLUTE_INSTALL_DIRECTORY}"
archive_dir="${2:-${XDG_CACHE_HOME:-$HOME/.cache}/exec-server}"
mkdir -p "$sdk_dir" "$archive_dir"
archive="$archive_dir/dotnet-sdk-11.0.100-linux-x64.tar.gz"
url=https://builds.dotnet.microsoft.com/dotnet/Sdk/11.0.100/dotnet-sdk-11.0.100-linux-x64.tar.gz
expected=1bec9cca2ec689b1e80245b84f2b53c9441d1aa6daccf2ceb27490768bffa95954fa585bd275def31069d874d543d9ba9e6591fe9ade2dbd9f34ec5c0fd0050c
if [[ ! -f "$archive" ]]; then
  curl --fail --location --retry 2 "$url" --output "$archive"
fi
printf '%s  %s\n' "$expected" "$archive" | sha512sum --check --status
tar -xzf "$archive" -C "$sdk_dir"
"$sdk_dir/dotnet" --version
