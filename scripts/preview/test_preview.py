"""Offline preview contracts: pinned artifacts, private state and effective Compose boundaries."""
import copy
import json
import os
from pathlib import Path
import shutil
import stat
import subprocess
import tempfile
import unittest
from unittest.mock import MagicMock, patch

import preview
from images import catalogue


class PreviewContracts(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="preview-contract-")
        self.addCleanup(self.temporary.cleanup)
        self.directory = Path(self.temporary.name) / "state"
        self.server = {"schemaVersion": 1, "component": catalogue("server"), "repository": "owner/repo",
                       "sourceSha": "a" * 40, "digest": "sha256:" + "b" * 64}
        self.worker = {"schemaVersion": 1, "component": catalogue("maf-worker"), "repository": "owner/repo",
                       "sourceSha": "c" * 40, "digest": "sha256:" + "d" * 64}

    def prepare(self, **arguments):
        return preview.prepare(self.directory, self.server, self.worker, "example", **arguments)

    def test_default_keeps_credentials_private_and_has_no_implicit_bootstrap(self):
        state = self.prepare()
        configuration = preview.read_json(self.directory / "server-configuration.json")
        self.assertFalse(configuration["Agentstration"]["Bootstrap"]["InitialBootstrapEnabled"])
        self.assertEqual("sqlite", state["database"])
        self.assertEqual(0o700, stat.S_IMODE(self.directory.stat().st_mode))
        for name in ("deployment.json", "server-configuration.json", "worker.key"):
            self.assertEqual(0o600, stat.S_IMODE((self.directory / name).stat().st_mode))
        key = (self.directory / "worker.key").read_text()
        self.assertGreaterEqual(len(key), 48)
        self.assertNotIn(key, json.dumps(state))
        self.assertNotIn(key, json.dumps(configuration))

    def test_independently_versioned_receipts_keep_their_own_sources_and_digests(self):
        state = self.prepare()
        self.assertEqual("a" * 40, state["sourceSha"])
        self.assertEqual("c" * 40, state["images"]["worker"]["sourceSha"])
        self.assertEqual("ghcr.io/owner/repo-server@" + self.server["digest"], state["images"]["server"]["reference"])
        self.assertEqual("ghcr.io/owner/repo-worker-maf@" + self.worker["digest"], state["images"]["worker"]["reference"])

    def test_repeat_preserves_identity_key_and_explicit_bootstrap(self):
        first = self.prepare(bootstrap={"InitialBootstrapEnabled": True, "Secrets": {"AdminPassword": "public-disposable-test-value"}})
        key = (self.directory / "worker.key").read_bytes()
        self.server["sourceSha"], self.server["digest"] = "e" * 40, "sha256:" + "f" * 64
        repeated = self.prepare()
        for name in ("instanceId", "workerId", "credentialId"):
            self.assertEqual(first[name], repeated[name])
        self.assertEqual(key, (self.directory / "worker.key").read_bytes())
        self.assertEqual("public-disposable-test-value", preview.read_json(self.directory / "server-configuration.json")["Agentstration"]["Bootstrap"]["Secrets"]["AdminPassword"])
        self.assertEqual("e" * 40, repeated["sourceSha"])

    def test_invalid_or_misclassified_receipts_cannot_create_state(self):
        for field, value in (("digest", "latest"), ("sourceSha", "branch"), ("schemaVersion", 2), ("repository", "owner/repo;command")):
            with self.subTest(field=field):
                server = copy.deepcopy(self.server)
                server[field] = value
                with self.assertRaises(ValueError):
                    preview.prepare(self.directory, server, self.worker, "example")
        for field, value in (("kind", "aep-extension"), ("id", "foundry"), ("imageName", "other"),
                             ("platform", "linux/arm64"), ("version", "invalid"), ("version", "1.0.0-alpha.01")):
            with self.subTest(field=field):
                server = copy.deepcopy(self.server)
                server["component"][field] = value
                with self.assertRaises(ValueError):
                    preview.prepare(self.directory, server, self.worker, "example")
        self.assertFalse(self.directory.exists())

    def test_invalid_ports_ids_and_remote_registries_are_rejected_before_writing(self):
        for arguments in ({"port": True}, {"port": -1}, {"port": 80}, {"port": 65536}, {"registry": "docker.io"},
                          {"registry": "untrusted.example:5000"}, {"registry": "localhost:80"}):
            with self.subTest(arguments=arguments), self.assertRaises(ValueError):
                self.prepare(**arguments)
        for identity in ("", "Upper", "../other", "x" * 41):
            with self.subTest(identity=identity), self.assertRaises(ValueError):
                preview.prepare(self.directory, self.server, self.worker, identity)
        self.assertFalse(self.directory.exists())

    def test_existing_environment_cannot_be_reassigned_or_have_its_missing_key_regenerated(self):
        self.prepare()
        original = (self.directory / "deployment.json").read_bytes()
        with self.assertRaises(ValueError):
            preview.prepare(self.directory, self.server, self.worker, "another")
        self.assertEqual(original, (self.directory / "deployment.json").read_bytes())
        (self.directory / "worker.key").unlink()
        with self.assertRaises(ValueError):
            self.prepare()
        self.assertFalse((self.directory / "worker.key").exists())

    def test_unrelated_directory_and_unignored_repository_paths_are_not_modified(self):
        self.directory.mkdir()
        sentinel = self.directory / "unrelated"
        sentinel.write_text("preserve")
        with self.assertRaises(ValueError):
            self.prepare()
        self.assertEqual([sentinel], list(self.directory.iterdir()))
        with patch.object(preview, "ROOT", Path(self.temporary.name)):
            target = Path(self.temporary.name) / "unignored"
            with self.assertRaises(ValueError):
                preview.prepare(target, self.server, self.worker, "example")
            self.assertFalse(target.exists())
            ignored = Path(self.temporary.name) / ".agentstration-preview/example"
            preview.prepare(ignored, self.server, self.worker, "example")
            self.assertTrue((ignored / "worker.key").is_file())

    def test_state_symlinks_are_not_followed(self):
        self.prepare()
        target = Path(self.temporary.name) / "outside.json"
        target.write_text((self.directory / "deployment.json").read_text())
        (self.directory / "deployment.json").unlink()
        (self.directory / "deployment.json").symlink_to(target)
        with self.assertRaises(ValueError):
            self.prepare()

    def test_repository_state_is_excluded_from_git_and_docker_build_context(self):
        docker_rules = (preview.ROOT / ".dockerignore").read_text().splitlines()
        git_rules = (preview.ROOT / ".gitignore").read_text().splitlines()
        self.assertIn("**/.agentstration*", docker_rules)
        self.assertIn(".agentstration*/", git_rules)

    def test_recorded_references_and_namespace_override_ambient_compose_values(self):
        state = self.prepare()
        with patch.dict(os.environ, {"COMPOSE_FILE": "untrusted.yml", "COMPOSE_PROJECT_NAME": "other",
                                    "COMPOSE_PROFILES": "inference", "PREVIEW_SERVER_IMAGE": "floating:latest"}):
            command, environment = preview.compose_command(self.directory)
        self.assertEqual("preview-example", command[command.index("--project-name") + 1])
        self.assertNotIn("COMPOSE_FILE", environment)
        self.assertNotIn("COMPOSE_PROFILES", environment)
        self.assertEqual(state["images"]["server"]["reference"], environment["PREVIEW_SERVER_IMAGE"])
        self.assertEqual("/dev/null", command[command.index("--env-file") + 1])

    @unittest.skipUnless(shutil.which("docker"), "Compose configuration check requires the Docker CLI, without a daemon or image pull")
    def test_effective_compose_has_only_pinned_services_and_least_privilege_mounts(self):
        state = self.prepare(port=0)
        command, environment = preview.compose_command(self.directory)
        model = json.loads(subprocess.check_output([*command, "config", "--format", "json"], env=environment, text=True))
        self.assertEqual({"server", "worker"}, set(model["services"]))
        self.assertEqual("preview-example", model["name"])
        for name, service in model["services"].items():
            self.assertNotIn("build", service)
            self.assertEqual(state["images"][name]["reference"], service["image"])
            self.assertNotIn("development", json.dumps(service))
        server, worker = model["services"]["server"], model["services"]["worker"]
        self.assertEqual("Preview", server["environment"]["ASPNETCORE_ENVIRONMENT"])
        self.assertEqual("Deterministic", server["environment"]["AI__Provider"])
        self.assertEqual("Sqlite", server["environment"]["Agentstration__Storage__Provider"])
        self.assertTrue(all(port["host_ip"] == "127.0.0.1" for port in server["ports"]))
        self.assertNotIn("ports", worker)
        self.assertNotIn("volumes", worker)
        self.assertEqual(["awp_key"], [value["source"] for value in worker["secrets"]])
        self.assertTrue(all("ConnectionStrings" not in key for key in worker["environment"]))

    def test_verification_rejects_unready_replaced_images_and_worker_database_access(self):
        state = self.prepare()
        containers = {name: {"Config": {"Image": image["reference"], "Env": []}, "Image": name,
                     "State": {"Health": {"Status": "healthy"}}, "Mounts": []} for name, image in state["images"].items()}
        containers["worker"]["Mounts"] = [{"Destination": "/run/secrets/awp_key", "RW": False}]

        def evaluate(values):
            def inspect(command, **_arguments):
                if command[1] == "inspect":
                    return json.dumps([values[command[-1]]])
                image = state["images"][command[-1]]
                return json.dumps([{"Config": {"Labels": {"org.opencontainers.image.revision": image["sourceSha"],
                                                        "org.opencontainers.image.version": image["version"]}}}])
            response = MagicMock()
            response.__enter__.return_value.status = 200
            with patch.object(preview, "compose", side_effect=lambda _directory, *args, **_kwargs: args[-1]), \
                    patch.object(preview, "server_address", return_value="http://127.0.0.1:5080"), \
                    patch.object(preview.subprocess, "check_output", side_effect=inspect), \
                    patch.object(preview.urllib.request, "urlopen", return_value=response):
                return preview.verify(self.directory)

        self.assertEqual("sqlite", evaluate(containers)["database"])
        for field, value in (("Config", {"Image": "floating:latest", "Env": []}),
                             ("State", {"Health": {"Status": "unhealthy"}}),
                             ("Mounts", [{"Destination": "/data", "RW": True}])):
            invalid = copy.deepcopy(containers)
            invalid["worker"][field] = value
            with self.subTest(field=field), self.assertRaises(ValueError):
                evaluate(invalid)
        invalid = copy.deepcopy(containers)
        invalid["worker"]["Config"]["Env"] = ["ConnectionStrings__Agentstration=forbidden"]
        with self.assertRaises(ValueError):
            evaluate(invalid)


if __name__ == "__main__":
    unittest.main()
