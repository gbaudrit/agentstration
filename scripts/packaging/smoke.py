#!/usr/bin/env python3
"""Smoke a staged OCI image without any cloud credential or model request."""
import argparse
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

from images import delivery_entry, smoke_environment


def check_manifest(manifest, delivery):
    entry = delivery_entry(delivery)
    if manifest.get("protocolVersion") != entry["aepVersion"]:
        raise ValueError("AEP protocol version does not match the image catalogue.")
    identity = manifest["extension"]
    if identity["id"] != entry["extensionId"] or identity["version"] != entry["version"]:
        raise ValueError("Published AEP identity/version does not match the extension release.")
    expected = entry.get("contributions", {entry["contributionKind"]: [entry["contributionId"]]})
    for kind, identities in expected.items():
        actual = {contribution["id"] for contribution in manifest.get("contributions", {}).get(kind, [])}
        if not set(identities).issubset(actual):
            raise ValueError("Expected extension contribution is missing.")


def wait_for_health(base, timeout_seconds=60):
    deadline = time.monotonic() + timeout_seconds
    while True:
        try:
            with urllib.request.urlopen(base + "/health", timeout=3) as response:
                if response.status != 200:
                    raise ValueError("Image is not healthy.")
            return
        except (urllib.error.URLError, TimeoutError, ConnectionError):
            # Docker's published port can accept TCP before the application is listening.
            if time.monotonic() >= deadline:
                raise ValueError(f"Image did not become healthy within {timeout_seconds} seconds.") from None
            time.sleep(1)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--delivery", required=True, type=Path)
    parser.add_argument("--image", required=True, help="Image staged in the disposable local registry")
    args = parser.parse_args()
    delivery = json.loads(args.delivery.read_text(encoding="utf-8"))
    if not args.image.endswith("@" + delivery["digest"]):
        raise ValueError("Smoke must consume the exact OCI delivery digest.")
    subprocess.run(["docker", "pull", args.image], check=True)
    name = "extension-smoke-" + uuid.uuid4().hex
    with tempfile.TemporaryDirectory(prefix="extension-smoke-") as temporary:
        key = secrets.token_urlsafe(48)
        key_path = Path(temporary) / "key"
        with open(key_path, "w", opener=lambda path, flags: os.open(path, flags, 0o600)) as output:
            output.write(key)
        configuration = []
        for setting, value in smoke_environment(delivery_entry(delivery)).items():
            configuration.extend(["--env", setting + "=" + value])
        try:
            subprocess.run(["docker", "run", "--detach", "--name", name,
                            "--user", f"{os.getuid()}:{os.getgid()}", "--publish", "127.0.0.1::8080",
                            "--mount", f"type=bind,source={key_path},target=/run/secrets/aep-key,readonly",
                            "--env", "Aep__EnrollmentMode=SharedKeyFile",
                            "--env", "Aep__SharedKeyFile__Path=/run/secrets/aep-key", *configuration, args.image], check=True)
            for iteration in range(2):
                # Docker can allocate a new ephemeral host port when the container restarts.
                container = json.loads(subprocess.check_output(["docker", "inspect", name], text=True))[0]
                port = container["NetworkSettings"]["Ports"]["8080/tcp"][0]["HostPort"]
                base = f"http://127.0.0.1:{port}"
                print(f"Checking {'startup' if iteration == 0 else 'restart'} at {base}.", flush=True)
                wait_for_health(base)
                try:
                    urllib.request.urlopen(base + "/.well-known/aep", timeout=5).close()
                    raise ValueError("AEP discovery unexpectedly accepted an unauthenticated request.")
                except urllib.error.HTTPError as error:
                    if error.code != 401:
                        raise ValueError("Unexpected unauthenticated AEP response.") from None
                request = urllib.request.Request(base + "/.well-known/aep", headers={"Authorization": "Bearer " + key})
                with urllib.request.urlopen(request, timeout=5) as response:
                    check_manifest(json.load(response), delivery)
                image = json.loads(subprocess.check_output(["docker", "image", "inspect", args.image], text=True))[0]
                labels = image["Config"]["Labels"]
                if labels.get("org.opencontainers.image.version") != delivery_entry(delivery)["version"] \
                        or labels.get("org.opencontainers.image.revision") != delivery["sourceSha"]:
                    raise ValueError("Image version/source labels do not match the delivery.")
                if iteration == 0:
                    subprocess.run(["docker", "restart", name], check=True)
            print("Image health, authenticated AEP identity, contribution, provenance labels and restart smoke passed.")
        finally:
            subprocess.run(["docker", "rm", "--force", name], check=False, stdout=subprocess.DEVNULL)


if __name__ == "__main__":
    main()
