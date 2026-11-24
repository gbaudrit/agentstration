"""Offline public contract boundaries; no provider credentials or network access."""
import copy
import json
import unittest
import uuid

import contract
import preview
from images import catalogue


def receipts():
    return [{"schemaVersion": 1, "component": catalogue(name), "repository": "owner/repo",
             "sourceSha": source * 40, "digest": "sha256:" + digest * 64}
            for name, source, digest in (("server", "a", "b"), ("maf-worker", "c", "d"))]


def request(**kwargs):
    if kwargs.get("provider", "docker") == "docker" and "target" not in kwargs:
        kwargs["target"] = {"port": 5080, "daemonId": "daemon-a"}
    return contract.plan(*receipts(), "example", **kwargs)


class ContractTests(unittest.TestCase):
    def test_default_request_is_canonical_and_keeps_independent_image_sources(self):
        value = request()
        self.assertEqual(value, contract.loads(json.dumps(value)))
        self.assertEqual("sqlite", value["database"])
        self.assertEqual("a" * 40, value["sourceSha"])
        self.assertEqual("c" * 40, contract.images(value)["worker"]["sourceSha"])
        self.assertNotIn("project", value["deliveries"]["server"]["component"])

    def test_unknown_or_secret_fields_are_rejected_at_every_boundary(self):
        for location in ((), ("target",), ("deliveries", "server"), ("deliveries", "server", "component")):
            value = request()
            selected = value
            for part in location:
                selected = selected[part]
            selected["password"] = "public-disposable-test-value"
            with self.subTest(location=location), self.assertRaises(ValueError):
                contract.validate(value)

    def test_contract_rejects_bad_types_ranges_and_unresolved_revisions(self):
        for key, invalid in (("schemaVersion", True), ("schemaVersion", 2), ("ttlHours", True), ("ttlHours", 0),
                             ("ttlHours", 169), ("ttlHours", "24"), ("environmentId", "../other"),
                             ("provider", ["docker"]), ("sourceSha", "main"), ("database", "other"),
                             ("dataMode", "implicit-reset"), ("testProfile", "live-llm"), ("registry", "docker.io")):
            value = request()
            value[key] = invalid
            with self.subTest(key=key, invalid=invalid), self.assertRaises(ValueError):
                contract.validate(value)
        value = request()
        del value["sourceSha"]
        with self.assertRaises(ValueError):
            contract.validate(value)

    def test_mutable_digests_wrong_kinds_and_protocols_are_rejected(self):
        for path, invalid in ((("digest",), "latest"), (("sourceSha",), "branch"), (("schemaVersion",), True),
                              (("component", "kind"), "aep-extension"), (("component", "version"), "1.0.0-alpha.01"),
                              (("component", "awpVersion"), "99.0"), (("component", "executionMaterialVersions"), [["1.0"]])):
            value = request()
            current = value["deliveries"]["worker"]
            for key in path[:-1]:
                current = current[key]
            current[path[-1]] = invalid
            with self.subTest(path=path), self.assertRaises(ValueError):
                contract.validate(value)

    def test_provider_fields_are_isolated_and_only_ssh_execution_is_unavailable(self):
        azure = request(provider="azure-vm", target={"subscriptionId": str(uuid.uuid4()), "resourceGroup": "previews", "imageVersion": "24.04.202511120", "region": "westeurope", "vmSize": "Standard_B2ms"})
        ssh = request(provider="ssh-docker", target={"host": "preview.example.test", "sshPort": 22, "user": "deploy",
                                                   "port": 5080, "hostKeyFingerprint": "SHA256:" + "a" * 43})
        for value in (azure, ssh):
            contract.operation("validate", value)
            for operation in ("deploy", "status", "destroy"):
                if value["provider"] == "ssh-docker":
                    with self.subTest(operation=operation), self.assertRaises(ValueError):
                        contract.operation(operation, value)
                else:
                    contract.operation(operation, value)
            wrong = copy.deepcopy(value)
            wrong["provider"] = "docker"
            with self.assertRaises(ValueError):
                contract.validate(wrong)
        azure["target"]["vmSize"] = "Standard_D64s_v5"
        with self.assertRaises(ValueError):
            contract.validate(azure)
        ssh["target"]["host"] = "host;command"
        with self.assertRaises(ValueError):
            contract.validate(ssh)

    def test_database_image_selection_is_optional_and_pinned(self):
        value = request(database="postgresql")
        self.assertRegex(value["postgresImage"], preview.POSTGRES_IMAGE)
        for image in ("postgres:17", "docker.io/library/postgres:18@sha256:" + "e" * 64):
            invalid = copy.deepcopy(value)
            invalid["postgresImage"] = image
            with self.assertRaises(ValueError):
                contract.validate(invalid)
        with self.assertRaises(ValueError):
            request(postgres_image=value["postgresImage"])

    def test_reset_is_only_an_explicit_destroy_operation(self):
        value = request(data_mode="reset")
        contract.operation("destroy", value)
        with self.assertRaises(ValueError):
            contract.operation("deploy", value)

    def test_duplicate_and_oversized_json_are_rejected(self):
        with self.assertRaises(ValueError):
            contract.loads('{"schemaVersion":1,"schemaVersion":1}')
        with self.assertRaises(ValueError):
            contract.loads(" " * 65537)


if __name__ == "__main__":
    unittest.main()
