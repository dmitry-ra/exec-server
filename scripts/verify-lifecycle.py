#!/usr/bin/env python3
"""Bounded Linux lifecycle acceptance against only subprocesses started here."""
import argparse
import concurrent.futures
import contextlib
import datetime
import fcntl
import hashlib
import json
import os
from pathlib import Path
import pty
import secrets
import select
import shutil
import signal
import socket
import subprocess
import sys
import termios
import threading
import time
import urllib.error
import urllib.request
import uuid


ROOT = Path(__file__).resolve().parents[1]
DEADLINE_SECONDS = 12
HTTP_TIMEOUT_SECONDS = 3
INTERRUPT_BYTE = b"\x03"
BLOCKED_INPUT_BYTES = 1024 * 1024
OWNED_DESCENDANT_SECONDS = 8
PENDING_WRITE_PROBE_SECONDS = .1
PIPE_REUSE_EXECUTIONS = 8
FD_BACKGROUND_ALLOWANCE = 2


def dotnet_command():
    private = ROOT / ".sdk/dotnet"
    command = str(private) if private.is_file() else shutil.which("dotnet")
    require(command, "A .NET SDK is required for the development harness")
    return command


def require(condition, message):
    if not condition:
        raise AssertionError(message)


def wait_until(check, seconds=DEADLINE_SECONDS):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        value = check()
        if value:
            return value
        time.sleep(.03)
    raise TimeoutError("Bounded lifecycle condition did not converge")


def free_port():
    with socket.socket() as probe:
        probe.bind(("127.0.0.1", 0))
        return probe.getsockname()[1]


