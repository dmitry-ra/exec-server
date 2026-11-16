# Running and using ExecServer

Use the self-contained binary for the target OS and architecture: `exec-server`
on Linux/macOS or `exec-server.exe` on Windows. A separate .NET installation is
not required on the target. Each platform has its own binary. Normal OS system
libraries and the programs you launch remain external requirements.

On Unix:

```sh
chmod +x exec-server
EXECSERVER_API_KEY='replace-with-a-random-secret' ./exec-server
```

The default listener is `http://127.0.0.1:5080`; change it with `--urls`. Every
route requires `Authorization: Bearer <key>`, including status and OpenAPI. The
server refuses to start without a configured key. Use an SSH tunnel or your own
TLS reverse proxy for remote access. The server does not provision that external
infrastructure. The key permits arbitrary execution as the service account;
ExecServer is not a sandbox.

OpenAPI: `GET /v1/openapi.json`. Service status: `GET /v1/status`.

Example Linux launch request for `POST /v1/executions`:

```json
{
  "executable": "/bin/ls",
  "args": ["-la", "/tmp"],
  "durable": true,
  "closeStdin": true,
  "waitMilliseconds": 1000
}
```

The response contains a service execution `id`, native `pid`, state, duration,
byte counts and any known exit code. `waitMilliseconds:0` returns immediately.
An HTTP disconnect does not cancel the process. List executions with
`GET /v1/executions`; read an individual state with `GET /v1/executions/{id}`.

`GET /v1/executions/{id}/output` reads stdout and stderr separately. Offsets
`stdoutOffset` and `stderrOffset` count raw bytes, not characters. Omitting an
offset reads from zero. `limit` bounds a chunk; `remainingBytes` and `nextOffset`
describe continuation. A large chunk returns a `blobId` instead of inline data;
`GET /v1/blobs/{blobId}` reads those raw bytes.

UTF-8 text is the default. `format=base64` returns binary data in `binary`.
Invalid or incomplete UTF-8 returns an explicit error rather than replacement
characters and does not consume output; retry as base64. Set `waitMilliseconds`
for long polling: new output wakes a pending read.

`POST /v1/executions/{id}/stdin` accepts either `{ "text":"..." }` or
`{ "binary":"BASE64" }`, never both. `{ "close":true }` or
`POST /v1/executions/{id}/close-stdin` closes the pipe; it does not send Ctrl-D.
A shell is an ordinary executable with explicit arguments and is not inserted
automatically. V1 uses pipes: it does not provide a terminal, TUI or TTY-based
interactive sudo. Explicit `sudo -S` is possible if sudo policy allows stdin.
Pending initial stdin is cancelled when its managed root exits; a descendant
holding that pipe does not keep record deletion waiting for initial delivery.

Capture/storage failures are recorded in the execution's `errors` list. V1 does
not kill the process, discard subsequent output or attempt storage recovery
automatically. An affected pipe may stop being drained and block a producer.
The client must inspect errors and choose the appropriate process control.

Without durable retention, process exit and reading both final stream tails
remove metadata and all output. Reading a tail abandons earlier bytes; byte-range
coverage is not tracked. This mode does not provide reliable client delivery
acknowledgements. With `durable:true`, both output and metadata remain until
`DELETE /v1/executions/{id}`. A running execution cannot be deleted. Durable
retention does not survive a restart: the next service startup clears its
owned temporary output files.

`POST /v1/signals` with `{ "pid":1234,"signal":15 }` sends a Unix signal.
Windows explicitly rejects this operation. PIDs use the target OS namespace.
`GET /v1/executions/{id}/processes` is a diagnostic PID/PPID/name snapshot,
without atomicity or historical guarantees.

`POST /v1/shutdown`, Ctrl-C and ordinary Unix SIGTERM enter draining: new starts
are rejected, existing I/O remains available and the service waits for its
directly launched processes. It does not automatically kill whole process trees.
SIGKILL or a crash does not guarantee descendant termination. Each child controls
its own descendants and inherited pipes.
Output capture does not extend service lifetime after the last managed root
exits. Read required output before requesting shutdown; a descendant holding an
inherited output pipe does not make draining wait for that descendant.

Configuration environment variables all use the `ExecServer__` prefix:
`MemoryThresholdBytes`, `MaxReadBytes`, `InlineBytes`, `DefaultWaitMilliseconds`,
`MaxWaitMilliseconds`, `SpoolDirectory`, `LogDirectory`, `LogFileBytes` and
`LogFileCount`. JSON logs rotate. They include command arguments, explicitly
supplied environment values and operation metadata, but not stdin/stdout/stderr
bodies or the Authorization header. Treat credentials in arguments and supplied
environment values as visible in diagnostics under the V1 contract.
