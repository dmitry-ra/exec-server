#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
export DOTNET_ROOT="$PWD/.sdk"
export PATH="$DOTNET_ROOT:$PATH"
tool=.artifacts/jetbrains/.store/jetbrains.resharper.globaltools/2026.3.0/jetbrains.resharper.globaltools/2026.3.0/tools/net10.0/any
exec .sdk/dotnet exec --runtimeconfig "$tool/inspectcode.unix.runtimeconfig.json" "$tool/inspectcode.exe" \
  ExecServer.slnx --output=.artifacts/inspectcode.sarif --format=Sarif --no-build --verbosity=WARN
