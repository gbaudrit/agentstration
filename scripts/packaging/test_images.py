"""Offline publication contracts; no registry, cloud credential or model access."""
import argparse
import copy
import hashlib
import http.server
import json
from pathlib import Path
import socket
import struct
import subprocess
import tempfile
import threading
import unittest
import urllib.error
from unittest.mock import MagicMock, patch

import images
import smoke


class ImageContracts(unittest.TestCase):
    def setUp(self):
        self.entry = images.catalogue("foundry")
        self.temporary = tempfile.TemporaryDirectory(prefix="image-contract-")
        self.addCleanup(self.temporary.cleanup)
        self.directory = Path(self.temporary.name)

    def delivery(self):
        archive = self.directory / "image.oci.tar"
        archive.write_bytes(b"offline-archive-fixture")
        delivery = {"extension": self.entry, "sourceSha": "a" * 40, "digest": "sha256:" + "b" * 64,
                    "archive": archive.name, "archiveSha256": hashlib.sha256(archive.read_bytes()).hexdigest()}
        path = self.directory / "delivery.json"
        path.write_text(json.dumps(delivery))
        return path, delivery

    def test_catalogue_owns_version_and_rejects_unknown_or_unsafe_ids(self):
        self.assertEqual((images.ROOT / self.entry["versionFile"]).read_text().strip(), self.entry["version"])
        self.assertEqual("Agentstration.Extensions.Foundry.dll", self.entry["entrypoint"])
        for extension in ("../foundry", "foundry;command", "unimplemented"):
            with self.subTest(extension=extension), self.assertRaises(ValueError):
                images.catalogue(extension)

    def test_awp_worker_has_an_independent_catalogue_and_release_namespace(self):
        worker = images.catalogue("maf-worker")
        self.assertEqual("awp-worker", worker["kind"])
        self.assertEqual((images.ROOT / worker["versionFile"]).read_text().strip(), worker["version"])
        self.assertEqual("Dockerfile.worker", worker["dockerfile"])
        self.assertNotIn("aepVersion", worker)
        images.release_tag(worker, "worker/maf-worker/v" + worker["version"])
        with self.assertRaises(ValueError):
            images.release_tag(worker, "extension/maf-worker/v" + worker["version"])
        with self.assertRaises(ValueError):
            images.release_tag(self.entry, "worker/foundry/v" + self.entry["version"])

    def test_server_follows_product_properties_without_a_second_version_source(self):
        entry = json.loads((images.ROOT / "deploy/images/applications/server.json").read_text())
        root = self.directory / "repository"
        for name in ("project", "dockerfile", "versionFile", "testProject"):
            path = root / entry[name]
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text("fixture")
        path = root / "deploy/images/applications/server.json"
        path.parent.mkdir(parents=True)
        path.write_text(json.dumps(entry))
        for properties, expected in (("<VersionPrefix>7.4.2</VersionPrefix><VersionSuffix>preview.9</VersionSuffix>", "7.4.2-preview.9"),
                                     ("<VersionPrefix>8.0.0</VersionPrefix>", "8.0.0")):
            with self.subTest(expected=expected), patch.object(images, "ROOT", root):
                (root / entry["versionFile"]).write_text("<Project><PropertyGroup>" + properties + "</PropertyGroup></Project>")
                server = images.catalogue("server")
                self.assertEqual(expected, server["version"])
                self.assertEqual("server", server["kind"])
                with self.assertRaises(ValueError):
                    images.release_tag(server, "v" + expected)
        with patch.object(images, "ROOT", root):
            (root / entry["versionFile"]).write_text("<Project><PropertyGroup><VersionPrefix>$(Undefined)</VersionPrefix></PropertyGroup></Project>")
            with self.assertRaises(ValueError):
                images.catalogue("server")

    def test_server_build_reuses_the_exact_pinned_companion_inputs(self):
        bases = {"sdk": "sdk@sha256:" + "a" * 64, "runtime": "runtime@sha256:" + "b" * 64}
        directory = self.directory / "server"

        def build_fixture(command, **_arguments):
            (directory / "image.oci.tar").write_bytes(b"offline-server-archive")
            (directory / "build-metadata.json").write_text(json.dumps({"containerimage.digest": "sha256:" + "c" * 64}))

        with patch.object(images, "output", side_effect=["", "d" * 40]), \
                patch.object(images, "resolve_image") as resolve, \
                patch.object(images.subprocess, "run", side_effect=build_fixture) as build:
            delivery = images.build("server", directory, "owner/repo", bases=bases)
        resolve.assert_not_called()
        self.assertEqual("server", delivery["component"]["kind"])
        self.assertEqual(bases, delivery["buildImages"])
        self.assertIn("SDK_IMAGE=" + bases["sdk"], build.call_args.args[0])
        self.assertNotIn("extension", delivery)

    def test_unpinned_companion_inputs_fail_before_a_build(self):
        with patch.object(images, "output", side_effect=["", "d" * 40]), \
                patch.object(images.subprocess, "run") as build, patch.object(images, "resolve_image") as resolve:
            with self.assertRaises(ValueError):
                images.build("server", self.directory / "invalid", "owner/repo",
                             bases={"sdk": "sdk:latest", "runtime": "runtime@sha256:" + "b" * 64})
        build.assert_not_called()
        resolve.assert_not_called()

    def test_delivery_accepts_legacy_aep_and_neutral_worker_metadata_without_ambiguity(self):
        worker = images.catalogue("maf-worker")
        self.assertEqual(self.entry, images.delivery_entry({"extension": self.entry}))
        self.assertEqual(worker, images.delivery_entry({"component": worker}))
        for delivery in ({}, {"component": worker, "extension": self.entry}):
            with self.subTest(delivery=delivery), self.assertRaises(ValueError):
                images.delivery_entry(delivery)

    def test_release_tag_is_extension_specific_and_matches_its_version(self):
        entry = {**self.entry, "version": "1.0.0"}
        images.release_tag(entry, "extension/foundry/v1.0.0")
        for tag in ("v1.0.0", "extension/openapi/v1.0.0", "extension/foundry/v2.0.0"):
            with self.subTest(tag=tag), self.assertRaises(ValueError):
                images.release_tag(entry, tag)

    def test_build_pins_inputs_and_exports_one_archive_with_attestations(self):
        bases = {"sdk": "sdk@sha256:" + "a" * 64, "runtime": "runtime@sha256:" + "b" * 64}
        command = images.build_command(self.entry, self.directory / "image.tar", self.directory / "metadata.json",
                                       bases, "c" * 40, "owner/repo")
        self.assertIn("SDK_IMAGE=" + bases["sdk"], command)
        self.assertIn("RUNTIME_IMAGE=" + bases["runtime"], command)
        self.assertIn("--provenance=true", command)
        self.assertIn("--sbom=true", command)
        self.assertIn("org.opencontainers.image.version=" + self.entry["version"], command)
        self.assertIn("type=oci,dest=" + str(self.directory / "image.tar"), command)
        self.assertNotIn("--push", command)

    def test_changed_archive_is_rejected_before_registry_access(self):
        path, _ = self.delivery()
        (self.directory / "image.oci.tar").write_bytes(b"changed")
        with patch.object(images.subprocess, "run") as run:
            with self.assertRaises(ValueError):
                images.copy_archive(path, "example/image:1.0.0", self.directory / "auth.json")
            run.assert_not_called()

    def test_archive_symlink_cannot_escape_the_delivery_directory(self):
        path, _ = self.delivery()
        with tempfile.TemporaryDirectory() as outside:
            target = Path(outside) / 'image.oci.tar'
            target.write_bytes(b'offline-archive-fixture')
            archive = self.directory / 'image.oci.tar'
            archive.unlink()
            archive.symlink_to(target)
            with patch.object(images.subprocess, 'run') as run:
                with self.assertRaises(ValueError):
                    images.copy_archive(path, 'example/image:1.0.0', self.directory / 'auth.json')
                run.assert_not_called()

    def test_copy_preserves_all_manifests_without_rebuilding_or_passing_credentials(self):
        path, _ = self.delivery()
        missing = subprocess.CompletedProcess([], 1, stdout=b"", stderr=b"manifest unknown")
        with patch.object(images.subprocess, "run", return_value=missing) as run:
            images.copy_archive(path, "example/image:1.0.0", self.directory / "auth.json")
        command = run.call_args_list[1].args[0]
        self.assertEqual(["skopeo", "copy", "--all", "--preserve-digests"], command[:4])
        self.assertIn("oci-archive:" + str(self.directory / "image.oci.tar"), command)
        self.assertNotIn("--dest-creds", command)
        self.assertNotIn("docker", command)

    def test_existing_release_cannot_be_replaced_and_auth_errors_do_not_trigger_push(self):
        path, _ = self.delivery()
        for result in (subprocess.CompletedProcess([], 0, stdout=b"different-manifest", stderr=b""),
                       subprocess.CompletedProcess([], 1, stdout=b"", stderr=b"unauthorized")):
            with patch.object(images.subprocess, "run", return_value=result) as run:
                with self.assertRaises(ValueError):
                    images.copy_archive(path, "example/image:1.0.0", self.directory / "auth.json")
                self.assertEqual(1, run.call_count)

    def test_republishing_the_same_validated_digest_is_idempotent(self):
        path, delivery = self.delivery()
        manifest = b'{"schemaVersion":2,"mediaType":"application/vnd.oci.image.index.v1+json","manifests":[]}'
        delivery["digest"] = "sha256:" + hashlib.sha256(manifest).hexdigest()
        path.write_text(json.dumps(delivery))
        existing = subprocess.CompletedProcess([], 0, stdout=manifest, stderr=b"")
        with patch.object(images.subprocess, "run", return_value=existing) as run:
            images.copy_archive(path, "example/image:1.0.0", self.directory / "auth.json")
        self.assertEqual(1, run.call_count)
        self.assertEqual("inspect", run.call_args.args[0][1])

    def test_public_package_is_rejected(self):
        result = subprocess.CompletedProcess([], 0, stdout='{"visibility":"public"}', stderr="")
        with patch.object(images, "output", return_value="User"), patch.object(images.subprocess, "run", return_value=result):
            with self.assertRaises(ValueError):
                images.private_package("owner/repo", "repo-extension-foundry")

    def test_unknown_or_nonprivate_package_stops_before_any_image_copy(self):
        path, delivery = self.delivery()
        delivery["repository"] = "owner/repo"
        path.write_text(json.dumps(delivery))
        arguments = argparse.Namespace(action="publish", delivery=path, registry="ghcr",
                                       authfile=self.directory / "auth.json", release_tag=None, namespace=None)
        responses = [subprocess.CompletedProcess([], 1, stdout="", stderr=f"HTTP {status}")
                     for status in (404, 403, 500)]
        responses += [subprocess.CompletedProcess([], 0, stdout=json.dumps({"visibility": visibility}), stderr="")
                      for visibility in ("public", "internal")]
        for response in responses:
            with self.subTest(response=response), \
                    patch.object(images.argparse.ArgumentParser, "parse_args", return_value=arguments), \
                    patch.object(images, "output", side_effect=lambda command: "a" * 40 if command[0] == "git" else "User"), \
                    patch.object(images.subprocess, "run", return_value=response), \
                    patch.object(images, "copy_archive") as copy_archive:
                with self.assertRaises(ValueError):
                    images.main()
                copy_archive.assert_not_called()
                self.assertFalse((self.directory / "publication-ghcr.json").exists())

    def test_verified_private_package_records_digest_after_copy_and_privacy_recheck(self):
        path, delivery = self.delivery()
        delivery["repository"] = "owner/repo"
        path.write_text(json.dumps(delivery))
        arguments = argparse.Namespace(action="publish", delivery=path, registry="ghcr",
                                       authfile=self.directory / "auth.json", release_tag=None, namespace=None)
        response = subprocess.CompletedProcess([], 0, stdout='{"visibility":"private"}', stderr="")
        with patch.object(images.argparse.ArgumentParser, "parse_args", return_value=arguments), \
                patch.object(images, "output", side_effect=lambda command: "a" * 40 if command[0] == "git" else "User"), \
                patch.object(images.subprocess, "run", return_value=response) as api, \
                patch.object(images, "copy_archive") as copy_archive, patch("builtins.print"):
            images.main()
        copy_archive.assert_called_once()
        self.assertEqual(2, api.call_count)
        receipt = json.loads((self.directory / "publication-ghcr.json").read_text())
        self.assertEqual("ghcr.io/owner/repo-extension-foundry@" + delivery["digest"], receipt["reference"])

    def test_worker_publication_reuses_the_privacy_gate_and_digest_receipt(self):
        path, delivery = self.delivery()
        del delivery["extension"]
        delivery.update(component=images.catalogue("maf-worker"), repository="owner/repo")
        path.write_text(json.dumps(delivery))
        arguments = argparse.Namespace(action="publish", delivery=path, registry="ghcr",
                                       authfile=self.directory / "auth.json", release_tag=None, namespace=None)
        with patch.object(images.argparse.ArgumentParser, "parse_args", return_value=arguments), \
                patch.object(images, "output", return_value="a" * 40), \
                patch.object(images, "private_package") as privacy, \
                patch.object(images, "copy_archive") as copy_archive, patch("builtins.print"):
            images.main()
        copy_archive.assert_called_once()
        self.assertEqual(2, privacy.call_count)
        privacy.assert_called_with("owner/repo", "repo-worker-maf")
        receipt = json.loads((self.directory / "publication-ghcr.json").read_text())
        self.assertEqual("ghcr.io/owner/repo-worker-maf@" + delivery["digest"], receipt["reference"])

    def test_real_aep_manifest_shape_checks_identity_protocol_and_contribution(self):
        _, delivery = self.delivery()
        manifest = {"protocolVersion": "agentstration.io/v1",
                    "extension": {"id": self.entry["extensionId"], "version": self.entry["version"]},
                    "contributions": {"modelProviders": [{"id": "microsoft-foundry"}]}}
        smoke.check_manifest(manifest, delivery)
        for identity in ("id", "version"):
            invalid = copy.deepcopy(manifest)
            invalid["extension"][identity] = "wrong"
            with self.assertRaises(ValueError):
                smoke.check_manifest(invalid, delivery)
        manifest["contributions"]["modelProviders"] = []
        with self.assertRaises(ValueError):
            smoke.check_manifest(manifest, delivery)

    def test_health_wait_retries_startup_transport_failures(self):
        errors = (ConnectionResetError("reset"), ConnectionRefusedError("refused"),
                  TimeoutError("timeout"), urllib.error.URLError("not listening"))
        for error in errors:
            with self.subTest(error=type(error).__name__):
                healthy = MagicMock()
                healthy.__enter__.return_value.status = 200
                with patch.object(smoke.urllib.request, "urlopen", side_effect=[error, healthy]) as request, \
                        patch.object(smoke.time, "sleep") as pause:
                    smoke.wait_for_health("http://127.0.0.1:8080")
                self.assertEqual(2, request.call_count)
                pause.assert_called_once_with(1)

    def test_health_wait_stops_when_the_startup_deadline_expires(self):
        with patch.object(smoke.urllib.request, "urlopen", side_effect=ConnectionResetError("reset")) as request, \
                patch.object(smoke.time, "monotonic", side_effect=[0, 1]), \
                patch.object(smoke.time, "sleep") as pause:
            with self.assertRaisesRegex(ValueError, "within 1 seconds"):
                smoke.wait_for_health("http://127.0.0.1:8080", timeout_seconds=1)
        self.assertEqual(1, request.call_count)
        pause.assert_not_called()

    def test_health_wait_recovers_from_a_real_tcp_reset(self):
        attempts = [0]

        class StartingServer(http.server.BaseHTTPRequestHandler):
            def do_GET(self):
                attempts[0] += 1
                if attempts[0] == 1:
                    self.connection.setsockopt(socket.SOL_SOCKET, socket.SO_LINGER, struct.pack("ii", 1, 0))
                    self.close_connection = True
                    return
                self.send_response(200)
                self.end_headers()

            def log_message(self, *args):
                pass

        server = http.server.HTTPServer(("127.0.0.1", 0), StartingServer)
        worker = threading.Thread(target=server.serve_forever, daemon=True)
        worker.start()
        try:
            for _ in range(2):  # Initial startup and the same transport race after restart.
                attempts[0] = 0
                smoke.wait_for_health(f"http://127.0.0.1:{server.server_port}", timeout_seconds=5)
                self.assertGreaterEqual(attempts[0], 2)
        finally:
            server.shutdown()
            server.server_close()
            worker.join()

    def test_smoke_reinspects_the_mapped_port_after_container_restart(self):
        path, delivery = self.delivery()
        visited_ports = []
        current = [0]
        entry = delivery["extension"]
        manifest = {"protocolVersion": entry["aepVersion"],
                    "extension": {"id": entry["extensionId"], "version": entry["version"]},
                    "contributions": {entry["contributionKind"]: [{"id": entry["contributionId"]}]}}

        class Extension(http.server.BaseHTTPRequestHandler):
            def do_GET(self):
                if self.path == "/health":
                    visited_ports.append(self.server.server_port)
                    self.send_response(200)
                elif not self.headers.get("Authorization", "").startswith("Bearer "):
                    self.send_response(401)
                else:
                    self.send_response(200)
                    self.end_headers()
                    self.wfile.write(json.dumps(manifest).encode())
                    return
                self.end_headers()

            def log_message(self, *args):
                pass

        servers = [http.server.HTTPServer(("127.0.0.1", 0), Extension) for _ in range(2)]
        workers = [threading.Thread(target=server.serve_forever, daemon=True) for server in servers]
        for worker in workers:
            worker.start()

        def inspect(command, **kwargs):
            if command[:3] == ["docker", "image", "inspect"]:
                return json.dumps([{"Config": {"Labels": {"org.opencontainers.image.version": entry["version"],
                                                          "org.opencontainers.image.revision": delivery["sourceSha"]}}}])
            self.assertEqual(["docker", "inspect"], command[:2])
            return json.dumps([{"NetworkSettings": {"Ports": {"8080/tcp": [
                {"HostPort": str(servers[current[0]].server_port)}]}}}])

        def run(command, **kwargs):
            if command[:2] == ["docker", "restart"]:
                current[0] = 1
            return subprocess.CompletedProcess(command, 0)

        try:
            image = "localhost:5000/extension@" + delivery["digest"]
            with patch("sys.argv", ["smoke.py", "--delivery", str(path), "--image", image]), \
                    patch.object(smoke.subprocess, "run", side_effect=run), \
                    patch.object(smoke.subprocess, "check_output", side_effect=inspect), patch("builtins.print"):
                smoke.main()
            self.assertEqual([server.server_port for server in servers], visited_ports)
        finally:
            for server in servers:
                server.shutdown()
                server.server_close()
            for worker in workers:
                worker.join()


if __name__ == "__main__":
    unittest.main()
