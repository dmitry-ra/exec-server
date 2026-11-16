# API contract

`openapi.json` is the source of truth for `/v1`. The abstract server controller
and DTOs in `src/ExecServer/Contracts/Api.g.cs` are generated with pinned
NSwag 14.8.0:

```sh
dotnet run --project tools/ContractGenerator -- api/openapi.json src/ExecServer/Contracts/Api.g.cs
dotnet run --project tools/ContractGenerator -- api/openapi.json src/ExecServer/Contracts/Api.g.cs --check
```

The generator is a development tool. Running the delivered server requires
neither NSwag, Python, Node.js nor a separately installed .NET runtime. Generator
dependencies have a lock file. Generated contracts use System.Text.Json;
Newtonsoft.Json is only a transitive generator dependency.

The schema defines required fields and types. Unknown structured request fields
are rejected. Request validation enforces mutually exclusive `text` and `binary`
values: C# DTOs alone cannot express `oneOf`. The generator removes that constraint
only from its temporary schema projection so NSwag retains both properties. The
original OpenAPI and mandatory request validation retain it. C# `byte[]` values
use ordinary base64 strings in JSON.

Every route requires a Bearer API key, including status, shutdown and OpenAPI.
`GET /v1/openapi.json` returns the embedded original document. This host route is
excluded only from abstract-controller generation to avoid duplicate handlers.
The key grants execution as the service account; it is authentication, not a
sandbox or command allowlist.

Offsets and limits count original bytes. `limit` applies separately to stdout
and stderr, bounded by the server's read limit. The client explicitly chooses
text or base64; UTF-8 is the default. Invalid UTF-8, including a slice inside a
character, returns an error with the captured length without consuming output.
The client may retry in binary form; the server does not guess the format.

Each `blobId` addresses one original stream. Returning a reference does not
consume data: `returnedBytes` is zero and `nextOffset` does not advance. The blob
endpoint returns raw bytes and positions in `X-Output-*` headers. With
`durable:false`, successful server-side delivery of the final tail of each
stream after capture completion permits deletion of the whole execution. Empty
completed streams need no separate read. With `durable:true`, only an explicit
DELETE removes output and metadata together. Server-side response completion is
not a reliable acknowledgement of delivery to the client.

The environment and working directory inherit from the service unless supplied;
explicit environment entries override individual values. stdin stays open until
`close` or `close-stdin`, unless `closeStdin` was set at launch. An HTTP wait
timeout does not stop the program. A service restart does not restore executions.
EOF describes completed capture, independently of the root process exit.
