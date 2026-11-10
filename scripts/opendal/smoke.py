#!/usr/bin/env python3
"""Run the published extension and MCP sample, then restart and verify persisted content."""
import argparse
import os
from pathlib import Path
import secrets
import socket
import subprocess
import tempfile
import time
import urllib.request


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--publish-dir", required=True, type=Path)
    parser.add_argument("--dotnet", default="dotnet")
    args = parser.parse_args()
    repository = Path(__file__).resolve().parents[2]
    published = args.publish_dir.resolve()
    native = "opendal_dotnet.dll" if os.name == "nt" else "libopendal_dotnet.so"
    for required in (native, "Agentstration.Extensions.OpenDal.dll", "licenses/opendal/LICENSE",
                     "licenses/opendal/NOTICE", "licenses/opendal/LICENSE-MPL-2.0.txt",
                     "licenses/opendal/LICENSE-CC0-1.0.txt", "licenses/opendal/README.md"):
        if not (published / required).is_file():
            raise RuntimeError("Published distribution is missing " + required)
    sample = repository / "samples/OpenDalSmoke/bin/Release/net10.0/OpenDalSmoke.dll"
    with tempfile.TemporaryDirectory(prefix="opendal-distribution-") as temporary:
        directory = Path(temporary)
        key = directory / "shared.key"
        key.write_text(secrets.token_urlsafe(32), encoding="utf-8")
        key.chmod(0o600)
        for read_only in (False, True):
            with socket.socket() as listener:
                listener.bind(("127.0.0.1", 0))
                port = listener.getsockname()[1]
            endpoint = "http://127.0.0.1:" + str(port)
            environment = dict(os.environ, ASPNETCORE_URLS=endpoint, OpenDal__RootDirectory=str(directory / "content"),
                               OpenDal__AllowedClients__0="smoke", Aep__EnrollmentMode="SharedKeyFile",
                               Aep__SharedKeyFile__ClientId="smoke", Aep__SharedKeyFile__Path=str(key))
            with (directory / "host.log").open("w", encoding="utf-8") as log:
                process = subprocess.Popen([args.dotnet, str(published / "Agentstration.Extensions.OpenDal.dll")],
                                           cwd=published, env=environment, stdout=log, stderr=subprocess.STDOUT)
                try:
                    deadline = time.monotonic() + 30
                    while True:
                        if process.poll() is not None:
                            raise RuntimeError("The standalone extension exited before readiness; inspect " + str(directory / "host.log"))
                        try:
                            with urllib.request.urlopen(endpoint + "/health", timeout=1) as response:
                                if response.status == 200:
                                    break
                        except (OSError, urllib.error.URLError):
                            pass
                        if time.monotonic() >= deadline:
                            raise TimeoutError("The standalone extension did not become ready.")
                        time.sleep(0.1)
                    command = [args.dotnet, str(sample), endpoint, str(key)]
                    if read_only:
                        command.append("--read-only")
                    subprocess.run(command, check=True, timeout=30)
                finally:
                    process.terminate()
                    try:
                        process.wait(timeout=10)
                    except subprocess.TimeoutExpired:
                        process.kill()
                        process.wait(timeout=10)
        print("Published native package and persisted MCP content passed before and after process restart.")


if __name__ == "__main__":
    main()
