#!/usr/bin/env python3
"""Prepare and run a local, digest-pinned preview outside the product Runtime."""
import argparse
import json
import os
from pathlib import Path
import re
import secrets
import subprocess
import sys
import urllib.request
import uuid

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts/packaging"))
from images import DIGEST, delivery_entry, valid_version

SHA = re.compile(r"[0-9a-f]{40}")
ENVIRONMENT_ID = re.compile(r"[a-z][a-z0-9-]{0,39}")
REGISTRY = re.compile(r"ghcr\.io|localhost:[0-9]{1,5}")
POSTGRES_IMAGE = re.compile(r"docker\.io/library/postgres:17@sha256:[0-9a-f]{64}")


def require(condition, message):
    if not condition:
        raise ValueError(message)


def read_json(path):
    return json.loads(Path(path).read_text(encoding="utf-8"))


def image_record(delivery, kind, registry):
    require(delivery.get("schemaVersion") == 1 and SHA.fullmatch(delivery.get("sourceSha", ""))
            and DIGEST.fullmatch(delivery.get("digest", "")), "Expected an exact source SHA and OCI delivery digest.")
    entry = delivery_entry(delivery)
    if kind == "aep-extension":
        import extensions
        require(entry.get("kind", "aep-extension") == kind and extensions.ID.fullmatch(entry.get("id", ""))
                and re.fullmatch(r"extension-[a-z][a-z0-9-]{0,31}", entry.get("imageName", ""))
                and re.fullmatch(r"[A-Za-z][A-Za-z0-9_.-]{0,127}", entry.get("extensionId", ""))
                and "__" not in entry["extensionId"] and entry.get("aepVersion") == "agentstration.io/v1"
                and entry.get("contributionKind") in ("modelProviders", "tools", "sourceProviders", "dataSourceProfileBundles", "schemaFormats", "connectedServiceTypes", "connectedServiceOperationTypes", "storageTypes")
                and isinstance(entry.get("contributionId"), str) and re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_.-]{0,127}", entry["contributionId"]),
                "Unsupported AEP delivery identity/protocol/contribution metadata.")
        expected_id, expected_name = entry["id"], entry["imageName"]
    else:
        expected_id, expected_name = ("server", "server") if kind == "server" else ("maf-worker", "worker-maf")
    require(entry.get("kind", "aep-extension") == kind and entry.get("id") == expected_id and entry.get("imageName") == expected_name
            and entry.get("platform") == "linux/amd64" and valid_version(entry.get("version")),
            "The delivery does not identify a supported OCI component.")
    repository = delivery.get("repository", "").lower()
    require(re.fullmatch(r"[a-z0-9_.-]+/[a-z0-9_.-]+", repository), "Expected an owner/repository image source.")
    require(REGISTRY.fullmatch(registry) and (registry == "ghcr.io" or 1024 <= int(registry.split(":")[1]) <= 65535),
            "Use private GHCR or a loopback-only staging registry.")
    owner, name = repository.split("/")
    record = {"reference": f"{registry}/{owner}/{name}-{entry['imageName']}@{delivery['digest']}",
              "sourceSha": delivery["sourceSha"], "version": entry["version"], "platform": entry["platform"]}
    if kind == "awp-worker":
        record.update({key: entry[key] for key in ("runtimeKind", "capabilityVersion", "executionMaterialVersions", "awpVersion")})
    if kind == "aep-extension":
        record.update({key: entry[key] for key in ("extensionId", "aepVersion", "contributionKind", "contributionId")})
    return record


def private_write(path, value):
    require(not path.is_symlink(), "Preview state files must not be symbolic links.")
    if path.exists():
        path.chmod(0o600)
    with open(path, "w", encoding="utf-8", opener=lambda name, flags: os.open(name, flags, 0o600)) as stream:
        stream.write(value)


