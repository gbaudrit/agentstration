#!/usr/bin/env python3
"""Validate an exact AEP extension image against a real SQLite authority in isolated Compose."""
import argparse
import base64
import hmac
import json
import os
import re
from pathlib import Path
import secrets
import sqlite3
import subprocess
import tempfile
import time
import urllib.error
import urllib.request
import uuid

import images
import smoke
from images import delivery_entry

ROOT = Path(__file__).resolve().parents[2]
EXTENSION_ENDPOINT = "http://extension:8080/"


def require(condition, message):
    if not condition:
        raise ValueError(message)


def authority_environment(delivery):
    """Configure trust for the selected AEP identity without fixed provider names."""
    entry = delivery_entry(delivery)
    require(entry.get("kind", "aep-extension") == "aep-extension", "Enrollment smoke requires an AEP extension.")
    extension_id = entry.get("extensionId", "")
    require(re.fullmatch(r"[A-Za-z][A-Za-z0-9_.-]*", extension_id) and "__" not in extension_id,
            "Invalid AEP extension configuration identity.")
    prefix = "Agentstration__Extensions__" + extension_id
    return {prefix + "__EnrollmentMode": "SharedKeyFile",
            prefix + "__SharedKeyFile__Path": "/run/secrets/aep-key"}


def enrollment_snapshot(database, delivery, instance_id):
    """Read only technical enrollment state, never Secret records or payloads in logs."""
    with sqlite3.connect(database.resolve().as_uri() + "?mode=ro", uri=True, timeout=5) as connection:
        rows = connection.execute(
            "SELECT Kind, Payload FROM ControlPlaneResources WHERE Kind IN (?, ?)",
            ("AepEnrollmentRequest", "ExtensionRegistration")).fetchall()
    enrollments = [json.loads(payload) for kind, payload in rows if kind == "AepEnrollmentRequest"]
    registrations = [json.loads(payload) for kind, payload in rows if kind == "ExtensionRegistration"]
    require(len(enrollments) == len(registrations) == 1, f"Expected one enrollment and one registration; found {len(enrollments)} and {len(registrations)}.")
    enrollment, registration = enrollments[0], registrations[0]
    definition = enrollment["definition"]
    require(definition["instanceId"] == instance_id, "Authority enrollment does not match the persisted instance.")
    require(definition["extensionId"] == delivery_entry(delivery)["extensionId"]
            and definition["extensionVersion"] == delivery_entry(delivery)["version"], "Authority identity/version mismatch.")
    require(definition["state"] == "available" and definition["targetScopeRef"] == "/instance",
            f"Enrollment state={definition['state']}, outcome={definition.get('outcome')}; expected available at /instance.")
    require(definition["enrollmentMode"] == "sharedKeyFile", "Wrong enrollment method.")
    require(registration["metadata"]["name"] == definition["registrationName"]
            and registration["scopeRef"] == "/instance", "Registration is not bound to the enrollment scope.")
    properties = registration["definition"]
    require(properties["enabled"] and properties["expectedExtensionId"] == definition["extensionId"]
            and properties["enrollmentMode"] == "sharedKeyFile"
            and properties["authenticationMode"] == "staticBearer"
            and properties["endpoint"] == definition["endpoint"] == EXTENSION_ENDPOINT,
            "Authority registration has unexpected identity, endpoint or authentication.")
    return {"instanceId": instance_id, "enrollmentUid": enrollment["uid"],
            "enrollmentGeneration": enrollment["generation"], "registrationUid": registration["uid"],
            "registrationGeneration": registration["generation"], "registrationName": definition["registrationName"]}


def request(base, path, body=None, headers=None, expected=200):
    value = None if body is None else json.dumps(body, separators=(",", ":")).encode()
    call = urllib.request.Request(base + path, data=value, headers=headers or {})
    if value is not None:
        call.add_header("Content-Type", "application/json")
    try:
        with urllib.request.urlopen(call, timeout=10) as response:
            status, payload, content_type = response.status, response.read(), response.headers.get("Content-Type", "")
    except urllib.error.HTTPError as error:
        status, payload, content_type = error.code, error.read(), error.headers.get("Content-Type", "")
    require(status == expected, f"Unexpected HTTP status for {path}: {status}, expected {expected}.")
    return json.loads(payload) if payload and "json" in content_type else None


