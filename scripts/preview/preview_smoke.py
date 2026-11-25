#!/usr/bin/env python3
"""Exercise the actual preview composition with exact images and persisted state."""
import argparse
import json
from pathlib import Path
import re
import secrets
import tempfile
import uuid
import urllib.request

import preview
import contract
import lifecycle
from worker_smoke import Api, completed_flow, until, worker_evidence


def bootstrap_password():
    # The non-Development identity policy requires all four character classes.
    return secrets.token_urlsafe(40) + "aA1!"


def failure_diagnostics(directory, secret_values):
    """Retain bounded technical failures without raw logs, configuration or credential values."""
    containers = {}
    state = preview.read_json(directory / "deployment.json")
    for service in state["images"]:
        try:
            identity = preview.compose(directory, "ps", "--all", "--quiet", service, capture=True).strip()
            if not re.fullmatch(r"[0-9a-f]{12,64}", identity):
                continue
            value = json.loads(preview.subprocess.check_output(
                ["docker", "inspect", identity], text=True, stderr=preview.subprocess.DEVNULL))[0]
            container_state = value["State"]
            containers[service] = {"status": container_state["Status"], "exitCode": container_state["ExitCode"],
                                   "oomKilled": container_state.get("OOMKilled", False), "restarts": value.get("RestartCount", 0),
                                   "health": container_state.get("Health", {}).get("Status")}
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


def database_evidence(directory, database):
    if database == "sqlite":
        return {"provider": database}
    query = "SELECT DISTINCT table_schema FROM information_schema.tables WHERE table_schema IN " \
            "('management','work','flow','runtime','identity','scheduler','resource_planning') ORDER BY table_schema"
    schemas = preview.compose(directory, "exec", "-T", "postgres", "psql", "--username", "agentstration",
                              "--dbname", "agentstration", "--no-align", "--tuples-only", "--set", "ON_ERROR_STOP=1",
                              "--command", query, capture=True).splitlines()
    preview.require(set(schemas) == {"management", "work", "flow", "runtime", "identity", "scheduler", "resource_planning"},
                    "The PostgreSQL preview did not initialize every module-owned schema.")
    return {"provider": database, "schemas": schemas}


