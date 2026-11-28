#!/usr/bin/env python3
"""External deploy/status/destroy entrypoint; first executable provider: a trusted local Docker host."""
import argparse
from contextlib import contextmanager
from datetime import datetime, timedelta, timezone
import fcntl
import json
import os
from pathlib import Path
import subprocess
import sys
import uuid

import contract
import container_health
import extensions
import preview

OWNER = "io.agentstration.preview.owner"
PROJECT = "com.docker.compose.project"
ENVIRONMENT = "io.agentstration.preview.environment"


def docker(*arguments):
    result = subprocess.run(["docker", *arguments], text=True, capture_output=True, timeout=60)
    if result.returncode:
        raise RuntimeError("Docker lifecycle operation failed; inspect the trusted host's Docker daemon.")
    return result.stdout.strip()


def inventory(project):
    resources = {"containers": [], "volumes": [], "networks": []}
    for kind, command in (("containers", ("ps", "--all")), ("volumes", ("volume", "ls")), ("networks", ("network", "ls"))):
        identifiers = docker(*command, "--filter", "label=" + PROJECT + "=" + project,
                             "--format", "{{.ID}}" if kind == "containers" else "{{.Name}}").splitlines()
        if identifiers:
            inspect = ("inspect",) if kind == "containers" else ("volume", "inspect") if kind == "volumes" else ("network", "inspect")
            resources[kind] = json.loads(docker(*inspect, *identifiers))
    return resources


def owned(resources, owner):
    for kind, values in resources.items():
        for value in values:
            labels = value.get("Config", {}).get("Labels", {}) if kind == "containers" else value.get("Labels", {})
            preview.require((labels or {}).get(OWNER) == owner, "Foreign or unmanaged project resources exist; refusing to adopt, replace or delete them.")


