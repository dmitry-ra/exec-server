"""Run dependency installation and source builds through ExecServer, not SSH."""
import argparse
import base64
import json
from pathlib import Path
import time
import urllib.error
import urllib.parse
import urllib.request

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--url", default="http://127.0.0.1:18081")
parser.add_argument("--key-file", type=Path, required=True)
parser.add_argument("--source", default="/home/runner/exec-server-source")
parser.add_argument("--receipts", type=Path, required=True)
options = parser.parse_args()
options.receipts.mkdir(parents=True, exist_ok=True)
key = options.key_file.read_text().strip()


def call(method, path, body=None, raw=False):
    data = json.dumps(body).encode() if body is not None else None
    request = urllib.request.Request(options.url + path, method=method, data=data,
                                    headers={"Authorization": "Bearer " + key, "Content-Type": "application/json"})
    with urllib.request.urlopen(request, timeout=40) as response:
        content = response.read()
        return content if raw else json.loads(content) if content else None


def run(name, executable, args, cwd=None, environment=None, deadline=600):
    body = {"executable": executable, "args": args, "durable": True,
            "closeStdin": True, "waitMilliseconds": 0}
    if cwd: body["cwd"] = cwd
    if environment: body["environment"] = environment
    record = call("POST", "/v1/executions", body)
    ident = record["id"]
    started = time.monotonic()
    print(json.dumps({"stage": name, "id": ident, "pid": record["pid"]}), flush=True)
    while record["state"] != "exited" or not record["captureComplete"]:
        if time.monotonic() - started > deadline:
            raise TimeoutError(f"{name}: deadline exceeded, execution retained as {ident}")
        time.sleep(.25)
        record = call("GET", "/v1/executions/" + ident)
    output = {"request": body, "execution": record, "streams": {}}
    for stream in ("stdout", "stderr"):
        offset = 0
        content = bytearray()
        while True:
            query = urllib.parse.urlencode({stream + "Offset": offset, "format": "base64", "limit": 65536})
            chunk = call("GET", "/v1/executions/" + ident + "/output?" + query)[stream]
            if "blobId" in chunk:
                data = call("GET", "/v1/blobs/" + chunk["blobId"] + "?offset=" + str(offset), raw=True)
            else:
                data = base64.b64decode(chunk.get("binary", ""))
            content.extend(data)
            offset += len(data)
            if offset >= record[stream + "Bytes"]: break
            if not data: raise RuntimeError("No forward progress reading output")
        output["streams"][stream] = {"bytes": len(content), "text": content.decode("utf-8", errors="replace")}
    (options.receipts / (name + ".json")).write_text(json.dumps(output, indent=2) + "\n")
    call("DELETE", "/v1/executions/" + ident)
    if record.get("exitCode") != 0 or record.get("errors"):
        raise RuntimeError(f"{name} failed: exit={record.get('exitCode')}; see retained receipt")
    print(json.dumps({"stage": name, "exitCode": record["exitCode"], "durationMilliseconds": record["durationMilliseconds"]}), flush=True)


status = call("GET", "/v1/status")
(options.receipts / "status.json").write_text(json.dumps(status, indent=2) + "\n")
run("make-source-directory", "/usr/bin/mkdir", ["-p", options.source])
run("extract-source", "/usr/bin/tar", ["-xzf", "/home/runner/exec-server-test/source.tar.gz", "-C", options.source])
run("apt-update", "/usr/bin/sudo", ["-n", "apt-get", "update"])
run("install-dependencies", "/usr/bin/sudo", ["-n", "apt-get", "install", "-y", "curl", "ca-certificates", "git", "libicu78", "libssl3t64"])
run("install-sdk", "/bin/bash", [options.source + "/scripts/install-sdk.sh", "/home/runner/exec-server-sdk"], deadline=900)
sdk = "/home/runner/exec-server-sdk/dotnet"
run("build-source", sdk, ["build", "ExecServer.slnx", "-c", "Release", "--nologo"], cwd=options.source,
    environment={"DOTNET_ROOT": "/home/runner/exec-server-sdk", "DOTNET_CLI_TELEMETRY_OPTOUT": "1"}, deadline=900)
run("storage-tests", sdk, ["run", "--project", "tests/ExecServer.Storage.Tests", "-c", "Release", "--no-build"], cwd=options.source,
    environment={"DOTNET_ROOT": "/home/runner/exec-server-sdk"})
