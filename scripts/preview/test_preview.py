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
import preview_smoke
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
        self.assertEqual("service_started", worker["depends_on"]["server"]["condition"])
        self.assertEqual(["awp_key"], [value["source"] for value in worker["secrets"]])
        self.assertTrue(all("ConnectionStrings" not in key for key in worker["environment"]))

    def test_verification_rejects_unready_replaced_images_and_worker_database_access(self):
        state = self.prepare()
        containers = {name: {"Config": {"Image": image["reference"], "Env": []}, "Image": name,
                     "State": {"Health": {"Status": "healthy"}}, "Mounts": []} for name, image in state["images"].items()}
        containers["worker"]["Mounts"] = [{"Destination": "/run/secrets/awp_key", "RW": False}]
        containers["server"]["HostConfig"] = {"PortBindings": {"8080/tcp": [{"HostIp": "127.0.0.1", "HostPort": str(state["port"])}]}}

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
        for address in ('0.0.0.0', '::'):
            invalid = copy.deepcopy(containers)
            invalid['server']['HostConfig']['PortBindings']['8080/tcp'][0]['HostIp'] = address
            with self.subTest(address=address), self.assertRaisesRegex(ValueError, 'loopback'):
                evaluate(invalid)
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

    def test_failure_evidence_keeps_technical_codes_and_redacts_credentials(self):
        self.prepare()
        credential = "private-credential-must-not-appear"
        opaque_token = "x" * 48
        logs = "System.InvalidOperationException: bootstrap_profile_failed " + credential + " " + opaque_token
        with patch.object(preview, "compose", return_value=logs):
            diagnostics = preview_smoke.failure_diagnostics(self.directory, [credential])
        encoded = json.dumps(diagnostics)
        self.assertNotIn(credential, encoded)
        self.assertNotIn(opaque_token, encoded)
        self.assertIn("bootstrap_profile_failed", diagnostics["codes"])
        self.assertIn("InvalidOperationException", encoded)

    def test_smoke_password_always_satisfies_the_non_development_character_policy(self):
        with patch.object(preview_smoke.secrets, "token_urlsafe", return_value="x" * 54):
            password = preview_smoke.bootstrap_password()
        self.assertGreaterEqual(len(password), 12)
        self.assertTrue(any(value.islower() for value in password))
        self.assertTrue(any(value.isupper() for value in password))
        self.assertTrue(any(value.isdigit() for value in password))
        self.assertTrue(any(not value.isalnum() for value in password))

    def test_postgresql_credentials_are_private_and_preserved_on_repeat(self):
        first = self.prepare(database="postgresql")
        secret = (self.directory / "postgres.password").read_text()
        self.assertEqual(64, len(secret))
        self.assertEqual(0o600, stat.S_IMODE((self.directory / "postgres.password").stat().st_mode))
        self.assertRegex(first["images"]["postgres"]["reference"], preview.POSTGRES_IMAGE)
        configuration = preview.read_json(self.directory / "server-configuration.json")
        self.assertIn("Password=" + secret, configuration["ConnectionStrings"]["Agentstration"])
        self.assertNotIn(secret, json.dumps(first))
        _, environment = preview.compose_command(self.directory)
        self.assertFalse(any(secret in value for value in environment.values()),
                         "A database credential must not enter the Compose process environment.")
        repeated = self.prepare()
        self.assertEqual(first, repeated)
        self.assertEqual(secret, (self.directory / "postgres.password").read_text())
        changed = "docker.io/library/postgres:17@sha256:" + "e" * 64
        self.assertEqual(changed, self.prepare(postgres_image=changed)["images"]["postgres"]["reference"])

    def test_database_selection_cannot_change_for_existing_state(self):
        for database, other in (("sqlite", "postgresql"), ("postgresql", "sqlite")):
            with self.subTest(database=database):
                self.directory = Path(self.temporary.name) / database
                self.prepare(database=database)
                before = {file.name: file.read_bytes() for file in self.directory.iterdir()}
                with self.assertRaises(ValueError):
                    self.prepare(database=other)
                self.assertEqual(before, {file.name: file.read_bytes() for file in self.directory.iterdir()})

    def test_unsupported_database_and_floating_or_major_upgrade_images_are_rejected_before_writing(self):
        for arguments in ({"database": "unknown"}, {"database": "postgresql", "postgres_image": "postgres:17"},
                          {"database": "postgresql", "postgres_image": "docker.io/library/postgres:18@sha256:" + "e" * 64},
                          {"database": "postgresql", "postgres_image": "untrusted.example/postgres:17@sha256:" + "e" * 64},
                          {"postgres_image": "docker.io/library/postgres:17@sha256:" + "e" * 64}):
            with self.subTest(arguments=arguments), self.assertRaises(ValueError):
                self.prepare(**arguments)
            self.assertFalse(self.directory.exists())

    def test_postgresql_missing_or_symlinked_password_is_not_regenerated(self):
        self.prepare(database="postgresql")
        path = self.directory / "postgres.password"
        original = path.read_text()
        path.unlink()
        with self.assertRaises(ValueError):
            self.prepare()
        self.assertFalse(path.exists())
        target = Path(self.temporary.name) / "outside.password"
        target.write_text(original)
        path.symlink_to(target)
        with self.assertRaises(ValueError):
            self.prepare()
        self.assertEqual(original, target.read_text())

    @unittest.skipUnless(shutil.which("docker"), "Compose configuration requires the Docker CLI")
    def test_effective_postgresql_overlay_excludes_password_values_and_keeps_worker_isolated(self):
        state = self.prepare(database="postgresql")
        command, environment = preview.compose_command(self.directory)
        model = json.loads(subprocess.check_output([*command, "config", "--format", "json"], env=environment, text=True))
        self.assertEqual({"server", "worker", "postgres"}, set(model["services"]))
        database = model["services"]["postgres"]
        self.assertEqual(state["images"]["postgres"]["reference"], database["image"])
        self.assertEqual("linux/amd64", database["platform"])
        self.assertNotIn("ports", database)
        self.assertNotIn("POSTGRES_PASSWORD", database["environment"])
        self.assertEqual("/run/secrets/postgres_password", database["environment"]["POSTGRES_PASSWORD_FILE"])
        secret = (self.directory / "postgres.password").read_text()
        self.assertNotIn(secret, json.dumps(model))
        server, worker = model["services"]["server"], model["services"]["worker"]
        self.assertEqual("PostgreSql", server["environment"]["Agentstration__Storage__Provider"])
        self.assertEqual("service_healthy", server["depends_on"]["postgres"]["condition"])
        self.assertEqual("service_started", worker["depends_on"]["server"]["condition"])
        self.assertNotIn("ConnectionStrings__Agentstration", server["environment"])
        self.assertNotIn("volumes", worker)
        self.assertEqual(["awp_key"], [item["source"] for item in worker["secrets"]])
        self.assertEqual({"preview-example_data", "preview-example_postgres-data"},
                         {volume["name"] for volume in model["volumes"].values()})

    def test_postgresql_verification_rejects_published_ports_and_password_environments(self):
        state = self.prepare(database="postgresql")
        postgres = {"Config": {"Image": state["images"]["postgres"]["reference"], "Env": []},
                    "State": {"Health": {"Status": "healthy"}}, "HostConfig": {"PortBindings": {}}, "Image": "postgres"}

        def inspect(command, **_arguments):
            if command[1] == "image":
                image = state["images"][command[-1]]
                return json.dumps([{"Config": {"Labels": {"org.opencontainers.image.revision": image["sourceSha"],
                                                         "org.opencontainers.image.version": image["version"]}}}])
            if command[-1] == "postgres":
                return json.dumps([postgres])
            return json.dumps([{"Config": {"Image": state["images"][command[-1]]["reference"], "Env": []},
                                "HostConfig": {"PortBindings": {"8080/tcp": [{"HostIp": "127.0.0.1", "HostPort": str(state['port'])}]}},
                                "Image": command[-1], "State": {"Health": {"Status": "healthy"}},
                                "Mounts": [{"Destination": "/run/secrets/awp_key", "RW": False}]}])

        response = MagicMock()
        response.__enter__.return_value.status = 200
        with patch.object(preview, "compose", side_effect=lambda _directory, *args, **_kwargs: args[-1]), \
                patch.object(preview, "server_address", return_value="http://127.0.0.1:5080"), \
                patch.object(preview.subprocess, "check_output", side_effect=inspect), \
                patch.object(preview.urllib.request, "urlopen", return_value=response):
            self.assertEqual("postgresql", preview.verify(self.directory)["database"])
            postgres["HostConfig"]["PortBindings"] = {"5432/tcp": [{"HostPort": "5432"}]}
            with self.assertRaises(ValueError):
                preview.verify(self.directory)
            postgres["HostConfig"]["PortBindings"] = {}
            postgres["Config"]["Env"] = ["POSTGRES_PASSWORD=disposable-test-value"]
            with self.assertRaises(ValueError):
                preview.verify(self.directory)

    def test_postgresql_smoke_requires_module_tables_in_the_selected_database(self):
        schemas = ["management", "work", "flow", "runtime", "identity", "scheduler", "resource_planning"]
        with patch.object(preview, "compose", return_value="\n".join(schemas)) as execute:
            self.assertEqual(set(schemas), set(preview_smoke.database_evidence(self.directory, "postgresql")["schemas"]))
            self.assertIn("psql", execute.call_args.args)
        with patch.object(preview, "compose", return_value="management\n"):
            with self.assertRaises(ValueError):
                preview_smoke.database_evidence(self.directory, "postgresql")


if __name__ == "__main__":
    unittest.main()