@contextmanager
def locked(root, environment_id):
    locks = root / ".locks"
    preview.require(not locks.is_symlink(), "Lifecycle lock directories must not be symlinks.")
    locks.mkdir(parents=True, exist_ok=True, mode=0o700)
    lock_path = locks / (environment_id + ".lock")
    preview.require(not lock_path.is_symlink(), "Lifecycle lock files must not be symlinks.")
    with open(lock_path, "a", opener=lambda name, flags: os.open(name, flags, 0o600)) as stream:
        try:
            fcntl.flock(stream, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError as error:
            raise ValueError("Another lifecycle operation owns this environment lock; retry after it completes.") from error
        try:
            yield
        finally:
            fcntl.flock(stream, fcntl.LOCK_UN)


def result(request, state, resources=None, daemon=None, metadata=None):
    inspected = resources is not None
    resources = resources or {"containers": [], "volumes": [], "networks": []}
    identifiers = {kind: [item["Name"] if kind == "volumes" else item["Id"] for item in values]
                   for kind, values in resources.items()}
    url = None
    for item in resources["containers"]:
        if item["Config"]["Labels"].get("com.docker.compose.service") == "server":
            ports = item.get("NetworkSettings", {}).get("Ports", {}).get("8080/tcp") or []
            if ports and ports[0]["HostIp"] == "127.0.0.1":
                url = "http://127.0.0.1:" + ports[0]["HostPort"]
    expiry = (metadata or {}).get("expiresAt")
    return {"schemaVersion": 1, "environmentId": request["environmentId"], "provider": request["provider"],
            "sourceSha": request["sourceSha"], "images": contract.images(request), "database": request["database"],
            "observedImages": {item["Config"]["Labels"].get("com.docker.compose.service", "unknown"): item["Config"]["Image"]
                               for item in resources["containers"]},
            "state": state, "url": url, "expiresAt": expiry,
            "health": container_health.from_containers(contract.images(request), resources["containers"]) if inspected else {},
            "providerResourceIds": {"dockerDaemon": daemon, "composeProject": "preview-" + request["environmentId"], **identifiers} if daemon else {}}


def observed(request, resources):
    containers = resources["containers"]
    if not containers:
        return "stopped" if any(resources.values()) else "absent"
    selected = contract.images(request)
    services = {item["Config"]["Labels"].get("com.docker.compose.service"): item for item in containers}
    if set(services) != set(selected) or len(services) != len(containers):
        return "drifted"
    if any(services[name]["Config"]["Image"] != image["reference"] for name, image in selected.items()):
        return "drifted"
    return "running" if all(item["State"]["Running"] and item["State"].get("Health", {}).get("Status") == "healthy"
                            for item in containers) else "unhealthy"


def execute(action, request, state_root, bootstrap=None, now=None, extension_configuration=None, reset_before_deploy=False):
    preview.require(type(reset_before_deploy) is bool and (not reset_before_deploy or
                    action == 'deploy' and request['provider'] == 'azure-vm'),
                    'Reset-before-deploy requires an Azure deployment.')
    raw_configuration = os.environ.pop("PREVIEW_EXTENSION_CONFIGURATION", "") or "{}"
    contract.operation(action, request)
    settings = extensions.configuration(extension_configuration if extension_configuration is not None else raw_configuration,
                                        request.get("extensions", {})) if action == "deploy" else {}
    if action == "validate":
        return result(request, "validated")
    if request["provider"] == "azure-vm":
        preview.require(state_root is None and bootstrap is None,
                        "Azure uses its private blob/disk state; Docker host state and Bootstrap files are not transferred.")
        import azure
        return azure.execute(action, request, **({"extension_configuration": settings} if settings else {}),
                             **({'reset_before_deploy': True} if reset_before_deploy else {}))
    preview.require(state_root is not None, "Docker operations require a protected persistent state root.")
    root = Path(state_root).resolve()
    directory = root / request["environmentId"]
    preview.require(not directory.is_symlink(), "Lifecycle state directories must not be symlinks.")
    preview.require(not directory.is_relative_to(preview.ROOT) or any(
        part.startswith(".agentstration") for part in directory.relative_to(preview.ROOT).parts),
        "Keep private deployment state outside the repository or under an ignored .agentstration directory.")
    with locked(root, request["environmentId"]):
        daemon = docker("info", "--format", "{{.ID}}")
        preview.require(bool(daemon), "Docker did not identify its daemon.")
        preview.require(daemon == request["target"]["daemonId"], "This request targets another Docker daemon; no provider resources were changed.")
        project = "preview-" + request["environmentId"]
        path = directory / "lifecycle.json"
        preview.require(not path.is_symlink(), "Lifecycle metadata must not be a symlink.")
        metadata = preview.read_json(path) if path.exists() else None
        resources = inventory(project)
        if metadata:
            contract.fields(metadata, ("schemaVersion", "environmentId", "dockerDaemon", "ownerId", "createdAt", "request", "expiresAt"))
            preview.require(metadata["schemaVersion"] == 1 and metadata["environmentId"] == request["environmentId"]
                            and metadata["dockerDaemon"] == daemon, "Lifecycle state belongs to another environment or Docker daemon.")
            uuid.UUID(metadata["ownerId"])
            previous = contract.validate(metadata["request"])
            if action == 'deploy' and any(resources.values()):
                import extension_deployments
                extension_deployments.require_preserving_change(previous, request)
            state_path = directory / "deployment.json"
            if state_path.exists():
                preview.require(not state_path.is_symlink() and preview.read_json(state_path).get("deploymentOwnerId") == metadata["ownerId"],
                                "Deployment credentials and lifecycle ownership do not match; restore the original host state.")
            owned(resources, metadata["ownerId"])
            preview.require(request["database"] == previous["database"], "An existing lifecycle cannot switch databases.")
            if action != "deploy":
                preview.require(contract.images(request) == contract.images(previous) and request["target"] == previous["target"],
                                "Status/destroy must identify the recorded deployment; stale image or target selections are rejected.")
        else:
            preview.require(not any(resources.values()), "Unmanaged project resources exist; restore their original state or choose another environment ID.")
            if action != "deploy":
                return result(request, "destroyed" if action == "destroy" else "absent", resources, daemon)
        if action == "status":
            return result(request, observed(request, resources), resources, daemon, metadata)
        if action == "deploy":
            state = preview.prepare(directory, request["deliveries"]["server"], request["deliveries"]["worker"],
                                    request["environmentId"], port=request["target"]["port"], registry=request["registry"],
                                    bootstrap=bootstrap, database=request["database"], postgres_image=request.get("postgresImage"),
                                    extension_deliveries=request.get("extensions", {}), extension_configuration=settings,
                                    extension_deployments=request.get('extensionDeployments', {}))
            if metadata is None:
                created = now or datetime.now(timezone.utc)
                metadata = {"schemaVersion": 1, "environmentId": request["environmentId"], "dockerDaemon": daemon,
                            "ownerId": str(uuid.uuid4()), "createdAt": created.isoformat()}
            metadata["request"] = request
            metadata["expiresAt"] = (datetime.fromisoformat(metadata["createdAt"]) + timedelta(hours=request["ttlHours"])).isoformat()
            state["deploymentOwnerId"] = metadata["ownerId"]
            preview.private_write(directory / "deployment.json", json.dumps(state, indent=2) + "\n")
            preview.private_write(path, json.dumps(metadata, indent=2) + "\n")
            # Docker volume creation is atomic; existing labels are never overwritten.
            docker("volume", "create", "--label", PROJECT + "=" + project, "--label", OWNER + "=" + metadata["ownerId"],
                   "--label", ENVIRONMENT + "=" + request["environmentId"], project + "-owner")
            owned(inventory(project), metadata["ownerId"])
            # Remove only inspected owned containers that were explicitly deselected. Retain their volumes/keys.
            selected_services = set(contract.images(request))
            for item in resources["containers"]:
                name = item["Config"]["Labels"].get("com.docker.compose.service", "")
                if name not in selected_services and name not in ('server', 'worker', 'postgres'):
                    import extension_deployments
                    historical = extension_deployments.images(state.get('extensionDeploymentHistory', {}))
                    preview.require(name.startswith('extension-') and name[len("extension-"):] in state.get("extensionKeys", {}) or name in historical,
                                    "Unexpected extension container; inspect ownership before removal.")
                    docker("stop", "--time", "20", item["Id"])
                    docker("rm", item["Id"])
            preview.compose(directory, "pull", capture=True)
            preview.compose(directory, "up", "--detach", "--wait", "--wait-timeout", "180", capture=True)
            if request["testProfile"] == "readiness" or request.get("extensions"):
                preview.verify(directory)
            resources = inventory(project)
            owned(resources, metadata["ownerId"])
            state_name = observed(request, resources)
            preview.require(state_name == "running", "Deployed resources are unhealthy or differ from the request.")
            return result(request, state_name, resources, daemon, metadata)
        # Only inspected, ownership-checked IDs are removed. The host itself is never removed.
        order = {"worker": 0, "server": 1, "postgres": 2}
        containers = sorted(resources["containers"], key=lambda item: order.get(
            item["Config"]["Labels"].get("com.docker.compose.service"), 3))
        for item in containers:
            docker("stop", "--time", "20", item["Id"])
            docker("rm", item["Id"])
        for network in resources["networks"]:
            docker("network", "rm", network["Id"])
        if request["dataMode"] == "reset":
            for volume in resources["volumes"]:
                docker("volume", "rm", volume["Name"])
            state_path = directory / 'deployment.json'
            if state_path.exists():
                import extension_deployments
                state = preview.validate_state(preview.read_json(state_path))
                extension_deployments.reset(directory, state)
                preview.private_write(state_path, json.dumps(state, indent=2) + '\n')
        return result(request, "destroyed", inventory(project), daemon, metadata)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="operation", required=True)
    plan = commands.add_parser("plan")
    plan.add_argument("--server-delivery", type=Path, required=True)
    plan.add_argument("--worker-delivery", type=Path, required=True)
    plan.add_argument("--environment-id", required=True)
    plan.add_argument("--provider", choices=sorted(contract.PROVIDERS), default="docker")
    plan.add_argument("--database", choices=("sqlite", "postgresql"), default="sqlite")
    plan.add_argument("--target", type=Path)
    plan.add_argument("--port", type=int)
    plan.add_argument("--ttl-hours", type=int, default=24)
    plan.add_argument("--data-mode", choices=("preserve", "reset"), default="preserve")
    plan.add_argument("--test-profile", choices=("none", "readiness"), default="readiness")
    plan.add_argument("--registry", default="ghcr.io")
    plan.add_argument("--postgres-image")
    for action in sorted(contract.OPERATIONS):
        command = commands.add_parser(action)
        command.add_argument("--request", type=Path, required=True)
        if action != "validate":
            command.add_argument("--state-root", type=Path)
        if action == "deploy":
            command.add_argument("--bootstrap-config", type=Path)
            command.add_argument("--extension-configuration", type=Path)
            command.add_argument("--reset-before-deploy", action="store_true")
        command.add_argument("--output", type=Path)
    plan.add_argument("--output", type=Path)
    args = parser.parse_args()
    if args.operation == "plan":
        preview.require(args.port is None or (args.provider == "docker" and args.target is None), "Port overrides are for inferred local Docker targets only.")
        target = preview.read_json(args.target) if args.target else None
        if target is None and args.provider == "docker":
            target = {"port": args.port if args.port is not None else 5080, "daemonId": docker("info", "--format", "{{.ID}}")}
        value = contract.plan(preview.read_json(args.server_delivery), preview.read_json(args.worker_delivery), args.environment_id,
                              args.database, args.provider, target,
                              args.ttl_hours, args.data_mode, args.test_profile, args.registry, args.postgres_image)
    else:
        with args.request.open(encoding="utf-8") as stream:
            request = contract.loads(stream.read(65537))
        bootstrap = getattr(args, "bootstrap_config", None)
        configuration = getattr(args, "extension_configuration", None)
        private_settings = None
        if configuration:
            preview.require(configuration.is_file(), "Expected a private extension configuration file.")
            with configuration.open(encoding="utf-8") as stream:
                private_settings = stream.read(16385)
        value = execute(args.operation, request, getattr(args, "state_root", None), preview.read_json(bootstrap) if bootstrap else None,
                        extension_configuration=private_settings, reset_before_deploy=getattr(args, 'reset_before_deploy', False))
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        preview.private_write(args.output, json.dumps(value, indent=2) + "\n")
    else:
        print(json.dumps(value))


if __name__ == "__main__":
    try:
        main()
    except (ValueError, RuntimeError) as error:
        print("Preview lifecycle failed: " + str(error), file=sys.stderr)
        raise SystemExit(1)
    except (subprocess.SubprocessError, OSError):
        print("Preview lifecycle failed. Validate the contract, ownership and trusted host state before retrying.", file=sys.stderr)
        raise SystemExit(1)
