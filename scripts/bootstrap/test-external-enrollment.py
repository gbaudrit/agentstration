#!/usr/bin/env python3
"""Smoke-test real Web/OpenAPI processes with delayed enrollment and persisted restart.

Build Agentstration.slnx in Release first. Requires Python 3.9+ and the .NET runtime;
does not require Docker, a model provider or Internet access. Only fresh temporary
data and key files are used. Logs remain in the printed temporary directory.
"""
import argparse
import json
import os
import pathlib
import secrets
import shutil
import socket
import sqlite3
import subprocess
import tempfile
import time
import urllib.error
import urllib.request


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dotnet", default=shutil.which("dotnet"), help="Path to the dotnet executable")
    args = parser.parse_args()
    if not args.dotnet:
        parser.error("dotnet must be on PATH or supplied with --dotnet")
    repo = pathlib.Path(__file__).resolve().parents[2]
    root = pathlib.Path(tempfile.mkdtemp(prefix="agentstration-bootstrap-smoke-"))
    print("Smoke logs and isolated data:", root, flush=True)
    key = root / "openapi.key"
    key.write_text(secrets.token_urlsafe(40))
    key.chmod(0o600)
    sockets = [socket.socket(), socket.socket()]
    for listener in sockets:
        listener.bind(("127.0.0.1", 0))
    authority, endpoint = [f"http://127.0.0.1:{listener.getsockname()[1]}" for listener in sockets]
    for listener in sockets:
        listener.close()
    # Start from a controlled environment, not the operator's product settings.
    environment = {k: v for k, v in os.environ.items() if not k.lower().startswith(
        ("agentstration__", "aep__", "openapi__", "data__", "ai__", "connectionstrings__", "aspnetcore_"))}
    web = environment | {
        "ASPNETCORE_ENVIRONMENT": "Development", "ASPNETCORE_URLS": authority,
        "Data__Directory": str(root / "data"), "AI__Provider": "Deterministic",
        "Logging__LogLevel__Default": "Warning",
        "Agentstration__InstanceId": "bootstrap-smoke",
        "Agentstration__Bootstrap__Path": str(repo / "deploy/bootstrap/profiles"),
        "Agentstration__Bootstrap__InitialBootstrapEnabled": "true",
        "Agentstration__Bootstrap__PhasedStartupEnabled": "true",
        "Agentstration__Bootstrap__ExtensionDependencyTimeout": "00:02:00",
        "Agentstration__Bootstrap__InitialProfiles__0": "development",
        "Agentstration__Bootstrap__InitialProfiles__1": "bootstrap-openapi",
        "Agentstration__Bootstrap__InitialApplications__0__Profiles__0": "bootstrap-workspace-example",
        "Agentstration__Bootstrap__InitialApplications__0__TargetLevel": "/tenants/dev/workspaces/default",
        "Agentstration__Bootstrap__Secrets__AdminPassword": secrets.token_urlsafe(30),
        "Agentstration__Aep__Enrollment__AutoEnrollmentLevel": "/instance",
        "Agentstration__Extensions__Agentstration.Extensions.OpenApi__EnrollmentMode": "SharedKeyFile",
        "Agentstration__Extensions__Agentstration.Extensions.OpenApi__SharedKeyFile__Path": str(key),
        **{f"Agentstration__{family}Api__BaseAddress": authority for family in ("Management", "Flow", "Work", "Runtime")},
    }
    extension = environment | {
        "ASPNETCORE_ENVIRONMENT": "Development", "ASPNETCORE_URLS": endpoint,
        "OpenApi__AllowedDomains__0": "example.invalid",
        "Aep__EnrollmentMode": "SharedKeyFile", "Aep__SharedKeyFile__Path": str(key),
        "Aep__SharedKeyFile__AuthorityUrl": authority, "Aep__SharedKeyFile__PublicEndpoint": endpoint,
        "Aep__SharedKeyFile__StateFile": str(root / "extension-instance"),
        "Aep__SharedKeyFile__AllowInsecureHttp": "true",
    }
    processes = []

    def start(project, settings, log_name):
        dll = repo / "src" / project / "bin/Release/net10.0" / (project + ".dll")
        if not dll.is_file():
            raise RuntimeError(f"Build Release before running this smoke check: missing {dll}")
        with (root / (log_name + ".log")).open("w") as log:
            processes.append(subprocess.Popen([args.dotnet, str(dll)], cwd=repo / "src" / project,
                                              env=settings, stdout=log, stderr=subprocess.STDOUT))

    def status(path):
        try:
            with urllib.request.urlopen(authority + path, timeout=2) as response:
                return response.status
        except urllib.error.HTTPError as error:
            return error.code
        except (urllib.error.URLError, TimeoutError):
            return 0

    def until(predicate, seconds, label):
        deadline = time.monotonic() + seconds
        while time.monotonic() < deadline:
            if predicate():
                return
            if any(process.poll() is not None for process in processes):
                raise RuntimeError(f"A process exited while waiting for {label}; see {root}")
            time.sleep(.25)
        raise RuntimeError(f"Timed out waiting for {label}; see {root}")

    def stop():
        for process in reversed(processes):
            if process.poll() is None:
                process.terminate()
        for process in reversed(processes):
            try:
                process.wait(timeout=20)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait()
        processes.clear()

    def snapshot():
        result = {}
        with sqlite3.connect(f"file:{root / 'data/control-plane.db'}?mode=ro", uri=True) as db:
            for kind, name, uid, etag, payload in db.execute(
                    "SELECT Kind, Name, Uid, ETag, Payload FROM ControlPlaneResources"):
                if kind == "AepEnrollmentRequest":
                    definition = json.loads(payload)["definition"]
                    result[kind + "/" + name] = {k: definition[k] for k in
                        ("instanceId", "targetScopeRef", "registrationName", "state")}
                elif kind == "ExtensionRegistration" or (name.startswith("bootstrap-") and kind != "InstanceInitialization"):
                    result[kind + "/" + name] = (uid, etag)
        with sqlite3.connect(f"file:{root / 'data/flow-plane.db'}?mode=ro", uri=True) as db:
            for key, kind, etag in db.execute("SELECT Key, Kind, ETag FROM FlowResources WHERE FlowId='bootstrap-echo'"):
                result["Flow/" + key] = (kind, etag)
        with sqlite3.connect(f"file:{root / 'data/work-plane.db'}?mode=ro", uri=True) as db:
            for identifier, payload in db.execute("SELECT Id, Payload FROM Entries WHERE Name='bootstrap-echo'"):
                result["Entry/" + identifier] = payload
        return result

    try:
        start("Agentstration.Web", web, "web-first")
        until(lambda: status("/health") == 200, 90, "liveness")
        assert status("/health/ready") == 503
        assert status("/api/tools") == 503
        print("PASS delayed extension: live=200, ready=503, normal API=503", flush=True)
        start("Agentstration.Extensions.OpenApi", extension, "extension-first")
        until(lambda: status("/health/ready") == 200, 150, "complete contribution Bootstrap")
        before = snapshot()
        assert "ToolSet/bootstrap-openapi-tools" in before
        assert "DataSource/bootstrap-openapi-source" in before
        assert any(k.startswith("Flow/") for k in before)
        assert any(k.startswith("Entry/") for k in before)
        assert len([k for k in before if k.startswith("AepEnrollmentRequest/")]) == 1
        identity = (root / "extension-instance").read_text()
        print("PASS enrollment, contributed Tool/DataSourceProfile, ToolSet, DataSource and Workspace Flow/Entry", flush=True)
        stop()
        start("Agentstration.Web", web, "web-restart")
        start("Agentstration.Extensions.OpenApi", extension, "extension-restart")
        until(lambda: status("/health/ready") == 200, 150, "restart readiness")
        assert before == snapshot(), "Restart changed an assignment or a Bootstrap consumer identity/version"
        assert identity == (root / "extension-instance").read_text()
        print("PASS restart: installation, assignment and Bootstrap consumers preserved", flush=True)
    finally:
        stop()


if __name__ == "__main__":
    main()