def validate_state(state):
    require(state.get("schemaVersion") == 1 and state.get("database") in ("sqlite", "postgresql")
            and ENVIRONMENT_ID.fullmatch(state.get("environmentId", "")), "Invalid preview state.")
    require(type(state.get("port")) is int and (state["port"] == 0 or 1024 <= state["port"] <= 65535),
            "Choose port 0 for a disposable test or a port between 1024 and 65535.")
    for name in ("instanceId", "workerId", "credentialId"):
        uuid.UUID(state[name])
    if "deploymentOwnerId" in state:
        uuid.UUID(state["deploymentOwnerId"])
    expected = {"server", "worker", "postgres"} if state["database"] == "postgresql" else {"server", "worker"}
    optional = {name for name in state["images"] if name.startswith("extension-")}
    import extensions
    extensions.validate_ids(sorted(name[len("extension-"):] for name in optional))
    require(set(state["images"]) == expected | optional, "Preview images must match its selected database.")
    if state["database"] == "postgresql":
        postgres = state["images"]["postgres"]
        require(POSTGRES_IMAGE.fullmatch(postgres["reference"]) and postgres["platform"] == "linux/amd64",
                "PostgreSQL requires an official version 17 image pinned by digest.")
    for name in ("server", "worker", *optional):
        image = state["images"][name]
        require(re.fullmatch(r"(?:ghcr\.io|localhost:[0-9]{1,5})/[a-z0-9_.-]+/[a-z0-9_.-]+@sha256:[0-9a-f]{64}", image["reference"])
                and SHA.fullmatch(image["sourceSha"]) and valid_version(image["version"]), "Invalid pinned preview image.")
    require(state["sourceSha"] == state["images"]["server"]["sourceSha"], "Preview revision differs from its server image.")
    return state


def prepare(directory, server_delivery, worker_delivery, environment_id, port=5080, registry="ghcr.io", bootstrap=None,
            database=None, postgres_image=None, extension_deliveries=None):
    require(ENVIRONMENT_ID.fullmatch(environment_id), "Environment ID must use 1–40 lowercase letters, digits or hyphens, starting with a letter.")
    require(bootstrap is None or isinstance(bootstrap, dict), "Bootstrap configuration must be a JSON object.")
    images = {"server": image_record(server_delivery, "server", registry),
              "worker": image_record(worker_delivery, "awp-worker", registry)}
    directory = Path(directory).resolve()
    require(not directory.is_relative_to(ROOT) or any(part.startswith(".agentstration") for part in directory.relative_to(ROOT).parts),
            "Keep credential state outside the repository or inside an ignored .agentstration directory.")
    state_path = directory / "deployment.json"
    require(not state_path.is_symlink(), "Preview state files must not be symbolic links.")
    if state_path.exists():
        state = validate_state(read_json(state_path))
        require(state["environmentId"] == environment_id, "Existing state belongs to another preview; use another directory.")
        require(database is None or database == state["database"],
                "An existing preview cannot switch databases; use a new environment and state directory.")
        files = ["worker.key", "server-configuration.json", "deployment.json"]
        if state["database"] == "postgresql":
            files.append("postgres.password")
        require(all((directory / name).is_file() and not (directory / name).is_symlink() for name in files),
                "Restore the existing preview state; credentials must not be silently regenerated.")
        configuration = read_json(directory / "server-configuration.json")
    else:
        require(not directory.exists() or not any(directory.iterdir()), "Refusing to overwrite an unrelated nonempty directory.")
        state = {"schemaVersion": 1, "environmentId": environment_id, "database": database or "sqlite",
                 **{name: str(uuid.uuid4()) for name in ("instanceId", "workerId", "credentialId")}}
        configuration = {"Agentstration": {"Bootstrap": {"InitialBootstrapEnabled": False, "Path": "/app/bootstrap/profiles"}}}
    if state["database"] == "postgresql":
        previous = state.get("images", {}).get("postgres")
        images["postgres"] = {"reference": postgres_image or (previous or read_json(ROOT / "deploy/preview/postgresql.json"))["reference"],
                              "platform": "linux/amd64"}
    else:
        require(postgres_image is None, "A PostgreSQL image can only be selected for the PostgreSQL profile.")
    for identifier, receipt in (extension_deliveries or {}).items():
        import extensions
        images[extensions.service(identifier)] = image_record(receipt, "aep-extension", registry)
    state.update(port=port, images=images, sourceSha=images["server"]["sourceSha"])
    validate_state(state)
    if bootstrap is not None:
        configuration["Agentstration"]["Bootstrap"] = {"Path": "/app/bootstrap/profiles", **bootstrap}
    configuration["Agentstration"].update(InstanceId=state["instanceId"], AwpWorkerTrust={
        "Enabled": True, "InstanceId": state["instanceId"], "Credentials": [{"WorkerId": state["workerId"],
        "CredentialId": state["credentialId"], "SharedKeyFile": "/run/secrets/awp_key"}]})
    directory.mkdir(parents=True, exist_ok=True, mode=0o700)
    directory.chmod(0o700)
    if not state_path.exists():
        private_write(directory / "worker.key", secrets.token_urlsafe(48))
        if state["database"] == "postgresql":
            private_write(directory / "postgres.password", secrets.token_urlsafe(48))
    import extensions
    extensions.prepare(directory, state, configuration, extension_deliveries or {})
    if state["database"] == "postgresql":
        password = (directory / "postgres.password").read_text()
        require(re.fullmatch(r"[A-Za-z0-9_-]{64}", password), "Restore the original generated PostgreSQL password file.")
        (directory / "postgres.password").chmod(0o600)
        configuration["ConnectionStrings"] = {"Agentstration":
            "Host=postgres;Port=5432;Database=agentstration;Username=agentstration;Password=" + password}
    private_write(directory / "server-configuration.json", json.dumps(configuration, indent=2) + "\n")
    private_write(state_path, json.dumps(state, indent=2) + "\n")
    return state


