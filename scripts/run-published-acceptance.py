#!/usr/bin/env python3
"""Exercise only fresh copies of the published server and fixture binaries."""
import argparse
import datetime
import hashlib
import json
import os
from pathlib import Path
import platform
import secrets
import shutil
import socket
import subprocess
import tempfile
import time
import urllib.error
import urllib.request
import uuid


START_TIMEOUT_SECONDS = 30
HARNESS_TIMEOUT_SECONDS = 180
EXIT_TIMEOUT_SECONDS = 20
HTTP_TIMEOUT_SECONDS = 3


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def free_port():
    with socket.socket() as probe:
        probe.bind(("127.0.0.1", 0))
        return probe.getsockname()[1]


def reject_broken_links(server, environment, private, public_log, output):
    checks = {}
    for kind, filename, setting in (
            ("spool", ".lock", "ExecServer__SpoolDirectory"),
            ("log", "server.jsonl", "ExecServer__LogDirectory")):
        case = private / ("broken-link-" + kind)
        directory = case / "configured"
        directory.mkdir(parents=True)
        target = case / "outside-configured-directory"
        link = directory / filename
        link.symlink_to(target)
        probe_environment = dict(environment)
        probe_environment.update({"ExecServer__SpoolDirectory": str(case / "spool"),
                                  "ExecServer__LogDirectory": str(case / "logs"),
                                  setting: str(directory)})
        result = subprocess.run([str(server), "--urls", "http://127.0.0.1:" + str(free_port())],
                                cwd=server.parent, env=probe_environment, stdin=subprocess.DEVNULL,
                                stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                timeout=START_TIMEOUT_SECONDS)
        public_log(result.stdout, output / ("broken-link-" + kind + ".stdout.log"))
        public_log(result.stderr, output / ("broken-link-" + kind + ".stderr.log"))
        if result.returncode == 0 or b"symbolic link" not in result.stdout + result.stderr:
            raise RuntimeError("Published server did not reject the broken " + kind + " link")
        if target.exists() or not link.is_symlink():
            raise RuntimeError("Broken-link rejection modified the outside target or link")
        checks[kind] = {"rejected": True, "outside_target_created": False,
                        "server_exit_code": result.returncode}
    return checks


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--server-binary", type=Path, required=True)
    parser.add_argument("--fixture", type=Path, required=True)
    parser.add_argument("--harness", type=Path, required=True)
    parser.add_argument("--dotnet", type=Path, required=True)
    parser.add_argument("--output-dir", type=Path)
    args = parser.parse_args()
    inputs = {name: getattr(args, name).resolve(strict=True)
              for name in ("server_binary", "fixture", "harness", "dotnet")}
    stamp = datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%dT%H%M%SZ")
    output = (args.output_dir or Path(".artifacts/published-acceptance") / (stamp + "-" + uuid.uuid4().hex[:8])).resolve()
    output.mkdir(parents=True, exist_ok=False)
    receipt = {
        "schema": 1, "started_at": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        "platform": platform.platform(), "architecture": platform.machine(),
        "inputs": {name: {"sha256": digest(path), "bytes": path.stat().st_size}
                   for name, path in inputs.items() if name != "dotnet"},
        "status": "failed", "errors": []
    }
    key = secrets.token_urlsafe(32)
    key_bytes = key.encode()
    process = None

    def public_log(content, path):
        if key_bytes in content:
            receipt["errors"].append("credential_found_in_process_output")
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(content.replace(key_bytes, b"[REDACTED-TEST-CREDENTIAL]"))

    with tempfile.TemporaryDirectory(prefix="exec-server-published-") as temporary:
        private = Path(temporary)
        server_dir = private / "server"
        fixture_dir = private / "fixture"
        server_dir.mkdir()
        fixture_dir.mkdir()
        server = server_dir / inputs["server_binary"].name
        fixture = fixture_dir / inputs["fixture"].name
        shutil.copy2(inputs["server_binary"], server)
        shutil.copy2(inputs["fixture"], fixture)
        if os.name != "nt":
            server.chmod(0o700)
            fixture.chmod(0o700)
        receipt["isolated_server_files"] = [path.name for path in server_dir.iterdir()]
        receipt["isolated_fixture_files"] = [path.name for path in fixture_dir.iterdir()]
        key_file = private / "test.key"
        key_file.write_text(key)
        if os.name != "nt": key_file.chmod(0o600)
        port = free_port()
        base = "http://127.0.0.1:" + str(port)

        def api(method, path):
            request = urllib.request.Request(base + path, method=method,
                                             data=b"" if method == "POST" else None,
                                             headers={"Authorization": "Bearer " + key})
            with urllib.request.urlopen(request, timeout=HTTP_TIMEOUT_SECONDS) as response:
                body = response.read()
                return json.loads(body) if body else None

        def close_owned_inputs():
            try:
                records = api("GET", "/v1/executions")
            except (OSError, ValueError):
                return
            for record in records:
                if record["state"] != "running": continue
                try: api("POST", "/v1/executions/" + record["id"] + "/close-stdin")
                except (OSError, ValueError): pass

        environment = {name: value for name, value in os.environ.items()
                       if not name.upper().startswith(("EXECSERVER__", "DOTNET_ROOT"))
                       and name.upper() != "EXECSERVER_API_KEY"}
        environment.update({
            "EXECSERVER_API_KEY": key,
            "ExecServer__SpoolDirectory": str(private / "spool"),
            "ExecServer__LogDirectory": str(private / "diagnostics"),
            "ExecServer__MemoryThresholdBytes": "32768",
            "ExecServer__InlineBytes": "4096", "ExecServer__MaxReadBytes": "65536",
            "ExecServer__MaxWaitMilliseconds": "5000",
            "DOTNET_BUNDLE_EXTRACT_BASE_DIR": str(private / "bundle"),
            "DOTNET_CLI_TELEMETRY_OPTOUT": "1"
        })
        server_stdout = private / "server.stdout"
        server_stderr = private / "server.stderr"
        try:
            receipt["broken_symlink_checks"] = reject_broken_links(
                server, environment, private, public_log, output)
            with server_stdout.open("wb") as stdout, server_stderr.open("wb") as stderr:
                process = subprocess.Popen([str(server), "--urls", base], cwd=server_dir,
                                           env=environment, stdin=subprocess.DEVNULL,
                                           stdout=stdout, stderr=stderr,
                                           start_new_session=os.name != "nt",
                                           creationflags=subprocess.CREATE_NEW_PROCESS_GROUP if os.name == "nt" else 0)
            receipt["server_pid"] = process.pid
            deadline = time.monotonic() + START_TIMEOUT_SECONDS
            while True:
                if process.poll() is not None:
                    raise RuntimeError("Published server exited before HTTP readiness")
                try:
                    status = api("GET", "/v1/status")
                    if status.get("mode") != "running": raise RuntimeError("Unexpected service mode at readiness")
                    receipt["initial_status"] = status
                    break
                except (OSError, ValueError):
                    if time.monotonic() >= deadline: raise TimeoutError("Published server did not become ready")
                    time.sleep(.05)
            command = [str(inputs["dotnet"]), str(inputs["harness"]), "--url", base,
                       "--token-file", str(key_file), "--fixture", str(fixture), "--include-shutdown"]
            result = subprocess.run(command, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                    timeout=HARNESS_TIMEOUT_SECONDS, cwd=private)
            public_log(result.stdout, output / "harness.stdout.jsonl")
            public_log(result.stderr, output / "harness.stderr.log")
            receipt["harness_exit_code"] = result.returncode
            report = json.loads(result.stdout.replace(key_bytes, b"[REDACTED-TEST-CREDENTIAL]").decode())
            receipt["harness"] = report
            if result.returncode != 0 or report.get("failed") != 0:
                raise RuntimeError("Published HTTP acceptance failed; inspect the individual checks")
            receipt["server_exit_code"] = process.wait(timeout=EXIT_TIMEOUT_SECONDS)
            if process.returncode != 0: raise RuntimeError("Server did not exit cleanly after drain")
            if receipt["errors"]: raise RuntimeError("Credential exclusion check failed")
            receipt["status"] = "passed"
        except (Exception, KeyboardInterrupt) as error:
            receipt["errors"].append(type(error).__name__ + ": " + str(error).replace(key, "[REDACTED]"))
        finally:
            if process is not None and process.poll() is None:
                close_owned_inputs()
                try: api("POST", "/v1/shutdown")
                except (OSError, ValueError): pass
                try: process.wait(timeout=EXIT_TIMEOUT_SECONDS)
                except subprocess.TimeoutExpired:
                    # Popen owns the Windows process handle; an unreaped Unix child
                    # cannot have its PID reused. No process group is signalled.
                    process.kill()
                    process.wait(timeout=EXIT_TIMEOUT_SECONDS)
                    receipt["errors"].append("owned_server_required_forced_cleanup")
                    receipt["status"] = "failed"
            if process is not None: receipt["final_server_exit_code"] = process.returncode
            for source, name in ((server_stdout, "server.stdout.log"), (server_stderr, "server.stderr.log")):
                if source.exists(): public_log(source.read_bytes(), output / name)
            for source in (private / "diagnostics").glob("server.jsonl*"):
                public_log(source.read_bytes(), output / "diagnostics" / source.name)
            if receipt["errors"]: receipt["status"] = "failed"
    receipt["finished_at"] = datetime.datetime.now(datetime.timezone.utc).isoformat()
    receipt_text = json.dumps(receipt, indent=2)
    if key in receipt_text:
        receipt["status"] = "failed"
        receipt["errors"].append("credential_found_in_receipt")
        receipt_text = json.dumps(receipt, indent=2).replace(key, "[REDACTED-TEST-CREDENTIAL]")
    (output / "receipt.json").write_text(receipt_text + "\n")
    print(json.dumps({"status": receipt["status"], "receipt": str(output / "receipt.json"),
                      "checks": receipt.get("harness", {}).get("passed", 0)}))
    return 0 if receipt["status"] == "passed" else 1


if __name__ == "__main__":
    raise SystemExit(main())
