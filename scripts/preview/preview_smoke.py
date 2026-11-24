#!/usr/bin/env python3
"""Exercise the actual SQLite preview composition with exact server/Worker deliveries."""
import argparse
import json
from pathlib import Path
import re
import secrets
import tempfile
import uuid
import urllib.request

import preview
from worker_smoke import Api, completed_flow, until, worker_evidence


def bootstrap_password():
    # The non-Development identity policy requires all four character classes.
    return secrets.token_urlsafe(40) + "aA1!"


def failure_diagnostics(directory, secret_values):
    """Retain bounded technical failures without raw logs, configuration or credential values."""
    containers = {}
    for service in ("server", "worker"):
        try:
            identity = preview.compose(directory, "ps", "--all", "--quiet", service, capture=True).strip()
            if not re.fullmatch(r"[0-9a-f]{12,64}", identity):
                continue
            value = json.loads(preview.subprocess.check_output(
                ["docker", "inspect", identity], text=True, stderr=preview.subprocess.DEVNULL))[0]
            state = value["State"]
            containers[service] = {"status": state["Status"], "exitCode": state["ExitCode"],
                                   "oomKilled": state.get("OOMKilled", False), "restarts": value.get("RestartCount", 0),
                                   "health": state.get("Health", {}).get("Status")}
        except Exception:
            containers[service] = {"statusUnavailable": True}
    try:
        logs = preview.compose(directory, "logs", "--no-log-prefix", "--tail", "120", capture=True)
    except Exception:
        return {"logsUnavailable": True, "containers": containers}
    for value in secret_values:
        logs = logs.replace(value, "[redacted]")
    logs = re.sub(r"[A-Za-z0-9_-]{32,}", "[redacted]", logs)
    lines = [line.strip()[:500]
             for line in logs.splitlines() if re.search(r"\b(?:[A-Za-z0-9_]+\.)*[A-Za-z0-9_]+Exception:", line)]
    return {"containers": containers, "exceptions": lines[-8:], "codes": sorted(set(re.findall(
        r"\b(?:bootstrap|extension|flow|worker|resource)_[a-z0-9_]+\b", logs)))[:16]}


def run(server_delivery, worker_delivery, registry, output):
    environment_id = "smoke-" + uuid.uuid4().hex[:12]
    with tempfile.TemporaryDirectory(prefix="preview-smoke-") as temporary:
        directory = Path(temporary)
        state = preview.prepare(directory, server_delivery, worker_delivery, environment_id, port=0, registry=registry)
        password = bootstrap_password()
        bootstrap = {"InitialBootstrapEnabled": True, "InitialProfiles": ["development"],
                     "Secrets": {"AdminPassword": password}, "InitialApplications": [{
                         "Profiles": ["bootstrap-workspace-example"], "TargetLevel": "/tenants/dev/workspaces/default"}]}
        sessions, executions = [], []
        phase = "default"
        try:
            # Test the actual non-Development default before explicitly applying any fixture data.
            preview.compose(directory, "up", "--detach", "--wait", "--wait-timeout", "180")
            baseline = preview.verify(directory)
            Api(baseline["url"]).call("/api/auth/local/login", {"userName": "admin", "password": "admin"}, expected=401)
            with urllib.request.urlopen(baseline["url"] + "/bootstrap", timeout=10) as response:
                preview.require(response.status == 200, "Manual initialization page is not available.")
            preview.compose(directory, "down", "--volumes", "--timeout", "20")
            state = preview.prepare(directory, server_delivery, worker_delivery, environment_id, port=0,
                                    registry=registry, bootstrap=bootstrap)
            for iteration in range(2):
                phase = "bootstrap" if iteration == 0 else "recreation"
                preview.compose(directory, "up", "--detach", "--wait", "--wait-timeout", "180")
                base = preview.verify(directory)["url"]
                api = Api(base)
                api.call("/api/auth/local/login", {"userName": "admin", "password": password}, expected=204)
                api.call("/api/awp/v1/workers/register", {}, expected=401)
                descriptor = api.call("/api/runtime/workers/" + state["workerId"])["worker"]
                evidence = worker_evidence(descriptor, state["images"]["worker"], state["workerId"])
                preview.require(evidence["sessionId"] not in sessions, "Recreated Worker did not get a new session.")
                sessions.append(evidence["sessionId"])
                expected = {"echo": "preview-smoke-" + str(iteration)}
                pending = api.call("/api/flows/bootstrap-echo/runs", {"input": expected}, expected=202)
                executions.append(until(lambda: completed_flow(api, pending["id"], expected, state["workerId"], sessions[-1]),
                                        "preview deterministic execution"))
                if iteration == 0:
                    preview.compose(directory, "down", "--timeout", "20")
                else:
                    preview.require(completed_flow(api, executions[0]["runId"], {"echo": "preview-smoke-0"},
                                                   state["workerId"], sessions[0]), "First execution was not retained in SQLite.")
                print(f"PASS preview {'startup' if iteration == 0 else 'recreation'}: exact images, AWP execution and persisted SQLite state.", flush=True)
            report = {"sourceSha": state["sourceSha"], "images": state["images"], "database": "sqlite",
                      "passed": True,
                      "workerId": state["workerId"], "credentialId": state["credentialId"], "sessions": sessions,
                      "executions": executions, "nonDevelopmentDefault": True, "defaultCredentialRejected": True,
                      "manualBootstrapAvailable": True, "bootstrapExplicitlySelected": True,
                      "recreationPreservesDataAndIdentity": True, "workerDatabaseAccess": False}
            output.parent.mkdir(parents=True, exist_ok=True)
            output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
        except Exception:
            diagnostics = failure_diagnostics(directory, [password, (directory / "worker.key").read_text()])
            output.parent.mkdir(parents=True, exist_ok=True)
            output.write_text(json.dumps({"passed": False, "phase": phase, "diagnostics": diagnostics}, indent=2) + "\n")
            print("Preview diagnostics:", json.dumps({"phase": phase, **diagnostics}), flush=True)
            raise
        finally:
            # This randomly named test project owns only these two containers and its SQLite volume.
            preview.compose(directory, "down", "--volumes", "--remove-orphans", "--timeout", "20")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--server-delivery", required=True, type=Path)
    parser.add_argument("--worker-delivery", required=True, type=Path)
    parser.add_argument("--registry", default="ghcr.io")
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    run(preview.read_json(args.server_delivery), preview.read_json(args.worker_delivery), args.registry, args.output)


if __name__ == "__main__":
    main()