def announce(authority, manifest, instance_id, key, expected=200, endpoint=EXTENSION_ENDPOINT):
    # Match AEP's documented HMAC JSON payload/property order, including nullable pairingUri.
    identity = {field: manifest["extension"].get(field) for field in ("id", "name", "version", "description")}
    announcement = {"instanceId": instance_id, "extension": identity, "endpoint": endpoint,
                    "pairingUri": None, "method": "sharedKeyFile"}
    timestamp = int(time.time())
    payload = json.dumps({"timestamp": timestamp, "announcement": announcement}, separators=(",", ":")).encode()
    signature = base64.b64encode(hmac.digest(key.encode(), payload, "sha256")).decode()
    return request(authority, "/api/aep/enrollments/announce", announcement,
                   {"X-AEP-Enrollment-Timestamp": str(timestamp), "X-AEP-Enrollment-Signature": signature}, expected)


def run(delivery_path, image, authority_image, output):
    delivery = json.loads(delivery_path.read_text(encoding="utf-8"))
    trust = authority_environment(delivery)
    require(images.DIGEST.fullmatch(delivery["digest"]) and image.endswith("@" + delivery["digest"]),
            "Enrollment smoke must consume the exact OCI delivery digest.")
    subprocess.run(["docker", "pull", image], check=True)
    authority_metadata = json.loads(images.output(["docker", "image", "inspect", authority_image]))[0]
    require(authority_metadata["Config"]["Labels"].get("org.opencontainers.image.revision") == delivery["sourceSha"],
            "The authority fixture must be built from the same source revision as the delivery.")
    authority_id = authority_metadata["Id"]
    project = "aep-enrollment-smoke-" + uuid.uuid4().hex[:12]
    with tempfile.TemporaryDirectory(prefix=project + "-") as temporary:
        directory = Path(temporary)
        for name in ("authority", "extension"):
            (directory / name).mkdir()
        (directory / "authority-aep.env").write_text(
            "".join(name + "=" + value + "\n" for name, value in trust.items()), encoding="utf-8")
        (directory / "extension.env").write_text(
            "".join(name + "=" + value + "\n" for name, value in images.smoke_environment(delivery_entry(delivery)).items()),
            encoding="utf-8")
        key = secrets.token_urlsafe(48)
        with open(directory / "key", "w", opener=lambda path, flags: os.open(path, flags, 0o600)) as stream:
            stream.write(key)
        environment = {name: value for name, value in os.environ.items() if not name.startswith("COMPOSE_")}
        environment.update(SMOKE_UID=str(os.getuid()), SMOKE_GID=str(os.getgid()), SMOKE_DIRECTORY=str(directory), SMOKE_BOOTSTRAP_DIRECTORY=str(ROOT / "deploy/bootstrap/profiles"),
                           SMOKE_AUTHORITY_IMAGE=authority_id, SMOKE_EXTENSION_IMAGE=image,
                           SMOKE_ADMIN_PASSWORD=secrets.token_urlsafe(40))
        command = ["docker", "compose", "--project-name", project, "--file", str(ROOT / "scripts/packaging/aep-extension.smoke.compose.yml")]

        def compose(*arguments, capture=False):
            return subprocess.run([*command, *arguments], env=environment, check=True, text=True,
                                  stdout=subprocess.PIPE if capture else None).stdout

        def address(service):
            port = compose("port", service, "8080", capture=True).strip().rsplit(":", 1)[1]
            return "http://127.0.0.1:" + port

        before = None
        try:
            for iteration in range(2):
                compose("up", "--detach", "--no-build", "authority")
                authority = address("authority")
                smoke.wait_for_health(authority, 120)
                deadline = time.monotonic() + 120
                while True:
                    try:
                        request(authority, "/health/ready")
                        break
                    except (ValueError, urllib.error.URLError, TimeoutError):
                        require(time.monotonic() < deadline, "Authority did not become ready.")
                        time.sleep(1)
                compose("up", "--detach", "--no-build", "extension")
                endpoint = address("extension")
                smoke.wait_for_health(endpoint)
                request(endpoint, "/.well-known/aep", expected=401)
                request(endpoint, "/.well-known/aep", headers={"Authorization": "Bearer " + secrets.token_urlsafe(48)}, expected=401)
                manifest = request(endpoint, "/.well-known/aep", headers={"Authorization": "Bearer " + key})
                smoke.check_manifest(manifest, delivery)
                instance_id = str(uuid.UUID((directory / "extension/instance").read_text().strip()))
                deadline = time.monotonic() + 120
                while True:
                    try:
                        snapshot = enrollment_snapshot(directory / "authority/control-plane.db", delivery, instance_id)
                        break
                    except (ValueError, sqlite3.OperationalError) as error:
                        require(time.monotonic() < deadline, f"Authority did not persist an available AEP enrollment: {error}")
                        time.sleep(1)
                # Prove idempotency of a signed reannouncement; an invalid proof must be rejected even for an existing ID.
                response = announce(authority, manifest, instance_id, key)
                require(response["requestId"] == instance_id and response["state"].lower() == "available",
                        "Signed reannouncement did not reuse the available enrollment.")
                announce(authority, manifest, instance_id, secrets.token_urlsafe(48), expected=401)
                require(snapshot == enrollment_snapshot(directory / "authority/control-plane.db", delivery, instance_id),
                        "Reannouncement changed enrollment or registration identity/generation.")
                if before is None:
                    before = snapshot
                    # Remove processes and networks, retaining only the explicitly mounted state.
                    compose("down", "--timeout", "20")
                else:
                    require(before == snapshot, "Container recreation changed installation, registration or scope assignment.")
                print(f"PASS AEP {'startup' if iteration == 0 else 'recreation'}: authenticated discovery, available enrollment and stable identity.", flush=True)
            report = {"reference": image, "sourceSha": delivery["sourceSha"], "version": delivery_entry(delivery)["version"],
                      "catalogueId": delivery_entry(delivery)["id"], "extensionId": delivery_entry(delivery)["extensionId"],
                      "authorityImageId": authority_id, "aepVersion": manifest["protocolVersion"],
                      "platform": delivery_entry(delivery)["platform"], "scopeRef": "/instance",
                      "storage": "Sqlite", "enrollmentMode": "SharedKeyFile",
                      "authenticatedDiscovery": True, "invalidBearerRejected": True, "invalidAnnouncementProofRejected": True,
                      "signedReannouncementIdempotent": True, "containerRecreationPreservesIdentity": True, **before}
            output.parent.mkdir(parents=True, exist_ok=True)
            output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
        except Exception:
            # Emit only recognized technical failure categories, never raw logs/configuration.
            logs = subprocess.run([*command, "logs", "--no-log-prefix", "--tail", "100"], env=environment,
                                  capture_output=True, text=True, check=False).stdout
            statuses = re.findall(r"Response status code does not indicate success: ([0-9]{3})", logs)
            reasons = re.findall(r"Automatic AEP enrollment .*? could not complete: ([a-z_]+)", logs)
            print("Enrollment diagnostics:", json.dumps({"announcementHttpFailures": statuses,
                  "automaticEnrollmentFailures": reasons, "connectionRefused": "Connection refused" in logs,
                  "dnsFailure": "Name or service not known" in logs}), flush=True)
            raise
        finally:
            # Do not dump container logs/configuration or archive key files and generated stores.
            compose("down", "--volumes", "--remove-orphans", "--timeout", "20")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--delivery", required=True, type=Path)
    parser.add_argument("--image", required=True)
    parser.add_argument("--authority-image", required=True, help="Locally built server test fixture; never published")
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    run(args.delivery, args.image, args.authority_image, args.output)


if __name__ == "__main__":
    main()
