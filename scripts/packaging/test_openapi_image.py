"""OpenAPI catalogue contracts; HTTP/policy fixtures run in the selected MSTest project."""
import json
import unittest

import images
import smoke


class OpenApiImageContracts(unittest.TestCase):
    def test_catalogue_uses_openapi_identity_and_all_existing_contributions(self):
        entry = images.catalogue("openapi")
        self.assertEqual("0.1.0", entry["version"])
        self.assertEqual("Agentstration.Extensions.OpenApi", entry["extensionId"])
        self.assertEqual("Dockerfile.extension", entry["dockerfile"])
        self.assertEqual("extension-openapi", entry["imageName"])
        self.assertIn("Agentstration.Extensions.OpenApi.Tests", entry["testProject"])
        self.assertEqual({"OpenApi__AllowedDomains__0": "fixture.example.test"}, images.smoke_environment(entry))
        manifest = {"protocolVersion": "agentstration.io/v1",
                    "extension": {"id": "Agentstration.Extensions.OpenApi", "version": "0.1.0"},
                    "contributions": {"schemaFormats": [{"id": "openapi.validation", "version": "1.0.0"}],
                                      "tools": [{"id": "openapi.contract.discover"}, {"id": "openapi.operation.invoke"}],
                                      "dataSourceProfileBundles": [{"id": "openapi-governed"}, {"id": "openapi-connected-services"}],
                                      "connectedServiceTypes": [{"id": "openapi"}],
                                      "connectedServiceOperationTypes": [{"id": "openapi-operation"}]}}
        smoke.check_manifest(manifest, {"extension": entry})
        images.release_tag(entry, "extension/openapi/v0.1.0")
        with self.assertRaises(ValueError):
            images.release_tag(entry, "extension/foundry/v0.1.0")


if __name__ == "__main__":
    unittest.main()
