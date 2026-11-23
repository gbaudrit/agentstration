#!/usr/bin/env python3
"""Validate real authenticated AWP registration and execution without model inference."""
import argparse
import http.cookiejar
import json
import os
from pathlib import Path
import secrets
import subprocess
import tempfile
import time
import urllib.error
import urllib.request
import uuid

from enrollment_smoke import require, request
from images import delivery_entry, output, DIGEST

ROOT = Path(__file__).resolve().parents[2]


class Api:
    def __init__(self, base):
        self.base = base
        self.opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))

    def call(self, path, body=None, expected=200):
        data = None if body is None else json.dumps(body).encode()
        call = urllib.request.Request(self.base + path, data=data, headers={"Content-Type": "application/json"})
        try:
            with self.opener.open(call, timeout=10) as response:
                status, payload = response.status, response.read()
        except urllib.error.HTTPError as error:
            status, payload = error.code, error.read()
        require(status == expected, f"Unexpected API status for {path}: {status}; expected {expected}.")
        return json.loads(payload) if payload else None


def until(probe, label, seconds=120):
    deadline = time.monotonic() + seconds
    while True:
        try:
            value = probe()
            if value:
                return value
        except (urllib.error.URLError, TimeoutError, ConnectionError, ValueError):
            pass
        require(time.monotonic() < deadline, f"Timed out waiting for {label}.")
        time.sleep(1)


def authority_health(base):
    request(base, "/health/ready")
    return True


def health(base, ready, live=True):
    request(base, "/health/live", expected=200 if live else 503)
    request(base, "/health/ready", expected=200 if ready else 503)
    return True


def worker_evidence(worker, entry, worker_id):
    require(worker["workerId"] == worker_id and worker["sessionId"], "Worker identity/session is missing.")
    require(worker["enrollmentState"].lower() == "active", "Worker identity is not active.")
    require(worker["softwareVersion"].split("+")[0] == entry["version"], "Worker software and OCI versions differ.")
    require(worker["maximumConcurrentAssignments"] == 1, "Worker capacity differs from the test configuration.")
    capabilities = [value for value in worker["capabilities"] if value["runtimeKind"] == entry["runtimeKind"]]
    require(len(capabilities) == 1 and capabilities[0]["capabilityVersion"] == entry["capabilityVersion"]
            and set(capabilities[0]["executionMaterialVersions"]) == set(entry["executionMaterialVersions"]),
            "Registered AWP runtime/material capabilities do not match the catalogue.")
    return {"workerId": worker_id, "sessionId": worker["sessionId"], "softwareVersion": worker["softwareVersion"]}


def completed_flow(api, run_id, expected_output, worker_id, session_id):
    run = api.call("/api/flowRuns/" + run_id)
    if str(run["status"]).lower() in ("pending", "running", "0", "1"):
        return None
    require(str(run["status"]).lower() in ("succeeded", "3") and run["output"] == expected_output,
            "The Worker did not complete the deterministic Flow with its exact expected output.")
    placement = api.call("/api/runtime/placements/FlowRun/" + run_id)
    require(placement["state"].lower() == "succeeded" and len(placement["attempts"]) == 1,
            "Expected one completed AWP assignment attempt.")
    attempt = placement["attempts"][0]
    require(attempt["workerId"] == worker_id and attempt["workerSessionId"] == session_id,
            "The Flow executed on an unexpected Worker/session.")
    return {"runId": run_id, "assignmentId": placement["assignmentId"], "attemptId": attempt["attemptId"]}


