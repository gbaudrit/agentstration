"""Versioned, credential-free deployment requests; independent of provider execution."""
import json
import re
import uuid

import preview
import extensions
from images import delivery_entry

PROVIDERS = {"docker", "azure-vm", "ssh-docker"}
OPERATIONS = {"deploy", "status", "destroy", "validate"}
AZURE_OS_PROFILE = "ubuntu-24.04"


def azure_target(request):
    # Legacy v1 Azure requests already meant this fixed Ubuntu profile.
    return {"osProfile": AZURE_OS_PROFILE, **request["target"]}


def fields(value, expected):
    preview.require(isinstance(value, dict) and set(value) == set(expected), "Unexpected or missing contract fields.")


def _delivery(receipt):
    entry = {"kind": "aep-extension", **delivery_entry(receipt)}
    keys = ["id", "kind", "imageName", "platform", "version"]
    if entry.get("kind") == "awp-worker":
        keys += ["runtimeKind", "capabilityVersion", "executionMaterialVersions", "awpVersion"]
    if entry["kind"] == "aep-extension":
        keys += ["extensionId", "aepVersion", "contributionKind", "contributionId"]
    return {"schemaVersion": 1, "component": {key: entry[key] for key in keys},
            **{key: receipt[key] for key in ("repository", "sourceSha", "digest")}}


def delivery(receipt):
    try:
        return _delivery(receipt)
    except (KeyError, TypeError, AttributeError) as error:
        raise ValueError("Invalid image delivery metadata.") from error


def images(request):
    selected = {"server": preview.image_record(request["deliveries"]["server"], "server", request["registry"]),
                "worker": preview.image_record(request["deliveries"]["worker"], "awp-worker", request["registry"])}
    for identifier, receipt in request.get("extensions", {}).items():
        selected[extensions.service(identifier)] = preview.image_record(receipt, "aep-extension", request["registry"])
    if request["database"] == "postgresql":
        selected["postgres"] = {"reference": request["postgresImage"], "platform": "linux/amd64"}
    return selected


def validate_azure_target(target):
    expected_target = {"subscriptionId", "resourceGroup", "region", "vmSize", "imageVersion"}
    if "osProfile" in target:
        expected_target.add("osProfile")
    fields(target, expected_target)
    preview.require(target.get("osProfile", AZURE_OS_PROFILE) == AZURE_OS_PROFILE,
                    "Only the qualified ubuntu-24.04 OS profile is supported.")
    try:
        uuid.UUID(target["subscriptionId"])
    except (ValueError, TypeError, AttributeError) as error:
        raise ValueError("Invalid Azure subscription identity.") from error
    preview.require(isinstance(target["region"], str) and re.fullmatch(r"[a-z][a-z0-9]{1,31}", target["region"])
                    and target["vmSize"] in ("Standard_B2ms", "Standard_B4ms"), "Invalid Azure region or VM size.")
    preview.require(isinstance(target["resourceGroup"], str)
                    and re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_-]{0,79}", target["resourceGroup"]), "Invalid dedicated Azure resource group.")
    preview.require(isinstance(target["imageVersion"], str) and re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+", target["imageVersion"]),
                    "Azure requires an exact Ubuntu image version, never latest.")


