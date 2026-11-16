# Testing ExecServer

Acceptance covers V1 with pipes. PTY/ConPTY, terminal menus and restart recovery
are outside this scope. The primary deployment requirement is that a delivered
executable runs on the target without an SDK or separately installed .NET
runtime. Building source requires the SDK; running the self-contained file does
not. Normal .NET platform requirements for OS system libraries still apply.

## Test components

- `tests/ExecServer.Fixture` is a deterministic child program without a shell or
  external utilities. It provides separate streams, binary output, waiting,
  echo until EOF, large output, delayed output and selected exit codes.
- `tools/ExecServer.Harness` is a separate C# HTTP client using raw JSON rather
  than server DTOs. It emits JSON results with individual checks, durations and
  failures. A nonzero exit code indicates failure.
- Storage checks separately cover spill, concurrency, notification and cleanup.
- `tests/ExecServer.Processes.Tests` compiles the actual execution manager and
  record sources. Its deterministic output-read/deletion interleaving verifies
  an admitted reader can read both streams, later readers are refused, metadata
  remains until cleanup finishes and output readers do not extend root draining.

The harness does not print the API key, HTTP headers or launch environment. It
starts its own children and removes only its own execution records. Publishing
a complete operator session log is not required to report acceptance.

## Local end-to-end run

From the repository root:

```bash
bash scripts/install-sdk.sh "$PWD/.sdk"
.sdk/dotnet build tests/ExecServer.Fixture/ExecServer.Fixture.csproj
.sdk/dotnet build tools/ExecServer.Harness/ExecServer.Harness.csproj
```

In one terminal, start the service with a new test key. Supply the key through
the environment; the repository contains no keys:

```bash
export EXECSERVER_API_KEY="$(openssl rand -hex 32)"
export ExecServer__MemoryThresholdBytes=32768
export ExecServer__InlineBytes=4096
export ExecServer__MaxReadBytes=65536
export ExecServer__MaxWaitMilliseconds=5000
.sdk/dotnet run --project src/ExecServer -- --urls http://127.0.0.1:5080
```

From another terminal with that same test key:

```bash
.sdk/dotnet tools/ExecServer.Harness/bin/Debug/net11.0/exec-server-harness.dll \
  --url http://127.0.0.1:5080 --token "$EXECSERVER_API_KEY" \
  --fixture "$PWD/tests/ExecServer.Fixture/bin/Debug/net11.0/exec-server-fixture.dll" \
  --dotnet "$PWD/.sdk/dotnet"
```

`--dotnet` is the fixture launcher path on the service machine. For remote
checks, both `--fixture` and `--dotnet` must exist there. Alternatively, copy a
self-contained fixture and omit `--dotnet`. The server itself needs no SDK.

The ordinary harness does not stop the server. Add `--include-shutdown` only for
an explicit final run: it checks draining, rejection of new starts and stdin
writes to an existing child. The service must exit after its last managed
process finishes; no subsequent HTTP response is expected from an exited host.

## Behavior checked by the HTTP harness

1. Missing and incorrect keys receive 401 on status, listing, launch, output,
   blob, stdin/EOF, signal, diagnostics, deletion and shutdown routes. An
   authenticated probe confirms that a denied launch created no marker.
2. Conflicting text/base64, malformed base64 and unknown fields are rejected
   before starting a child.
3. stdout and stderr remain separate with correct counts and exit status.
4. Initial and later stdin preserve order. Closing stdin produces EOF. Binary
   input retains its exact bytes.
5. An asynchronous execution appears in listing; diagnostics contain its root PID.
6. Omitted offsets repeat from zero. Explicit offset and limit produce correct
   bytes, nextOffset and remainingBytes.
7. Invalid UTF-8 reports the accumulated length. A later base64 read proves the
   failed text read did not consume output.
8. Two MiB of output are retained and read as blob chunks of 4096 bytes. The
   documented thresholds force spill; different settings must actually cross
   the threshold before a run can claim disk-spill coverage.
9. Durable output survives reading until DELETE. Ordinary final-tail consumption
   removes the execution. Byte-range coverage is deliberately not tracked.
10. Long polling wakes when data arrives rather than only at timeout.
11. Unix accepts SIGTERM; Windows explicitly rejects Unix signals.
12. A separate final run checks draining.

A passing count alone does not establish correctness. A corresponding defect
must make its check fail: disabled authentication, merged streams, truncated
output, premature deletion or missing notification must not pass silently.

## Clean Linux VM acceptance

Create a new owned VM rather than reusing an owner's server or an established
lab. Hostnames, addresses, image paths and keys are local inputs, not published
owner infrastructure.

1. Pin the Ubuntu image URL and hash; record VM resources and host changes.
   Reuse an existing suitable lab provisioner where available.
2. Copy the self-contained server and fixture, supply a new test key and bind
   REST to loopback. Use SSH for initial delivery/start, the tunnel and an
   independent result check.
3. Verify that dotnet is absent and the server still starts. Run the HTTP
   harness through the tunnel without a direct public listener.
4. Transfer project source. Use separate POST /v1/executions requests to install
   system dependencies and the SDK, restore, build and test. Poll a long-running
   command by ID and read both streams. Start an explicit /bin/bash executable
   when shell operators are needed.
5. Run sudo commands only on the owned lab VM. Where policy permits stdin,
   use sudo -S; pipes do not provide an ordinary terminal prompt. Never include
   a password in arguments or public logs.
6. Test the resulting binary with the same harness. Retain versions, hashes,
   exit codes and individual results, independently checked through SSH.
7. Stop only lab-owned processes, remove the tunnel and follow the lab's
   explicit removal procedure. Do not delete unrelated VMs or disks.

## Platform matrix and evidence boundaries

| Platform | Minimum acceptance |
|---|---|
| Current Ubuntu Linux | Full harness, single-file launch without runtime, clean VM, dependencies and source build through REST |
| Windows 10 | Self-contained win-x64, stdin/EOF, independent binary streams, storage, Unix-signal refusal |
| Windows 11 | The same harness on a separate actual installation |
| Current macOS | Explicit GitHub Actions invocation, build and harness on a standard public runner |

Workflows have timeouts and concurrency limits. These harnesses do not create
releases, tags or PRs. A local build does not establish the whole matrix or the
single-file deployment requirement.

## Recorded acceptance

Verified on 16 November 2026: revision 7c53909 passed 17 HTTP checks on each
hosted Ubuntu/Windows/macOS runner and four Linux lifecycle checks. See
[CI](ci.md) for run links and the corrected Darwin defect. Separate actual
Windows 10 (19045.2006) and Windows 11 (26300.9974) runs passed 17 checks with
server and harness exit codes zero. Hosted Windows does not replace those runs.

A new Ubuntu 26.04.1 VM ran the binary before SDK installation. Dependencies and
SDK were then installed through REST commands; source and storage checks were
built and run through the same interface, with every command exiting zero.
See [Linux lab](linux-lab.md). Results apply only to their identified artifacts;
new versions need new receipts. The final tested implementation revision is
4ae844a, with the exact artifact and complete matrix recorded in CI.
