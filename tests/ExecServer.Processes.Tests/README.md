# Execution ownership checks

Run `dotnet run --project tests/ExecServer.Processes.Tests --configuration Release`.
This package-free harness links the actual execution record/manager sources and
uses its own short-lived child. It holds an output read across deletion, reads
both real captured streams, checks admission closes for new readers, then
releases the read and verifies cleanup/metadata removal. Root-only draining is
checked while output deletion is waiting. Temporary data is private and removed.