def _validate(request):
    preview.require(isinstance(request, dict), "Expected a deployment request object.")
    expected = {"schemaVersion", "environmentId", "provider", "sourceSha", "database", "registry", "deliveries",
                "ttlHours", "dataMode", "testProfile", "target"}
    if request.get("database") == "postgresql":
        expected.add("postgresImage")
    if request.get("schemaVersion") in (2, 4):
        expected.add("extensions")
    fields(request, expected)
    # Version 3 is reserved by the parallel GUID deployment-profile change (#1422).
    preview.require(type(request["schemaVersion"]) is int and request["schemaVersion"] in (1, 2, 4), "Unsupported deployment contract version.")
    preview.require(isinstance(request["environmentId"], str) and preview.ENVIRONMENT_ID.fullmatch(request["environmentId"]),
                    "Invalid deployment environment ID.")
    preview.require(request["provider"] in PROVIDERS and request["database"] in ("sqlite", "postgresql"),
                    "Unsupported provider or database profile.")
    preview.require(type(request["ttlHours"]) is int and 1 <= request["ttlHours"] <= 168, "TTL must be 1–168 hours.")
    preview.require(request["dataMode"] in ("preserve", "reset") and request["testProfile"] in ("none", "readiness"),
                    "Unsupported data mode or test profile.")
    fields(request["deliveries"], ("server", "worker"))
    optional = request.get("extensions", {})
    preview.require(isinstance(optional, dict) and (request["schemaVersion"] != 2 or bool(optional)), "Version 2 requires selected extensions.")
    extensions.validate_ids(list(optional))
    identities = []
    for identifier, receipt in optional.items():
        preview.require(receipt["component"]["id"] == identifier, "Extension key differs from delivery ID.")
        identities.append(receipt["component"]["extensionId"].casefold())
    preview.require(len(identities) == len(set(identities)), "Duplicate AEP identities.")
    try:
        for receipt in [*request["deliveries"].values(), *optional.values()]:
            preview.require(type(receipt.get("schemaVersion")) is int and receipt == delivery(receipt),
                            "Deployment requests accept only canonical public delivery metadata.")
        selected = images(request)
    except (KeyError, TypeError, AttributeError) as error:
        raise ValueError("Invalid image delivery metadata.") from error
    preview.require(isinstance(request['sourceSha'], str) and preview.SHA.fullmatch(request['sourceSha']), 'Requested revision must be an exact source SHA.')
    if request['schemaVersion'] in (1, 2):
        preview.require(request["sourceSha"] == selected["server"]["sourceSha"], "Legacy revision must equal the exact server source SHA.")
    worker = selected["worker"]
    preview.require(worker["runtimeKind"] == "microsoft-agent-framework" and worker["awpVersion"] == "1.0"
                    and worker["capabilityVersion"] == "1.0"
                    and isinstance(worker["executionMaterialVersions"], list)
                    and bool(worker["executionMaterialVersions"])
                    and set(worker["executionMaterialVersions"]) <= {"1.0", "1.1"}, "Unsupported Worker protocol metadata.")
    if request["database"] == "postgresql":
        preview.require(preview.POSTGRES_IMAGE.fullmatch(request["postgresImage"]), "PostgreSQL requires an exact supported image digest.")
    target = request["target"]
    if request["provider"] == "docker":
        fields(target, ("port", "daemonId"))
        preview.require(type(target["port"]) is int and (target["port"] == 0 or 1024 <= target["port"] <= 65535), "Invalid Docker host port.")
        preview.require(isinstance(target["daemonId"], str) and re.fullmatch(r"[a-zA-Z0-9:_-]{1,128}", target["daemonId"]),
                        "An exact trusted Docker daemon identity is required.")
    elif request["provider"] == "azure-vm":
        validate_azure_target(target)
    else:
        fields(target, ("host", "sshPort", "user", "hostKeyFingerprint", "port"))
        preview.require(isinstance(target["host"], str) and re.fullmatch(r"[a-zA-Z0-9][a-zA-Z0-9.-]{0,252}", target["host"])
                        and isinstance(target["user"], str) and re.fullmatch(r"[a-z_][a-z0-9_-]{0,31}", target["user"]), "Invalid SSH host or user.")
        preview.require(type(target["sshPort"]) is int and 1 <= target["sshPort"] <= 65535
                        and type(target["port"]) is int and 1024 <= target["port"] <= 65535
                        and isinstance(target["hostKeyFingerprint"], str)
                        and re.fullmatch(r"SHA256:[A-Za-z0-9+/]{43}", target["hostKeyFingerprint"]), "Invalid SSH ports or host-key fingerprint.")
    return request


def validate(request):
    try:
        return _validate(request)
    except (KeyError, TypeError, AttributeError) as error:
        raise ValueError("Invalid deployment contract types or image metadata.") from error


def plan(server, worker, environment_id, database="sqlite", provider="docker", target=None, ttl_hours=24,
         data_mode="preserve", test_profile="readiness", registry="ghcr.io", postgres_image=None, extension_deliveries=None, source_sha=None):
    preview.require(target is not None, "A provider-specific target is required.")
    request = {"schemaVersion": 1, "environmentId": environment_id, "provider": provider,
               "sourceSha": server["sourceSha"], "database": database, "registry": registry,
               "deliveries": {"server": delivery(server), "worker": delivery(worker)}, "ttlHours": ttl_hours,
               "dataMode": data_mode, "testProfile": test_profile, "target": target}
    if extension_deliveries:
        request.update(schemaVersion=2, extensions={key: delivery(value) for key, value in sorted(extension_deliveries.items())})
    if source_sha is not None and source_sha != server['sourceSha']:
        request.update(schemaVersion=4, sourceSha=source_sha, extensions=request.get('extensions', {}))
    if database == "postgresql":
        request["postgresImage"] = postgres_image or preview.read_json(preview.ROOT / "deploy/preview/postgresql.json")["reference"]
    else:
        preview.require(postgres_image is None, "A PostgreSQL image requires the PostgreSQL profile.")
    return validate(request)


def operation(action, request):
    preview.require(action in OPERATIONS, "Unsupported lifecycle operation.")
    validate(request)
    if action in ("deploy", "validate"):
        extensions.trusted(request.get("extensions", {}))
    preview.require(action != "deploy" or request["dataMode"] == "preserve", "Reset is explicit destroy only; destroy with reset before redeploying.")
    preview.require(action == "validate" or request["provider"] in ("docker", "azure-vm"),
                    "SSH execution remains #1288; it never falls back to another provider.")


def parse(text):
    preview.require(len(text.encode("utf-8")) <= 65536, "Deployment requests are limited to 64 KiB.")

    def unique(pairs):
        preview.require(len(pairs) == len(dict(pairs)), "Duplicate JSON contract fields are not accepted.")
        return dict(pairs)

    return json.loads(text, object_pairs_hook=unique)


def loads(text):
    return validate(parse(text))