def compose_command(directory):
    directory = Path(directory).resolve()
    state = validate_state(read_json(directory / "deployment.json"))
    environment = {key: value for key, value in os.environ.items() if not key.startswith("COMPOSE_")}
    environment.update(PREVIEW_STATE_DIRECTORY=str(directory), PREVIEW_ENVIRONMENT_ID=state["environmentId"],
        PREVIEW_PORT=str(state["port"]), PREVIEW_INSTANCE_ID=state["instanceId"],
        PREVIEW_WORKER_ID=state["workerId"], PREVIEW_CREDENTIAL_ID=state["credentialId"])
    environment["PREVIEW_OWNER_ID"] = state.get("deploymentOwnerId", "unmanaged")
    for name, image in state["images"].items():
        environment["PREVIEW_" + name.upper() + "_IMAGE"] = image["reference"]
        if name != "postgres":
            environment["PREVIEW_" + name.upper() + "_SHA"] = image["sourceSha"]
    command = ["docker", "compose", "--project-name", "preview-" + state["environmentId"], "--env-file", "/dev/null",
               "--file", str(ROOT / "deploy/preview/compose.yml")]
    if state["database"] == "postgresql":
        command += ["--file", str(ROOT / "deploy/preview/postgresql.yml")]
    import extensions
    optional = extensions.overlay(directory, state)
    if optional["services"]:
        path = directory / "extensions.compose.json"
        private_write(path, json.dumps(optional) + "\n")
        command += ["--file", str(path)]
    return command, environment


def compose(directory, *arguments, capture=False):
    command, environment = compose_command(directory)
    return subprocess.run([*command, *arguments], env=environment, check=True, text=True,
                          stdout=subprocess.PIPE if capture else None).stdout


def server_address(directory):
    port = compose(directory, "port", "server", "8080", capture=True).strip().rsplit(":", 1)[1]
    return "http://127.0.0.1:" + port


