#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
export DOTNET_ROOT="$PWD/.sdk"
export PATH="$DOTNET_ROOT:$PATH"
readonly JETBRAINS_VERSION=2026.3.0
tool=".artifacts/jetbrains/.store/jetbrains.resharper.globaltools/$JETBRAINS_VERSION/jetbrains.resharper.globaltools/$JETBRAINS_VERSION/tools/net10.0/any"
if [[ ! -f "$tool/inspectcode.exe" ]]; then
  .sdk/dotnet tool install JetBrains.ReSharper.GlobalTools \
    --version "$JETBRAINS_VERSION" --tool-path .artifacts/jetbrains --allow-roll-forward
fi
exec .sdk/dotnet exec --runtimeconfig "$tool/inspectcode.unix.runtimeconfig.json" "$tool/inspectcode.exe" \
  ExecServer.slnx --output=.artifacts/inspectcode.sarif --format=Sarif --no-build --verbosity=WARN
