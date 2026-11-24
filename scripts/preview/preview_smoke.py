#!/usr/bin/env python3
"""Exercise the actual SQLite preview composition with exact server/Worker deliveries."""
import argparse
import json
from pathlib import Path
import secrets
import tempfile
import uuid
import urllib.request

import preview
from worker_smoke import Api, completed_flow, until, worker_evidence


def run(server_delivery, worker_delivery, registry, output):
    environment_id = "smoke-" + uuid.uuid4().hex[:12]
    with tempfile.TemporaryDirectory(prefix="preview-smoke-") as temporary:
        directory = Path(temporary)
        state = preview.prepare(directory, server_delivery, worker_delivery, environment_id, port=0, registry=registry)
        password = secrets.token_urlsafe(40)
        bootstrap = {"InitialBootstrapEnabled": True, "InitialProfiles": ["development"],
                     "Secrets": {"AdminPassword": password}, "InitialApplications": [{
                         "Profiles": ["bootstrap-workspace-example"], "TargetLevel": "/tenants/dev/workspaces/default"}]}
        sessions, executions = [], []
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
                      "workerId": state["workerId"], "credentialId": state["credentialId"], "sessions": sessions,
                      "executions": executions, "nonDevelopmentDefault": True, "defaultCredentialRejected": True,
                      "manualBootstrapAvailable": True, "bootstrapExplicitlySelected": True,
                      "recreationPreservesDataAndIdentity": True, "workerDatabaseAccess": False}
            output.parent.mkdir(parents=True, exist_ok=True)
            output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
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
