# Development and acceptance tools

These tools build and test the project. A service user needs only the binary
for their OS/architecture and an API key.

- `install-sdk.sh DIRECTORY [CACHE]`: install pinned official SDK 11.0.100
  Linux-x64 with SHA-512 verification, without a global installation change.
- `run-dev.sh`: loopback development service on port 18080 with a random private
  key and separate spool/log directories. The key is not printed or committed.
- `run-harness.sh [--include-shutdown]`: C# HTTP checks of the development instance.
- `stop-dev.sh`: REST drain of that development instance.
- `close-dev-fixture-inputs.py`: close stdin only for this repository's own
  fixtures retained after a failed test.
- `inspect.sh`: install pinned JetBrains InspectCode 2026.3.0 locally if absent,
  then analyze the solution using the private SDK 11 install. Run SDK install,
  restore and build first; produces `.artifacts/inspectcode.sarif`.
- `create-linux-vm.sh`: create a new Ubuntu 26.04 VM from a pinned image.
  Existing names/files are refused; existing machines are not modified.
- `bootstrap-linux-vm.sh`: copy one binary and private environment into the lab,
  confirm dotnet is absent and launch the service with systemd.
- `tunnel-linux-vm.sh`: SSH tunnel to the lab's loopback API.
- `vm-build-through-api.py`: install dependencies/SDK and build source through
  individual REST commands, retaining IDs, exit codes and both streams.
- `run-published-acceptance.py`: isolated copies of published server/fixture,
  C# HTTP harness, actual SHA-256 values and graceful-exit checks.
- `prepare-windows-payload.py`: SHA-256 manifest of public acceptance inputs.
- `run-windows-vm-acceptance.ps1`: accept three self-contained files in a new
  private Windows directory without changing owner services or desktop settings.
- `verify-lifecycle.py`: isolated real-terminal Ctrl-C, missing-key refusal,
  exclusive spool, rotating-log, blocked initial/later input with queued close,
  and repeated execution pipe-descriptor release checks.
  Supports a self-contained server;
  see `docs/lifecycle-evidence.md`.
