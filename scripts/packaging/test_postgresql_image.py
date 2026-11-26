"""Contracts for the implemented PostgreSQL JSONB document extension delivery."""
import copy
import unittest

import images
import smoke


class PostgreSqlImageContracts(unittest.TestCase):
    def test_document_extension_uses_its_actual_identity_and_shared_recipe(self):
        entry = images.catalogue("postgresql")
        self.assertEqual("agentstration.extensions.postgresql", entry["extensionId"])
        self.assertEqual("0.1.0", entry["version"])
        self.assertEqual("Dockerfile.extension", entry["dockerfile"])
        self.assertEqual("extension-postgresql", entry["imageName"])
        self.assertEqual("src/extensions/Agentstration.Extensions.PostgreSql/Agentstration.Extensions.PostgreSql.csproj", entry["project"])
        self.assertEqual({"PostgreSqlDocuments__AllowedClients__0": "agentstration-development"}, images.smoke_environment(entry))
        images.release_tag(entry, "extension/postgresql/v0.1.0")
        with self.assertRaises(ValueError):
            images.release_tag(entry, "extension/foundry/v0.1.0")

    def test_discovery_requires_both_independent_document_and_service_contributions(self):
        entry = images.catalogue("postgresql")
        manifest = {"protocolVersion": "agentstration.io/v1",
                    "extension": {"id": "agentstration.extensions.postgresql", "version": "0.1.0"},
                    "contributions": {"storageTypes": [{"id": "postgresql.jsonb", "preferredVersion": "1.0.0"}],
                                      "connectedServiceTypes": [{"id": "postgresql", "preferredVersion": "1.0.0"}]}}
        smoke.check_manifest(manifest, {"extension": entry})
        for kind in ("storageTypes", "connectedServiceTypes"):
            invalid = copy.deepcopy(manifest)
            invalid["contributions"][kind] = []
            with self.subTest(kind=kind), self.assertRaises(ValueError):
                smoke.check_manifest(invalid, {"extension": entry})


if __name__ == "__main__":
    unittest.main()