def run(delivery_path, image, authority_image, report_path):
    delivery = json.loads(delivery_path.read_text())
    entry = delivery_entry(delivery)
    require(entry.get("kind") == "awp-worker" and DIGEST.fullmatch(delivery["digest"])
            and image.endswith("@" + delivery["digest"]), "Worker smoke requires the exact AWP image delivery digest.")
    subprocess.run(["docker", "pull", image], check=True)
    metadata = json.loads(output(["docker", "image", "inspect", image]))[0]
    require(metadata["Config"]["Labels"].get("org.opencontainers.image.version") == entry["version"]
            and metadata["Config"]["Labels"].get("org.opencontainers.image.revision") == delivery["sourceSha"],
            "Worker OCI version/source metadata mismatch.")
    authority = json.loads(output(["docker", "image", "inspect", authority_image]))[0]
    require(authority["Config"]["Labels"].get("org.opencontainers.image.revision") == delivery["sourceSha"],
            "Authority fixture and Worker must use the same source revision.")
    project = "worker-smoke-" + uuid.uuid4().hex[:12]
    worker_id, credential_id = str(uuid.uuid4()), str(uuid.uuid4())
    with tempfile.TemporaryDirectory(prefix=project + "-") as temporary:
        directory = Path(temporary)
        (directory / "authority").mkdir()
        with open(directory / "key", "w", opener=lambda path, flags: os.open(path, flags, 0o600)) as stream:
            stream.write(secrets.token_urlsafe(48))
        password = secrets.token_urlsafe(40)
        environment = {key: value for key, value in os.environ.items() if not key.startswith("COMPOSE_")}
        environment.update(SMOKE_DIRECTORY=str(directory), SMOKE_UID=str(os.getuid()), SMOKE_GID=str(os.getgid()),
                           SMOKE_BOOTSTRAP_DIRECTORY=str(ROOT / "deploy/bootstrap/profiles"),
                           SMOKE_AUTHORITY_IMAGE=authority["Id"], SMOKE_WORKER_IMAGE=image,
                           SMOKE_WORKER_ID=worker_id, SMOKE_CREDENTIAL_ID=credential_id, SMOKE_ADMIN_PASSWORD=password)
        command = ["docker", "compose", "--project-name", project, "--file", str(ROOT / "scripts/packaging/maf-worker.smoke.compose.yml")]

        def compose(*args, capture=False):
            return subprocess.run([*command, *args], env=environment, check=True, text=True,
                                  stdout=subprocess.PIPE if capture else None).stdout

        def address(service):
            return "http://127.0.0.1:" + compose("port", service, "8080", capture=True).strip().rsplit(":", 1)[1]

        sessions, executions = [], []
        try:
            # A live Worker must remain unready while its authority is unavailable.
            compose("up", "--detach", "--no-build", "worker")
            endpoint = address("worker")
            until(lambda: health(endpoint, ready=False), "unregistered Worker liveness/readiness")
            compose("stop", "--timeout", "20", "worker")
            for iteration in range(2):
                compose("up", "--detach", "--no-build", "authority")
                base = address("authority")
                until(lambda: authority_health(base), "SQLite authority readiness")
                api = Api(base)
                api.call("/api/auth/local/login", {"userName": "admin", "password": password}, expected=204)
                api.call("/api/awp/v1/workers/register", {}, expected=401)
                expected_output = {"echo": "worker-smoke-" + str(iteration)}
                pending = api.call("/api/flows/bootstrap-echo/runs", {"input": expected_output}, expected=202)
                run_id = pending["id"]
                require(str(api.call("/api/flowRuns/" + run_id)["status"]).lower() in ("pending", "0"),
                        "Flow should remain pending before a Worker starts.")
                compose("up", "--detach", "--no-build", "worker")
                endpoint = address("worker")
                until(lambda: health(endpoint, ready=True), "authenticated AWP Worker readiness")
                descriptor = api.call("/api/runtime/workers/" + worker_id)["worker"]
                evidence = worker_evidence(descriptor, entry, worker_id)
                require(evidence["sessionId"] not in sessions, "Worker recreation must create a fresh session.")
                sessions.append(evidence["sessionId"])
                execution = until(lambda: completed_flow(api, run_id, expected_output, worker_id, sessions[-1]),
                                  "deterministic AWP execution")
                executions.append(execution)
                container_id = compose("ps", "--quiet", "worker", capture=True).strip()
                container = json.loads(output(["docker", "inspect", container_id]))[0]
                require({mount["Destination"] for mount in container["Mounts"]} == {"/run/secrets/awp-key"}
                        and all(not mount["RW"] for mount in container["Mounts"]),
                        "The Worker must receive only its read-only credential mount, never server data.")
                require(not any(value.lower().startswith("connectionstrings") for value in container["Config"]["Env"]),
                        "Worker must not receive database connection settings.")
                print(f"PASS AWP {'startup' if iteration == 0 else 'recreation'}: registered Worker, exact version/capabilities and deterministic execution.", flush=True)
                if iteration == 0:
                    compose("down", "--timeout", "20")
            report_path.parent.mkdir(parents=True, exist_ok=True)
            report_path.write_text(json.dumps({"reference": image, "sourceSha": delivery["sourceSha"],
                "version": entry["version"], "awpVersion": entry["awpVersion"], "authorityImageId": authority["Id"],
                "workerId": worker_id, "credentialId": credential_id, "sessions": sessions, "executions": executions,
                "livenessBeforeRegistration": True, "readinessRequiresRegistration": True,
                "unsignedRegistrationRejected": True, "credentialAndIdentityPreserved": True,
                "databaseAccessGranted": False, "storage": "Sqlite"}, indent=2) + "\n")
        finally:
            compose("down", "--volumes", "--remove-orphans", "--timeout", "20")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--delivery", required=True, type=Path)
    parser.add_argument("--image", required=True)
    parser.add_argument("--authority-image", required=True)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    run(args.delivery, args.image, args.authority_image, args.output)


if __name__ == "__main__":
    main()