def verify(directory):
    state = validate_state(read_json(Path(directory) / "deployment.json"))
    for service, expected in state["images"].items():
        container_id = compose(directory, "ps", "--quiet", service, capture=True).strip()
        require(container_id and "\n" not in container_id, "Expected one preview container per service.")
        container = json.loads(subprocess.check_output(["docker", "inspect", container_id], text=True))[0]
        require(container["Config"]["Image"] == expected["reference"] and container["State"].get("Health", {}).get("Status") == "healthy",
                "Preview container is unready or differs from its recorded digest.")
        if service == "server":
            require(container.get("HostConfig", {}).get("PortBindings", {}).get("8080/tcp") ==
                    [{"HostIp": "127.0.0.1", "HostPort": str(state["port"])}],
                    "Preview application ports must bind only the requested guest loopback port.")
        if service != "postgres":
            image = json.loads(subprocess.check_output(["docker", "image", "inspect", container["Image"]], text=True))[0]
            labels = image["Config"]["Labels"]
            require(labels.get("org.opencontainers.image.revision") == expected["sourceSha"]
                    and labels.get("org.opencontainers.image.version") == expected["version"], "Preview OCI source/version mismatch.")
        else:
            require(not any((container["HostConfig"].get("PortBindings") or {}).values())
                    and not any(value.startswith("POSTGRES_PASSWORD=") for value in container["Config"]["Env"]),
                    "PostgreSQL must not publish a host port or receive a plaintext password environment variable.")
        if service.startswith("extension-"):
            bindings = container["HostConfig"].get("PortBindings") or {}
            require(set(bindings) == {"8080/tcp"} and len(bindings["8080/tcp"]) == 1
                    and bindings["8080/tcp"][0]["HostIp"] == "127.0.0.1", "Extension ports must bind only guest loopback.")
            mounts = {mount["Destination"]: mount for mount in container["Mounts"] if mount["Type"] != "tmpfs"}
            require(set(mounts) == {"/run/secrets/aep-key", "/var/lib/aep"}
                    and not mounts["/run/secrets/aep-key"]["RW"] and mounts["/var/lib/aep"]["RW"]
                    and not any(value.lower().startswith("connectionstrings") for value in container["Config"]["Env"]),
                    "Extensions may receive only their own enrollment key and state volume.")
        if service == "worker":
            require({mount["Destination"] for mount in container["Mounts"]} == {"/run/secrets/awp_key"}
                    and all(not mount["RW"] for mount in container["Mounts"])
                    and not any(value.lower().startswith("connectionstrings") for value in container["Config"]["Env"]),
                    "Worker must receive only its read-only credential and no database settings.")
    base = server_address(directory)
    for path in ("/health/live", "/health/ready"):
        with urllib.request.urlopen(base + path, timeout=10) as response:
            require(response.status == 200, "Server is not ready.")
    import extensions
    verified = extensions.verify(directory, state)
    return {"extensionsVerified": verified, "environmentId": state["environmentId"], "database": state["database"], "images": state["images"], "url": base}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="action", required=True)
    initialize = sub.add_parser("prepare")
    initialize.add_argument("--server-delivery", required=True, type=Path)
    initialize.add_argument("--worker-delivery", required=True, type=Path)
    initialize.add_argument("--environment-id", required=True)
    initialize.add_argument("--port", type=int, default=5080)
    initialize.add_argument("--registry", default="ghcr.io")
    initialize.add_argument("--bootstrap-config", type=Path)
    initialize.add_argument("--database", choices=("sqlite", "postgresql"))
    initialize.add_argument("--postgres-image", help="Official PostgreSQL 17 image reference pinned by digest")
    for command in (initialize, sub.add_parser("compose"), sub.add_parser("check")):
        command.add_argument("--directory", required=True, type=Path)
    sub.choices["compose"].add_argument("arguments", nargs=argparse.REMAINDER)
    args = parser.parse_args()
    if args.action == "prepare":
        prepare(args.directory, read_json(args.server_delivery), read_json(args.worker_delivery), args.environment_id,
                args.port, args.registry, read_json(args.bootstrap_config) if args.bootstrap_config else None,
                args.database, args.postgres_image)
    elif args.action == "compose":
        arguments = args.arguments[1:] if args.arguments[:1] == ["--"] else args.arguments
        compose(args.directory, *arguments)
    else:
        print(json.dumps(verify(args.directory)))


if __name__ == "__main__":
    main()
