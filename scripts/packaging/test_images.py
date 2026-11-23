"""Offline publication contracts; no registry, cloud credential or model access."""
import copy
import hashlib
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

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


if __name__ == "__main__":
    unittest.main()
