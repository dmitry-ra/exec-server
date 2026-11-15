#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
umask 077
mkdir -p .artifacts
if [[ ! -f .artifacts/dev.key ]]; then
  head -c 32 /dev/urandom | base64 > .artifacts/dev.key
fi
export EXECSERVER_API_KEY
EXECSERVER_API_KEY="$(cat .artifacts/dev.key)"
export ExecServer__SpoolDirectory="$PWD/.artifacts/dev-spool"
export ExecServer__MemoryThresholdBytes=1024
export ExecServer__MaxReadBytes=65536
export ExecServer__InlineBytes=4096
exec .sdk/dotnet src/ExecServer/bin/Debug/net11.0/linux-x64/exec-server.dll --urls http://127.0.0.1:18080 >> .artifacts/dev-console.jsonl 2>&1
