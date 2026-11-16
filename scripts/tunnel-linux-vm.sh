#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
work="$PWD/.artifacts/linux-vm"
exec ssh -N -o BatchMode=yes -o ExitOnForwardFailure=yes -o ServerAliveInterval=15 \
  -o ServerAliveCountMax=2 -o StrictHostKeyChecking=yes -o "UserKnownHostsFile=$work/known_hosts" \
  -L 127.0.0.1:18081:127.0.0.1:5080 "runner@$(cat "$work/address")"
