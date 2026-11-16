# ExecServer

A versioned REST API for starting and controlling processes on another machine.
ExecServer runs as one self-contained .NET executable for the target platform.
Clients can launch a program, poll long-running work, read stdout/stderr, write
stdin and retrieve exit status without maintaining an interactive SSH session.

V1 supports pipes on Linux, Windows and macOS. It does not provide a terminal,
PTY/ConPTY or recovery of executions across a service restart.

## Run

Copy the appropriate binary to the target. On Unix:

```sh
chmod +x exec-server
EXECSERVER_API_KEY='replace-with-a-random-secret' ./exec-server
```

The default listener is http://127.0.0.1:5080. Every route requires a Bearer API
key. Use an SSH tunnel or your own TLS reverse proxy for remote access. The key
grants execution with the service account's permissions; this is not a sandbox.
A separate .NET runtime is not required to run the delivered binary. Normal OS
libraries and the programs you execute remain external requirements.

- [Usage and configuration](docs/usage.md)
- [OpenAPI contract](api/README.md) and [specification](api/openapi.json)
- [Testing and platform evidence](docs/testing.md)
- [Published-binary CI](docs/ci.md)
- [Development tools](scripts/README.md)
- [Clean Linux lab](docs/linux-lab.md)
- [Linux lifecycle evidence](docs/lifecycle-evidence.md)

## Build

Source builds require the SDK pinned in global.json. With that SDK installed:

```sh
dotnet restore ExecServer.slnx --locked-mode
dotnet build ExecServer.slnx --configuration Release --no-restore
```

The server controller and DTOs are generated from OpenAPI using the pinned
contract generator; CI checks that generated source matches the specification.
The HTTP harness exercises actual server processes with separate deterministic
child fixtures. Acceptance applies to the exact artifacts identified by receipts.

Licensed under the [MIT license](LICENSE).
