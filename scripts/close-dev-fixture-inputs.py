"""Close stdin only for this repository's own running HTTP fixtures."""
import json
from pathlib import Path
import urllib.request

root = Path(__file__).resolve().parents[1]
key = (root / ".artifacts/dev.key").read_text().strip()
base = "http://127.0.0.1:18080/v1"


def request(path, post=False):
    req = urllib.request.Request(base + path, data=b"" if post else None,
                                 headers={"Authorization": "Bearer " + key})
    with urllib.request.urlopen(req, timeout=10) as response:
        return json.load(response)


for record in request("/executions"):
    args = record.get("args", [])
    if record["state"] == "running" and args and "exec-server-fixture" in Path(args[0]).name:
        request("/executions/" + record["id"] + "/close-stdin", True)
        print(json.dumps({"closed_fixture": record["id"], "pid": record["pid"]}))
