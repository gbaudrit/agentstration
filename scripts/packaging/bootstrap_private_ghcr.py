"""One-time, owner-authorized initialization of empty private GHCR destinations."""
import json
import os
from pathlib import Path
import subprocess
import tempfile
import time
import urllib.error
import urllib.request

OWNER = "gbaudrit"
REPOSITORY = "gbaudrit/agentstration"
PACKAGES = tuple("agentstration-extension-" + name for name in ("openapi", "postgresql", "ollama"))


class InitializationError(Exception):
    pass


def api(token, path):
    request = urllib.request.Request("https://api.github.com/" + path, headers={
        "Authorization": "Bearer " + token, "Accept": "application/vnd.github+json",
        "X-GitHub-Api-Version": "2022-11-28", "User-Agent": "agentstration-empty-package-init"})
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            return response.status, json.load(response), response.headers
    except urllib.error.HTTPError as error:
        return error.code, None, error.headers


def verify_owner(token):
    if not token:
        raise InitializationError("GHCR_BOOTSTRAP_TOKEN is missing; no registry operation attempted.")
    status, identity, headers = api(token, "user")
    if status != 200:
        raise InitializationError(f"Bootstrap identity validation returned HTTP {status}; no image uploaded.")
    scopes = set(headers.get("X-OAuth-Scopes", "").replace(" ", "").split(","))
    # write:packages includes read:packages when GitHub normalizes implied scopes.
    if identity.get("login", "").lower() != OWNER or "write:packages" not in scopes:
        raise InitializationError("Expected owner identity with read/write packages access; no image uploaded.")
    return {"login": identity["login"], "readPackages": True, "writePackages": True}


def inventory(token):
    names = set()
    for page in range(1, 101):
        status, packages, _ = api(token, f"user/packages?package_type=container&per_page=100&page={page}")
        if status != 200:
            raise InitializationError(f"Owner package inventory returned HTTP {status}.")
        names.update(package["name"] for package in packages)
        if len(packages) < 100:
            return names
    raise InitializationError("Owner package inventory exceeded the pagination bound.")


def metadata(token, package):
    return api(token, f"users/{OWNER}/packages/container/{package}")[:2]


def require_private(package, record):
    if record.get("name") != package or record.get("visibility") != "private":
        raise InitializationError(f"{package}: private visibility is not verified; no application upload permitted.")


def wait_private(token, package, linked=False):
    for _ in range(12):
        status, record = metadata(token, package)
        if status == 200:
            require_private(package, record)
            association = (record.get("repository") or {}).get("full_name")
            if association and association != REPOSITORY:
                raise InitializationError(f"{package}: unexpected repository association; stopping.")
            if not linked or association == REPOSITORY:
                return record
        elif status != 404:
            raise InitializationError(f"{package}: metadata returned HTTP {status}.")
        time.sleep(3)
    raise InitializationError(f"{package}: private metadata/repository association could not be verified.")


def docker(command, env, token_input=None):
    result = subprocess.run(["docker", *command], input=token_input, text=True, capture_output=True, env=env)
    if result.returncode:
        raise InitializationError(f"Docker {command[0]} failed with exit code {result.returncode}.")
    return result.stdout


def push_empty(package, tag, linked, context, env):
    reference = f"ghcr.io/{OWNER}/{package}:{tag}"
    command = ["build", "--network=none", "--tag", reference]
    if linked:
        command += ["--label", "org.opencontainers.image.source=https://github.com/" + REPOSITORY]
    docker([*command, str(context)], env)
    image = json.loads(docker(["image", "inspect", reference], env))[0]
    config = image.get("Config") or {}
    if image["RootFS"].get("Layers") or any(config.get(k) for k in ("Env", "Cmd", "Entrypoint")):
        raise InitializationError("Refusing upload: the image must have no filesystem layer, environment or executable.")
    docker(["push", reference], env)
    return {"tag": tag, "repositoryLinkedLabel": linked, "reference": reference, "imageId": image["Id"]}


def main():
    token = os.environ.get("GHCR_BOOTSTRAP_TOKEN", "").strip()
    report = {"repository": REPOSITORY, "applicationImagesUploaded": False, "packages": []}
    output = Path("artifacts/ghcr-bootstrap.json")
    output.parent.mkdir(exist_ok=True)
    try:
        report["identity"] = verify_owner(token)
        known = inventory(token)
        statuses = {}
        for package in PACKAGES:
            status, record = metadata(token, package)
            if status == 200:
                require_private(package, record)
            elif status != 404 or package in known:
                raise InitializationError(f"{package}: absence/access is ambiguous (HTTP {status}); stopping.")
            statuses[package] = (status, record)
        prefix = "registry-init-" + os.environ["GITHUB_RUN_ID"] + "-" + os.environ["GITHUB_RUN_ATTEMPT"]
        with tempfile.TemporaryDirectory(prefix="empty-context-") as directory, tempfile.TemporaryDirectory(prefix="ghcr-auth-") as auth:
            context = Path(directory)
            (context / "Dockerfile").write_text("FROM scratch\n", encoding="utf-8")
            env = {**os.environ, "DOCKER_CONFIG": auth}
            docker(["login", "ghcr.io", "--username", OWNER, "--password-stdin"], env, token + "\n")
            try:
                for package in PACKAGES:
                    status, record = statuses[package]
                    item = {"name": package, "created": status == 404, "emptyImages": []}
                    report["packages"].append(item)
                    if status == 404:
                        item["emptyImages"].append(push_empty(package, prefix, False, context, env))
                        record = wait_private(token, package)
                    require_private(package, record)
                    item["visibilityBeforeAssociation"] = record["visibility"]
                    association = (record.get("repository") or {}).get("full_name")
                    if association and association != REPOSITORY:
                        raise InitializationError(f"{package}: already associated to another repository; stopping.")
                    if association != REPOSITORY:
                        item["emptyImages"].append(push_empty(package, prefix + "-linked", True, context, env))
                    record = wait_private(token, package, linked=True)
                    item.update(visibility=record["visibility"], repository=record["repository"]["full_name"],
                                settingsUrl=f"https://github.com/users/{OWNER}/packages/container/{package}/settings")
                    print(json.dumps(item))
            finally:
                docker(["logout", "ghcr.io"], env)
    except InitializationError as error:
        report["error"] = str(error)
        print(str(error))
        raise SystemExit(1) from None
    finally:
        output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
