#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
curl --fail --silent --show-error --request POST \
  --header "Authorization: Bearer $(cat .artifacts/dev.key)" \
  http://127.0.0.1:18080/v1/shutdown
