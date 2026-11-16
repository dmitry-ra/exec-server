# Manual published-binary acceptance matrix

`Published binary acceptance` runs only through workflow_dispatch; pushes and
PRs do not start it automatically. It uses standard public-repository
ubuntu-latest, windows-latest and macos-latest runners, without larger,
self-hosted or paid runners. GitHub documents standard public hosted runners
as free: [hosted runners](https://docs.github.com/en/actions/reference/runners/github-hosted-runners).
The latest label means GitHub's current stable image, not necessarily the
vendor's newest OS release.

SDK 11.0.100 and full official action SHAs are pinned. Dependency restoration
uses lock files. Jobs and risky steps have timeouts; concurrency cancels an
older run of the same branch. Jobs have a 15-minute deadline. Artifacts expire
after three days.

GitHub requires the workflow in the default branch before a manual invocation
is available: [manual workflows](https://docs.github.com/en/actions/how-tos/manage-workflow-runs/manually-run-a-workflow).
The same workflow was therefore registered in main as a technical bootstrap,
while implementation remained in develop. Use `--ref` to select the branch to
test. The workflow does not change the default branch or merge implementation.

Each OS determines its actual RID through dotnet --info; macOS architecture is
not assumed. Jobs build the solution, check contract generation and storage,
then publish independent self-contained single-file server and fixture binaries.

`scripts/run-published-acceptance.py` copies only the server file into a new empty
directory and the fixture file into another. Its own service uses a random
loopback port and random key in the environment, not argv. The HTTP harness
receives its key through a private file. The SDK runs only the test harness;
server and fixture launch directly. This establishes independence of the copied
files. An SDK-equipped hosted runner does not establish that the entire machine
lacks a runtime; the clean VM provides that separate evidence.

HTTP checks cover missing/incorrect keys, strict requests, separate streams,
later stdin, binary output, asynchronous executions, complete spill/blob reads,
deletion and drain. Drain must end in a normal service exit. Failed runs clean
up only processes they created. Linux also checks genuine terminal Ctrl-C using
the same published server binary.

Artifacts include actual binary SHA-256 values, OS identity, individual results,
exit codes and safe logs. The random key/private work directory are excluded.
Finding the key in output fails acceptance; copied logs additionally redact it
before upload.

Local invocation for existing binaries:

```sh
python scripts/run-published-acceptance.py \
  --server-binary /path/to/exec-server \
  --fixture /path/to/exec-server-fixture \
  --harness tools/ExecServer.Harness/bin/Release/net11.0/exec-server-harness.dll \
  --dotnet /path/to/dotnet
```

Hosted Windows is Windows Server, not a substitute for separate actual Windows
10/11 VM runs. Success applies only to the specific source/artifacts/run. Merely
having this workflow is not evidence that a matrix has passed.

## Historical first run and corrected Darwin defect

[Run 43777442558](https://github.com/dmitry-ra/exec-server/actions/runs/43777442558)
tested d9832c18b727fdf9a941d0608453d24e07c46e73:

| Platform | Actual result |
|---|---|
| Hosted Ubuntu | 17/17 HTTP and 4/4 lifecycle; SUCCESS |
| Hosted Windows | 17/17 HTTP; SUCCESS |
| macOS 26.7.2 ARM64 | 16/17 HTTP; FAILURE |

The macOS closed-stdout fixture did not establish EOF before long polling.
Build, generation, storage, publication, other checks and normal shutdown passed.
That run did not accept the macOS cell or establish a long-polling defect.

The initial fixture-only hypothesis was disproved when ordinary /bin/sh
reproduced the failure in run 43778273661. The cause was an incorrect ABI
signature for variadic fcntl on Darwin ARM64. The Unix adapter now uses fixed
SystemNative_FcntlSetFD/GetFD entry points from .NET's bundled native library
and verifies close-on-exec by reading the flag back. No additional executable
is delivered.

[Run 43779859999](https://github.com/dmitry-ra/exec-server/actions/runs/43779859999)
on 7c5390976ca864d5dc55348f9e654dd892b9deed passed all three platforms: 17 HTTP
checks each, plus four Linux lifecycle checks. The failing check was not relaxed.

The final [run 43780846370](https://github.com/dmitry-ra/exec-server/actions/runs/43780846370)
accepted 4ae844aaf296bc1bba1f52f6afdd5d23c38ac20b after the captured-byte EOF
consistency fix. All three jobs passed 17/17 HTTP checks and exited normally;
Linux also passed 4/4 lifecycle checks using the same single-file binary.
Linux server: 53,269,632 bytes, SHA-256
`1cae87f43e64bd3b7442e00b6cd8692f1f3e34c2b83e94b35b90bb560cbb2b68`.
Those run artifacts have a three-day retention period. Later documentation-only
commits do not change the previously tested implementation.

The publication branch starts from main without rewriting develop. Its first
implementation commit, e1a4a28cb19996b0e77242836ac398c12f446d06, passed the
complete three-platform workflow in
[run 43929165714](https://github.com/dmitry-ra/exec-server/actions/runs/43929165714).
Later publication commits require their own runs; this earlier receipt is not
silently attributed to the final branch head.
