#!/usr/bin/env python3
"""External OCI image catalogue: build once, then copy the validated archive to registries."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
SLUG = re.compile(r"[a-z][a-z0-9-]*")
VERSION = re.compile(r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?")
DIGEST = re.compile(r"sha256:[0-9a-f]{64}")


def output(command):
    return subprocess.check_output(command, text=True).strip()


def valid_version(value):
    match = VERSION.fullmatch(value) if isinstance(value, str) else None
    return bool(match and not (match[4] and any(part.isdigit() and len(part) > 1 and part[0] == "0"
                                              for part in match[4].split("."))))


def catalogue(extension):
    if not SLUG.fullmatch(extension):
        raise ValueError("Invalid extension catalogue ID.")
    families = {"extensions": "aep-extension", "workers": "awp-worker", "applications": "server"}
    paths = [ROOT / "deploy/images" / family / f"{extension}.json" for family in families]
    matches = [path for path in paths if path.is_file()]
    if len(matches) != 1:
        raise ValueError("The image ID must identify exactly one implemented catalogue entry.")
    path = matches[0]
    entry = json.loads(path.read_text(encoding="utf-8"))
    if entry.get("schemaVersion") != 1 or entry.get("id") != extension:
        raise ValueError("Invalid image catalogue identity/version.")
    kind = entry.get("kind", "aep-extension")
    if families[path.parent.name] != kind:
        raise ValueError("Catalogue family and executable kind do not match.")
    for name in ("project", "dockerfile", "versionFile", "testProject"):
        resolved = (ROOT / entry[name]).resolve()
        if not resolved.is_relative_to(ROOT) or not resolved.is_file():
            raise ValueError(f"Catalogue {name} must identify an existing repository file.")
    if not SLUG.fullmatch(entry["imageName"]) or entry["platform"] != "linux/amd64":
        raise ValueError("Invalid image name or unsupported platform.")
    if not re.fullmatch(r"[A-Za-z0-9.]+\.dll", entry["entrypoint"]):
        raise ValueError("Invalid extension DLL name.")
    if kind == "server":
        properties = ET.parse(ROOT / entry["versionFile"])
        prefix, suffix = properties.findtext("./PropertyGroup/VersionPrefix"), properties.findtext("./PropertyGroup/VersionSuffix")
        version = (prefix or "") + ("-" + suffix if suffix else "")
    else:
        version = (ROOT / entry["versionFile"]).read_text(encoding="utf-8").strip()
    if not valid_version(version):
        raise ValueError("Extension version must be SemVer without build metadata.")
    smoke_environment(entry)
    return {**entry, "version": version}


def smoke_environment(entry):
    """Public fixture settings only; authentication stays in isolated generated mounts."""
    values = entry.get("smokeEnvironment", {})
    if not isinstance(values, dict) or any(
            not re.fullmatch(r"[A-Za-z][A-Za-z0-9_]*", name)
            or name.lower().startswith(("aep__", "aspnetcore_", "dotnet_"))
            or re.search(r"credential|secret|password|token|key", name, re.IGNORECASE)
            or not isinstance(value, str) or not re.fullmatch(r"[A-Za-z0-9./,:_-]+", value)
            for name, value in values.items()):
        raise ValueError("Smoke environment must contain only public, literal fixture settings.")
    return values


def release_tag(entry, tag):
    if entry.get("kind") == "server":
        raise ValueError("This server catalogue supports private previews; official server releases use the existing product release workflow.")
    family = "worker" if entry.get("kind") == "awp-worker" else "extension"
    if tag != f"{family}/{entry['id']}/v{entry['version']}":
        raise ValueError("Release tag does not match the extension's own version.")


def delivery_entry(delivery):
    # Accept existing Foundry receipts while writing neutral metadata for new deliveries.
    keys = [key for key in ("component", "extension") if key in delivery]
    if len(keys) != 1:
        raise ValueError("Delivery must identify exactly one component.")
    return delivery[keys[0]]


def resolve_image(reference):
    inspected = output(["docker", "buildx", "imagetools", "inspect", reference])
    match = re.search(r"^Digest:\s+(sha256:[0-9a-f]{64})$", inspected, re.MULTILINE)
    if not match:
        raise ValueError("Registry did not return an immutable base-image digest.")
    return reference.rsplit(":", 1)[0] + "@" + match[1]


def build_command(entry, archive, metadata, bases, sha, repository):
    return ["docker", "buildx", "build", *(["--build-arg", "INSTALL_GIT=true"] if entry.get("installGit", False) else []), "--file", str(ROOT / entry["dockerfile"]),
            "--platform", entry["platform"], "--output", f"type=oci,dest={archive}",
            "--metadata-file", str(metadata), "--provenance=true", "--sbom=true",
            "--build-arg", f"SDK_IMAGE={bases['sdk']}", "--build-arg", f"RUNTIME_IMAGE={bases['runtime']}",
            "--build-arg", f"PROJECT={entry['project']}", "--build-arg", f"ENTRYPOINT_DLL={entry['entrypoint']}",
            "--tag", f"{entry['imageName']}:{entry['version']}",
            "--label", f"org.opencontainers.image.revision={sha}",
            "--label", f"org.opencontainers.image.version={entry['version']}",
            "--label", f"org.opencontainers.image.source=https://github.com/{repository}",
            "--label", "org.opencontainers.image.licenses=Apache-2.0", str(ROOT)]


def build(extension, directory, repository, bases=None):
    entry = catalogue(extension)
    if not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", repository):
        raise ValueError("Expected an owner/repository name.")
    if output(["git", "-C", str(ROOT), "status", "--porcelain"]):
        raise ValueError("Build a clean checkout so the image identifies an exact source revision.")
    sha = output(["git", "-C", str(ROOT), "rev-parse", "HEAD"])
    if bases is None:
        bases = {"sdk": resolve_image("mcr.microsoft.com/dotnet/sdk:10.0.300"),
                 "runtime": resolve_image(entry.get("runtimeImage", "mcr.microsoft.com/dotnet/aspnet:10.0"))}
    if set(bases) != {"sdk", "runtime"} or not all(
            re.fullmatch(r"[a-z0-9./_-]+@sha256:[0-9a-f]{64}", value) for value in bases.values()):
        raise ValueError("SDK and runtime build inputs must be pinned by digest.")
    directory.mkdir(parents=True, exist_ok=True)
    archive, metadata = directory / "image.oci.tar", directory / "build-metadata.json"
    subprocess.run(build_command(entry, archive, metadata, bases, sha, repository), check=True)
    digest = json.loads(metadata.read_text(encoding="utf-8"))["containerimage.digest"]
    if not DIGEST.fullmatch(digest):
        raise ValueError("Build did not produce an immutable image digest.")
    with archive.open("rb") as stream:
        archive_hash = hashlib.file_digest(stream, "sha256").hexdigest()
    delivery = {"schemaVersion": 1, ("extension" if entry.get("kind", "aep-extension") == "aep-extension" else "component"): entry, "sourceSha": sha, "repository": repository,
                "digest": digest, "buildImages": bases, "archive": archive.name,
                "archiveSha256": archive_hash}
    (directory / "delivery.json").write_text(json.dumps(delivery, indent=2) + "\n", encoding="utf-8")
    return delivery


def private_package(repository, package):
    owner = repository.split("/")[0]
    owner_type = output(["gh", "api", f"repos/{repository}", "--jq", ".owner.type"])
    endpoint = f"{'orgs' if owner_type == 'Organization' else 'users'}/{owner}/packages/container/{package}"
    result = subprocess.run(["gh", "api", endpoint], capture_output=True, text=True)
    if result.returncode:
        raise ValueError("Cannot verify private GHCR package access. Provision the private package and grant repository Actions access before publishing.")
    if json.loads(result.stdout)["visibility"] != "private":
        raise ValueError("Refusing publication to a non-private GHCR package.")


def copy_archive(delivery_path, destination, authfile):
    delivery = json.loads(delivery_path.read_text(encoding="utf-8"))
    archive = delivery_path.parent / delivery["archive"]
    if archive.resolve().parent != delivery_path.parent.resolve() or not DIGEST.fullmatch(delivery["digest"]):
        raise ValueError("Invalid delivery archive/digest.")
    with archive.open("rb") as stream:
        if hashlib.file_digest(stream, "sha256").hexdigest() != delivery["archiveSha256"]:
            raise ValueError("The OCI archive differs from the validated delivery.")
    inspect = subprocess.run(["skopeo", "inspect", "--authfile", str(authfile), "--raw", f"docker://{destination}"],
                             capture_output=True)
    if inspect.returncode == 0:
        if "sha256:" + hashlib.sha256(inspect.stdout).hexdigest() != delivery["digest"]:
            raise ValueError("Destination tag already points at a different image; release tags cannot be replaced.")
        return
    if not any(value in inspect.stderr.lower() for value in (b"manifest unknown", b"name unknown", b"not found", b"404")):
        raise ValueError("Cannot inspect the registry destination before publication.")
    subprocess.run(["skopeo", "copy", "--all", "--preserve-digests", "--authfile", str(authfile),
                    f"oci-archive:{archive}", f"docker://{destination}"], check=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="action", required=True)
    describe = sub.add_parser("describe")
    describe.add_argument("--image", "--extension", dest="extension", required=True)
    describe.add_argument("--release-tag")
    builder = sub.add_parser("build")
    builder.add_argument("--image", "--extension", dest="extension", required=True)
    builder.add_argument("--directory", required=True, type=Path)
    builder.add_argument("--repository", required=True)
    publisher = sub.add_parser("publish")
    publisher.add_argument("--delivery", required=True, type=Path)
    publisher.add_argument("--registry", required=True, choices=("ghcr", "dockerhub"))
    publisher.add_argument("--authfile", required=True, type=Path)
    publisher.add_argument("--namespace", help="Docker Hub namespace for official publication")
    publisher.add_argument("--release-tag")
    args = parser.parse_args()
    if args.action == "describe":
        entry = catalogue(args.extension)
        if args.release_tag:
            release_tag(entry, args.release_tag)
        print(json.dumps(entry))
    elif args.action == "build":
        build(args.extension, args.directory, args.repository)
    else:
        delivery = json.loads(args.delivery.read_text(encoding="utf-8"))
        entry = catalogue(delivery_entry(delivery)["id"])
        if delivery_entry(delivery) != entry or delivery["sourceSha"] != output(["git", "rev-parse", "HEAD"]):
            raise ValueError("Publication checkout does not match the validated delivery.")
        if args.release_tag:
            release_tag(entry, args.release_tag)
        if args.registry == "dockerhub":
            if not args.release_tag or not args.namespace or not re.fullmatch(r"[a-z0-9][a-z0-9_-]*", args.namespace):
                raise ValueError("Official Docker Hub publication requires a matching release tag and namespace.")
            destination = f"docker.io/{args.namespace}/{entry['imageName']}:{entry['version']}"
        else:
            repository = delivery["repository"].lower()
            package = f"{repository.split('/')[1]}-{entry['imageName']}"
            private_package(repository, package)
            tag = entry["version"] if args.release_tag else f"sha-{delivery['sourceSha']}"
            destination = f"ghcr.io/{repository.split('/')[0]}/{package}:{tag}"
        copy_archive(args.delivery, destination, args.authfile)
        if args.registry == "ghcr":
            private_package(repository, package)
        publication = {"image": destination, "digest": delivery["digest"], "sourceSha": delivery["sourceSha"],
                       "reference": destination.rsplit(":", 1)[0] + "@" + delivery["digest"]}
        (args.delivery.parent / f"publication-{args.registry}.json").write_text(
            json.dumps(publication, indent=2) + "\n", encoding="utf-8")
        print(json.dumps(publication))


if __name__ == "__main__":
    main()
