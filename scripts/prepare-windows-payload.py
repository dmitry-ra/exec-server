"""Write an integrity manifest for public test binaries already copied into a dedicated payload directory."""
import hashlib
import json
from pathlib import Path

directory = Path(__file__).resolve().parents[1] / ".artifacts/windows-payload"
names = ("exec-server.exe", "exec-server-fixture.exe", "exec-server-harness.exe", "run-windows-vm-acceptance.ps1")
manifest = {name: hashlib.file_digest((directory / name).open("rb"), "sha256").hexdigest() for name in names}
path = directory / "manifest.json"
path.write_text(json.dumps(manifest, sort_keys=True, indent=2) + "\n")
print(json.dumps({"manifest_sha256": hashlib.sha256(path.read_bytes()).hexdigest(), "files": manifest}))
