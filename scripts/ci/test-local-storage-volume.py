#!/usr/bin/env python3
"""Run the explicit Storage probe in separate real containers with retained mounts."""
import argparse
import json
import subprocess
import uuid


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--image", required=True, help="Image containing the published StorageVolumeProbe")
    parser.add_argument("--docker-host", help="Explicit Docker daemon selector")
    args = parser.parse_args()
    docker = ["docker"] + (["--host", args.docker_host] if args.docker_host else [])
    prefix = "agentstration-storage-probe-" + uuid.uuid4().hex[:16]
    owned_volumes = []
    containers = []

    def run(*arguments):
        result = subprocess.run(docker + list(arguments), text=True, capture_output=True, check=True)
        return result.stdout.strip()

    try:
        for suffix in ("data", "content"):
            name = prefix + "-" + suffix
            existing = subprocess.run(docker + ["volume", "inspect", name], capture_output=True)
            if existing.returncode == 0:
                raise RuntimeError("Refusing an existing volume; this probe only owns newly created resources.")
            run("volume", "create", "--label", "io.agentstration.test=storage-volume-probe", name)
            owned_volumes.append(name)
        results = {}
        for phase in ("write", "read"):
            name = prefix + "-" + phase
            containers.append(name)
            output = run("run", "--name", name, "--network", "none",
                         "--mount", f"type=volume,src={owned_volumes[0]},dst=/data",
                         "--mount", f"type=volume,src={owned_volumes[1]},dst=/storage",
                         "-e", "DOTNET_PROCESSOR_COUNT=4",
                         args.image, phase, "/data", "/storage")
            results[phase] = output
            print(output, flush=True)
            # Removal is intentional: the next phase must execute in another container.
            run("rm", name)
            containers.remove(name)
        print(json.dumps({"image": args.image, "result": "passed", "phases": results}, ensure_ascii=False))
    except subprocess.CalledProcessError as error:
        print(error.stdout or "", flush=True)
        print(error.stderr or "", flush=True)
        raise
    finally:
        for name in containers:
            subprocess.run(docker + ["rm", "--force", name], capture_output=True)
        for name in owned_volumes:
            subprocess.run(docker + ["volume", "rm", name], capture_output=True)


if __name__ == "__main__":
    main()
