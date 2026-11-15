#!/usr/bin/env bash
# Transport/bootstrap only. Subsequent dependency installation/builds use HTTP.
set -euo pipefail
cd "$(dirname "$0")/.."
umask 077
work="$PWD/.artifacts/linux-vm"
address="$(cat "$work/address")"
ssh_options=(-o BatchMode=yes -o ConnectTimeout=10 -o StrictHostKeyChecking=accept-new -o "UserKnownHostsFile=$work/known_hosts")
if [[ ! -f "$work/api.key" ]]; then head -c 32 /dev/urandom | base64 > "$work/api.key"; fi
{
  printf 'EXECSERVER_API_KEY=%s\n' "$(cat "$work/api.key")"
  printf 'ExecServer__SpoolDirectory=/home/runner/exec-server-test/spool\n'
  printf 'ExecServer__LogDirectory=/home/runner/exec-server-test/logs\n'
  printf 'ExecServer__MemoryThresholdBytes=1024\nExecServer__InlineBytes=4096\n'
} > "$work/server.env"
ssh "${ssh_options[@]}" "runner@$address" 'mkdir -p -m 700 /home/runner/exec-server-test; if command -v dotnet; then exit 1; fi; cat /etc/os-release'
scp "${ssh_options[@]}" .artifacts/publish/linux-x64/exec-server "$work/server.env" "runner@$address:/home/runner/exec-server-test/"
ssh "${ssh_options[@]}" "runner@$address" 'chmod 600 /home/runner/exec-server-test/server.env; chmod 700 /home/runner/exec-server-test/exec-server; sudo systemd-run --unit=exec-server-test --uid=runner --property=EnvironmentFile=/home/runner/exec-server-test/server.env --property=UMask=0077 /home/runner/exec-server-test/exec-server --urls http://127.0.0.1:5080; sha256sum /home/runner/exec-server-test/exec-server'
