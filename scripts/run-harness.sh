#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
.sdk/dotnet tools/ExecServer.Harness/bin/Debug/net11.0/exec-server-harness.dll \
  --url http://127.0.0.1:18080 --token-file .artifacts/dev.key \
  --fixture "$PWD/tests/ExecServer.Fixture/bin/Debug/net11.0/exec-server-fixture.dll" \
  --dotnet "$PWD/.sdk/dotnet" "$@" | tee .artifacts/host-suite.latest.json
