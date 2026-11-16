# Clean Linux lab

The lab is separate from existing machines. Host requirements are libvirt/QEMU,
virt-install, cloud-localds, curl, sha256sum and a usable local SSH public key at
`~/.ssh/id_ed25519.pub`. The script uses sudo only for its own new VM, disk and
seed; it does not change existing guests or network settings.

Create it with `bash scripts/create-linux-vm.sh`: VM `exec-server-test-20261116`,
Ubuntu 26.04, two vCPUs, 4 GiB RAM and a 35 GiB disk with a backing image. The
script pins the base image SHA-256. During preparation, the SHA256SUMS signature
was checked against Ubuntu Cloud Images key
`D2EB44626FDDC30B513D5BB71A5D6C4C7DB87C81`.
Keys are not committed. Working files use `.artifacts/linux-vm`; the script's
local image cache is `/mnt/data/exec-server-lab-inputs`. This is a lab path,
not a required service installation path.

Initially copy only the self-contained binary. Check that dotnet is absent and
the API starts. Subsequent package/SDK installation and source builds run through
ExecServer, one command per request with recorded ID and exit status. SSH remains
the file transport, tunnel and initial server launcher.

Stop the owned VM with `sudo virsh shutdown exec-server-test-20261116`, then
check `sudo virsh domstate exec-server-test-20261116`. Full removal is a separate
explicit operation after shutdown: remove only that VM definition, its qcow2 and
seed. Retain the shared base image. This procedure does not authorize deletion
of other machines or shared caches.

Verified on 16 November 2026: this lab was created from scratch. Before SDK
installation, a single 53,269,130-byte server file started successfully, SHA-256
`4b4c2371ec26df2504d28e505171e73af91638cc86bf38715413a99b259035b0`.
Dependencies, SDK 11.0.100, source revision a7389af and storage checks were then
installed/built/run through REST. Every command returned exitCode zero. Separate
streams, PIDs, IDs and durations remain in private local
`.artifacts/linux-vm/build-receipts`. This initial installation evidence does
not automatically apply to later binaries.

The final Linux binary from CI run 43780846370, source 4ae844a, was additionally
copied into the same VM. Its SHA-256 matched before and after transfer:
`1cae87f43e64bd3b7442e00b6cd8692f1f3e34c2b83e94b35b90bb560cbb2b68`.
The self-contained fixture was copied separately; the harness used an SSH tunnel
and passed 17 HTTP checks, including graceful drain. Private receipt:
`.artifacts/linux-vm/final-ci-harness.json`. Dependencies were not reinstalled:
this final-binary run used the already created lab.