def run(server_delivery, worker_delivery, registry, output, database="sqlite", postgres_image=None):
    environment_id = "smoke-" + uuid.uuid4().hex[:12]
    with tempfile.TemporaryDirectory(prefix="preview-smoke-") as temporary:
        root = Path(temporary)
        directory = root / environment_id
        request = contract.plan(server_delivery, worker_delivery, environment_id, database=database,
                                target={"port": 0, "daemonId": lifecycle.docker("info", "--format", "{{.ID}}")},
                                registry=registry, postgres_image=postgres_image)
        reset_request = {**request, "dataMode": "reset"}
        state = preview.prepare(directory, server_delivery, worker_delivery, environment_id, port=0, registry=registry,
                                database=database, postgres_image=postgres_image)
        password = bootstrap_password()
        bootstrap = {"InitialBootstrapEnabled": True, "InitialProfiles": ["development"],
                     "Secrets": {"AdminPassword": password}, "InitialApplications": [{
                         "Profiles": ["bootstrap-workspace-example"], "TargetLevel": "/tenants/dev/workspaces/default"}]}
        sessions, executions = [], []
        phase = "default"
        other_volume = "preview-other-" + uuid.uuid4().hex[:12]
        try:
            lifecycle.docker("volume", "create", "--label", lifecycle.PROJECT + "=" + other_volume, other_volume)
            lifecycle.execute("validate", request, root)
            preview.require(lifecycle.execute("status", request, root)["state"] == "absent", "New lifecycle was not absent.")
            # An unmanaged same-name volume must survive a refused destroy.
            foreign_volume = "preview-" + environment_id + "-foreign-fixture"
            lifecycle.docker("volume", "create", "--label", lifecycle.PROJECT + "=preview-" + environment_id, foreign_volume)
            try:
                try:
                    lifecycle.execute("destroy", reset_request, root)
                except ValueError:
                    lifecycle.docker("volume", "inspect", foreign_volume)
                else:
                    raise ValueError("The lifecycle accepted unmanaged resources.")
            finally:
                lifecycle.docker("volume", "rm", foreign_volume)
            # Test the actual non-Development default before explicitly applying any fixture data.
            baseline_result = lifecycle.execute("deploy", request, root)
            expiry = baseline_result["expiresAt"]
            baseline = preview.verify(directory)
            storage = database_evidence(directory, database)
            Api(baseline["url"]).call("/api/auth/local/login", {"userName": "admin", "password": "admin"}, expected=401)
            with urllib.request.urlopen(baseline["url"] + "/bootstrap", timeout=10) as response:
                preview.require(response.status == 200, "Manual initialization page is not available.")
            lifecycle.execute("destroy", reset_request, root)
            preview.require(lifecycle.execute("status", request, root)["state"] == "absent", "Reset retained application resources.")
            state = preview.prepare(directory, server_delivery, worker_delivery, environment_id, port=0,
                                    registry=registry, bootstrap=bootstrap)
            for iteration in range(2):
                phase = "bootstrap" if iteration == 0 else "recreation"
                deployed = lifecycle.execute("deploy", request, root)
                state = preview.read_json(directory / "deployment.json")
                preview.require(deployed["expiresAt"] == expiry, "Retry/recreation silently renewed the environment TTL.")
                preview.require(lifecycle.execute("status", request, root)["state"] == "running", "Lifecycle status did not observe healthy exact images.")
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
                    preview.compose(directory, "exec", "-T", "server", "touch", "/data/preview-smoke-persistence")
                    lifecycle.execute("destroy", request, root)
                    lifecycle.execute("destroy", request, root)
                    preview.require(lifecycle.execute("status", request, root)["state"] == "stopped", "Preserve removed retained data volumes.")
                else:
                    preview.require(completed_flow(api, executions[0]["runId"], {"echo": "preview-smoke-0"},
                                                   state["workerId"], sessions[0]), "First execution was not retained in the selected database.")
                    preview.compose(directory, "exec", "-T", "server", "test", "-f", "/data/preview-smoke-persistence")
                    database_evidence(directory, database)
                print(f"PASS {database} preview {'startup' if iteration == 0 else 'recreation'}: exact images, AWP execution and persisted state.", flush=True)
            report = {"sourceSha": state["sourceSha"], "images": state["images"], "database": database,
                      "storage": storage, "fileBackedStatePreserved": True,
                      "passed": True,
                      "workerId": state["workerId"], "credentialId": state["credentialId"], "sessions": sessions,
                      "executions": executions, "nonDevelopmentDefault": True, "defaultCredentialRejected": True,
                      "manualBootstrapAvailable": True, "bootstrapExplicitlySelected": True,
                      "recreationPreservesDataAndIdentity": True, "workerDatabaseAccess": False,
                      "lifecycle": {"deployStatusDestroy": True, "resetAndPreserve": True, "unmanagedResourcesRejected": True,
                                    "expiryPreserved": True, "providerResourceIds": deployed["providerResourceIds"]}}
            lifecycle.execute("destroy", reset_request, root)
            lifecycle.execute("destroy", reset_request, root)
            preview.require(lifecycle.execute("status", request, root)["state"] == "absent", "Repeated reset left resources.")
            lifecycle.docker("volume", "inspect", other_volume)
            report["lifecycle"]["otherEnvironmentPreserved"] = True
            output.parent.mkdir(parents=True, exist_ok=True)
            output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
        except Exception:
            values = [password, (directory / "worker.key").read_text()]
            if database == "postgresql":
                values.append((directory / "postgres.password").read_text())
            diagnostics = failure_diagnostics(directory, values)
            output.parent.mkdir(parents=True, exist_ok=True)
            output.write_text(json.dumps({"passed": False, "phase": phase, "diagnostics": diagnostics}, indent=2) + "\n")
            print("Preview diagnostics:", json.dumps({"phase": phase, **diagnostics}), flush=True)
            raise
        finally:
            # Only this randomly named project's containers and data volumes are removed.
            lifecycle.execute("destroy", reset_request, root)
            lifecycle.docker("volume", "rm", other_volume)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--server-delivery", required=True, type=Path)
    parser.add_argument("--worker-delivery", required=True, type=Path)
    parser.add_argument("--registry", default="ghcr.io")
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--database", choices=("sqlite", "postgresql"), default="sqlite")
    parser.add_argument("--postgres-image")
    args = parser.parse_args()
    run(preview.read_json(args.server_delivery), preview.read_json(args.worker_delivery), args.registry, args.output,
        args.database, args.postgres_image)


if __name__ == "__main__":
    main()
