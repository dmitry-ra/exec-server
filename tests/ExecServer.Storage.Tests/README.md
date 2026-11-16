# Output storage checks

Run from the repository root:

```sh
.sdk/dotnet run --project tests/ExecServer.Storage.Tests/ExecServer.Storage.Tests.csproj
```

This independent executable harness has no third-party packages. It compiles the
actual `OutputBuffer.cs` directly, so storage can be tested independently of the
HTTP host. On Windows, use the installed `dotnet` without the `.sdk/` prefix.

Checks cover complete byte preservation across memory-to-file spill, offsets,
waking on append/EOF/error, wait cancellation, concurrent reads, spill-file
creation failure without losing the captured prefix, and waiting for an active
read before deleting its file. Each run uses and removes only its own temporary
directory.

Storage does not interpret UTF-8 or decide execution retention; those belong to
the HTTP contract and execution manager. The operator must protect the spool
directory. Storage accepts no client-supplied path: it creates a random
`BlobId.blob` using CreateNew and private Unix permissions.
