"""Offline catalogue and version contracts for the autonomous Ollama AEP image."""
from pathlib import Path
import tempfile
import unittest

import images
import smoke


class OllamaImageContracts(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.directory = Path(temporary.name)

    def test_ollama_catalogue_uses_its_real_identity_and_independent_version(self):
        entry = images.catalogue("ollama")
        self.assertEqual("0.1.0", entry["version"])
        self.assertEqual("Agentstration.Extensions.Ollama", entry["extensionId"])
        self.assertEqual("Agentstration.Extensions.Ollama.dll", entry["entrypoint"])
        self.assertEqual("Dockerfile.extension", entry["dockerfile"])
        self.assertEqual("agentstration.io/v1", entry["aepVersion"])
        self.assertEqual(("modelProviders", "ollama"), (entry["contributionKind"], entry["contributionId"]))
        manifest = {"protocolVersion": entry["aepVersion"], "extension": {"id": entry["extensionId"], "version": entry["version"]},
                    "contributions": {"modelProviders": [{"id": "ollama"}]}}
        smoke.check_manifest(manifest, {"extension": entry})
        images.release_tag(entry, "extension/ollama/v0.1.0")
        for tag in ("extension/foundry/v0.1.0", "extension/ollama/v1.0.0"):
            with self.subTest(tag=tag), self.assertRaises(ValueError):
                images.release_tag(entry, tag)
        # Its source assembly owns the extension version, not the protocol or option contract.
        project = (images.ROOT / entry["project"]).read_text()
        self.assertIn("$(MSBuildProjectDirectory)/version.txt", project)
        self.assertIn("<Version>", project)
        command = images.build_command(entry, self.directory / "image.tar", self.directory / "metadata.json",
                                       {"sdk": "sdk@sha256:" + "a" * 64, "runtime": "runtime@sha256:" + "b" * 64}, "c" * 40, "owner/repo")
        self.assertIn("org.opencontainers.image.version=0.1.0", command)
        self.assertIn("--provenance=true", command)
        self.assertIn("--sbom=true", command)


if __name__ == "__main__":
    unittest.main()