def sha256(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


class Service:
    def __init__(self, work, command, key=True, spool=None, terminal=False,
                 log_file_bytes=10485760):
        self.work = work
        self.work.mkdir(mode=0o700)
        self.port = free_port()
        self.token = secrets.token_hex(32)
        self.spool = spool or work / "spool"
        self.logs = work / "logs"
        self.children = []
        self.master = None
        self.collector = None
        self.collector_stop = threading.Event()
        environment = os.environ.copy()
        environment.pop("EXECSERVER_API_KEY", None)
        if key:
            environment["EXECSERVER_API_KEY"] = self.token
        environment.update({
            "ExecServer__SpoolDirectory": str(self.spool),
            "ExecServer__LogDirectory": str(self.logs),
            "ExecServer__LogFileBytes": str(log_file_bytes),
            "ExecServer__LogFileCount": "3",
        })
        arguments = command + ["--urls", f"http://127.0.0.1:{self.port}"]
        self.console = (work / "console.log").open("wb")
        if terminal:
            master, slave = pty.openpty()
            self.master = master
            attributes = termios.tcgetattr(slave)
            attributes[3] |= termios.ISIG | termios.ICANON
            attributes[6][termios.VINTR] = INTERRUPT_BYTE
            termios.tcsetattr(slave, termios.TCSANOW, attributes)

            def own_foreground_terminal():
                os.setsid()
                fcntl.ioctl(slave, termios.TIOCSCTTY, 0)
                os.tcsetpgrp(slave, os.getpid())

            self.process = subprocess.Popen(arguments, cwd=ROOT, env=environment,
                                            stdin=slave, stdout=slave, stderr=slave,
                                            preexec_fn=own_foreground_terminal)
            os.close(slave)
            self.collector = threading.Thread(target=self._collect, daemon=True)
            self.collector.start()
        else:
            self.process = subprocess.Popen(arguments, cwd=ROOT, env=environment,
                                            stdin=subprocess.DEVNULL,
                                            stdout=self.console, stderr=self.console)

    def _collect(self):
        while not self.collector_stop.is_set():
            try:
                ready, _, _ = select.select([self.master], [], [], .1)
                if ready:
                    data = os.read(self.master, 65536)
                    if not data:
                        return
                    self.console.write(data)
                    self.console.flush()
            except OSError:
                return

    def request(self, path, body=None, method=None, authenticated=True):
        headers = {}
        if authenticated:
            headers["Authorization"] = "Bearer " + self.token
        data = None
        if body is not None:
            headers["Content-Type"] = "application/json"
            data = json.dumps(body).encode()
        request = urllib.request.Request(f"http://127.0.0.1:{self.port}/v1" + path,
                                         data=data, headers=headers, method=method)
        try:
            response = urllib.request.urlopen(request, timeout=HTTP_TIMEOUT_SECONDS)
        except urllib.error.HTTPError as response:
            data = response.read()
            return response.code, json.loads(data) if data else None
        with response:
            data = response.read()
            return response.status, json.loads(data) if data else None

    def ready(self):
        if self.process.poll() is not None:
            raise RuntimeError("Own service exited before readiness")
        try:
            code, body = self.request("/status")
            return body if code == 200 else None
        except (urllib.error.URLError, TimeoutError, ConnectionError):
            return None

    def launch(self, fixture, mode, environment=None):
        is_assembly = fixture.suffix.lower() == ".dll"
        body = {"executable": dotnet_command() if is_assembly else str(fixture),
                "args": [str(fixture), mode] if is_assembly else [mode], "durable": True,
                "closeStdin": False, "waitMilliseconds": 0}
        if environment is not None:
            body["environment"] = environment
        code, record = self.request("/executions", body)
        require(code in (201, 202), "Own fixture launch refused")
        descriptor = os.pidfd_open(record["pid"])
        self.children.append((record["id"], descriptor))
        return record

    def stop(self):
        for ident, _ in self.children:
            with contextlib.suppress(Exception):
                self.request("/executions/" + ident + "/close-stdin", method="POST")
        if self.process.poll() is None:
            with contextlib.suppress(Exception):
                self.request("/shutdown", method="POST")
            with contextlib.suppress(subprocess.TimeoutExpired):
                self.process.wait(timeout=3)
        # pidfds prevent reuse from redirecting cleanup to an unrelated process.
        for _, descriptor in self.children:
            with contextlib.suppress(ProcessLookupError):
                signal.pidfd_send_signal(descriptor, signal.SIGKILL)
            os.close(descriptor)
        self.children.clear()
        if self.process.poll() is None:
            self.process.terminate()
            try:
                self.process.wait(timeout=3)
            except subprocess.TimeoutExpired:
                self.process.kill()
                self.process.wait(timeout=3)
        self.collector_stop.set()
        if self.collector:
            self.collector.join(timeout=2)
        if self.master is not None:
            os.close(self.master)
        self.console.close()


def missing_key(work, command, fixture):
    service = Service(work, command, key=False)
    try:
        code = service.process.wait(timeout=DEADLINE_SECONDS)
        require(code != 0, "Missing API key did not refuse startup")
        with socket.socket() as connection:
            require(connection.connect_ex(("127.0.0.1", service.port)) != 0,
                    "Missing-key service accepted a connection")
        return {"exitCode": code, "portAccepted": False}
    finally:
        service.stop()


def terminal_interrupt(work, command, fixture):
    service = Service(work, command, terminal=True)
    try:
        wait_until(service.ready)
        require(os.tcgetpgrp(service.master) == service.process.pid,
                "Own service was not the PTY foreground process group")
        record = service.launch(fixture, "echo")
        child_pid = record["pid"]
        wait_until(lambda: os.getsid(child_pid) == child_pid)
        require(os.getpgid(child_pid) != os.getpgid(service.process.pid),
                "Fixture shares the service's terminal process group")
        os.write(service.master, INTERRUPT_BYTE)

        def draining():
            code, status = service.request("/status")
            return status if code == 200 and status["mode"] == "draining" else None

        wait_until(draining)
        code, current = service.request("/executions/" + record["id"])
        require(code == 200 and current["state"] == "running",
                "Terminal Ctrl-C terminated the child instead of draining")
        code, written = service.request("/executions/" + record["id"] + "/stdin",
                                         {"text": "after-terminal-interrupt", "close": True})
        require(code == 200 and written["closed"], "Draining rejected existing stdin")
        exit_code = service.process.wait(timeout=DEADLINE_SECONDS)
        require(exit_code == 0, "Drained service did not exit normally")
        return {"servicePid": service.process.pid, "childPid": child_pid,
                "foregroundGroup": service.process.pid, "childSession": child_pid,
                "childSurvivedTerminalInterrupt": True,
                "existingStdinAccepted": True, "exitCode": exit_code}
    finally:
        service.stop()


def spool_lifetime(work, command, fixture):
    work.mkdir(mode=0o700)
    spool = work / "shared-spool"
    spool.mkdir(mode=0o700)
    stale = spool / (uuid.uuid4().hex + ".blob")
    stale.write_bytes(b"previous-run-output")
    unrelated = spool / "keep.txt"
    unrelated.write_bytes(b"unrelated-file")
    first = Service(work / "first", command, spool=spool)
    second = None
    try:
        wait_until(first.ready)
        require(not stale.exists(), "Startup retained stale UUID output")
        require(unrelated.read_bytes() == b"unrelated-file", "Startup removed an unrelated file")
        second = Service(work / "second", command, spool=spool)
        exit_code = second.process.wait(timeout=DEADLINE_SECONDS)
        require(exit_code != 0, "Second service acquired the same spool lease")
        require(first.request("/status")[0] == 200, "Lease refusal damaged the first service")
        return {"staleBlobRemoved": True, "unrelatedFilePreserved": True,
                "secondExitCode": exit_code, "firstServiceStillAvailable": True}
    finally:
        if second:
            second.stop()
        first.stop()


def diagnostic_logs(work, command, fixture):
    service = Service(work, command, log_file_bytes=6000)
    sentinel = "stream-body-" + uuid.uuid4().hex
    parameter = "parameter-" + uuid.uuid4().hex
    try:
        wait_until(service.ready)
        for _ in range(45):
            require(service.request("/status")[0] == 200, "Status request failed during log rotation")
        record = service.launch(fixture, "echo", {"LIFECYCLE_PARAMETER": parameter})
        code, written = service.request("/executions/" + record["id"] + "/stdin",
                                         {"text": sentinel, "close": True})
        require(code == 200 and written["bytesWritten"] == len(sentinel), "Fixture stdin failed")

        def exited():
            code, current = service.request("/executions/" + record["id"])
            return current if code == 200 and current["captureComplete"] and current["state"] == "exited" else None

        wait_until(exited)
        code, output = service.request("/executions/" + record["id"] + "/output")
        require(code == 200 and output["stdout"]["text"] == sentinel, "Fixture body was not echoed")
        service.request("/shutdown", method="POST")
        service.process.wait(timeout=DEADLINE_SECONDS)
        paths = list(service.logs.glob("server.jsonl*"))
        raw = b"".join(path.read_bytes() for path in paths)
        require(len(paths) in (2, 3), "JSON logs did not rotate within retention count")
        require(service.token.encode() not in raw, "API key leaked into file diagnostics")
        require(sentinel.encode() not in raw, "Stream body leaked into file diagnostics")
        require(parameter.encode() in raw, "Launch parameters are absent from structured logs")
        rows = [json.loads(line) for line in raw.splitlines()]
        require(any("Bytes" in row.get("fields", {}) for row in rows), "Input byte counter is absent")
        require(any("StdoutBytes" in row.get("fields", {}) for row in rows), "Output byte counter is absent")
        return {"files": len(paths), "jsonRecords": len(rows),
                "apiKeyAbsent": True, "streamBodyAbsent": True,
                "launchParametersPresent": True, "byteCountersPresent": True}
    finally:
        service.stop()


def exited_root_with_blocked_input(work, command, fixture, initial):
    service = Service(work, command)
    try:
        wait_until(service.ready)
        child_pid_file = work / "owned-descendant.pid"
        child_script = work / "hold-input.py"
        release = work / "exit-root"
        child_script.write_text(
            "import os, pathlib, sys, time\n"
            "if os.fork():\n"
            " deadline = time.monotonic() + " + str(OWNED_DESCENDANT_SECONDS) + "\n"
            " while sys.argv[2] != '-' and not pathlib.Path(sys.argv[2]).exists() and time.monotonic() < deadline: time.sleep(.01)\n"
            " os._exit(0)\n"
            "pathlib.Path(sys.argv[1]).write_text(str(os.getpid()))\n"
            "os.close(1); os.close(2)\n"
            "time.sleep(" + str(OWNED_DESCENDANT_SECONDS) + ")\n")
        body = {"executable": sys.executable,
                "args": [str(child_script), str(child_pid_file), "-" if initial else str(release)],
                "durable": True, "waitMilliseconds": 0}
        if initial:
            body["stdin"] = {"text": "x" * BLOCKED_INPUT_BYTES}
        code, record = service.request("/executions", body)
        require(code in (201, 202), "Blocked-input fixture launch failed")
        if not initial:
            service.children.append((record["id"], os.pidfd_open(record["pid"])))
        wait_until(lambda: child_pid_file.exists() and child_pid_file.read_text())
        descendant = os.pidfd_open(int(child_pid_file.read_text()))
        service.children.append((record["id"], descendant))

        def finished():
            status, current = service.request("/executions/" + record["id"])
            return current if status == 200 and current["state"] == "exited" and current["captureComplete"] else None

        with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
            writer = closer = None
            if not initial:
                writer = pool.submit(service.request, "/executions/" + record["id"] + "/stdin",
                                     {"text": "x" * BLOCKED_INPUT_BYTES})
                time.sleep(PENDING_WRITE_PROBE_SECONDS)
                require(not writer.done(), "Later writer did not block in inherited pipe")
                closer = pool.submit(service.request, "/executions/" + record["id"] + "/close-stdin", None, "POST")
                time.sleep(PENDING_WRITE_PROBE_SECONDS)
                require(not closer.done(), "Close did not queue behind the blocked writer")
                release.write_text("exit")
            wait_until(finished)
            started = time.monotonic()
            code, _ = service.request("/executions/" + record["id"], method="DELETE")
            require(code == 204, "Exited root could not be deleted while descendant holds stdin")
            if writer:
                require(writer.result(timeout=HTTP_TIMEOUT_SECONDS)[0] == 409, "Later write was not refused on root exit")
                close_code, closed = closer.result(timeout=HTTP_TIMEOUT_SECONDS)
                require(close_code == 200 and closed["closed"], "Queued close did not settle idempotently")
            require(not select.select([descendant], [], [], 0)[0], "Descendant exited before deletion was tested")
            return {"rootExited": True, "captureComplete": True, "descendantStillAlive": True,
                    "laterWriteSettled": not initial, "queuedCloseSettled": not initial,
                    "deleteMilliseconds": round((time.monotonic() - started) * 1000)}
    finally:
        service.stop()


def record_pipe_handles_released(work, command, fixture):
    service = Service(work, command)
    try:
        wait_until(service.ready)

        def run_and_delete():
            record = service.launch(fixture, "emit")
            def finished():
                code, current = service.request("/executions/" + record["id"])
                return code == 200 and current["state"] == "exited" and current["captureComplete"]
            wait_until(finished)
            require(service.request("/executions/" + record["id"], method="DELETE")[0] == 204, "Pipe fixture deletion failed")

        run_and_delete()
        descriptors = Path("/proc") / str(service.process.pid) / "fd"
        before = len(list(descriptors.iterdir()))
        for _ in range(PIPE_REUSE_EXECUTIONS):
            run_and_delete()
        after = len(list(descriptors.iterdir()))
        require(after <= before + FD_BACKGROUND_ALLOWANCE, "Repeated execution deletion retained pipe descriptors")
        return {"executionsDeleted": PIPE_REUSE_EXECUTIONS + 1, "descriptorsBefore": before, "descriptorsAfter": after}
    finally:
        service.stop()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--server-binary", type=Path)
    parser.add_argument("--fixture", type=Path)
    options = parser.parse_args()
    require(os.name == "posix" and Path("/proc/self").exists(), "This acceptance runner requires Linux")
    fixture = options.fixture.resolve() if options.fixture else ROOT / "tests/ExecServer.Fixture/bin/Debug/net11.0/exec-server-fixture.dll"
    target = options.server_binary.resolve() if options.server_binary else ROOT / "src/ExecServer/bin/Debug/net11.0/linux-x64/exec-server.dll"
    command = [str(target)] if options.server_binary else [dotnet_command(), str(target)]
    require(target.is_file() and fixture.is_file(), "Build the service and fixture before this acceptance runner")
    stamp = datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%dT%H%M%SZ")
    work = ROOT / ".artifacts/lifecycle" / (stamp + "-" + uuid.uuid4().hex[:8])
    work.mkdir(mode=0o700, parents=True)
    initial_hash = sha256(target)
    results = []
    for name, check in [("missing_api_key", missing_key), ("terminal_ctrl_c", terminal_interrupt),
                        ("spool_ownership", spool_lifetime), ("rotated_json_diagnostics", diagnostic_logs),
                        ("exited_root_with_blocked_initial_input", lambda w, c, f: exited_root_with_blocked_input(w, c, f, True)),
                        ("exited_root_with_blocked_later_input", lambda w, c, f: exited_root_with_blocked_input(w, c, f, False)),
                        ("record_pipe_handles_released", record_pipe_handles_released)]:
        started = time.monotonic()
        try:
            evidence = check(work / name, command, fixture)
            results.append({"name": name, "status": "passed", "evidence": evidence,
                            "milliseconds": round((time.monotonic() - started) * 1000)})
        except Exception as error:
            results.append({"name": name, "status": "failed", "error": str(error),
                            "errorType": type(error).__name__})
    unchanged = initial_hash == sha256(target)
    report = {"startedAt": stamp, "serverSha256": initial_hash, "serverUnchanged": unchanged,
              "fixtureSha256": sha256(fixture), "checks": results,
              "passed": sum(row["status"] == "passed" for row in results),
              "failed": sum(row["status"] != "passed" for row in results) + int(not unchanged)}
    (work / "receipt.json").write_text(json.dumps(report, indent=2) + "\n")
    print(json.dumps({"receipt": str(work / "receipt.json"), **report}))
    return int(report["failed"] != 0)


if __name__ == "__main__":
    raise SystemExit(main())
