# Linux lifecycle evidence

An actual run on 15 November 2026 at 23:31 UTC used:

```sh
python3 scripts/verify-lifecycle.py
```

The script uses only Python's standard library. It creates independent service
instances on unused loopback ports, private keys/directories and owned child
processes. It does not include keys or stream bodies in its receipt.

That historical run tested the .NET 11 Debug server, SHA-256
`fd58aac9c492c88b47ce5d9a5907165bed595255338bcb1d0bd96330b83e0078`.
The executable module hash was unchanged afterwards. Fixture hash:
`7e1c56785a5c3bd2a9a3986b757f0bfc9b605650dace43864d5b26ad768bb265`.
Private receipt: `.artifacts/lifecycle/20261115T233116Z-43b3bc29/receipt.json`.

| Check | Observed result |
|---|---|
| Missing API key | Service exited before accepting connections; no listener remained. |
| Genuine terminal Ctrl-C | A byte written to an owned foreground PTY became a kernel signal to the foreground process group. The service entered draining; its child in a separate session stayed alive. Later stdin/EOF succeeded and the service exited zero. |
| Exclusive spool | The first service removed a stale UUID output file but preserved keep.txt. A second instance could not acquire the same directory; the first stayed responsive. |
| Rotating structured logs | Three files contained 40 valid JSON records with command parameters and byte counters. Neither the key nor test stdin/stdout bodies appeared. |

Result: 4/4, with no test-owned server or child left running. Raw receipts and
console evidence are in ignored .artifacts, not committed. These checks establish
Linux lifecycle behavior only, not complete Windows/macOS or clean-host acceptance.

A specific single-file artifact can be checked separately:

```sh
python3 scripts/verify-lifecycle.py --server-binary .artifacts/publish/linux-x64/exec-server
```

That command is not part of the historical Debug receipt above. Every invocation
creates a new case and records the hash of the file it actually tested.

Initially, missing-key and spool-conflict startup failures caused an unhandled
exception with exit code -6. After correction, a 16 November single-file run
passed 4/4 and configuration failures produced a structured message with exit
code 2. Tested SHA-256:
`b969301a2cbdbe90bcc2e83d52c8c671d6d0aaa4b0b7972357f0a02da2fc43d1`.
Private receipt: `.artifacts/lifecycle/20261116T001616Z-164a147e/receipt.json`.

This run did not test an exited root leaving a descendant with an inherited
open pipe. A separate session does not guarantee whole-tree termination after
an abrupt service kill. The final CI artifact has its own four-check lifecycle
receipt; see [CI](ci.md), without relabeling these earlier artifacts.
